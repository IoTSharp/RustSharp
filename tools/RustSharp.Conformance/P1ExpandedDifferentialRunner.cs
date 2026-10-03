using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>
/// Runs the frozen 32-case <c>p1-differential-v3</c> source suite.  The suite
/// deliberately uses the expanded manifest rather than discovering fixtures at
/// runtime, and every external process is executed by <see cref="BoundedProcessRunner"/>.
/// </summary>
internal static class P1ExpandedDifferentialRunner
{
    internal const string ProfileName = "p1-differential-v3";
    internal const string ManifestFileName = P1ExpandedSuiteValidator.ManifestFileName;
    private const string CompilerProfile = "safe-core-mir-p1-v2";
    private const string RustVersion = "1.98.0";
    private const string Edition = "2024";
    private const int MaximumCases = P1ExpandedSuiteValidator.DifferentialDenominator;
    private const int MaximumTimeoutSeconds = 300;
    private const int MaximumDeadlineSeconds = 900;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> RunAsync(
        string repositoryRoot,
        string reportPath,
        TimeSpan timeout,
        TimeSpan deadline,
        DateTimeOffset startedAtUtc,
        Stopwatch harnessClock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(MaximumDeadlineSeconds))
            throw new ArgumentOutOfRangeException(nameof(deadline));

