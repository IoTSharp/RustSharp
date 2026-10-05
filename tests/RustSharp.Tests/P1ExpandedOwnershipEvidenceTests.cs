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
        new("P1 expanded Drop runtime failures require matching traces", RuntimeFailureAsync),
        new("P1 expanded Drop corpus contains executable semantic sources", PlaceholderScopeAsync),
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

    private static Task RuntimeFailureAsync()
    {
        BoundedProcessResult failure = Failure(string.Empty) with
        {
            ExitCode = 101,
            StandardOutput = "body\ninner-drop\nouter-drop\n",
        };
        AssertEx.True(P1ExpandedDifferentialRunner.MatchesRuntimeFailure(failure, "body\ninner-drop\nouter-drop\n"),
            "A bounded nonzero runtime with the exact Drop trace must satisfy a runtime-failure case.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesRuntimeFailure(failure with { ExitCode = 0 }, "body\ninner-drop\nouter-drop\n"),
            "A zero exit cannot satisfy a panic differential.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesRuntimeFailure(failure with { StandardOutput = "body\nouter-drop\n" }, "body\ninner-drop\nouter-drop\n"),
            "A reordered or incomplete Drop trace cannot satisfy a panic differential.");
        AssertEx.False(P1ExpandedDifferentialRunner.MatchesRuntimeFailure(failure with { StandardOutputTruncated = true }, "body\ninner-drop\nouter-drop\n"),
            "Truncated runtime evidence cannot satisfy a panic differential.");
        return Task.CompletedTask;
    }

    private static Task PlaceholderScopeAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        P1ExpandedSuiteValidator.ExpandedManifest manifest = P1ExpandedSuiteValidator.ParseManifest(File.ReadAllText(Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", P1ExpandedSuiteValidator.ManifestFileName)), root);
        P1ExpandedSuiteValidator.SuiteSpec suite = manifest.Suites.Single(static item => item.Profile == P1ExpandedDifferentialRunner.ProfileName);
        AssertEx.Equal(0, suite.Cases.Count(static item => P1ExpandedDifferentialRunner.IsPlaceholderCase(item.Id)));
        AssertEx.True(suite.Cases.All(static item => !P1ExpandedDifferentialRunner.IsPlaceholderCase(item.Id)),
            "Every frozen differential case must carry an executable semantic scope.");
        string fixtureRoot = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures");
        string[] dropIds = [
            "drop-aggregate-fields", "drop-partial-move", "drop-assignment-replacement",
            "drop-temporary-scope", "drop-unwind-nested", "drop-double-panic",
        ];
        foreach (string id in dropIds)
        {
            P1ExpandedSuiteValidator.CaseSpec fixture = suite.Cases.Single(item => item.Id == id);
            string source = File.ReadAllText(Path.Combine(fixtureRoot, fixture.Source));
            AssertEx.True(source.Contains("impl Drop", StringComparison.Ordinal), $"{id} must declare an executable Drop body.");
            AssertEx.False(source.Contains($"println!(\"{id}\")", StringComparison.Ordinal), $"{id} must not be a label-only source.");
        }
        return Task.CompletedTask;
    }

    private static BoundedProcessResult Failure(string error) => new(
        new BoundedProcessStarted(123, Environment.ProcessId, DateTimeOffset.UtcNow, "fixture-compiler", [], Environment.CurrentDirectory),
        1, "", error, BoundedProcessTermination.Exited, TimeSpan.FromMilliseconds(1));
}
