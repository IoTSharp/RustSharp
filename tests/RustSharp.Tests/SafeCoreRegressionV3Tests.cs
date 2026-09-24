using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Conformance;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class SafeCoreRegressionV3Tests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Safe-core regression v3 preserves immutable v2 and advances completed contracts", VersionedContractAsync),
        new("Safe-core regression v3 includes exact family and place sample sources", CompletionFixturesAsync),
        new("Safe-core regression v3 rejects version, expectation and coverage mutations", MutationAsync),
        new("P1 report aggregation selects exact v2 or v3 regression contracts", GateIntegrationAsync),
    ];

    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string FixtureRoot => Path.Combine(Root, "tools", "RustSharp.Conformance", "fixtures");
    private static SafeCoreRegressionV2ProfileRunner.Manifest V3() =>
        SafeCoreRegressionV2ProfileRunner.ParseManifest(File.ReadAllText(Path.Combine(FixtureRoot,
            SafeCoreRegressionV2ProfileRunner.ManifestV3FileName)), SafeCoreRegressionV2ProfileRunner.ProfileV3Name);

    private static Task VersionedContractAsync()
    {
        byte[] v2Bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, SafeCoreRegressionV2ProfileRunner.ManifestFileName));
        AssertEx.Equal("7ACD9B4140CA477290A1724DBC82E0BB76BCC1AF7C252CFDD1061EAC1261C7CF",
            Convert.ToHexString(SHA256.HashData(v2Bytes)), "The published v2 manifest must remain byte-for-byte immutable.");
        var v2 = SafeCoreRegressionV2ProfileRunner.ParseManifest(System.Text.Encoding.UTF8.GetString(v2Bytes));
        var v3 = V3();
        AssertEx.Equal(3, v3.Version);
        AssertEx.Equal(26, v3.Denominator);
        AssertEx.Equal(26, v3.DeclaredLimits!.MaximumCases);
        AssertEx.Equal(26, v3.Cases.Count);
        for (int index = 0; index < v2.Cases.Count; index++)
        {
            var previous = v2.Cases[index];
            var current = v3.Cases[index];
            if (previous.Id is "typed-mir-binding-or-pattern" or "typed-mir-mutable-capture")
            {
                AssertEx.Equal(previous.Id, current.Id);
                AssertEx.Equal(previous.File, current.File);
                AssertEx.Equal("rustsharp-fail", previous.Expectation);
                AssertEx.Equal("RSM2002", previous.RustSharpDiagnostic!);
                AssertEx.Equal("run-pass", current.Kind);
                AssertEx.Equal("run-pass", current.Expectation);
                AssertEx.Equal(previous.Id == "typed-mir-binding-or-pattern" ? "7\n" : "3\n", current.ExpectedOutput!);
                AssertEx.True(current.RustSharpDiagnostic is null, "A successful v3 case must not retain a failure contract.");
            }
            else AssertEx.Equal(previous, current, "V3 must preserve every other v2 source contract.");
        }
        return Task.CompletedTask;
    }

    private static Task CompletionFixturesAsync()
    {
        var manifest = V3();
        string[] samples = ["mir-families", "mir-places"];
        for (int index = 0; index < samples.Length; index++)
        {
            var fixture = manifest.Cases[24 + index];
            byte[] source = File.ReadAllBytes(Path.Combine(Root, "samples", samples[index] + ".rs"));
            byte[] copied = File.ReadAllBytes(Path.Combine(FixtureRoot, fixture.File));
            AssertEx.True(source.AsSpan().SequenceEqual(copied), "Completion fixtures must exactly match the verified public samples.");
            AssertEx.True(copied.Length is > 0 and <= SafeCoreRegressionV2ProfileRunner.MaximumFixtureBytes, "Completion fixture must remain bounded.");
            AssertEx.Equal("run-pass", fixture.Kind);
            AssertEx.True(!string.IsNullOrEmpty(fixture.ExpectedOutput), "Completion fixture must have an oracle output contract.");
        }
        AssertEx.Equal(4, manifest.Cases.Count(static fixture => fixture.Kind == "compile-fail"));
        AssertEx.Equal(17, manifest.Cases.Count(static fixture => fixture.Kind == "run-pass"));
        return Task.CompletedTask;
    }

    private static async Task GateIntegrationAsync()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.Combine(temporaryRoot, "rustsharp-regression-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            JsonArray Cases(int count) => new(Enumerable.Range(0, count).Select(index => (JsonNode)new JsonObject
                { ["id"] = "synthetic-" + index, ["status"] = "passed" }).ToArray());
            JsonObject Summary(int count) => new()
            {
                ["status"] = "passed", ["denominator"] = count, ["executed"] = count,
                ["passed"] = count, ["failed"] = 0, ["blocked"] = 0, ["skipped"] = 0,
            };
            string Write(string name, JsonObject document)
            {
                string path = Path.Combine(directory, name + ".json");
                File.WriteAllText(path, document.ToJsonString());
                return path;
            }
            JsonObject Platform(string name, string rid) => new()
            {
                ["evidenceKind"] = "p1-platform-coreclr-ilverify-native-aot", ["profile"] = "p1-differential-v2",
                ["platform"] = new JsonObject { ["name"] = name, ["runtimeIdentifier"] = rid },
                ["manifest"] = new JsonObject { ["denominator"] = 12 }, ["summary"] = Summary(12), ["cases"] = Cases(12),
            };
            string windows = Write("windows", Platform("windows-x64", "win-x64"));
            string linux = Write("linux", Platform("linux-x64", "linux-x64"));
            var differentialSummary = Summary(16);
            differentialSummary["borrowDenominator"] = 10;
            differentialSummary["dropDenominator"] = 6;
            string differential = Write("differential", new JsonObject { ["schemaVersion"] = 2, ["profile"] = "p1-differential-v2",
                ["oracle"] = new JsonObject { ["available"] = true, ["version"] = "rustc 1.98.0 (synthetic-validator-input)" },
                ["summary"] = differentialSummary, ["cases"] = Cases(16) });
            string Regression(bool version3, bool corruptCoverage)
            {
                int count = version3 ? 26 : 24;
                var summary = Summary(count);
                summary["coverage"] = new JsonObject
                {
                    ["compile-pass"] = 1, ["compile-fail"] = version3 ? 4 : 6,
                    ["run-pass"] = corruptCoverage ? 16 : version3 ? 17 : 13, ["differential"] = 4,
                    ["legacy"] = 8, ["typed-mir"] = version3 ? 14 : 12, ["borrow"] = 2, ["drop"] = 2,
                };
                return Write("regression", new JsonObject { ["schemaVersion"] = 2, ["evidenceKind"] = "safe-core-typed-mir-regression",
                    ["profile"] = version3 ? "safe-core-regression-v3" : "safe-core-regression-v2", ["summary"] = summary, ["cases"] = Cases(count) });
            }
            string pwsh = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe") : "pwsh";
            // Four finite validator runs exercise both accepted versions and independent mutations.
            foreach (var scenario in new (bool V3, bool SelectV3, bool Corrupt, bool Pass)[] { (false, false, false, true),
                (true, true, false, true), (false, true, false, false), (true, true, true, false) })
            {
                deadline.Token.ThrowIfCancellationRequested();
                string regression = Regression(scenario.V3, scenario.Corrupt);
                string output = Path.Combine(directory, "gate.json");
                var arguments = new List<string> { "-NoProfile", "-File", Path.Combine(Root, "eng", "Test-P1ExitGate.ps1"),
                    "-WindowsPlatformReport", windows, "-LinuxPlatformReport", linux,
                    "-WindowsDifferentialReport", differential, "-LinuxDifferentialReport", differential,
                    "-WindowsRegressionReport", regression, "-LinuxRegressionReport", regression, "-EvidencePath", output };
                if (scenario.SelectV3) arguments.AddRange(["-RegressionProfile", "safe-core-regression-v3"]);
                var run = await new BoundedProcessRunner().RunAsync(new(pwsh, arguments, directory, TimeSpan.FromSeconds(15)), deadline.Token).ConfigureAwait(false);
                AssertEx.False(run.ProcessTreeCleanupIncomplete, "Validator process tree must be reclaimed.");
                using var report = JsonDocument.Parse(File.ReadAllText(output));
                AssertEx.Equal(scenario.SelectV3 ? "safe-core-regression-v3" : "safe-core-regression-v2",
                    report.RootElement.GetProperty("RegressionProfile").GetString()!);
                AssertEx.Equal(scenario.Pass ? "passed" : "failed", report.RootElement.GetProperty("Summary").GetProperty("Status").GetString()!);
                AssertEx.Equal(scenario.Pass, run.Succeeded, run.StandardError);
            }
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(temporaryRoot, StringComparison.Ordinal), "Cleanup is confined to the owned test directory.");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task MutationAsync()
    {
        var manifest = V3();
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(manifest));
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(
            manifest with { Version = 2 }, SafeCoreRegressionV2ProfileRunner.ProfileV3Name));
        var cases = manifest.Cases.ToArray();
        cases[17] = cases[17] with { Kind = "compile-fail", Expectation = "rustsharp-fail", ExpectedOutput = null, RustSharpDiagnostic = "RSM2002" };
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(
            manifest with { Cases = cases }, SafeCoreRegressionV2ProfileRunner.ProfileV3Name));
        var coverage = new Dictionary<string, int>(manifest.DeclaredCoverage!);
        coverage["run-pass"] = 16;
        AssertEx.Throws<ArgumentException>(() => SafeCoreRegressionV2ProfileRunner.ValidateManifest(
            manifest with { DeclaredCoverage = coverage }, SafeCoreRegressionV2ProfileRunner.ProfileV3Name));
        return Task.CompletedTask;
    }
}