        string root = Path.GetFullPath(repositoryRoot);
        string fullReport = Path.GetFullPath(reportPath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReport)!);
        string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", ManifestFileName);
        P1ExpandedSuiteValidator.ExpandedManifest? expanded = null;
        P1ExpandedSuiteValidator.SuiteSpec? suite = null;
        string? harnessError = null;
        byte[]? manifestBytes = null;
        try
        {
            manifestBytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            expanded = P1ExpandedSuiteValidator.ParseManifest(System.Text.Encoding.UTF8.GetString(manifestBytes), root);
            suite = expanded.Suites.Single(static item => item.Profile == ProfileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            harnessError = Trim(exception.Message);
        }

        using var cancellation = new CancellationTokenSource(deadline);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var cases = new List<JsonObject>(MaximumCases);
        string runDirectory = Path.Combine(Path.GetDirectoryName(fullReport)!, ".run-p1-differential-v3-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        string? cleanupDiagnostic = null;
        try
        {
            if (suite is not null)
            {
                Directory.CreateDirectory(runDirectory);
                var runner = new BoundedProcessRunner();
                foreach (P1ExpandedSuiteValidator.CaseSpec fixture in suite.Cases)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        cases.Add(BlockedCase(fixture, "P1 differential v3 deadline expired before this case started."));
                        continue;
                    }
                    try
                    {
                        cases.Add(await RunCaseAsync(runner, root, runDirectory, fixture, timeout, cancellation.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        cases.Add(BlockedCase(fixture, "P1 differential v3 case deadline or cancellation prevented complete evidence."));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception)
                    {
                        cases.Add(BlockedCase(fixture, Trim(exception.Message)));
                    }
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            cleanupDiagnostic = TryDeleteDirectory(runDirectory);
        }

        if (suite is not null)
        {
            while (cases.Count < suite.Denominator)
                cases.Add(BlockedCase(suite.Cases[cases.Count], harnessError ?? "P1 differential v3 case did not start."));
        }
        harnessClock.Stop();
        int denominator = suite?.Denominator ?? 0;
        int passed = cases.Count(static item => item["status"]?.GetValue<string>() == "passed");
        int failed = cases.Count(static item => item["status"]?.GetValue<string>() == "failed");
        int blocked = cases.Count(static item => item["status"]?.GetValue<string>() == "blocked");
        string status = suite is null || harnessError is not null || cleanupDiagnostic is not null || cancellation.IsCancellationRequested || blocked > 0
            ? "blocked" : failed > 0 ? "failed" : passed == denominator ? "passed" : "blocked";
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["evidenceKind"] = "p1-source-borrow-drop-differential",
            ["profile"] = ProfileName,
            ["candidateSha"] = ResolveCandidateSha(),
            ["backend"] = suite?.Backend ?? "",
            ["compilerSha256"] = suite?.CompilerSha256 ?? "",
            ["manifest"] = new JsonObject
            {
                ["path"] = Path.GetRelativePath(root, manifestPath).Replace(Path.DirectorySeparatorChar, '/'),
                ["sha256"] = suite?.ManifestSha256 ?? (manifestBytes is null ? "" : Convert.ToHexString(SHA256.HashData(manifestBytes))),
                ["denominator"] = denominator,
                ["validated"] = suite is not null,
            },
            ["compiler"] = new JsonObject
            {
                // The manifest freezes the candidate identity.  The observed hash is
                // retained separately so a stale candidate cannot be mistaken for it.
                ["sha256"] = suite?.CompilerSha256 ?? "",
                ["observedSha256"] = ComputeSha256IfPresent(Path.Combine(root, "src", "RustSharp.Cli", "bin", "Release", "net10.0", "rsc.dll")),
                ["profile"] = CompilerProfile,
            },
            ["platform"] = new JsonObject
            {
                ["name"] = OperatingSystem.IsWindows() ? "windows-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unknown",
                ["runtimeIdentifier"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                ["backend"] = suite?.Backend ?? "p1-differential-v3",
            },
            ["oracle"] = new JsonObject { ["version"] = "rustc 1.98.0 (required)" },
            ["toolVersions"] = new JsonObject
            {
                ["dotnet"] = Environment.Version.ToString(),
                ["sdkVersion"] = Environment.GetEnvironmentVariable("DOTNET_SDK_VERSION") ?? "unknown",
                ["rustc"] = "rustc 1.98.0 (required)",
            },
            ["summary"] = new JsonObject
            {
                ["status"] = status, ["exitCode"] = status == "passed" ? 0 : status == "failed" ? 1 : 2,
                ["denominator"] = denominator, ["executed"] = passed + failed,
                ["passed"] = passed, ["failed"] = failed, ["blocked"] = blocked, ["skipped"] = 0,
            },
            ["cases"] = new JsonArray(cases.Select(static item => (JsonNode)item).ToArray()),
            ["execution"] = new JsonObject
            {
                ["startedAtUtc"] = startedAtUtc, ["finishedAtUtc"] = DateTimeOffset.UtcNow,
                ["elapsedMilliseconds"] = harnessClock.Elapsed.TotalMilliseconds, ["deadlineExpired"] = cancellation.IsCancellationRequested,
            },
            ["cleanup"] = new JsonObject { ["completed"] = cleanupDiagnostic is null, ["diagnostic"] = cleanupDiagnostic },
            ["harnessError"] = harnessError,
        };
        string temporary = fullReport + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, report.ToJsonString(JsonOptions)).ConfigureAwait(false);
            File.Move(temporary, fullReport, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine(report.ToJsonString(JsonOptions));
        return status == "passed" ? 0 : status == "failed" ? 1 : 2;
    }

    private static async Task<JsonObject> RunCaseAsync(
        BoundedProcessRunner runner,
        string root,
        string runDirectory,
        P1ExpandedSuiteValidator.CaseSpec fixture,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string source = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", fixture.Source);
        string directory = Path.Combine(runDirectory, fixture.Id);
        Directory.CreateDirectory(directory);
        string oracleOutput = Path.Combine(directory, OperatingSystem.IsWindows() ? "oracle.exe" : "oracle");
        string rustSharpOutput = Path.Combine(directory, "rustsharp.dll");
        ProcessEvidence? rustcCompile = null, rustcRun = null, rustSharpCheck = null, rustSharpCompile = null, rustSharpRun = null;
        BoundedProcessResult oracle = await runner.RunAsync(new BoundedProcessRequest("rustc", ["+" + RustVersion, source, "--edition", Edition, "-C", "overflow-checks=yes", "-o", oracleOutput], root, timeout), cancellationToken).ConfigureAwait(false);
        rustcCompile = ProcessEvidence.From(oracle);
        bool oracleCompiles = Succeeded(oracle);
        if (oracleCompiles)
        {
            BoundedProcessResult run = await runner.RunAsync(new BoundedProcessRequest(oracleOutput, [], directory, timeout), cancellationToken).ConfigureAwait(false);
            rustcRun = ProcessEvidence.From(run);
        }
        BoundedProcessResult check = await runner.RunAsync(new BoundedProcessRequest("dotnet", BuildRustSharpArguments(root, "check", source, "--profile", CompilerProfile), root, timeout), cancellationToken).ConfigureAwait(false);
        rustSharpCheck = ProcessEvidence.From(check);
        if (Succeeded(check) || !oracleCompiles)
        {
            BoundedProcessResult compile = await runner.RunAsync(new BoundedProcessRequest("dotnet", BuildRustSharpArguments(root, "compile", source, "--output", rustSharpOutput, "--profile", CompilerProfile), root, timeout), cancellationToken).ConfigureAwait(false);
            rustSharpCompile = ProcessEvidence.From(compile);
            if (Succeeded(compile))
            {
                BoundedProcessResult run = await runner.RunAsync(new BoundedProcessRequest("dotnet", [rustSharpOutput], directory, timeout), cancellationToken).ConfigureAwait(false);
                rustSharpRun = ProcessEvidence.From(run);
            }
        }
        bool passed = oracleCompiles
            ? rustcRun is { Succeeded: true } && rustSharpCheck is { Succeeded: true } && rustSharpCompile is { Succeeded: true } && rustSharpRun is { Succeeded: true } && Normalize(rustcRun.StandardOutput) == Normalize(rustSharpRun.StandardOutput)
            : rustcCompile is { ExitCode: 1, Termination: "exited" } && rustSharpCheck is { ExitCode: 1, Termination: "exited" } && rustSharpCompile is { ExitCode: 1, Termination: "exited" };
        string? difference = passed ? null : "rustc and RustSharp emitted different compile/run outcomes.";
        var result = new JsonObject
        {
            ["id"] = fixture.Id, ["source"] = fixture.Source, ["sourceSha256"] = fixture.SourceSha256,
            ["expectationSha256"] = fixture.ExpectationSha256, ["status"] = passed ? "passed" : "failed", ["difference"] = difference,
        };
        result["rustcCompile"] = rustcCompile?.ToJson();
        result["rustcRun"] = rustcRun?.ToJson();
        result["rustSharpCheck"] = rustSharpCheck?.ToJson();
        result["rustSharpCompile"] = rustSharpCompile?.ToJson();
        result["rustSharpRun"] = rustSharpRun?.ToJson();
        return result;
    }

    private static JsonObject BlockedCase(P1ExpandedSuiteValidator.CaseSpec fixture, string reason) => new()
    {
        ["id"] = fixture.Id, ["source"] = fixture.Source, ["sourceSha256"] = fixture.SourceSha256,
        ["expectationSha256"] = fixture.ExpectationSha256, ["status"] = "blocked", ["difference"] = reason,
    };

    private static bool Succeeded(BoundedProcessResult result) => result.Succeeded && !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut && !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;

    private static List<string> BuildRustSharpArguments(string root, params string[] arguments)
    {
        string cli = Path.Combine(root, "src", "RustSharp.Cli", "bin", "Release", "net10.0", "rsc.dll");
        var result = new List<string> { cli };
        result.AddRange(arguments);
        return result;
    }

    private static string Normalize(string? value) => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string ComputeSha256IfPresent(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
    private static string? ResolveCandidateSha() =>
        Environment.GetEnvironmentVariable("GITHUB_SHA") ??
        Environment.GetEnvironmentVariable("CI_COMMIT_SHA");
    private static string Trim(string value) => value.Length <= 512 ? value : value[..512] + "...";

    private sealed record ProcessEvidence(
        string CommandLine, int ProcessId, int ParentProcessId, DateTimeOffset StartedAtUtc,
        int? ExitCode, string Termination, double ElapsedMilliseconds, string StandardOutput,
        string StandardError, bool OutputTruncated, bool OutputReadTimedOut, bool OutputDrainTimedOut,
        bool OutputReadLimitReached, bool CleanupIncomplete, string? Diagnostic)
    {
        public bool Succeeded => Termination == "exited" && ExitCode == 0 && !OutputTruncated && !OutputReadTimedOut && !OutputDrainTimedOut && !OutputReadLimitReached && !CleanupIncomplete;
        public static ProcessEvidence From(BoundedProcessResult result) => new(result.StartedProcess.CommandLine, result.StartedProcess.ProcessId, result.StartedProcess.ParentProcessId, result.StartedProcess.StartedAt, result.ExitCode, result.Termination.ToString().ToLowerInvariant(), result.Elapsed.TotalMilliseconds, result.StandardOutput, result.StandardError, result.OutputTruncated, result.OutputReadTimedOut, result.OutputDrainTimedOut, result.OutputReadLimitReached, result.ProcessTreeCleanupIncomplete, result.OutputDiagnostic ?? result.ProcessTreeCleanupDiagnostic);
        public JsonObject ToJson() => new()
        {
            ["commandLine"] = CommandLine, ["processId"] = ProcessId, ["parentProcessId"] = ParentProcessId,
            ["startedAtUtc"] = StartedAtUtc, ["exitCode"] = ExitCode, ["termination"] = Termination,
            ["elapsedMilliseconds"] = ElapsedMilliseconds, ["standardOutput"] = StandardOutput, ["standardError"] = StandardError,
            ["outputTruncated"] = OutputTruncated, ["outputReadTimedOut"] = OutputReadTimedOut,
            ["outputDrainTimedOut"] = OutputDrainTimedOut, ["outputReadLimitReached"] = OutputReadLimitReached,
            ["cleanupIncomplete"] = CleanupIncomplete, ["diagnostic"] = Diagnostic,
        };
    }

    private static string? TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return null;
        Exception? last = null;
        Stopwatch clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { Directory.Delete(path, true); } catch (IOException exception) { last = exception; } catch (UnauthorizedAccessException exception) { last = exception; }
            if (!Directory.Exists(path)) return null;
            Thread.Sleep(50);
        }
        return "P1 differential run directory cleanup failed: " + (last?.Message ?? "directory still exists");
    }
}
