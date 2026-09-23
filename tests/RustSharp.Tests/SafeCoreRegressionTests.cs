using System.Text.Json;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class SafeCoreRegressionTests
{
    private const string ManifestName = "safe-core-regression-manifest.json";
    private const int Denominator = 8;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("safe-core regression manifest covers all four outcome kinds", VerifiesManifestAsync),
        new("safe-core run-pass cases require an exact rustc oracle comparison", VerifiesRunPassOracleContractAsync),
        new("safe-core regression manifest rejects unbounded or incomplete contracts", RejectsMalformedManifestAsync),
    ];

    private static Task VerifiesRunPassOracleContractAsync()
    {
        AssertEx.True(
            SafeCoreRegressionProfileRunner.RequiresOracle("run-pass"),
            "run-pass cases must be skipped when the pinned rustc oracle is unavailable.");
        AssertEx.True(
            SafeCoreRegressionProfileRunner.RunPassOutputsMatch(
                "42\r\n",
                "",
                "42\n",
                "",
                "42\r\n"),
            "Run-pass output comparison must normalize line endings while preserving exact output.");
        AssertEx.False(
            SafeCoreRegressionProfileRunner.RunPassOutputsMatch(
                "42\n",
                "",
                "41\n",
                "",
                "42\n"),
            "RustSharp output must match the rustc oracle output.");
        AssertEx.False(
            SafeCoreRegressionProfileRunner.RunPassOutputsMatch(
                "42\n",
                "warning\n",
                "42\n",
                "",
                "42\n"),
            "Run-pass stderr must remain empty on the RustSharp side.");
        AssertEx.False(
            SafeCoreRegressionProfileRunner.RunPassOutputsMatch(
                "42\n",
                "",
                "42\n",
                "warning\n",
                "42\n"),
            "Run-pass stderr must remain empty on the rustc side.");
        AssertEx.False(
            SafeCoreRegressionProfileRunner.RunPassOutputsMatch(
                "",
                "",
                "",
                "",
                null),
            "A run-pass comparison requires the manifest expected output contract.");
        return Task.CompletedTask;
    }

    private static Task VerifiesManifestAsync()
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string manifestPath = Path.Combine(
            repositoryRoot,
            "tools",
            "RustSharp.Conformance",
            "fixtures",
            ManifestName);
        SafeCoreRegressionProfileRunner.RegressionManifest manifest =
            SafeCoreRegressionProfileRunner.ParseManifest(File.ReadAllText(manifestPath));

        AssertEx.Equal(SafeCoreRegressionProfileRunner.ProfileName, manifest.Profile);
        AssertEx.Equal(SafeCoreRegressionProfileRunner.ManifestVersion, manifest.Version);
        AssertEx.Equal(Denominator, manifest.Denominator);
        AssertEx.Equal(Denominator, manifest.Cases.Count);
        AssertEx.Equal(64, AssertEx.NotNull(manifest.DeclaredLimits, "Manifest limits must be declared.").MaximumCases);
        AssertEx.Equal(1, AssertEx.NotNull(manifest.DeclaredCoverage, "Manifest coverage must be declared.")["compile-pass"]);
        IReadOnlyDictionary<string, int> counts =
            SafeCoreRegressionProfileRunner.CountKinds(manifest.Cases);
        AssertEx.Equal(1, counts["compile-pass"]);
        AssertEx.Equal(2, counts["compile-fail"]);
        AssertEx.Equal(2, counts["run-pass"]);
        AssertEx.Equal(3, counts["differential"]);
        AssertEx.Equal(
            "immutable",
            AssertEx.NotNull(
                manifest.Cases.Single(fixture => fixture.Id == "compile-fail-immutable").DiagnosticContains,
                "The immutable compile-fail case must declare a diagnostic contract."));
        AssertEx.True(
            SafeCoreRegressionProfileRunner.DiagnosticMatches("", "error: immutable binding", "immutable"),
            "An optional compile-fail diagnostic contract must match bounded process output.");
        AssertEx.False(
            SafeCoreRegressionProfileRunner.DiagnosticMatches("", "error: type mismatch", "immutable"),
            "A compile-fail diagnostic contract must reject unrelated output.");
        var effectiveLimits = SafeCoreRegressionProfileRunner.ApplyDeclaredLimits(
            manifest with
            {
                DeclaredLimits = new SafeCoreRegressionProfileRunner.RegressionManifestLimits(
                    64, 4 * 1024 * 1024, 1 * 1024 * 1024, 5, 7),
            },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(180));
        AssertEx.Equal(TimeSpan.FromSeconds(5), effectiveLimits.Timeout,
            "Manifest case timeout must cap the requested timeout.");
        AssertEx.Equal(TimeSpan.FromSeconds(7), effectiveLimits.Deadline,
            "Manifest deadline must cap the requested deadline.");
        AssertEx.Equal(
            manifest.Cases.Count,
            manifest.Cases.Select(static fixture => fixture.Id)
                .Distinct(StringComparer.Ordinal)
                .Count());
        return Task.CompletedTask;
    }

    private static Task RejectsMalformedManifestAsync()
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string manifestPath = Path.Combine(
            repositoryRoot,
            "tools",
            "RustSharp.Conformance",
            "fixtures",
            ManifestName);
        string manifestJson = File.ReadAllText(manifestPath);
        string duplicatePropertyJson = manifestJson.Replace(
            "  \"profile\": \"safe-core-regression-v1\",",
            "  \"profile\": \"safe-core-regression-v1\",\n  \"profile\": \"safe-core-regression-v1\",",
            StringComparison.Ordinal);
        AssertEx.Throws<JsonException>(() => SafeCoreRegressionProfileRunner.ParseManifest(duplicatePropertyJson));

        SafeCoreRegressionProfileRunner.RegressionManifest valid =
            new(
                SafeCoreRegressionProfileRunner.ProfileName,
                SafeCoreRegressionProfileRunner.ManifestVersion,
                SafeCoreRegressionProfileRunner.RustVersion,
                SafeCoreRegressionProfileRunner.Edition,
                SafeCoreRegressionProfileRunner.CompilerProfile,
                4,
                [
                    new("compile-pass", "a.rs", "compile-pass", null),
                    new("compile-fail", "b.rs", "compile-fail", null),
                    new("run-pass", "c.rs", "run-pass", "ok\n"),
                    new("differential", "d.rs", "differential", "ok\n"),
                ]);
        SafeCoreRegressionProfileRunner.ValidateManifest(valid);

        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with { Denominator = 3 }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    Cases =
                    [
                        .. valid.Cases,
                        new("compile-pass", "e.rs", "compile-pass", null),
                    ],
                    Denominator = 5,
                }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    Cases =
                    [
                        valid.Cases[0] with { File = "../escape.rs" },
                        .. valid.Cases.Skip(1),
                    ],
                }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    Cases =
                    [
                        valid.Cases[0] with { Kind = "unknown" },
                        .. valid.Cases.Skip(1),
                    ],
                }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    Cases =
                    [
                        valid.Cases[0],
                        valid.Cases[1],
                        valid.Cases[2] with { ExpectedOutput = null },
                        valid.Cases[3],
                    ],
                }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    DeclaredCoverage = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["compile-pass"] = 99,
                        ["compile-fail"] = 1,
                        ["run-pass"] = 1,
                        ["differential"] = 1,
                    },
                }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    DeclaredLimits = new SafeCoreRegressionProfileRunner.RegressionManifestLimits(
                        0, 4 * 1024 * 1024, 1 * 1024 * 1024, 300, 900),
                }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    Cases =
                    [
                        valid.Cases[0],
                        valid.Cases[1] with { DiagnosticContains = " " },
                        valid.Cases[2],
                        valid.Cases[3],
                    ],
                }));
        AssertEx.Throws<ArgumentException>(() =>
            SafeCoreRegressionProfileRunner.ValidateManifest(
                valid with
                {
                    Cases =
                    [
                        valid.Cases[0] with { DiagnosticContains = "error" },
                        .. valid.Cases.Skip(1),
                    ],
                }));
        return Task.CompletedTask;
    }
}
