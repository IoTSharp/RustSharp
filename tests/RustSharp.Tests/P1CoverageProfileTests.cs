using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1CoverageProfileTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 coverage manifest fixes all requirement categories and source hashes", ManifestAsync),
        new("P1 coverage runner emits a reproducible report", ReportAsync),
    ];

    private static Task ManifestAsync()
    {
        string root = RepositoryRoot();
        string path = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures",
            P1CoverageProfileRunner.ManifestFileName);
        P1CoverageProfileRunner.Manifest manifest = P1CoverageProfileRunner.ParseManifest(
            File.ReadAllText(path), root);
        AssertEx.Equal(P1CoverageProfileRunner.ProfileName, manifest.Profile);
        AssertEx.Equal(P1CoverageProfileRunner.RequirementDenominator, manifest.Denominator);
        AssertEx.Equal(160, manifest.Cases.Count);
        AssertEx.True(manifest.Requirements.All(requirement => manifest.Cases.Count(item =>
            item.RequirementId == requirement.Id) == 4),
            "Every frozen requirement must have positive, negative, boundary and budget rows.");
        AssertEx.Throws<ArgumentException>(() => P1CoverageProfileRunner.ValidateManifest(
            manifest with { Denominator = manifest.Denominator - 1 }));
        var cases = manifest.Cases.ToArray();
        cases[0] = cases[0] with { SourceSha256 = new string('0', 64) };
        AssertEx.Throws<ArgumentException>(() => P1CoverageProfileRunner.ValidateManifest(
            manifest with { Cases = cases }, root));
        return Task.CompletedTask;
    }

    private static async Task ReportAsync()
    {
        string root = RepositoryRoot();
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-p1-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string reportPath = Path.Combine(directory, "coverage.json");
        try
        {
            int code = await P1CoverageProfileRunner.RunAsync(root, reportPath,
                TimeSpan.FromSeconds(20), DateTimeOffset.UtcNow,
                System.Diagnostics.Stopwatch.StartNew()).ConfigureAwait(false);
            AssertEx.Equal(0, code);
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            AssertEx.Equal("passed", report.RootElement.GetProperty("status").GetString()!);
            AssertEx.Equal(40, report.RootElement.GetProperty("summary").GetProperty("denominator").GetInt32());
            AssertEx.Equal(160, report.RootElement.GetProperty("summary").GetProperty("caseDenominator").GetInt32());
            string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures",
                P1CoverageProfileRunner.ManifestFileName);
            string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)));
            AssertEx.Equal(expectedHash, report.RootElement.GetProperty("manifestSha256").GetString()!);
            P1CoverageProfileRunner.ValidateReport(File.ReadAllText(reportPath), root);
            JsonObject mutated = JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
            mutated["summary"]!.AsObject()["skipped"] = 1;
            AssertEx.Throws<ArgumentException>(() => P1CoverageProfileRunner.ValidateReport(
                mutated.ToJsonString(), root));
            JsonObject staleCase = JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
            staleCase["manifestVersion"] = 99;
            AssertEx.Throws<ArgumentException>(() => P1CoverageProfileRunner.ValidateReport(
                staleCase.ToJsonString(), root));
            JsonObject staleBackend = JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
            staleBackend["cases"]![0]! ["backends"] = new JsonArray("coreclr");
            AssertEx.Throws<ArgumentException>(() => P1CoverageProfileRunner.ValidateReport(
                staleBackend.ToJsonString(), root));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
