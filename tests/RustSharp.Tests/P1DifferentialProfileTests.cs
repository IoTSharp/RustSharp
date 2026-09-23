using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1DifferentialProfileTests
{
    private const string ManifestName = "p1-differential-manifest.json";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 differential manifest fixes borrow and Drop denominators", ManifestContractAsync),
        new("P1 differential v2 preserves v1 and fixes sixteen source cases", V2ManifestContractAsync),
        new("P1 differential compile-fail rejects unsupported timeout and incomplete evidence", FailureEvidenceAsync),
        new("P1 differential oracle requires exact stable rustc 1.98.0", OracleVersionAsync),
        new("P1 differential v2 rejects lowered coverage and diagnostic substitutions", V2NegativeContractsAsync),
        new("P1 differential manifests enforce byte case and execution budgets", ManifestBudgetsAsync),
    ];

    private static Task ManifestContractAsync()
    {
        string path = Path.Combine(
            RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures", ManifestName);
        P1DifferentialProfileRunner.Manifest manifest =
            P1DifferentialProfileRunner.ParseManifest(File.ReadAllText(path));
        AssertEx.Equal(P1DifferentialProfileRunner.ProfileName, manifest.Profile);
        AssertEx.Equal(P1DifferentialProfileRunner.ManifestVersion, manifest.Version);
        AssertEx.Equal(P1DifferentialProfileRunner.RustVersion, manifest.RustVersion);
        AssertEx.Equal(P1DifferentialProfileRunner.CompilerProfile, manifest.CompilerProfile);
        AssertEx.Equal(4, manifest.Denominator);
        AssertEx.Equal(4, manifest.Cases.Count);
        AssertEx.Equal(2, manifest.BorrowCount);
        AssertEx.Equal(2, manifest.DropCount);
        AssertEx.True(manifest.DeclaredLimits is not null, "P1 differential manifest must declare bounded execution limits.");
        AssertEx.Equal(300, manifest.DeclaredLimits!.CaseTimeoutSeconds);
        AssertEx.Equal(900, manifest.DeclaredLimits.DeadlineSeconds);
        AssertEx.True(manifest.Cases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() == 4,
            "P1 differential case IDs must be unique.");
        AssertEx.Throws<ArgumentException>(() =>
            P1DifferentialProfileRunner.ValidateManifest(manifest with { Denominator = 3 }));
        AssertEx.Throws<ArgumentException>(() =>
            P1DifferentialProfileRunner.ValidateManifest(manifest with
            {
                Cases = [.. manifest.Cases, new("invalid-kind", "p1-invalid.rs", "other", "")],
                Denominator = 5,
            }));
        return Task.CompletedTask;
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static P1DifferentialProfileRunner.Manifest ReadV2() => P1DifferentialProfileRunner.ParseManifest(
        File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures", P1DifferentialProfileRunner.ManifestV2FileName)));

    private static Task V2ManifestContractAsync()
    {
        P1DifferentialProfileRunner.Manifest v1 = P1DifferentialProfileRunner.ParseManifest(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures", ManifestName)));
        P1DifferentialProfileRunner.Manifest v2 = ReadV2();
        AssertEx.Equal("p1-differential-v2", v2.Profile);
        AssertEx.Equal(2, v2.Version);
        AssertEx.Equal(16, v2.Denominator);
        AssertEx.Equal(10, v2.BorrowCount);
        AssertEx.Equal(6, v2.DropCount);
        AssertEx.Equal(4, v2.Cases.Count(static item => item.Expectation == "compile-fail"));
        AssertEx.Equal(12, v2.Cases.Count(static item => item.Expectation == "run-pass"));
        AssertEx.True(v1.Cases.SequenceEqual(v2.Cases.Take(4)), "V2 must preserve all original V1 cases and expectations.");
        foreach (P1DifferentialProfileRunner.Fixture fixture in v2.Cases)
        {
            string path = Path.Combine(RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures", fixture.File);
            AssertEx.True(File.Exists(path), "Every fixed differential source must exist: " + fixture.File);
            AssertEx.True(new FileInfo(path).Length is > 0 and <= P1DifferentialProfileRunner.MaximumFixtureBytes,
                "Every fixed differential source must fit the execution byte limit.");
        }
        return Task.CompletedTask;
    }

    private static Task FailureEvidenceAsync()
    {
        P1DifferentialProfileRunner.ProcessEvidence good = P1DifferentialProfileRunner.ProcessEvidence.Empty with
        {
            ProcessId = 1, ExitCode = 1, Termination = "exited", StandardError = "test.rs[1..3]: error RSO1002: active borrow conflict\n",
        };
        AssertEx.True(P1DifferentialProfileRunner.MatchesCompileFailure(good, "RSO1002"), "Exact compiler ownership diagnostics must match.");
        AssertEx.False(P1DifferentialProfileRunner.MatchesCompileFailure(good with { StandardError = "error RSM2002: unsupported" }, "RSO1002"), "Unsupported syntax cannot prove borrow soundness.");
        AssertEx.False(P1DifferentialProfileRunner.MatchesCompileFailure(good with { StandardError = "note: RSO1002" }, "RSO1002"), "Mentioning a diagnostic without an error cannot pass.");
        AssertEx.False(P1DifferentialProfileRunner.MatchesCompileFailure(good with { ExitCode = 0 }, "RSO1002"), "An accepted source cannot pass a rejection case.");
        AssertEx.False(P1DifferentialProfileRunner.MatchesCompileFailure(good with { Termination = "timedout" }, "RSO1002"), "Timeout cannot pass a rejection case.");
        AssertEx.False(P1DifferentialProfileRunner.MatchesCompileFailure(good with { OutputTruncated = true }, "RSO1002"), "Truncated evidence cannot pass.");
        AssertEx.False(P1DifferentialProfileRunner.MatchesCompileFailure(good with { CleanupIncomplete = true }, "RSO1002"), "Incomplete cleanup cannot pass.");
        AssertEx.False(P1DifferentialProfileRunner.MatchesCompileFailure(good with { ProcessId = 0 }, "RSO1002"), "Unstarted processes cannot pass.");
        AssertEx.True(P1DifferentialProfileRunner.MatchesCompileFailure(good with { StandardError = "error[E0499]: cannot borrow value twice" }, "E0499"), "Rustc structured error codes must match.");
        return Task.CompletedTask;
    }

    private static Task OracleVersionAsync()
    {
        AssertEx.True(P1DifferentialProfileRunner.IsRequestedRustcVersion("rustc 1.98.0 (88d9e12ae 2026-08-18)"), "Requested stable oracle must match.");
        AssertEx.False(P1DifferentialProfileRunner.IsRequestedRustcVersion("rustc 1.98.1 (hash date)"), "Another patch release cannot silently replace the fixed oracle.");
        AssertEx.False(P1DifferentialProfileRunner.IsRequestedRustcVersion("rustc 1.98.0-nightly (hash date)"), "Nightly cannot replace the stable oracle.");
        AssertEx.False(P1DifferentialProfileRunner.IsRequestedRustcVersion("rustc 1.98.0-dev (hash date)"), "Development rustc cannot replace the stable oracle.");
        AssertEx.False(P1DifferentialProfileRunner.IsRequestedRustcVersion(null), "Missing version cannot prove oracle identity.");
        return Task.CompletedTask;
    }

    private static Task V2NegativeContractsAsync()
    {
        P1DifferentialProfileRunner.Manifest manifest = ReadV2();
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with
        {
            Cases = manifest.Cases.Take(15).ToArray(), Denominator = 15,
        }));
        P1DifferentialProfileRunner.Fixture[] renamed = [.. manifest.Cases];
        renamed[4] = renamed[4] with { Id = "replacement" };
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with { Cases = renamed }));
        P1DifferentialProfileRunner.Fixture[] unsupported = [.. manifest.Cases];
        unsupported[8] = unsupported[8] with { RustSharpDiagnostic = "RSM2002" };
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with { Cases = unsupported }));
        P1DifferentialProfileRunner.Fixture[] outputs = [.. manifest.Cases];
        outputs[8] = outputs[8] with { ExpectedOutput = "not a run case" };
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with { Cases = outputs }));
        return Task.CompletedTask;
    }

    private static Task ManifestBudgetsAsync()
    {
        P1DifferentialProfileRunner.Manifest manifest = ReadV2();
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ParseManifest(new string('x', P1DifferentialProfileRunner.MaximumManifestBytes + 1)));
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with { DeclaredLimits = null }));
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with
        {
            DeclaredLimits = manifest.DeclaredLimits! with { CaseTimeoutSeconds = 301 },
        }));
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with
        {
            DeclaredLimits = manifest.DeclaredLimits! with { MaximumCases = 15 },
        }));
        P1DifferentialProfileRunner.Fixture[] oversized = [.. manifest.Cases];
        oversized[0] = oversized[0] with { ExpectedOutput = new string('x', P1DifferentialProfileRunner.MaximumFixtureBytes + 1) };
        AssertEx.Throws<ArgumentException>(() => P1DifferentialProfileRunner.ValidateManifest(manifest with { Cases = oversized }));
        return Task.CompletedTask;
    }
}
