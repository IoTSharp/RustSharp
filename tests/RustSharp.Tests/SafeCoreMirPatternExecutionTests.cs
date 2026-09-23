using RustSharp.Semantics;
using RustSharp.Syntax;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class SafeCoreMirPatternExecutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR v2 lowers scalar tuple match and guard through CFG", MatchAsync),
        new("MIR v2 expands a captured closure call into typed MIR", ClosureAsync),
        new("MIR v2 rejects closure escape at its source span", ClosureEscapeAsync),
        new("MIR v2 rejects mutable closure captures at their source span", MutableCaptureAsync),
        new("MIR v2 rejects binding or-patterns at their source span", BindingOrPatternAsync),
        new("MIR v2 bounds pattern alternatives", PatternBudgetAsync),
        new("MIR v2 executes match and closure through CoreCLR", CoreClrAsync),
    ];

    private static Task MatchAsync()
    {
        const string source = "fn choose(value: (bool, i32)) -> i32 { match value { (true, x) if x > 0 => x, (true, x) => x + 1, (false, x) => x - 1 } }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-match.rs");
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics) + " | mir=" + Format(result.Mir?.Diagnostics ?? []));
        AssertEx.True(result.MirSnapshot!.Contains("branch", StringComparison.Ordinal), "Match must lower to explicit MIR branches.");
        AssertEx.True(result.MirSnapshot.Contains("field", StringComparison.Ordinal), "Tuple pattern bindings must lower through field projections.");
        AssertEx.True(result.MirSnapshot.Contains("mir-v2-match.rs:", StringComparison.Ordinal), "Match MIR must preserve source mapping.");
        SafeCoreMirPipelineResult alternatives = Analyze(
            "fn either(value: bool) -> i32 { match value { true | false => 1 } }",
            "mir-v2-or.rs");
        AssertEx.True(alternatives.IsSuccessful, Format(alternatives.Diagnostics));
        AssertEx.True(alternatives.MirSnapshot!.Contains("branch", StringComparison.Ordinal),
            "Or-patterns must lower through bounded CFG branches.");
        return Task.CompletedTask;
    }

    private static Task ClosureAsync()
    {
        const string source = "fn apply() -> i32 { let add = |value: i32| value + 1; add(2) }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-closure.rs");
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics) + " | mir=" + Format(result.Mir?.Diagnostics ?? []));
        AssertEx.True(result.MirSnapshot!.Contains("binary +", StringComparison.Ordinal), "Closure body must be emitted into MIR.");
        AssertEx.False(result.MirSnapshot.Contains("closure#", StringComparison.OrdinalIgnoreCase), "Closure calls must not use an interpreter marker.");
        return Task.CompletedTask;
    }

    private static Task ClosureEscapeAsync()
    {
        const string source = "fn invalid() -> i32 { let add = |value: i32| value + 1; let escaped = add; 0 }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-closure-escape.rs");
        AssertEx.False(result.IsSuccessful, "Escaping closure values are outside the static expansion contract.");
        AssertEx.Equal(SafeCoreMirLowering.UnsupportedSyntax, result.Diagnostics.Single().Code);
        AssertEx.True(result.Diagnostics.Single().Span.Start >= 0, "Closure escape diagnostic must retain a source span.");
        return Task.CompletedTask;
    }

    private static Task MutableCaptureAsync()
    {
        const string source = "fn apply() -> i32 { let mut base = 2; let add = |value: i32| value + base; add(1) }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-mutable-capture.rs");
        AssertEx.False(result.IsSuccessful, "Mutable closure captures require place-aware write-back semantics.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirLowering.UnsupportedSyntax),
            Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task BindingOrPatternAsync()
    {
        const string source = "fn choose(value: (i32, i32)) -> i32 { match value { (x, 0) | (0, x) => x, _ => -1 } }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-binding-or.rs");
        AssertEx.False(result.IsSuccessful, "Binding or-patterns require binding all alternatives consistently.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirLowering.UnsupportedSyntax),
            Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task PatternBudgetAsync()
    {
        const string source = "fn choose(value: bool) -> i32 { match value { true => 1, false => 0 } }";
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(source, "mir-v2-pattern-budget.rs", new()
        {
            EnableP1Extensions = true,
            MaximumPatternAlternatives = 1,
            Timeout = TimeSpan.FromSeconds(5),
        });
        AssertEx.False(result.IsSuccessful, "The fixed pattern alternative budget must be enforced.");
        AssertEx.Equal(SafeCoreMirLowering.LimitReached, result.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task CoreClrAsync() => WithWorkspace(async (directory, token) =>
    {
        const string source = "fn choose(value: bool) -> i32 { match value { true => 1, false => 0 } } fn apply() -> i32 { let base = 2; let add = |value: i32| value + base; add(2) } fn main() { println!(\"{}\", choose(true)); println!(\"{}\", apply()); }";
        string output = Path.Combine(directory, "program.dll");
        CompilationResult compiled = CompilerDriver.Compile(source, "mir-v2-execution.rs", output,
            assemblyName: "MirV2Execution", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.True(compiled.Success, Format(compiled.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded, run.StandardError);
        AssertEx.Equal("1\n4\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });

    private static SafeCoreMirPipelineResult Analyze(string source, string path) =>
        SafeCoreMirPipeline.Analyze(source, path, new()
        {
            EnableP1Extensions = true,
            EnableRepeatedArrays = true,
            RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5),
        });

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static d => $"{d.Code}@{d.Span.Start}+{d.Span.Length}: {d.Message}"));

    private static async Task WithWorkspace(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "mir-v2-pattern-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Cleanup must target only this pattern test's owned workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
