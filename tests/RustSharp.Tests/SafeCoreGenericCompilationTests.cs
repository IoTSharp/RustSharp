using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreGenericCompilationTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCoreGenerics;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generic CLR executes independent primitive and unit instances", PrimitiveInstancesAsync),
        new("generic CLR executes closed record tuple and tuple-struct values", AggregateInstancesAsync),
        new("generic CLR preserves source order and snapshots earlier aggregate arguments", EvaluationOrderAsync),
        new("generic CLR preserves early returns and closed recursive calls", ReturnsAndRecursionAsync),
        new("generic CLR emits deterministic closed metadata and source mapped PDB", DeterministicMetadataAsync),
        new("generic CLR rejects oversized closed signatures and layouts without artifacts", LayoutLimitsAsync),
        new("generic CLR preserves cancellation before emission", CancellationAsync),
    ];

    private static Task PrimitiveInstancesAsync() => RunAsync("""
        fn identity<T>(value: T) -> T { value }
        fn unit(value: ()) -> () { identity(value) }
        fn main() {
            println!("{}", identity::<i32>(42));
            println!("{}", identity::<bool>(true));
            unit(identity::<()>(()));
            println!("{}", identity(identity(9)));
        }
        """, "42\ntrue\n9\n");

    private static Task AggregateInstancesAsync() => RunAsync("""
        struct Box<T> { value: T }
        struct Pair<T, U>(T, U);
        struct Empty;
        fn identity<T>(value: T) -> T { value }
        fn swap<T, U>(pair: Pair<T, U>) -> Pair<U, T> { Pair(pair.1, pair.0) }
        fn wrap<T>(value: T) -> Box<T> { Box { value } }
        fn main() {
            let pair = swap(Pair::<i32, bool>(42, true));
            println!("{}", pair.1);
            println!("{}", pair.0);
            let nested = wrap(identity((7, (false, ()))));
            println!("{}", nested.value.0);
            println!("{}", nested.value.1.0);
            identity(nested.value.1.1);
            identity(Empty);
            let flag = identity(Box::<bool> { value: true });
            println!("{}", flag.value);
        }
        """, "42\ntrue\n7\nfalse\ntrue\n");

    private static Task EvaluationOrderAsync() => RunAsync("""
        struct Pair<T> { first: T, second: T }
        fn trace(value: i32) -> i32 { println!("{}", value); value }
        fn first<T>(left: T, right: T) -> T { left }
        fn main() {
            let pair = Pair { second: trace(2), first: trace(1) };
            println!("{}", pair.first);
            println!("{}", pair.second);
            let mut current = Pair { first: 3, second: 4 };
            let earlier = first(current, { current = Pair { first: 5, second: 6 }; current });
            println!("{}", earlier.first);
            println!("{}", current.first);
            let tuple = (trace(7), trace(8));
            println!("{}", tuple.0);
            println!("{}", tuple.1);
        }
        """, "2\n1\n1\n2\n3\n5\n7\n8\n7\n8\n");

    private static Task ReturnsAndRecursionAsync() => RunAsync("""
        struct Box<T>(T);
        fn first<T>(left: T, right: T) -> T { left }
        fn trace(value: i32) -> i32 { println!("{}", value); value }
        fn early() -> i32 { first(trace(1), { return 9; }) }
        fn repeat<T>(value: T, count: i32) -> T {
            if count == 0 { value } else { repeat(value, count - 1) }
        }
        fn choose<T>(flag: bool, value: T, other: T) -> T {
            if flag { return value; } else { return other; }
        }
        fn main() {
            println!("{}", early());
            let integer = repeat(Box(42), 3);
            let boolean = repeat(Box(true), 2);
            println!("{}", integer.0);
            println!("{}", boolean.0);
            println!("{}", choose(false, 1, 2));
        }
        """, "1\n9\n42\ntrue\n2\n");

    private static Task DeterministicMetadataAsync()
    {
        const string source = "struct Box<T> { value: T } fn identity<T>(value: T) -> T { value } " +
            "fn main() { println!(\"{}\", identity(Box { value: 42 }).value); println!(\"{}\", identity(Box { value: true }).value); }";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "generic-deterministic.rs", null, deadline.Token);
        SafeCoreGenericAnalysisResult analysis = SafeCoreGenericAnalysis.Check(syntax, cancellationToken: deadline.Token);
        AssertEx.True(analysis.IsSuccessful, string.Join("; ", analysis.Diagnostics));
        SafeCoreClrResult first = SafeCoreGenericClrLowering.Lower(analysis.Program!, deadline.Token);
        SafeCoreClrResult second = SafeCoreGenericClrLowering.Lower(analysis.Program!, deadline.Token);
        AssertEx.True(first.IsSuccessful && second.IsSuccessful, string.Join("; ", first.Diagnostics.Concat(second.Diagnostics)));
        AssertEx.Equal(3, first.Methods.Count, "The two concrete identity instances must emit distinct methods.");
        AssertEx.True(first.ValueTypes.Length >= 3, "The emitted program must include unit and both concrete Box layouts.");
        AssertEx.True(first.Methods.All(method => method.Validate(deadline.Token).IsValid), "Every closed method must pass CLR LIR validation.");
        string path = Path.GetFullPath("generic-deterministic.rs");
        GeneratedAssembly left = ClrLirAssemblyEmitter.EmitProgram(first, "GenericDeterministic", source, path,
            "GenericDeterministic.pdb", cancellationToken: deadline.Token);
        GeneratedAssembly right = ClrLirAssemblyEmitter.EmitProgram(second, "GenericDeterministic", source, path,
            "GenericDeterministic.pdb", cancellationToken: deadline.Token);
        AssertEx.True(left.PeImage.AsSpan().SequenceEqual(right.PeImage), "Closed generic PE emission must be deterministic.");
        AssertEx.True(left.PdbImage!.AsSpan().SequenceEqual(right.PdbImage!), "Closed generic PDB emission must be deterministic.");
        using var pe = new PEReader(new MemoryStream(left.PeImage));
        MetadataReader metadata = pe.GetMetadataReader();
        AssertEx.Equal(0, metadata.GetTableRowCount(TableIndex.GenericParam), "All emitted methods and value types must already be closed.");
        AssertEx.True(metadata.TypeDefinitions.Count >= first.ValueTypes.Length + 2, "Each closed layout must have its own metadata type definition.");
        ManifestResource resource = metadata.GetManifestResource(metadata.ManifestResources.Single());
        AssertEx.Equal("RustSharp.Generics.v1.json", metadata.GetString(resource.Name));
        PEMemoryBlock section = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
        BlobReader reader = section.GetReader((int)resource.Offset, section.Length - (int)resource.Offset);
        using JsonDocument evidence = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
        AssertEx.Equal(1, evidence.RootElement.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal(3, evidence.RootElement.GetProperty("instances").GetArrayLength());
        using MetadataReaderProvider pdb = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(left.PdbImage!));
        AssertEx.Equal(1, pdb.GetMetadataReader().Documents.Count);
        return Task.CompletedTask;
    }

    private static Task LayoutLimitsAsync()
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "generic-execution-limits-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(directory, "program.dll");
        const int oversizedCount = 257;
        string fields = string.Join(", ", Enumerable.Range(0, oversizedCount).Select(static index => $"field{index}: i32"));
        string parameters = string.Join(", ", Enumerable.Range(0, oversizedCount).Select(static index => $"arg{index}: i32"));
        string arguments = string.Join(", ", Enumerable.Repeat("0", oversizedCount));
        string oversizedType = "i32";
        for (int depth = 0; depth < 18; depth++) oversizedType = "Double<" + oversizedType + ">";
        string[] sources =
        [
            $"struct Large {{ {fields} }} fn make() -> Large {{ make() }} fn main() {{ make(); }}",
            $"fn many({parameters}) {{}} fn main() {{ many({arguments}); }}",
            $"struct Double<T> {{ first: T, second: T }} fn make<T>() -> T {{ make::<T>() }} fn main() {{ make::<{oversizedType}>(); }}",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (string source in sources)
        {
            CompilationResult check = CompilerDriver.Check(source, "generic-limits.rs", Profile, deadline.Token);
            AssertEx.True(check.Success, "The source must be valid before CLR layout limits are enforced: " + string.Join("; ", check.Diagnostics));
            CompilationResult compilation = CompilerDriver.Compile(source, "generic-limits.rs", output,
                profile: Profile, cancellationToken: deadline.Token);
            AssertEx.False(compilation.Success, "An oversized closed signature or value layout must be rejected.");
            AssertEx.Equal(SafeCoreGenericDiagnosticCodes.LimitReached, compilation.Diagnostics.Single().Code);
            AssertEx.True(compilation.Output is null, "A rejected layout must not expose executable artifacts.");
            AssertEx.False(Directory.Exists(directory), "Layout validation must finish before creating outputs or locks.");
        }
        return Task.CompletedTask;
    }

    private static Task CancellationAsync()
    {
        SafeCoreGenericAnalysisResult analysis = SafeCoreGenericAnalysis.Check(SafeCoreSyntax.Parse("fn main() {}"));
        AssertEx.True(analysis.IsSuccessful, string.Join("; ", analysis.Diagnostics));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreGenericClrLowering.Lower(analysis.Program!, cancelled.Token));
        return Task.CompletedTask;
    }

    private static async Task RunAsync(string source, string expected)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "generic-execution-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "program.dll");
            CompilationResult result = CompilerDriver.Compile(source, Path.Combine(directory, "program.rs"), output,
                profile: Profile, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [output], directory,
                TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete, "Generic execution must reclaim its process tree.");
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                Path.GetFileName(directory).StartsWith("generic-execution-", StringComparison.Ordinal), "Cleanup must remain within this owned test directory.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
