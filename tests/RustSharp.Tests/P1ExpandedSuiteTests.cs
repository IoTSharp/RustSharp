using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1ExpandedSuiteTests
{
    private const string ManifestName = "p1-expanded-suites-v1-manifest.json";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 expanded manifest freezes differential and platform denominators", FrozenManifestAsync),
        new("P1 expanded evidence accepts complete fixed provenance", AcceptsCompleteReportAsync),
        new("P1 expanded evidence rejects duplicate case IDs", RejectsDuplicateCaseAsync),
        new("P1 expanded evidence rejects missing hashes", RejectsMissingHashAsync),
        new("P1 expanded evidence rejects skipped and altered denominators", RejectsSkipAndDenominatorAsync),
        new("P1 expanded evidence rejects over-bound reports", RejectsOverBoundReportAsync),
    ];

    private static Task FrozenManifestAsync()
    {
        P1ExpandedSuiteValidator.ExpandedManifest manifest = ReadManifest();
        AssertEx.Equal(2, manifest.Suites.Count);
        P1ExpandedSuiteValidator.SuiteSpec differential = manifest.Suites.Single(static suite => suite.Profile == P1ExpandedSuiteValidator.DifferentialProfile);
        P1ExpandedSuiteValidator.SuiteSpec platform = manifest.Suites.Single(static suite => suite.Profile == P1ExpandedSuiteValidator.PlatformProfile);
        AssertEx.Equal(P1ExpandedSuiteValidator.DifferentialDenominator, differential.Denominator);
        AssertEx.Equal(P1ExpandedSuiteValidator.PlatformDenominator, platform.Denominator);
        AssertEx.Equal(20, differential.Cases.Count(static item => item.Id.StartsWith("borrow-", StringComparison.Ordinal)));
        AssertEx.Equal(12, differential.Cases.Count(static item => item.Id.StartsWith("drop-", StringComparison.Ordinal)));
        AssertEx.Equal(2, differential.RuntimeIdentifiers.Count);
        AssertEx.True(differential.Cases.All(static item => item.SourceSha256.Length == 64 && item.ExpectationSha256.Length == 64), "Every expanded case must carry immutable hashes.");
        AssertEx.True(platform.Cases.All(static item => item.SourceSha256.Length == 64 && item.ExpectationSha256.Length == 64), "Every platform case must carry immutable hashes.");
        return Task.CompletedTask;
    }

    private static Task AcceptsCompleteReportAsync()
    {
        P1ExpandedSuiteValidator.ExpandedManifest manifest = ReadManifest();
        foreach (P1ExpandedSuiteValidator.SuiteSpec suite in manifest.Suites)
        {
            P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(BuildReport(suite).ToJsonString(), suite);
            AssertEx.True(result.Valid, $"{suite.Profile}: {string.Join("; ", result.Errors)}");
        }
        return Task.CompletedTask;
    }

    private static Task RejectsDuplicateCaseAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.DifferentialProfile);
        JsonObject report = BuildReport(suite);
        report["cases"]![1]! ["id"] = report["cases"]![0]!["id"]!.GetValue<string>();
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(report.ToJsonString(), suite);
        AssertEx.False(result.Valid, "Duplicate case IDs must fail the expanded evidence gate.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("unique", StringComparison.OrdinalIgnoreCase)), "The duplicate ID diagnostic must be retained.");
        return Task.CompletedTask;
    }

    private static Task RejectsMissingHashAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.PlatformProfile);
        JsonObject report = BuildReport(suite);
        report["cases"]![0]!.AsObject().Remove("sourceSha256");
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(report.ToJsonString(), suite);
        AssertEx.False(result.Valid, "Missing source hashes must fail the expanded evidence gate.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("sourceSha256", StringComparison.Ordinal)), "The missing source hash diagnostic must be retained.");
        return Task.CompletedTask;
    }

    private static Task RejectsSkipAndDenominatorAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.PlatformProfile);
        JsonObject report = BuildReport(suite);
        report["summary"]!["skipped"] = 1;
        report["cases"]![0]!["status"] = "skipped";
        report["manifest"]!["denominator"] = suite.Denominator - 1;
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(report.ToJsonString(), suite);
        AssertEx.False(result.Valid, "Skipped cases and altered denominators must fail the expanded evidence gate.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("skipped", StringComparison.OrdinalIgnoreCase)), "The skipped summary diagnostic must be retained.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("denominator", StringComparison.OrdinalIgnoreCase)), "The denominator diagnostic must be retained.");
        return Task.CompletedTask;
    }

    private static Task RejectsOverBoundReportAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites[0];
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(new string('x', P1ExpandedSuiteValidator.MaximumReportBytes + 1), suite);
        AssertEx.False(result.Valid, "An oversized report must be rejected before JSON parsing.");
        return Task.CompletedTask;
    }

    private static P1ExpandedSuiteValidator.ExpandedManifest ReadManifest()
    {
        string root = RepositoryRoot();
        string path = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", ManifestName);
        return P1ExpandedSuiteValidator.ParseManifest(File.ReadAllText(path), root);
    }

    private static JsonObject BuildReport(P1ExpandedSuiteValidator.SuiteSpec suite)
    {
        var cases = new JsonArray();
        foreach (P1ExpandedSuiteValidator.CaseSpec fixture in suite.Cases)
        {
            cases.Add((JsonNode)new JsonObject
            {
                ["id"] = fixture.Id,
                ["status"] = "passed",
                ["sourceSha256"] = fixture.SourceSha256,
                ["expectationSha256"] = fixture.ExpectationSha256,
            });
        }
        return new JsonObject
        {
            ["profile"] = suite.Profile,
            ["backend"] = suite.Backend,
            ["compilerSha256"] = suite.CompilerSha256,
            ["manifest"] = new JsonObject { ["version"] = P1ExpandedSuiteValidator.ManifestVersion, ["sha256"] = suite.ManifestSha256, ["denominator"] = suite.Denominator, ["validated"] = true },
            ["platform"] = new JsonObject { ["runtimeIdentifier"] = suite.RuntimeIdentifiers[0], ["backend"] = suite.Backend, ["oracle"] = suite.Oracle },
            ["toolVersions"] = new JsonObject { ["dotnet"] = "10.0.401", ["sdkVersion"] = "10.0.401", ["rustc"] = suite.Oracle },
            ["oracle"] = new JsonObject { ["version"] = suite.Oracle },
            ["summary"] = new JsonObject { ["status"] = "passed", ["denominator"] = suite.Denominator, ["executed"] = suite.Denominator, ["passed"] = suite.Denominator, ["failed"] = 0, ["blocked"] = 0, ["skipped"] = 0 },
            ["cases"] = cases,
            ["execution"] = new JsonObject { ["startedAtUtc"] = "2026-10-02T00:00:00Z", ["finishedAtUtc"] = "2026-10-02T00:00:01Z", ["deadlineExpired"] = false },
            ["cleanup"] = new JsonObject { ["completed"] = true, ["diagnostic"] = null },
        };
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
