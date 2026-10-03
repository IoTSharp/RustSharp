using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1EvidenceBindingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 evidence binding accepts complete candidate provenance", AcceptsCompleteReportAsync),
        new("P1 evidence binding rejects stale hashes and skipped cases", RejectsForgedReportAsync),
        new("P1 evidence binding rejects missing oracle and cleanup provenance", RejectsMissingProvenanceAsync),
        new("P1 evidence binding enforces an optional candidate SHA", CandidateShaAsync),
        .. P1PlatformBindingContractTests.All,
    ];

    private static Task AcceptsCompleteReportAsync()
    {
        const string manifestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string compilerHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        P1EvidenceBindingValidator.ValidationResult result = P1EvidenceBindingValidator.Validate(
            BuildReport(manifestHash, compilerHash).ToJsonString(),
            Expectation(manifestHash, compilerHash));
        AssertEx.True(result.Valid, string.Join("; ", result.Errors));
        AssertEx.Equal("passed", result.Status);
        return Task.CompletedTask;
    }

    private static Task RejectsForgedReportAsync()
    {
        const string manifestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string compilerHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        JsonObject report = BuildReport(manifestHash, compilerHash);
        report["manifest"]!["sha256"] = new string('c', 64);
        report["summary"]!["skipped"] = 1;
        report["cases"]![0]!["status"] = "skipped";
        P1EvidenceBindingValidator.ValidationResult result = P1EvidenceBindingValidator.Validate(
            report.ToJsonString(), Expectation(manifestHash, compilerHash));
        AssertEx.False(result.Valid, "Stale manifest and skipped evidence must fail the binding gate.");
        AssertEx.True(result.Errors.Any(error => error.Contains("manifest.sha256", StringComparison.Ordinal)), "Manifest hash mismatch must be reported.");
        AssertEx.True(result.Errors.Any(error => error.Contains("summary.skipped", StringComparison.Ordinal)), "Skipped denominator must be reported.");
        return Task.CompletedTask;
    }

    private static Task RejectsMissingProvenanceAsync()
    {
        const string manifestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string compilerHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        JsonObject report = BuildReport(manifestHash, compilerHash);
        report["toolVersions"]!["rustc"] = "rustc 1.98.0-nightly (forged)";
        report.Remove("cleanup");
        P1EvidenceBindingValidator.ValidationResult result = P1EvidenceBindingValidator.Validate(
            report.ToJsonString(), Expectation(manifestHash, compilerHash));
        AssertEx.False(result.Valid, "Nightly oracle and missing cleanup cannot pass.");
        AssertEx.True(result.Errors.Any(error => error.Contains("oracle", StringComparison.OrdinalIgnoreCase)), "Oracle identity must be checked.");
        AssertEx.True(result.Errors.Any(error => error.Contains("Cleanup provenance", StringComparison.Ordinal)), "Cleanup provenance must be required.");
        return Task.CompletedTask;
    }

    private static Task CandidateShaAsync()
    {
        const string manifestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string compilerHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string candidateSha = "cccccccccccccccccccccccccccccccccccccccc";
        JsonObject report = BuildReport(manifestHash, compilerHash);
        report["candidateSha"] = candidateSha;
        P1EvidenceBindingValidator.ValidationResult accepted = P1EvidenceBindingValidator.Validate(
            report.ToJsonString(), Expectation(manifestHash, compilerHash, candidateSha));
        AssertEx.True(accepted.Valid, string.Join("; ", accepted.Errors));
        report["candidateSha"] = new string('d', 40);
        P1EvidenceBindingValidator.ValidationResult rejected = P1EvidenceBindingValidator.Validate(
            report.ToJsonString(), Expectation(manifestHash, compilerHash, candidateSha));
        AssertEx.False(rejected.Valid, "A report from a different candidate SHA must be rejected.");
        AssertEx.True(rejected.Errors.Any(error => error.Contains("candidateSha", StringComparison.Ordinal)), "Candidate SHA mismatch must be reported.");
        return Task.CompletedTask;
    }

    private static P1EvidenceBindingValidator.BindingExpectation Expectation(string manifestHash, string compilerHash, string? candidateSha = null) =>
        new("p1-platform-coreclr-ilverify-native-aot", "p1-differential-v3", "win-x64", 1, manifestHash, compilerHash, CandidateSha: candidateSha);

    private static JsonObject BuildReport(string manifestHash, string compilerHash) => new()
    {
        ["evidenceKind"] = "p1-platform-coreclr-ilverify-native-aot",
        ["profile"] = "p1-differential-v3",
        ["manifest"] = new JsonObject { ["sha256"] = manifestHash, ["denominator"] = 1, ["validated"] = true },
        ["compiler"] = new JsonObject { ["sha256"] = compilerHash, ["profile"] = "safe-core-mir-p1-v3" },
        ["platform"] = new JsonObject { ["name"] = "windows-x64", ["runtimeIdentifier"] = "win-x64" },
        ["toolVersions"] = new JsonObject
        {
            ["dotnet"] = "10.0.401",
            ["sdkVersion"] = "10.0.401",
            ["rustc"] = "rustc 1.98.0 (88d9e12ae 2026-08-18)",
        },
        ["summary"] = new JsonObject
        {
            ["status"] = "passed", ["exitCode"] = 0, ["denominator"] = 1,
            ["executed"] = 1, ["passed"] = 1, ["failed"] = 0, ["blocked"] = 0, ["skipped"] = 0,
        },
        ["cases"] = new JsonArray((JsonNode)new JsonObject
        {
            ["id"] = "case-01", ["status"] = "passed", ["sourceSha256"] = new string('d', 64),
        }),
        ["execution"] = new JsonObject
        {
            ["startedAtUtc"] = "2026-10-02T00:00:00Z", ["finishedAtUtc"] = "2026-10-02T00:00:01Z", ["deadlineExpired"] = false,
        },
        ["cleanup"] = new JsonObject { ["completed"] = true, ["diagnostic"] = null },
    };
}
