using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class SafeCoreRegressionV2Tests
{
    private const string V1Manifest = "safe-core-regression-manifest.json";
    private const string V2Manifest = "safe-core-regression-v2-manifest.json";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Safe-core regression v2 preserves v1 and fixes a 24-case denominator", ManifestContractAsync),
        new("Safe-core regression v2 fixtures are bounded and source-addressable", FixtureContractAsync),
        new("Safe-core regression v2 preserves stable positive and negative contracts", CaseContractAsync),
        new("Safe-core regression v2 rejects manifest contract mutation", ManifestMutationAsync),
        new("Safe-core regression v2 process evidence rejects incomplete failures", EvidenceContractAsync),
    ];

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static SafeCoreRegressionV2ProfileRunner.Manifest ReadManifest() =>
        SafeCoreRegressionV2ProfileRunner.ParseManifest(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures", V2Manifest)));

    private static Task ManifestContractAsync()
    {
        string root = RepositoryRoot();
        SafeCoreRegressionProfileRunner.RegressionManifest v1 = SafeCoreRegressionProfileRunner.ParseManifest(File.ReadAllText(
            Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", V1Manifest)));
        SafeCoreRegressionV2ProfileRunner.Manifest v2 = ReadManifest();
        AssertEx.Equal(SafeCoreRegressionProfileRunner.ProfileName, v1.Profile);
        AssertEx.Equal(1, v1.Version);
        AssertEx.Equal(8, v1.Denominator);
        AssertEx.Equal(SafeCoreRegressionV2ProfileRunner.ProfileName, v2.Profile);
        AssertEx.Equal(SafeCoreRegressionV2ProfileRunner.ManifestVersion, v2.Version);
        AssertEx.Equal(SafeCoreRegressionV2ProfileRunner.Denominator, v2.Denominator);
        AssertEx.Equal(24, v2.Cases.Count);
        AssertEx.Equal("safe-core-mir-p1-v2", v2.CompilerProfile);
        AssertEx.True(v2.DeclaredLimits is not null, "V2 limits are mandatory.");
        AssertEx.Equal(24, v2.DeclaredLimits!.MaximumCases);
        AssertEx.Equal(30, v2.DeclaredLimits.CaseTimeoutSeconds);
        AssertEx.Equal(300, v2.DeclaredLimits.DeadlineSeconds);
        for (int index = 0; index < v1.Cases.Count; index++)
        {
            AssertEx.Equal(v1.Cases[index].Id, v2.Cases[index].Id, "V2 must preserve each v1 case ID.");
            AssertEx.Equal(v1.Cases[index].File, v2.Cases[index].File, "V2 must preserve each v1 source file.");
            AssertEx.Equal(v1.Cases[index].ExpectedOutput ?? string.Empty, v2.Cases[index].ExpectedOutput ?? string.Empty, "V2 must preserve each v1 output contract.");
        }
        return Task.CompletedTask;
    }

    private static Task FixtureContractAsync()
    {
        SafeCoreRegressionV2ProfileRunner.Manifest manifest = ReadManifest();
        string root = Path.Combine(RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures");
        foreach (SafeCoreRegressionV2ProfileRunner.Fixture fixture in manifest.Cases)
        {
            string path = Path.Combine(root, fixture.File);
            AssertEx.True(File.Exists(path), "Regression fixture is missing: " + fixture.File);
            AssertEx.True(new FileInfo(path).Length is > 0 and <= SafeCoreRegressionV2ProfileRunner.MaximumFixtureBytes,
                "Regression fixture exceeds its byte bound: " + fixture.File);
        }
        AssertEx.Equal(16, manifest.Cases.Skip(8).Count(static fixture => fixture.File.StartsWith("p1-regression-v2-", StringComparison.Ordinal)));
        AssertEx.Equal(13, manifest.Cases.Count(static fixture => fixture.Kind == "run-pass"));
        AssertEx.Equal(4, manifest.Cases.Count(static fixture => fixture.Kind == "differential"));
        return Task.CompletedTask;
    }

    private static Task CaseContractAsync()
    {
        SafeCoreRegressionV2ProfileRunner.Manifest manifest = ReadManifest();
        AssertEx.Equal(1, manifest.Cases.Count(static fixture => fixture.Kind == "compile-pass"));
        AssertEx.Equal(6, manifest.Cases.Count(static fixture => fixture.Kind == "compile-fail"));
        AssertEx.Equal(3, manifest.Cases.Count(static fixture => fixture.Expectation == "rustsharp-fail"));
        AssertEx.Equal(2, manifest.Cases.Count(static fixture => fixture.Area == "borrow"));
        AssertEx.Equal(2, manifest.Cases.Count(static fixture => fixture.Area == "drop"));
        foreach (SafeCoreRegressionV2ProfileRunner.Fixture fixture in manifest.Cases.Where(static fixture => fixture.Expectation == "rustsharp-fail"))
        {
            AssertEx.Equal("RSM2002", fixture.RustSharpDiagnostic ?? string.Empty,
                "Typed MIR unsupported boundaries must retain stable RSM2002 diagnostics.");
        }
        AssertEx.Equal("7\n", manifest.Cases.Single(static fixture => fixture.Id == "typed-mir-match-guard").ExpectedOutput ?? string.Empty);
        AssertEx.Equal("body\nsecond\nfirst\n", manifest.Cases.Single(static fixture => fixture.Id == "drop-reverse-locals").ExpectedOutput ?? string.Empty);
        return Task.CompletedTask;
    }

    private static Task ManifestMutationAsync()
    {
        SafeCoreRegressionV2ProfileRunner.Manifest manifest = ReadManifest();
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(manifest with { Denominator = 23 }));
        SafeCoreRegressionV2ProfileRunner.Fixture[] ids = [.. manifest.Cases];
        ids[8] = ids[8] with { Id = "replacement" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(manifest with { Cases = ids }));
        SafeCoreRegressionV2ProfileRunner.Fixture[] legacy = [.. manifest.Cases];
        legacy[0] = legacy[0] with { Id = "changed-v1-case" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(manifest with { Cases = legacy }));
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(manifest with { DeclaredLimits = manifest.DeclaredLimits! with { MaximumCases = 23 } }));
        return Task.CompletedTask;
    }

    private static Task EvidenceContractAsync()
    {
        SafeCoreRegressionV2ProfileRunner.ProcessEvidence good = SafeCoreRegressionV2ProfileRunner.ProcessEvidence.Empty with
        {
            ProcessId = 1,
            ParentProcessId = 2,
            Termination = "exited",
            ExitCode = 1,
            StandardError = "error RSO1002: active borrow conflict",
        };
        AssertEx.True(SafeCoreRegressionV2ProfileRunner.MatchesCompileFailure(good, "RSO1002"), "A clean diagnostic process should match the fixed borrow contract.");
        AssertEx.False(SafeCoreRegressionV2ProfileRunner.MatchesCompileFailure(good with { CleanupIncomplete = true }, "RSO1002"), "Incomplete cleanup must fail the evidence contract.");
        AssertEx.False(SafeCoreRegressionV2ProfileRunner.MatchesCompileFailure(good with { ProcessId = 0 }, "RSO1002"), "A missing PID must fail the evidence contract.");
        AssertEx.True(SafeCoreRegressionV2ProfileRunner.IsRequestedRustcVersion("rustc 1.98.0 (stable)"), "rustc 1.98.0 must be accepted.");
        AssertEx.False(SafeCoreRegressionV2ProfileRunner.IsRequestedRustcVersion("rustc 1.98.1 (stable)"), "rustc 1.98.1 must be rejected.");
        return Task.CompletedTask;
    }
}
