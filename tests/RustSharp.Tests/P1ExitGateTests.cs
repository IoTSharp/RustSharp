using System.Diagnostics;
using System.Text.Json;
using RustSharp.CodeGen.IL;
using RustSharp.Conformance;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1ExitGateTests
{
    private const string ManifestName = "p1-exit-gate-manifest.json";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 exit gate manifest fixes its five probe denominator", ManifestAsync),
        new("P1 exit gate emits a bounded reproducible report", ReportAsync),
    ];

    private static Task ManifestAsync()
    {
        string root = RepositoryRoot();
        string path = Path.Combine(
            root,
            "tools",
            "RustSharp.Conformance",
            "fixtures",
            ManifestName);
        P1ExitGateProfileRunner.GateManifest manifest =
            P1ExitGateProfileRunner.ParseManifest(File.ReadAllText(path));

        AssertEx.Equal(P1ExitGateProfileRunner.ProfileName, manifest.Profile);
        AssertEx.Equal(P1ExitGateProfileRunner.ManifestVersion, manifest.Version);
        AssertEx.Equal(P1ExitGateProfileRunner.Scope, manifest.Scope);
        AssertEx.Equal(5, manifest.Denominator);
        AssertEx.Equal(5, manifest.Cases.Count);
        AssertEx.Equal(8, manifest.Limits.MaximumCases);
        AssertEx.Equal(5000, manifest.Limits.CaseTimeoutMilliseconds);
        AssertEx.True(
            P1ExitGateProfileRunner.ProbeNames.Order(StringComparer.Ordinal)
                .SequenceEqual(manifest.Cases.Select(static item => item.Probe).Order(StringComparer.Ordinal)),
            "The manifest probe set must match the fixed gate contract.");
        AssertEx.True(
            manifest.Cases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() == 5,
            "The P1 gate case IDs must be unique.");
        AssertEx.Throws<ArgumentException>(() =>
            P1ExitGateProfileRunner.ValidateManifest(manifest with { Denominator = 4 }));
        return Task.CompletedTask;
    }

    private static async Task ReportAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rustsharp-p1-exit-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string reportPath = Path.Combine(directory, "report.json");
        try
        {
            var clock = Stopwatch.StartNew();
            int exitCode = await P1ExitGateProfileRunner.RunAsync(
                RepositoryRoot(),
                reportPath,
                TimeSpan.FromSeconds(60),
                DateTimeOffset.UtcNow,
                clock).ConfigureAwait(false);
            AssertEx.Equal(0, exitCode, "All fixed P1 exit-gate probes must pass.");

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            JsonElement root = report.RootElement;
            AssertEx.Equal(
                P1ExitGateProfileRunner.ProfileName,
                AssertEx.NotNull(root.GetProperty("contract").GetString(), "Report contract is required."));
            AssertEx.Equal(
                P1ExitGateProfileRunner.Scope,
                AssertEx.NotNull(root.GetProperty("evidenceScope").GetString(), "Report scope is required."));
            AssertEx.False(
                root.GetProperty("platformEvidence").GetProperty("nativeAot").GetBoolean(),
                "The in-process gate must not claim Native AOT evidence.");
            AssertEx.False(
                root.GetProperty("platformEvidence").GetProperty("crossPlatform").GetBoolean(),
                "The in-process gate must not claim cross-platform evidence.");
            JsonElement summary = root.GetProperty("summary");
            AssertEx.Equal(
                "passed",
                AssertEx.NotNull(
                    summary.GetProperty("status").GetString(),
                    "Report status is required."));
            AssertEx.Equal(5, summary.GetProperty("denominator").GetInt32());
            AssertEx.Equal(5, summary.GetProperty("passed").GetInt32());
            AssertEx.Equal(0, summary.GetProperty("skipped").GetInt32());
            AssertEx.Equal(5, root.GetProperty("cases").GetArrayLength());
            JsonElement typedMirCase = root.GetProperty("cases").EnumerateArray()
                .Single(item => string.Equals(
                    item.GetProperty("probe").GetString(),
                    "typed-mir-validation",
                    StringComparison.Ordinal));
            string typedMirEvidence = AssertEx.NotNull(
                typedMirCase.GetProperty("evidence").GetString(),
                "The typed-MIR gate case must retain source pipeline evidence.");
            AssertEx.True(typedMirEvidence.Contains("pipeline=successful", StringComparison.Ordinal),
                "The typed-MIR gate case must execute the source-to-MIR pipeline.");
            AssertEx.True(typedMirEvidence.Contains("sourcePath=p1-exit-gate/typed-mir-source.rs", StringComparison.Ordinal),
                "The typed-MIR gate case must retain its source path evidence.");
            AssertEx.True(typedMirEvidence.Contains("ownershipPaths=", StringComparison.Ordinal),
                "The typed-MIR gate case must retain ownership evidence.");
            JsonElement metadataCase = root.GetProperty("cases").EnumerateArray()
                .Single(item => string.Equals(
                    item.GetProperty("probe").GetString(),
                    "metadata-consumer",
                    StringComparison.Ordinal));
            string metadataEvidence = AssertEx.NotNull(
                metadataCase.GetProperty("evidence").GetString(),
                "The metadata gate case must retain producer/consumer evidence.");
            AssertEx.True(metadataEvidence.Contains("consumerCompiled=true", StringComparison.Ordinal),
                "The metadata gate must compile an independent consumer after reading the producer.");
            AssertEx.True(metadataEvidence.Contains("scalarSignatures=I32->I32,Bool->Bool", StringComparison.Ordinal),
                "The metadata gate must preserve both i32 and bool scalar signatures.");
            AssertEx.True(metadataEvidence.Contains("requiredExports=crate::helper,crate::negate", StringComparison.Ordinal),
                "The metadata gate must validate both scalar exports through the consumer compiler.");
            AssertEx.True(metadataEvidence.Contains("consumerAssemblySha256=", StringComparison.Ordinal),
                "The metadata gate must retain the compiled consumer assembly hash.");
            AssertEx.True(
                root.GetProperty("manifest").GetProperty("sha256").GetString()?.Length == 64,
                "The report must retain the manifest SHA-256 for reproducibility.");

            await VerifySourceMirBackendRuntimeAsync().ConfigureAwait(false);

            SafeCoreMirPipelineResult timedOut = SafeCoreMirPipeline.Analyze(
                "fn main() {}",
                "p1-timeout.rs",
                new SafeCoreMirPipelineOptions { Timeout = TimeSpan.FromTicks(1) });
            AssertEx.True(timedOut.IsTruncated && timedOut.Diagnostics.Count == 1,
                "A source-stage timeout must return bounded pipeline evidence rather than an uncaught exception.");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static async Task VerifySourceMirBackendRuntimeAsync()
    {
        const string source =
            "fn helper(value: i32) -> i32 { value + 1 } " +
            "fn main() { println!(\"{}\", helper(4)); }";
        string directory = Path.GetFullPath(Path.Combine(
            RepositoryRoot(), "artifacts", "tests",
            "p1-source-mir-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source,
                sourcePath,
                outputPath,
                "P1SourceMirRuntime",
                CompilationProfile.SafeCoreMir,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "SafeCoreMir backend compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            AssertEx.True(File.Exists(outputPath), "SafeCoreMir backend did not emit a runnable assembly.");

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                outputPath,
                SafeCoreMirPipeline.Profile,
                ["Main"]);
            AssertEx.True(imported.IsSuccessful,
                "SafeCoreMir metadata import failed: " + string.Join("; ", imported.Diagnostics));
            RustSharpMetadataDocument document = imported.Document ??
                throw new InvalidOperationException("SafeCoreMir metadata document is missing.");
            AssertEx.True(document.MirSnapshot?.StartsWith("safe-core-mir-v1\n", StringComparison.Ordinal) == true,
                "SafeCoreMir backend metadata must retain the typed-MIR snapshot.");
            AssertEx.True(document.Ownership.Length >= document.Functions.Length &&
                document.Ownership.Any(fact => fact.Outcomes?.Contains("Returned", StringComparer.Ordinal) == true),
                "SafeCoreMir backend metadata must retain returned ownership evidence.");
            AssertEx.True(document.CleanupSnapshot?.StartsWith(
                    "safe-core-mir-cleanup-p1-v1\n", StringComparison.Ordinal) == true,
                "SafeCoreMir backend metadata must retain compiler-integrated cleanup evidence.");

            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10)),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, "SafeCoreMir runtime execution failed: " + run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete,
                "SafeCoreMir runtime process cleanup must complete.");
            AssertEx.Equal("5\n",
                run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static void TryDelete(string path)
    {
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 20 && clock.Elapsed < TimeSpan.FromSeconds(2); attempt++)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!Directory.Exists(path)) return;
            Thread.Sleep(50);
        }
    }
}
