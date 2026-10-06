using System.Diagnostics;
using RustSharp.Compiler;
using RustSharp.CodeGen.IL;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1ControlFlowDropTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 generated break drops only exited loop scopes", BreakAsync),
        new("P1 generated continue consumes each iteration before the next", ContinueAsync),
        new("P1 generated labelled break cleans nested scopes in reverse order", LabelledBreakAsync),
        new("P1 generated early return evaluates its scalar operand once before cleanup", ReturnOperandAsync),
        new("P1 generated owned return transfers its Drop value exactly once", OwnedReturnAsync),
        new("P1 generated moved argument belongs only to its callee cleanup", MovedArgumentAsync),
        new("P1 generated uninitialized Drop storage is conditional on successful assignment", UninitializedOwnerAsync),
        new("P1 generated uninitialized enum cleanup does not read inactive storage", UninitializedEnumAsync),
        new("P1 generated discarded block result expires at its statement", DiscardedBlockAsync),
        new("P1 generated owned closure capture drops at its declaration scope", OwnedClosureCaptureAsync),
        new("P1 generated destructor return drops body locals before owned fields", DestructorReturnAsync),
        new("P1 generated abort policy skips local and caller unwind Drop", AbortPolicyAsync),
        new("P1 local panic interface rejects invalid and unsupported policy declarations", RejectsPolicyAsync),
    ];

    private static Task BreakAsync() => RunAsync("""
        struct Outer;
        struct Inner;
        impl Drop for Outer { fn drop(&mut self) { println!("outer"); } }
        impl Drop for Inner { fn drop(&mut self) { println!("inner"); } }
        fn main() { let outer = Outer; loop { let inner = Inner; println!("body"); break; } println!("after"); }
        """, "body\ninner\nafter\nouter\n");

    private static Task ContinueAsync() => RunAsync("""
        struct Marker;
        impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
        fn main() {
            let mut count: i32 = 0;
            while count < 2 { let marker = Marker; println!("iteration"); count += 1; continue; }
            println!("after");
        }
        """, "iteration\ndrop\niteration\ndrop\nafter\n");

    private static Task LabelledBreakAsync() => RunAsync("""
        struct Outer;
        struct First;
        struct Second;
        impl Drop for Outer { fn drop(&mut self) { println!("outer"); } }
        impl Drop for First { fn drop(&mut self) { println!("first"); } }
        impl Drop for Second { fn drop(&mut self) { println!("second"); } }
        fn main() {
            let outer = Outer;
            'target: loop { let first = First; loop { let second = Second; println!("body"); break 'target; } }
            println!("after");
        }
        """, "body\nsecond\nfirst\nafter\nouter\n");

    private static Task ReturnOperandAsync() => RunAsync("""
        struct Outer;
        struct Inner;
        impl Drop for Outer { fn drop(&mut self) { println!("outer"); } }
        impl Drop for Inner { fn drop(&mut self) { println!("inner"); } }
        fn operand() -> i32 { println!("operand"); 7 }
        fn value() -> i32 { let outer = Outer; { let inner = Inner; return operand(); } }
        fn main() { println!("{}", value()); }
        """, "operand\ninner\nouter\n7\n");

    private static Task OwnedReturnAsync() => RunAsync("""
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        fn value() -> Marker {
            let other = Marker { value: 1 };
            let returned = Marker { value: 2 };
            return returned;
        }
        fn tail() -> Marker { let other = Marker { value: 3 }; let returned = Marker { value: 4 }; returned }
        fn main() { let first = value(); let second = tail(); println!("body"); }
        """, "1\n3\nbody\n4\n2\n");

    private static Task DestructorReturnAsync() => RunAsync("""
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        struct Owner { field: Marker }
        impl Drop for Owner {
            fn drop(&mut self) { let local = Marker { value: 1 }; println!("outer"); return; }
        }
        fn main() { let owner = Owner { field: Marker { value: 2 } }; println!("body"); }
        """, "body\nouter\n1\n2\n");

    private static Task MovedArgumentAsync() => RunAsync("""
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        fn consume(value: Marker) { println!("callee"); }
        fn main() {
            let retained = Marker { value: 1 };
            let moved = Marker { value: 2 };
            consume(moved);
            println!("after");
        }
        """, "callee\n2\nafter\n1\n");

    private static Task UninitializedOwnerAsync() => RunAsync("""
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        fn main() {
            let absent: Marker;
            let present: Marker;
            if true { present = Marker { value: 1 }; }
            println!("body");
        }
        """, "body\n1\n");

    private static Task UninitializedEnumAsync() => RunAsync("""
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        enum Choice { One(Marker), Empty }
        fn main() {
            let absent: Choice;
            let present: Choice;
            if true { present = Choice::One(Marker { value: 1 }); }
            println!("body");
        }
        """, "body\n1\n");

    private static Task DiscardedBlockAsync() => RunAsync("""
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        fn main() { { let marker = Marker { value: 1 }; marker }; println!("after"); }
        """, "1\nafter\n");

    private static Task OwnedClosureCaptureAsync() => RunAsync("""
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        fn observe(value: &Marker) { println!("{}", value.value); }
        fn main() {
            let owner = Marker { value: 1 };
            let callback = move || observe(&owner);
            callback();
            println!("after");
        }
        """, "1\nafter\n1\n");

    private static Task RejectsPolicyAsync()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => CompilerDriver.CheckWithPanicStrategy(
            "fn main() {}", "panic-policy.rs", (SafeCorePanicStrategy)123));
        CompilationResult legacy = CompilerDriver.CheckWithPanicStrategy("fn main() {}", "panic-policy.rs",
            SafeCorePanicStrategy.Abort, CompilationProfile.SafeCorePrimitives);
        AssertEx.False(legacy.Success, "A non-MIR profile cannot silently ignore a declared panic policy.");
        AssertEx.Equal("RSC0010", legacy.Diagnostics.Single().Code);
        SafeCoreMirPipelineResult pipeline = SafeCoreMirPipeline.Analyze("fn main() {}", "panic-policy.rs", new()
        {
            PanicStrategy = SafeCorePanicStrategy.Abort,
            RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5),
        });
        AssertEx.True(pipeline.IsSuccessful, string.Join("; ", pipeline.Diagnostics));
        AssertEx.True(pipeline.Mir!.Program!.Functions.All(function => function.PanicStrategy == SafeCorePanicStrategy.Abort) &&
            pipeline.Ownership!.Program!.Functions.All(function => function.PanicStrategy == SafeCorePanicStrategy.Abort) &&
            pipeline.Cleanup!.Functions.All(function => function.PanicStrategy == SafeCorePanicStrategy.Abort),
            "MIR, ownership and cleanup evidence must publish the same declared policy.");
        return Task.CompletedTask;
    }

    private static async Task AbortPolicyAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("must-not-drop"); } }
            fn inner() { let marker = Marker; println!("body"); let max: i32 = 2147483647; println!("{}", max + 1); }
            fn main() { let marker = Marker; inner(); println!("must-not-run"); }
            """;
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "p1-control-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "program.dll");
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.CompileWithPanicStrategy(source,
                Path.Combine(directory, "program.rs"), output, SafeCorePanicStrategy.Abort,
                "P1AbortPolicy", cancellationToken: compileDeadline.Token);
            AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(output);
            AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
            AssertEx.True(imported.Document!.CallContracts.All(contract => contract.PanicStrategy == "abort"),
                "Generated package call contracts must retain the emitted abort policy.");
            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10), started =>
                    Console.WriteLine($"P1 abort process: pid={started.ProcessId} parent={started.ParentProcessId} started={started.StartedAt:O} command={started.CommandLine}")),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.Equal(BoundedProcessTermination.Exited, run.Termination);
            AssertEx.Equal(134, run.ExitCode!.Value);
            AssertEx.Equal("body\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            AssertEx.True(run.StandardError.Contains("RustSharp panic abort", StringComparison.Ordinal), run.StandardError);
            AssertEx.False(run.OutputTruncated || run.OutputReadTimedOut || run.ProcessTreeCleanupIncomplete,
                "Abort process evidence must be complete.");
        }
        finally { await CleanupAsync(directory, root).ConfigureAwait(false); }
    }

    private static async Task RunAsync(string source, string expected)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "p1-control-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "program.dll");
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(source, Path.Combine(directory, "program.rs"),
                output, "P1ControlFlowDrop", CompilationProfile.SafeCoreMirV2, compileDeadline.Token);
            AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10), started =>
                    Console.WriteLine($"P1 control Drop process: pid={started.ProcessId} parent={started.ParentProcessId} started={started.StartedAt:O} command={started.CommandLine}")),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, run.StandardError);
            AssertEx.False(run.OutputTruncated || run.OutputReadTimedOut || run.ProcessTreeCleanupIncomplete,
                "Generated control-flow evidence must be complete.");
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally { await CleanupAsync(directory, root).ConfigureAwait(false); }
    }

    private static async Task CleanupAsync(string directory, string root)
    {
        const string prefix = "p1-control-drop-";
        string path = Path.GetFullPath(directory);
        string name = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(path), root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _))
            throw new InvalidOperationException("Refusing to delete unowned control Drop files.");
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to delete redirected control Drop files.");
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!Directory.Exists(path)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }
        throw new IOException("Control Drop cleanup exceeded its five-second bound.");
    }
}
