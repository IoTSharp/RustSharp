using RustSharp.Compiler;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1ExpandedOwnershipEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 expanded ownership diagnostics accept exact semantic rejection", ExactDiagnosticAsync),
        new("P1 expanded ownership diagnostics reject unsupported lowering", UnsupportedDiagnosticAsync),
        new("P1 expanded ownership diagnostics reject incomplete process evidence", IncompleteProcessAsync),
        new("P1 expanded ownership evidence identifies every frozen placeholder", PlaceholderScopeAsync),
    ];

    private static Task ExactDiagnosticAsync()
    {
        AssertEx.True(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure("error RSO1002: conflicting borrow"), "RSO1002"), "The exact source ownership diagnostic is required.");
        AssertEx.True(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure("error[E0499]: two mutable borrows"), "E0499"), "The exact rustc diagnostic is required.");
        string source = Path.Combine(Environment.CurrentDirectory, "fixture.rs");
        AssertEx.True(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure(source + "[3..9]: error RSO1002: conflicting borrow"), "RSO1002"), "The CLI's rooted source-span error header must be accepted.");
        return Task.CompletedTask;
    }

    private static Task UnsupportedDiagnosticAsync()
    {
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure("error RSC0009: unsupported lowering"), "RSO1002"), "Unsupported executable syntax must not stand in for ownership rejection.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure("error RSO1001: moved value"), "RSO1002"), "A different ownership error cannot satisfy the fixed case.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure("error RSO1002: conflicting borrow\nerror RSC0009: unsupported lowering"), "RSO1002"), "Mixed unsupported and ownership diagnostics cannot satisfy the fixed semantic case.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure("error RSO10020: different code"), "RSO1002"), "A similar code must not match the fixed error identifier.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(Failure("warning: documentation mentions error RSO1002: conflicting borrow"), "RSO1002"), "A non-error mention cannot satisfy the fixed semantic case.");
        return Task.CompletedTask;
    }

    private static Task IncompleteProcessAsync()
    {
        BoundedProcessResult failure = Failure("error RSO1002: conflicting borrow");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(failure with { StandardErrorTruncated = true }, "RSO1002"), "Truncated evidence cannot satisfy a semantic rejection.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(failure with { ProcessTreeCleanupIncomplete = true }, "RSO1002"), "Unreclaimed process evidence cannot satisfy a semantic rejection.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesCompileFailure(failure with { Termination = BoundedProcessTermination.TimedOut }, "RSO1002"), "Timeout cannot stand in for a compiler diagnostic.");
        return Task.CompletedTask;
    }

    private static Task PlaceholderScopeAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        P1ExpandedSuiteValidator.ExpandedManifest manifest = P1ExpandedSuiteValidator.ParseManifest(File.ReadAllText(Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", P1ExpandedSuiteValidator.ManifestFileName)), root);
        P1ExpandedSuiteValidator.SuiteSpec suite = manifest.Suites.Single(static item => item.Profile == P1ExpandedDifferentialRunner.ProfileName);
        AssertEx.Equal(16, suite.Cases.Count(static item => P1ExpandedDifferentialRunner.IsPlaceholderCase(item.Id)));
        foreach (P1ExpandedSuiteValidator.CaseSpec fixture in suite.Cases)
        {
            string source = File.ReadAllText(Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", fixture.Source)).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            bool printlnOnly = source == $"// frozen P1 fixture: {fixture.Id}\nfn main() {{ println!(\"{fixture.Id}\"); }}";
            AssertEx.Equal(printlnOnly, P1ExpandedDifferentialRunner.IsPlaceholderCase(fixture.Id), $"The coverage classification for {fixture.Id} must describe its actual source.");
        }
        return Task.CompletedTask;
    }

    private static BoundedProcessResult Failure(string error) => new(
        new BoundedProcessStarted(123, Environment.ProcessId, DateTimeOffset.UtcNow, "fixture-compiler", [], Environment.CurrentDirectory),
        1, "", error, BoundedProcessTermination.Exited, TimeSpan.FromMilliseconds(1));
}
