using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>Executes the frozen 24-case platform suite with bounded managed probes.</summary>
internal static class P1ExpandedPlatformRunner
{
    internal const string ProfileName = "p1-platform-v2";
    private const int MaximumTimeoutSeconds = 300;
    private const int MaximumDeadlineSeconds = 900;
    private const string CompilerProfile = "safe-core-mir-p1-v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> RunAsync(string root, string reportPath, TimeSpan timeout, TimeSpan deadline, DateTimeOffset startedAtUtc, Stopwatch clock, string runtimeIdentifier)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(MaximumDeadlineSeconds)) throw new ArgumentOutOfRangeException(nameof(deadline));
        string fullReport = Path.GetFullPath(reportPath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReport)!);
        string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", P1ExpandedSuiteValidator.ManifestFileName);
        P1ExpandedSuiteValidator.SuiteSpec? suite = null;
        string? harnessError = null;
        try
        {
            P1ExpandedSuiteValidator.ExpandedManifest manifest = P1ExpandedSuiteValidator.ParseManifest(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false), root);
            suite = manifest.Suites.Single(static item => item.Profile == ProfileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            harnessError = exception.Message;
        }
        using var cancellation = new CancellationTokenSource(deadline);
        var cases = new List<JsonObject>();
        var processRunner = new BoundedProcessRunner();
        ToolProbe dotnetProbe = await ProbeToolAsync(processRunner, "dotnet", ["--version"], root, timeout, cancellation.Token).ConfigureAwait(false);
        ToolProbe rustcProbe = await ProbeToolAsync(processRunner, "rustc", ["+1.98.0", "--version"], root, timeout, cancellation.Token).ConfigureAwait(false);
        ToolProbe ilVerifyProbe = await ProbeToolAsync(processRunner, "dotnet", ["tool", "run", "ilverify", "--", "--version"], root, timeout, cancellation.Token).ConfigureAwait(false);
        string runDirectory = Path.Combine(Path.GetDirectoryName(fullReport)!, ".run-p1-platform-v2-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        string? cleanupDiagnostic = null;
        try
        {
            if (suite is not null)
            {
                Directory.CreateDirectory(runDirectory);
                foreach (P1ExpandedSuiteValidator.CaseSpec fixture in suite.Cases)
                {
                    if (cancellation.IsCancellationRequested) { cases.Add(Blocked(fixture, "Platform deadline expired before this case started.")); continue; }
                    try { cases.Add(await RunCaseAsync(processRunner, root, runDirectory, fixture, timeout, runtimeIdentifier, ilVerifyProbe, cancellation.Token).ConfigureAwait(false)); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cases.Add(Blocked(fixture, "Platform case deadline or cancellation prevented complete evidence.")); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { cases.Add(Blocked(fixture, exception.Message)); }
                }
            }
        }
        finally { cleanupDiagnostic = DeleteBounded(runDirectory); }
        if (suite is not null) while (cases.Count < suite.Denominator) cases.Add(Blocked(suite.Cases[cases.Count], harnessError ?? "Platform case did not start."));
        clock.Stop();
        int denominator = suite?.Denominator ?? 0;
        int passed = cases.Count(static item => item["status"]?.GetValue<string>() == "passed");
        int failed = cases.Count(static item => item["status"]?.GetValue<string>() == "failed");
        int blocked = cases.Count(static item => item["status"]?.GetValue<string>() == "blocked");
        string status = suite is null || harnessError is not null || cleanupDiagnostic is not null || cancellation.IsCancellationRequested || blocked > 0 ? "blocked" : failed > 0 ? "failed" : passed == denominator ? "passed" : "blocked";
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["evidenceKind"] = "p1-platform-coreclr-ilverify-native-aot",
            ["profile"] = ProfileName,
            ["candidateSha"] = ResolveCandidateSha(),
            ["backend"] = suite?.Backend ?? "",
            ["compilerSha256"] = suite?.CompilerSha256 ?? "",
            ["manifest"] = new JsonObject { ["path"] = Path.GetRelativePath(root, manifestPath).Replace(Path.DirectorySeparatorChar, '/'), ["sha256"] = suite?.ManifestSha256 ?? "", ["denominator"] = denominator, ["validated"] = suite is not null },
            ["compiler"] = new JsonObject { ["sha256"] = suite?.CompilerSha256 ?? "", ["profile"] = CompilerProfile },
            ["platform"] = new JsonObject { ["name"] = runtimeIdentifier == "win-x64" ? "windows-x64" : "linux-x64", ["runtimeIdentifier"] = runtimeIdentifier, ["backend"] = suite?.Backend ?? "", ["oracle"] = suite?.Oracle ?? "" },
            ["toolVersions"] = new JsonObject { ["dotnet"] = ProbeVersion(dotnetProbe, Environment.Version.ToString()), ["sdkVersion"] = Environment.GetEnvironmentVariable("DOTNET_SDK_VERSION") ?? ProbeVersion(dotnetProbe, "unknown"), ["rustc"] = ProbeVersion(rustcProbe, "unavailable (required rustc 1.98.0)"), ["ilverify"] = ProbeVersion(ilVerifyProbe, "unavailable") },
            ["oracle"] = new JsonObject { ["version"] = suite?.Oracle ?? "" },
            ["preflight"] = new JsonObject { ["dotnet"] = ProbeEvidence(dotnetProbe), ["rustc"] = ProbeEvidence(rustcProbe), ["ilverify"] = ProbeEvidence(ilVerifyProbe) },
            ["summary"] = new JsonObject { ["status"] = status, ["exitCode"] = status == "passed" ? 0 : status == "failed" ? 1 : 2, ["denominator"] = denominator, ["executed"] = cases.Count, ["passed"] = passed, ["failed"] = failed, ["blocked"] = blocked, ["skipped"] = 0 },
            ["cases"] = new JsonArray(cases.Select(static item => (JsonNode)item).ToArray()),
            ["execution"] = new JsonObject { ["startedAtUtc"] = startedAtUtc, ["finishedAtUtc"] = DateTimeOffset.UtcNow, ["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds, ["deadlineExpired"] = cancellation.IsCancellationRequested },
            ["cleanup"] = new JsonObject { ["completed"] = cleanupDiagnostic is null, ["diagnostic"] = cleanupDiagnostic },
            ["harnessError"] = harnessError,
        };
        string temp = fullReport + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temp, report.ToJsonString(JsonOptions)).ConfigureAwait(false); File.Move(temp, fullReport, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Console.WriteLine(report.ToJsonString(JsonOptions));
        return status == "passed" ? 0 : status == "failed" ? 1 : 2;
    }

    private static async Task<JsonObject> RunCaseAsync(BoundedProcessRunner runner, string root, string runDirectory, P1ExpandedSuiteValidator.CaseSpec fixture, TimeSpan timeout, string runtimeIdentifier, ToolProbe ilVerifyProbe, CancellationToken cancellationToken)
    {
        string source = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", fixture.Source);
        string directory = Path.Combine(runDirectory, fixture.Id); Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "program.dll");
        BoundedProcessResult compile = await runner.RunAsync(new BoundedProcessRequest("dotnet", [Path.Combine(root, "src", "RustSharp.Cli", "bin", "Release", "net10.0", "rsc.dll"), "compile", source, "--output", output, "--profile", CompilerProfile], root, timeout), cancellationToken).ConfigureAwait(false);
        BoundedProcessResult? run = Succeeded(compile) ? await runner.RunAsync(new BoundedProcessRequest("dotnet", [output], directory, timeout), cancellationToken).ConfigureAwait(false) : null;
        JsonObject ilVerify = await RunIlVerifyAsync(runner, root, directory, output, timeout, ilVerifyProbe, cancellationToken).ConfigureAwait(false);
        string assemblyName = Path.GetFileNameWithoutExtension(fixture.Source);
        JsonObject nativeAot = await RunNativeAotAsync(runner, root, directory, output, assemblyName, timeout, runtimeIdentifier, cancellationToken).ConfigureAwait(false);
        bool coreClrPassed = Succeeded(compile) && run is not null && Succeeded(run);
        bool ilVerifyPassed = ilVerify["status"]?.GetValue<string>() == "passed";
        bool nativeAotPassed = nativeAot["status"]?.GetValue<string>() == "passed";
        bool passed = coreClrPassed && ilVerifyPassed && nativeAotPassed;
        bool blocked = ilVerify["status"]?.GetValue<string>() == "blocked" || nativeAot["status"]?.GetValue<string>() == "blocked";
        string status = passed ? "passed" : blocked ? "blocked" : "failed";
        var result = new JsonObject { ["id"] = fixture.Id, ["source"] = fixture.Source, ["sourceSha256"] = fixture.SourceSha256, ["expectationSha256"] = fixture.ExpectationSha256, ["status"] = status, ["difference"] = passed ? null : "CoreCLR, ILVerify, or Native AOT evidence did not complete." };
        result["coreClrCompile"] = Evidence(compile); result["coreClrRun"] = run is null ? null : Evidence(run);
        result["ilVerify"] = ilVerify;
        result["nativeAot"] = nativeAot;
        return result;
    }
    private static async Task<JsonObject> RunIlVerifyAsync(BoundedProcessRunner runner, string root, string directory, string assembly, TimeSpan timeout, ToolProbe probe, CancellationToken cancellationToken)
    {
        if (!Succeeded(probe.Result)) return new JsonObject { ["status"] = "blocked", ["diagnostic"] = probe.Diagnostic ?? "ILVerify tool preflight did not succeed.", ["preflight"] = ProbeEvidence(probe) };
        string evidencePath = Path.Combine(directory, "ilverify.json");
        string script = Path.Combine(root, "eng", "Invoke-ILVerify.ps1");
        BoundedProcessResult result;
        string runtimeAssembly = Path.Combine(directory, "RustSharp.Runtime.dll");
        if (!File.Exists(runtimeAssembly)) return new JsonObject { ["status"] = "blocked", ["diagnostic"] = "RustSharp.Runtime.dll was not emitted beside the generated assembly." };
        try { result = await runner.RunAsync(new BoundedProcessRequest("pwsh", ["-NoLogo", "-NoProfile", "-File", script, "-AssemblyPath", assembly, "-ReferencePath", runtimeAssembly, "-EvidencePath", evidencePath, "-TimeoutSeconds", "120"], root, timeout), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new JsonObject { ["status"] = "blocked", ["diagnostic"] = "PowerShell 7 or ILVerify launcher is unavailable: " + exception.Message }; }
        bool verified = Succeeded(result) && File.Exists(evidencePath) && IlVerifyEvidenceSucceeded(evidencePath);
        bool unavailable = result.Termination != BoundedProcessTermination.Exited || result.StandardError.Contains("SDK", StringComparison.OrdinalIgnoreCase) || result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase) || result.StandardError.Contains("tool", StringComparison.OrdinalIgnoreCase) && result.StandardError.Contains("cannot", StringComparison.OrdinalIgnoreCase);
        return new JsonObject { ["status"] = verified ? "passed" : unavailable ? "blocked" : "failed", ["succeeded"] = verified, ["diagnostic"] = verified ? null : unavailable ? "ILVerify launcher or SDK is unavailable." : "ILVerify did not produce successful evidence.", ["process"] = Evidence(result), ["evidencePath"] = Path.GetRelativePath(root, evidencePath).Replace(Path.DirectorySeparatorChar, '/'), ["evidenceSha256"] = File.Exists(evidencePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidencePath))) : null };
    }
    private static async Task<JsonObject> RunNativeAotAsync(BoundedProcessRunner runner, string root, string directory, string assembly, string assemblyName, TimeSpan timeout, string runtimeIdentifier, CancellationToken cancellationToken)
    {
        bool compatible = runtimeIdentifier == "win-x64" ? OperatingSystem.IsWindows() : runtimeIdentifier == "linux-x64" && OperatingSystem.IsLinux();
        if (!compatible) return new JsonObject { ["status"] = "blocked", ["diagnostic"] = $"Native AOT evidence requires the {runtimeIdentifier} host." };
        try
        {
            var publisher = new NativeAotPublisher(runner);
            string outputDirectory = Path.Combine(directory, "native-aot");
            NativeAotPublishResult publish = await publisher.PublishAsync(new NativeAotPublishRequest(assembly, assemblyName, runtimeIdentifier, outputDirectory, timeout), cancellationToken).ConfigureAwait(false);
            if (!publish.Succeeded || publish.ExecutablePath is null)
            {
                string diagnostic = publish.ProcessResult.StandardError;
                bool unavailable = publish.ProcessResult.Termination != BoundedProcessTermination.Exited || diagnostic.Contains("SDK", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("not found", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("PublishAot", StringComparison.OrdinalIgnoreCase);
                return new JsonObject { ["status"] = unavailable ? "blocked" : "failed", ["diagnostic"] = string.IsNullOrWhiteSpace(diagnostic) ? "Native AOT publish did not produce an executable." : diagnostic, ["publish"] = Evidence(publish.ProcessResult), ["hostCleanupIncomplete"] = publish.HostCleanupIncomplete };
            }
            BoundedProcessResult run = await runner.RunAsync(new BoundedProcessRequest(publish.ExecutablePath, [], outputDirectory, timeout), cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["status"] = Succeeded(run) ? "passed" : "failed", ["succeeded"] = Succeeded(run), ["diagnostic"] = Succeeded(run) ? null : "Native AOT executable failed to run.", ["publish"] = Evidence(publish.ProcessResult), ["run"] = Evidence(run), ["executablePath"] = Path.GetRelativePath(root, publish.ExecutablePath).Replace(Path.DirectorySeparatorChar, '/'), ["executableSha256"] = File.Exists(publish.ExecutablePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(publish.ExecutablePath))) : null, ["hostCleanupIncomplete"] = publish.HostCleanupIncomplete };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return new JsonObject { ["status"] = "blocked", ["diagnostic"] = "Native AOT toolchain unavailable: " + exception.Message }; }
    }
    private static bool IlVerifyEvidenceSucceeded(string path)
    {
        try { using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path)); return document.RootElement.TryGetProperty("Succeeded", out JsonElement succeeded) && succeeded.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }
    private static async Task<ToolProbe> ProbeToolAsync(BoundedProcessRunner runner, string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { BoundedProcessResult result = await runner.RunAsync(new BoundedProcessRequest(fileName, arguments, workingDirectory, timeout), cancellationToken).ConfigureAwait(false); return new ToolProbe(result, result.Succeeded ? null : $"{fileName} preflight exited with code {result.ExitCode}."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new ToolProbe(null, $"{fileName} preflight was cancelled by the platform deadline."); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException) { return new ToolProbe(null, $"{fileName} preflight unavailable: {exception.Message}"); }
    }
    private static string ProbeVersion(ToolProbe probe, string fallback) => Succeeded(probe.Result) ? probe.Result!.StandardOutput.Trim() : fallback;
    private static JsonObject ProbeEvidence(ToolProbe probe) => probe.Result is null ? new JsonObject { ["status"] = "blocked", ["diagnostic"] = probe.Diagnostic } : Evidence(probe.Result);
    private sealed record ToolProbe(BoundedProcessResult? Result, string? Diagnostic);
    private static JsonObject Blocked(P1ExpandedSuiteValidator.CaseSpec fixture, string reason) => new() { ["id"] = fixture.Id, ["source"] = fixture.Source, ["sourceSha256"] = fixture.SourceSha256, ["expectationSha256"] = fixture.ExpectationSha256, ["status"] = "blocked", ["difference"] = reason };
    private static bool Succeeded(BoundedProcessResult? result) => result is not null && result.Succeeded && !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut && !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;
    private static JsonObject Evidence(BoundedProcessResult result) => new() { ["commandLine"] = result.StartedProcess.CommandLine, ["processId"] = result.StartedProcess.ProcessId, ["parentProcessId"] = result.StartedProcess.ParentProcessId, ["startedAtUtc"] = result.StartedProcess.StartedAt, ["exitCode"] = result.ExitCode, ["termination"] = result.Termination.ToString().ToLowerInvariant(), ["elapsedMilliseconds"] = result.Elapsed.TotalMilliseconds, ["standardOutput"] = result.StandardOutput, ["standardError"] = result.StandardError, ["cleanupIncomplete"] = result.ProcessTreeCleanupIncomplete };
    private static string? ResolveCandidateSha() => Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("CI_COMMIT_SHA");
    private static string? DeleteBounded(string path) { if (!Directory.Exists(path)) return null; Exception? last = null; Stopwatch clock = Stopwatch.StartNew(); for (int i = 0; i < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); i++) { try { Directory.Delete(path, true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { last = exception; } if (!Directory.Exists(path)) return null; Thread.Sleep(50); } return "Platform run directory cleanup failed: " + (last?.Message ?? "directory still exists"); }
}
