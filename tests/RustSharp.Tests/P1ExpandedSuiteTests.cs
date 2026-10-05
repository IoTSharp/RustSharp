using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1ExpandedSuiteTests
{
    private const string ManifestName = "p1-expanded-suites-v2-manifest.json";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 expanded manifest freezes differential and platform denominators", FrozenManifestAsync),
        new("P1 expanded evidence accepts complete fixed provenance", AcceptsCompleteReportAsync),
        new("P1 expanded evidence rejects duplicate case IDs", RejectsDuplicateCaseAsync),
        new("P1 expanded evidence rejects missing hashes", RejectsMissingHashAsync),
        new("P1 expanded evidence rejects skipped and altered denominators", RejectsSkipAndDenominatorAsync),
        new("P1 expanded evidence requires semantic closure fields", RejectsMissingSemanticClosureAsync),
        new("P1 expanded evidence rejects placeholder closure claims", RejectsPlaceholderClaimAsync),
        new("P1 expanded platform evidence requires process envelopes", RejectsMissingPlatformProcessAsync),
        new("P1 expanded platform evidence rejects empty process envelopes", RejectsEmptyPlatformProcessAsync),
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
        AssertEx.Equal(4, differential.Version);
        AssertEx.Equal(2, platform.Version);
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

    private static Task RejectsMissingSemanticClosureAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.DifferentialProfile);
        JsonObject report = BuildReport(suite);
        report.Remove("semanticClosureEligible");
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(report.ToJsonString(), suite);
        AssertEx.False(result.Valid, "A report without semantic closure eligibility must fail the expanded evidence gate.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("semanticClosureEligible", StringComparison.Ordinal)), "The missing semantic closure diagnostic must be retained.");
        return Task.CompletedTask;
    }

    private static Task RejectsPlaceholderClaimAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.DifferentialProfile);
        JsonObject report = BuildReport(suite);
        report["semanticClosureEligible"] = false;
        JsonObject placeholder = report["cases"]!.AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == "drop-aggregate-fields")!.AsObject();
        placeholder["semanticClosureEligible"] = false;
        placeholder["semanticCoverage"] = "placeholder";
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(report.ToJsonString(), suite);
        AssertEx.False(result.Valid, "A semantically incomplete suite must not claim expanded closure.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("semanticClosureEligible", StringComparison.OrdinalIgnoreCase)), "The semantic closure diagnostic must be retained.");
        return Task.CompletedTask;
    }

    private static Task RejectsMissingPlatformProcessAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.PlatformProfile);
        JsonObject report = BuildReport(suite);
        report["cases"]!.AsArray()[0]!.AsObject().Remove("nativeAot");
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(report.ToJsonString(), suite);
        AssertEx.False(result.Valid, "A platform case without Native AOT evidence must fail the expanded evidence gate.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("nativeAot", StringComparison.Ordinal)), "The missing Native AOT diagnostic must be retained.");
        return Task.CompletedTask;
    }

    private static Task RejectsEmptyPlatformProcessAsync()
    {
        P1ExpandedSuiteValidator.SuiteSpec suite = ReadManifest().Suites.Single(static item => item.Profile == P1ExpandedSuiteValidator.PlatformProfile);
        JsonObject report = BuildReport(suite);
        report["cases"]!.AsArray()[0]!.AsObject()["nativeAot"] = new JsonObject();
        P1ExpandedSuiteValidator.ValidationResult result = P1ExpandedSuiteValidator.ValidateReport(report.ToJsonString(), suite);
        AssertEx.False(result.Valid, "An empty platform process envelope must fail the expanded evidence gate.");
        AssertEx.True(result.Errors.Any(static error => error.Contains("empty", StringComparison.OrdinalIgnoreCase)), "The empty evidence diagnostic must be retained.");
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
            bool semanticEligible = suite.Profile == P1ExpandedSuiteValidator.DifferentialProfile
                ? !P1ExpandedDifferentialRunner.IsPlaceholderCase(fixture.Id)
                : P1ExpandedPlatformRunner.IsSemanticClosureEligible(fixture.Id);
            var item = new JsonObject
            {
                ["id"] = fixture.Id,
                ["status"] = "passed",
                ["sourceSha256"] = fixture.SourceSha256,
                ["expectationSha256"] = fixture.ExpectationSha256,
                ["semanticClosureEligible"] = semanticEligible,
                ["semanticCoverage"] = semanticEligible ? "ownership-drop-scenario" : "placeholder",
            };
            if (suite.Profile == P1ExpandedSuiteValidator.PlatformProfile)
            {
                item["coreClrCompile"] = new JsonObject { ["evidence"] = "synthetic" };
                item["coreClrRun"] = new JsonObject { ["evidence"] = "synthetic" };
                item["ilVerify"] = new JsonObject { ["evidence"] = "synthetic" };
                item["nativeAot"] = new JsonObject { ["evidence"] = "synthetic" };
            }
            cases.Add((JsonNode)item);
        }
        bool semanticEligibleForSuite = suite.Cases.All(fixture => suite.Profile == P1ExpandedSuiteValidator.DifferentialProfile
            ? !P1ExpandedDifferentialRunner.IsPlaceholderCase(fixture.Id)
            : P1ExpandedPlatformRunner.IsSemanticClosureEligible(fixture.Id));
        var report = new JsonObject
        {
            ["profile"] = suite.Profile,
            ["backend"] = suite.Backend,
            ["compilerSha256"] = suite.CompilerSha256,
            ["declaredCompilerSha256"] = suite.CompilerSha256,
            ["compiler"] = new JsonObject { ["declaredSha256"] = suite.CompilerSha256 },
            ["manifest"] = new JsonObject { ["version"] = P1ExpandedSuiteValidator.ManifestVersion, ["sha256"] = suite.ManifestSha256, ["declaredSha256"] = suite.ManifestSha256, ["denominator"] = suite.Denominator, ["validated"] = true },
            ["platform"] = new JsonObject { ["runtimeIdentifier"] = suite.RuntimeIdentifiers[0], ["backend"] = suite.Backend, ["oracle"] = suite.Oracle },
            ["toolVersions"] = new JsonObject { ["dotnet"] = "10.0.401", ["sdkVersion"] = "10.0.401", ["rustc"] = suite.Oracle },
            ["oracle"] = new JsonObject { ["version"] = suite.Oracle },
            ["semanticClosureEligible"] = semanticEligibleForSuite,
            ["summary"] = new JsonObject { ["status"] = "passed", ["denominator"] = suite.Denominator, ["executed"] = suite.Denominator, ["passed"] = suite.Denominator, ["failed"] = 0, ["blocked"] = 0, ["skipped"] = 0 },
            ["cases"] = cases,
            ["execution"] = new JsonObject { ["startedAtUtc"] = "2026-10-02T00:00:00Z", ["finishedAtUtc"] = "2026-10-02T00:00:01Z", ["deadlineExpired"] = false },
            ["cleanup"] = new JsonObject { ["completed"] = true, ["diagnostic"] = null },
        };
        if (suite.Profile == P1ExpandedSuiteValidator.DifferentialProfile)
        {
            P1ExpandedSuiteValidator.CaseSpec[] borrowCases = suite.Cases.Where(static fixture =>
                fixture.Id.StartsWith("borrow-", StringComparison.Ordinal)).ToArray();
            bool borrowEligible = borrowCases.All(fixture => !P1ExpandedDifferentialRunner.IsPlaceholderCase(fixture.Id));
            report["borrowSemanticClosureEligible"] = borrowEligible;
            report["borrowSemanticClosure"] = new JsonObject
            {
                ["eligible"] = borrowEligible,
                ["eligibleCases"] = borrowCases.Count(fixture => !P1ExpandedDifferentialRunner.IsPlaceholderCase(fixture.Id)),
                ["placeholderCases"] = borrowCases.Count(fixture => P1ExpandedDifferentialRunner.IsPlaceholderCase(fixture.Id)),
            };
        }
        return report;
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
