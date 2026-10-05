using RustSharp.Compiler;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1ExpandedPlatformEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 platform output evidence rejects altered output and incomplete capture", RejectsInvalidOutputAsync),
        new("P1 platform placeholder IDs cannot close semantic coverage", RejectsPlaceholderClosureAsync),
        new("P1 platform expanded corpus contains semantic sources for all additions", ExpandedCorpusSourcesAsync),
        new("P1 platform Native AOT uses emitted PE identity instead of output file name", PreservesAssemblyIdentityAsync),
    ];

    private static Task RejectsInvalidOutputAsync()
    {
        var started = new BoundedProcessStarted(1, 2, DateTimeOffset.UtcNow, "fixture", [], ".");
        var result = new BoundedProcessResult(started, 0, "body\r\ndrop\r\n", "", BoundedProcessTermination.Exited, TimeSpan.Zero);
        AssertEx.True(P1ExpandedPlatformRunner.OutputMatches(result, "body\ndrop\n"), "Only platform newline normalization is allowed.");
        AssertEx.False(P1ExpandedPlatformRunner.OutputMatches(result, "drop\nbody\n"), "Reordered destructor output must reject even with exit code zero.");
        AssertEx.False(P1ExpandedPlatformRunner.OutputMatches(result with { StandardOutputTruncated = true }, "body\ndrop\n"), "A matching prefix with truncated output is incomplete evidence.");
        AssertEx.False(P1ExpandedPlatformRunner.OutputMatches(result with { ProcessTreeCleanupIncomplete = true }, "body\ndrop\n"), "A surviving process tree must reject output evidence.");
        AssertEx.False(P1ExpandedPlatformRunner.OutputMatches(result with { ExitCode = 1 }, "body\ndrop\n"), "A failed process must reject output evidence.");
        return Task.CompletedTask;
    }

    private static Task RejectsPlaceholderClosureAsync()
    {
        AssertEx.True(P1ExpandedPlatformRunner.IsSemanticClosureEligible("drop-return-order"), "The bounded executable destructor fixture retains its own scope.");
        AssertEx.True(P1ExpandedPlatformRunner.IsSemanticClosureEligible("source-package"), "The source-package producer/consumer contract is executable.");
        AssertEx.True(P1ExpandedPlatformRunner.IsSemanticClosureEligible("panic-unwind-generated"), "The generated nested cleanup source carries real Drop behavior.");
        AssertEx.True(P1ExpandedPlatformRunner.IsSemanticClosureEligible("aggregate-struct-drop"), "The aggregate source carries field and owner Drop behavior.");
        AssertEx.True(P1ExpandedPlatformRunner.IsSemanticClosureEligible("aggregate-enum-drop"), "The enum source carries a real variant selection.");
        AssertEx.False(P1ExpandedPlatformRunner.IsSemanticClosureEligible("future-case"), "Unknown case identities cannot silently obtain closure eligibility.");
        return Task.CompletedTask;
    }

    private static Task ExpandedCorpusSourcesAsync()
    {
        string root = RepositoryRoot();
        string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", P1ExpandedSuiteValidator.ManifestFileName);
        P1ExpandedSuiteValidator.ExpandedManifest manifest = P1ExpandedSuiteValidator.ParseManifest(File.ReadAllText(manifestPath), root);
        P1ExpandedSuiteValidator.SuiteSpec suite = manifest.Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.PlatformProfile);
        string fixtureRoot = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures");
        string[] expandedIds =
        [
            "aggregate-struct-drop", "aggregate-enum-drop", "slice-unsize", "pattern-capture",
            "panic-unwind-generated", "panic-abort-generated", "generic-import-call", "byref-import-call",
            "metadata-contract", "mir-projection", "mir-family", "source-package",
        ];
        foreach (string id in expandedIds)
        {
            P1ExpandedSuiteValidator.CaseSpec fixture = suite.Cases.Single(item => item.Id == id);
            string source = File.ReadAllText(Path.Combine(fixtureRoot, fixture.Source));
            AssertEx.True(P1ExpandedPlatformRunner.IsSemanticClosureEligible(id), $"{id} must be eligible for semantic closure.");
            AssertEx.False(source.Contains($"println!(\"{id}\")", StringComparison.Ordinal), $"{id} must not be a label-only source.");
            AssertEx.True(source.Trim().Split('\n').Length > 2, $"{id} must carry a real multi-statement semantic fixture.");
        }
        return Task.CompletedTask;
    }

    private static Task PreservesAssemblyIdentityAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "p1-platform-identity-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(directory, "different-output.dll");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Directory.CreateDirectory(directory);
        try
        {
            CompilationResult result = CompilerDriver.Compile("fn main() { println!(\"identity\"); }", "frozen-source.rs", output, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, "The bounded source must emit a PE for the assembly identity regression.");
            string assemblyName = P1ExpandedPlatformRunner.ReadGeneratedAssemblyName(output);
            AssertEx.Equal("different-output", assemblyName);
            AssertEx.False(string.Equals("frozen-source", assemblyName, StringComparison.Ordinal), "The runner must use the emitted PE identity rather than the source label.");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
