using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirConstantExecutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("safe-core-mir constants scalar arithmetic and imports", () => RunAsync(
            "mod numbers { pub const BASE: i32 = 6; } use numbers::BASE as N; const FLAG: bool = N == 6; " +
            "const LETTER: i32 = '\\u{41}' as i32; fn main() { println!(\"{}\", N * 7); println!(\"{}\", FLAG); println!(\"{}\", LETTER); }", "42\ntrue\n65\n")),
        new("safe-core-mir constants tuples arrays and named aggregates", () => RunAsync(
            "struct Point { x: i32, y: i32 } const PAIR: (i32, bool) = (7, true); const VALUES: [i32; 3] = [2, 3, 5]; " +
            "const POINT: Point = Point { y: 11, x: 13 }; fn main() { println!(\"{}\", PAIR.0 + VALUES[2] + POINT.x); println!(\"{}\", POINT.y); }", "25\n11\n")),
        new("safe-core-mir constants independent aggregate materialization", () => RunAsync(
            "const VALUES: [i32; 2] = [3, 5]; fn main() { let mut first = VALUES; first[0] = 99; " +
            "let second = VALUES; println!(\"{}\", first[0]); println!(\"{}\", second[0]); }", "99\n3\n")),
        new("safe-core-mir constants inline const functions and bounded loops", () => RunAsync(
            "const fn sum(n: i32) -> i32 { let mut total = 0; let mut index = 0; while index < n { total += index; index += 1; } total } " +
            "const TOTAL: i32 = sum(6); fn main() { let value = const { let (x, y) = (2, 3); x * y }; println!(\"{}\", TOTAL + value); }", "21\n")),
        new("safe-core-mir constants retain const functions called at runtime", () => RunAsync(
            "const fn increment(value: i32) -> i32 { value + 1 } const VALUE: i32 = increment(3); " +
            "fn main() { let runtime = 7; println!(\"{}\", increment(runtime) + VALUE); }", "12\n")),
        new("safe-core-mir constants enum tuple and named payloads", () => RunAsync(
            "enum E { Empty, Pair(i32, i32), Named { value: i32 } } const PAIR: E = E::Pair(3, 5); const NAMED: E = E::Named { value: 7 }; " +
            "fn value(e: E) -> i32 { match e { E::Empty => 0, E::Pair(a, b) => a + b, E::Named { value } => value } } " +
            "fn main() { println!(\"{}\", value(PAIR)); println!(\"{}\", value(NAMED)); }", "8\n7\n")),
        new("safe-core-mir promotion survives return and call boundaries", () => RunAsync(
            "fn promoted(input: &i32) -> &i32 { &42 } fn identity(input: &i32) -> &i32 { input } " +
            "fn main() { let owner = 1; let first = promoted(&owner); let second = identity(&7); " +
            "println!(\"{}\", *first); println!(\"{}\", *second); }", "42\n7\n")),
        new("safe-core-mir promotion nested references and aggregate storage", () => RunAsync(
            "const PAIR: (&i32, &i32) = (&3, &5); fn promoted(input: &i32) -> &&i32 { &&11 } " +
            "fn main() { let owner = 1; let nested = promoted(&owner); let pair = PAIR; println!(\"{}\", **nested); " +
            "println!(\"{}\", *pair.0 + *pair.1); }", "11\n8\n")),
        new("safe-core-mir promotion constant array to slice", () => RunAsync(
            "const VALUES: &[i32] = &[3, 5, 7]; fn read(values: &[i32]) -> i32 { values[1] } " +
            "fn main() { println!(\"{}\", read(VALUES)); println!(\"{}\", VALUES.len()); }", "5\n3\n")),
        new("safe-core-mir promotion checked evidence is deterministic", EvidenceAsync),
        new("safe-core-mir promotion ownership evidence rejects forged lifetime", ForgedPromotionEvidenceAsync),
        new("safe-core-mir constants reject checked overflow", () => RejectAsync(
            "const BAD: i8 = 127 + 1; fn main() { println!(\"{}\", BAD); }", "RST2006")),
        new("safe-core-mir constants reject cyclic dependencies", () => RejectAsync(
            "const A: i32 = B; const B: i32 = A; fn main() {}", "RST2008")),
        new("safe-core-mir constants reject interpreter budget exhaustion", () => RejectAsync(
            "const BAD: i32 = loop {}; fn main() {}", "RST0002")),
        new("safe-core-mir constants reject borrowed local storage", () => RejectAsync(
            "const BAD: &i32 = { let local = 3; &local }; fn main() {}", "RST2010")),
        new("safe-core-mir constants reject mutable promotion", () => RejectAsync(
            "const BAD: &mut i32 = &mut 3; fn main() {}", "RST2010")),
    ];

    private static Task EvidenceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string source = "const VALUES: (i32, i32) = (3, 5); fn main() { let reference = &(VALUES.0 + VALUES.1); println!(\"{}\", *reference); }";
        SafeCoreMirPipelineOptions options = new()
        {
            EnableP1Extensions = true, RequireOwnershipEvidence = true,
            Timeout = TimeSpan.FromSeconds(4), CancellationToken = deadline.Token,
        };
        SafeCoreMirPipelineResult first = SafeCoreMirPipeline.Analyze(source, "promotion-evidence.rs", options);
        SafeCoreMirPipelineResult second = SafeCoreMirPipeline.Analyze(source, "promotion-evidence.rs", options);
        AssertEx.True(first.IsSuccessful, Format(first.Diagnostics));
        AssertEx.True(second.IsSuccessful, Format(second.Diagnostics));
        AssertEx.Equal(first.MirSnapshot!, second.MirSnapshot!);
        AssertEx.True(first.MirSnapshot!.Contains("PromotedBorrow", StringComparison.OrdinalIgnoreCase),
            "MIR must retain explicit promotion evidence instead of an escaping stack borrow.");
        return Task.CompletedTask;
    }

    private static Task ForgedPromotionEvidenceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreMirPipelineResult analyzed = SafeCoreMirPipeline.Analyze(
            "fn main() { let references = (&3, &5); println!(\"{}\", *references.0); }", "promotion-proof.rs",
            new() { EnableP1Extensions = true, RequireOwnershipEvidence = true,
                Timeout = TimeSpan.FromSeconds(4), CancellationToken = deadline.Token });
        AssertEx.True(analyzed.IsSuccessful, Format(analyzed.Diagnostics));
        SafeCoreOwnershipProgram proof = analyzed.Ownership!.Program!;
        SafeCoreMirOwnershipOptions options = new() { Timeout = TimeSpan.FromSeconds(3), CancellationToken = deadline.Token, InferNonLexicalLifetimes = true };
        SafeCoreMirOwnershipResult roundtrip = SafeCoreMirOwnershipAdapter.Analyze(analyzed.Mir!.Program!, proof, options);
        AssertEx.True(roundtrip.IsSuccessful, Format(roundtrip.Diagnostics));
        SafeCoreOwnershipFunction function = proof.Functions.Single();
        SafeCoreOwnershipBlock block = function.Blocks.Single();
        AssertEx.True(block.Instructions.Any(instruction => instruction.IsStaticBorrow), "The fixture must contain a promoted loan.");
        var forged = new SafeCoreOwnershipProgram([new SafeCoreOwnershipFunction(function.Name, function.Locals, function.Scopes,
            [new SafeCoreOwnershipBlock(block.Id, block.ScopeId,
                block.Instructions.Select(instruction => instruction with { IsStaticBorrow = false }).ToArray(),
                block.Terminator, block.Source)], function.EntryBlockId, function.PanicStrategy, function.Source)]);
        SafeCoreMirOwnershipResult rejected = SafeCoreMirOwnershipAdapter.Analyze(analyzed.Mir.Program!, forged, options);
        AssertEx.False(rejected.IsSuccessful, "Explicit evidence cannot alter checked constant storage lifetimes.");
        AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch), Format(rejected.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task RunAsync(string source, string expected) => WithWorkspaceAsync(async (directory, token) =>
    {
        string output = Path.Combine(directory, "constant.dll");
        CompilationResult result = CompilerDriver.Compile(source, "constant-runtime.rs", output,
            assemblyName: "ConstantRuntime", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.True(result.Success, Format(result.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });

    private static Task RejectAsync(string source, string code) => WithWorkspaceAsync((directory, token) =>
    {
        string output = Path.Combine(directory, "rejected.dll");
        CompilationResult result = CompilerDriver.Compile(source, "constant-rejected.rs", output,
            assemblyName: "ConstantRejected", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.False(result.Success, "Invalid constants must fail before emission.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == code), Format(result.Diagnostics));
        AssertEx.False(File.Exists(output), "Rejected constants must not publish an assembly.");
        return Task.CompletedTask;
    });

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ",
        diagnostics.Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Message));

    private static async Task WithWorkspaceAsync(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RustSharp.Tests"));
        string directory = Path.Combine(root, "mir-constants-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Constant test cleanup may delete only its own workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
