using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Globalization;
using System.Runtime.Loader;
using System.Diagnostics.CodeAnalysis;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreCompilationTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCorePrimitives;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("safe-core runs modules calls locals and typed branches", RunsCoreAsync),
        new("safe-core preserves short circuit side effects", RunsShortCircuitAsync),
        new("safe-core returns from operands with no residual IL stack", RunsEarlyReturnsAsync),
        new("safe-core supports integer forms and parameter mutation", RunsIntegersAsync),
        new("safe-core preserves shadowing and recursion", RunsShadowingAsync),
        new("safe-core diagnoses invalid types and immutable assignments", RejectsInvalidTypesAsync),
        new("safe-core rejects unsupported semantics explicitly", RejectsUnsupportedAsync),
        new("safe-core rejects input before writing artifacts", RejectsBeforeOutputAsync),
        new("safe-core emits deterministic PE and source-mapped PDB", EmitsMetadataAsync),
        new("safe-core respects cancellation and local limits", RespectsLimitsAsync),
        new("safe-core checked overflow fails execution", ChecksOverflowAsync),
        new("CLR LIR validates binary operands and argument indices", ValidatesNewInstructionsAsync),
        new("safe-core integer display is independent of culture", DisplaysInvariantlyAsync),
    ];

    private static Task RunsCoreAsync() => RunAsync("""
        mod math { pub fn adjust(value: i32, enabled: bool) -> i32 {
            if enabled { value * 2 + 1 } else { value - 1 }
        } }
        use math::adjust;
        fn main() {
            let mut value = 20;
            value = adjust(value, true);
            let answer: i32 = if value == 41 { value + 1 } else { 0 };
            println!("Safe core on .NET");
            println!("{}", answer);
            println!("{}", answer == 42 && !(answer < 0));
        }
        """, "Safe core on .NET\n42\ntrue\n");

    private static Task RunsShortCircuitAsync() => RunAsync("""
        fn probe() -> bool { println!("probe"); true }
        fn main() {
            println!("{}", false && probe());
            println!("{}", true || probe());
            println!("{}", true && probe());
            println!("{}", false || probe());
        }
        """, "false\ntrue\nprobe\ntrue\nprobe\ntrue\n");

    private static Task RunsEarlyReturnsAsync() => RunAsync("""
        fn sum(a: i32, b: i32) -> i32 { a + b }
        fn left() -> i32 { println!("left"); 3 }
        fn nested(flag: bool) -> i32 {
            let result = left() + if flag { return 9; } else { 4 };
            result
        }
        fn args() -> i32 { sum(left(), { return 11; }) }
        fn exits(flag: bool) -> i32 { if flag { return 1; } else { return 2; } }
        fn binding() -> i32 { let unused = { return 3; true }; }
        fn operand() -> i32 { let unused = 1 + { return 4; 2 }; }
        fn main() {
            println!("{}", nested(true));
            println!("{}", nested(false));
            println!("{}", args());
            println!("{}", exits(false));
            println!("{}", binding());
            println!("{}", operand());
            if true { println!("branch"); }
            println!("tail");
        }
        """, "left\n9\nleft\n7\nleft\n11\n2\n3\n4\nbranch\ntail\n");

    private static Task RunsIntegersAsync() => RunAsync("""
        fn inc(mut x: i32) -> i32 { x = x + 1; x }
        fn main() {
            println!("{}", inc(0b10_i32) + 0o10 + 0x10 + 1_0);
            println!("{}", -2147483648);
            println!("{}", !0i32);
            println!("{}", -1 <= 0 && 5 >= 5 && 2 != 3);
        }
        """, "37\n-2147483648\n-1\ntrue\n");

    private static Task RunsShadowingAsync() => RunAsync("""
        fn fib(n: i32) -> i32 { if n < 2 { n } else { fib(n - 1) + fib(n - 2) } }
        fn main() {
            let value = 7;
            let value = value + 1;
            { let value = true; println!("{}", value); }
            println!("{}", value);
            println!("{}", fib(8));
        }
        """, "true\n8\n21\n");

    private static Task RejectsInvalidTypesAsync()
    {
        (string Source, string Code)[] cases =
        [
            ("fn main() { let value: bool = 1; }", "RST1002"),
            ("fn main() { let x = 1; x = 2; }", "RST1003"),
            ("fn main() { let mut x = 1; x = true; }", "RST1002"),
            ("fn main() { if 1 {} }", "RST1002"),
            ("fn main() { let x = if true { 1 } else { false }; }", "RST1002"),
            ("fn main() { if true { 1 } }", "RST1002"),
            ("fn f() -> i32 { true } fn main() {}", "RST1002"),
            ("fn f(x: i32) {} fn main() { f(true); }", "RST1002"),
            ("fn f(x: i32) {} fn main() { f(); }", "RST1004"),
            ("fn main() -> i32 { 0 }", "RST1005"),
            ("fn main() { let x = 2147483648; }", "RST1006"),
            ("fn main() { let x = -2147483649; }", "RST1006"),
            ("fn main() { true + false; }", "RST1002"),
            ("fn main() { if true { 1 } println!(\"bad\"); }", "RST1002"),
            ("fn value() -> i32 { return 1; true } fn main() {}", "RST1002"),
            ("fn main() { let x = -( -2147483648); }", "RST1006"),
            ("fn main() { let x = (2147483647) + 1; }", "RST1006"),
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var item in cases) AssertRejected(item.Source, item.Code, deadline.Token);
        return Task.CompletedTask;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This regression loads emitted IL in the untrimmed test host to vary CurrentCulture. Native AOT is verified in a separate publish probe.")]
    private static Task DisplaysInvariantlyAsync()
    {
        const string source = "fn main() { println!(\"{}\", -42); }";
        var context = new AssemblyLoadContext("safe-core-culture-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        TextWriter previousOutput = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            SafeCoreClrResult lir = Lower(source);
            GeneratedAssembly first = ClrLirAssemblyEmitter.EmitProgram(lir, "Invariant", source, Path.GetFullPath("invariant.rs"), "invariant.pdb");
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "minus";
            CultureInfo.CurrentCulture = culture;
            GeneratedAssembly second = ClrLirAssemblyEmitter.EmitProgram(lir, "Invariant", source, Path.GetFullPath("invariant.rs"), "invariant.pdb");
            AssertEx.True(first.PeImage.AsSpan().SequenceEqual(second.PeImage), "Emission must be independent of culture.");
            using var stream = new MemoryStream(second.PeImage);
            Action entry = context.LoadFromStream(stream).EntryPoint!.CreateDelegate<Action>();
            Console.SetOut(output);
            entry();
            AssertEx.Equal("-42" + Environment.NewLine, output.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            CultureInfo.CurrentCulture = previousCulture;
            context.Unload();
        }

        return Task.CompletedTask;
    }

    private static Task RejectsUnsupportedAsync()
    {
        string[] sources =
        [
            "fn main() { let x: i32; }",
            "fn main() { let x = 1; let r = &x; }",
            "fn main() { let x = (1, 2); }",
            "fn main() { let x = [1, 2]; }",
            "fn main() { let x: u32 = 1; }",
            "fn main() { let x = 1u32; }",
            "fn main() { let x = 1.5; }",
            "fn main() { let x = +1; }",
            "fn main() { let x = 4 / 2; }",
            "fn main() { let mut x = 1; x += 1; }",
            "fn f<T>(x: T) {} fn main() {}",
            "struct Data { x: i32 } fn main() {}",
            "#[cfg(any())] fn main() {}",
            "fn main() { println!(\"value {}\", 1); }",
            "fn main() { let x = (); }",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (string source in sources) AssertRejected(source, "RST1001", deadline.Token);
        return Task.CompletedTask;
    }

    private static Task RejectsBeforeOutputAsync()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "rejected.dll");
            CompilationResult result = CompilerDriver.Compile("fn main() { let x: bool = 1; }",
                Path.Combine(directory, "rejected.rs"), path, profile: Profile);
            AssertEx.False(result.Success, "A type error must fail compilation.");
            AssertEx.Equal(0, Directory.GetFiles(directory).Length, "Rejected input must not create output or transaction files.");
        }
        finally { DeleteOwnedDirectory(directory); }
        return Task.CompletedTask;
    }

    private static Task EmitsMetadataAsync()
    {
        const string source = "fn helper(x: i32) -> i32 { x + 1 } fn main() { println!(\"{}\", helper(4)); }";
        SafeCoreClrResult lir = Lower(source);
        GeneratedAssembly first = ClrLirAssemblyEmitter.EmitProgram(lir, "SafeCoreMetadata", source,
            Path.GetFullPath("metadata.rs"), "metadata.pdb");
        GeneratedAssembly second = ClrLirAssemblyEmitter.EmitProgram(lir, "SafeCoreMetadata", source,
            Path.GetFullPath("metadata.rs"), "metadata.pdb");
        AssertEx.True(first.PeImage.AsSpan().SequenceEqual(second.PeImage), "PE must be deterministic.");
        AssertEx.True(first.PdbImage.AsSpan().SequenceEqual(second.PdbImage), "PDB must be deterministic.");
        using var pe = new PEReader(new MemoryStream(first.PeImage));
        AssertEx.Equal(2, pe.GetMetadataReader().MethodDefinitions.Count);
        using var pdb = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(first.PdbImage!));
        MetadataReader reader = pdb.GetMetadataReader();
        AssertEx.Equal(2, reader.MethodDebugInformation.Count);
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
            AssertEx.True(method.GetSequencePoints().Any(point => !point.IsHidden && point.StartLine == 1),
                "Each method must map to its Rust source span.");
            AssertEx.False(method.LocalSignature.IsNil, "Source mappings must carry the method local signature.");
        }

        return Task.CompletedTask;
    }

    private static Task RespectsLimitsAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CompilerDriver.Check("fn main() {}", profile: Profile,
            cancellationToken: cancelled.Token));
        string source = "fn main() {" + string.Concat(Enumerable.Repeat("let x = 1;", 257)) + "}";
        AssertRejected(source, "RST2001");
        return Task.CompletedTask;
    }

    private static Task ChecksOverflowAsync() => RunAsync("""
        fn add(x: i32) -> i32 { x + 1 }
        fn main() { println!("{}", add(2147483647)); }
        """, string.Empty, expectOverflow: true);

    private static Task ValidatesNewInstructionsAsync()
    {
        var invalidArgument = new ClrLirMethod("Main", ClrLirType.Void, [], [],
            [new ClrLirBlock("entry", [new ClrLirLoadArgument(0), new ClrLirReturn()])]);
        AssertEx.False(invalidArgument.Validate().IsValid, "Out-of-range arguments must fail validation.");
        var invalidOperation = new ClrLirMethod("Main", ClrLirType.Bool, [], [],
            [new ClrLirBlock("entry", [new ClrLirLoadBoolean(true), new ClrLirLoadBoolean(false),
                new ClrLirBinary(ClrLirBinaryOperator.AddChecked, ClrLirType.Bool), new ClrLirReturn()])]);
        AssertEx.False(invalidOperation.Validate().IsValid, "Boolean arithmetic must fail validation.");
        return Task.CompletedTask;
    }

    private static SafeCoreClrResult Lower(string source)
    {
        SafeCoreTypeCheckResult types = SafeCoreTypeChecking.Check(SafeCoreHirLowering.Lower(SafeCoreSyntax.Parse(source)));
        AssertEx.True(types.IsSuccessful, string.Join("; ", types.Diagnostics));
        SafeCoreClrResult lir = SafeCoreClrLowering.Lower(types.Program!);
        AssertEx.True(lir.IsSuccessful, string.Join("; ", lir.Diagnostics));
        return lir;
    }

    private static void AssertRejected(string source, string code, CancellationToken cancellationToken = default)
    {
        CompilationResult result = CompilerDriver.Check(source, "rejected.rs", Profile, cancellationToken);
        AssertEx.False(result.Success, "Expected rejection: " + source);
        AssertEx.Equal(code, result.Diagnostics[0].Code, source + ": " + string.Join("; ", result.Diagnostics));
        AssertEx.True(result.Diagnostics[0].Span.Start >= 0 && result.Diagnostics[0].Span.End <= source.Length,
            "Diagnostic span must refer to the source document.");
    }

    private static async Task RunAsync(string source, string expected, bool expectOverflow = false)
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "program.dll");
            CompilationResult compiled = CompilerDriver.Compile(source, Path.Combine(directory, "program.rs"),
                path, profile: Profile);
            AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [path], directory, TimeSpan.FromSeconds(15)), deadline.Token).ConfigureAwait(false);
            AssertEx.False(run.ProcessTreeCleanupIncomplete, "Owned process cleanup must complete.");
            if (expectOverflow)
            {
                AssertEx.False(run.Succeeded, "Checked overflow must fail execution.");
                AssertEx.True(run.StandardError.Contains("OverflowException", StringComparison.Ordinal), run.StandardError);
            }
            else AssertEx.True(run.Succeeded, run.StandardError);
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { DeleteOwnedDirectory(directory); }
    }

    private static string NewDirectory()
    {
        string path = Path.GetFullPath(Path.Combine("artifacts", "tests", "safe-core-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteOwnedDirectory(string path)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests")) + Path.DirectorySeparatorChar;
        AssertEx.True(Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal) &&
            Path.GetFileName(path).StartsWith("safe-core-", StringComparison.Ordinal), "Cleanup must remain in the task-owned test directory.");
        Directory.Delete(path, recursive: true);
    }
}
