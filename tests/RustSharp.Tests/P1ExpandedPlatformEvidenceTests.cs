using RustSharp.Compiler;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1ExpandedPlatformEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 platform output evidence rejects altered output and incomplete capture", RejectsInvalidOutputAsync),
        new("P1 platform placeholder IDs cannot close semantic coverage", RejectsPlaceholderClosureAsync),
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
        AssertEx.False(P1ExpandedPlatformRunner.IsSemanticClosureEligible("source-package"), "A label-printing source cannot establish imported calls.");
        AssertEx.False(P1ExpandedPlatformRunner.IsSemanticClosureEligible("panic-unwind-generated"), "A label-printing source cannot establish unwind behavior.");
        AssertEx.False(P1ExpandedPlatformRunner.IsSemanticClosureEligible("future-case"), "Unknown case identities cannot silently obtain closure eligibility.");
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
}
