using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private static readonly Dictionary<string, (string Rustc, string RustSharp)> ExpectedCompileFailures =
        new Dictionary<string, (string Rustc, string RustSharp)>(StringComparer.Ordinal)
        {
            ["borrow-fail-mut-alias"] = ("E0499", "RSO1002"),
            ["borrow-fail-shared-write"] = ("E0506", "RSO1002"),
            ["borrow-fail-moved-mut-ref"] = ("E0382", "RSO1001"),
            ["borrow-fail-escape"] = ("E0597", "RSO1005"),
            ["drop-partial-move"] = ("E0509", "RSO1008"),
        };

    private sealed record ExpectedRuntimeFailure(string Output, string TerminationKind);

    private static readonly Dictionary<string, ExpectedRuntimeFailure> ExpectedRuntimeFailures =
        new(StringComparer.Ordinal)
        {
            ["drop-unwind-nested"] = new("body\ninner-drop\nouter-drop\n", "panic-unwind"),
            ["drop-double-panic"] = new("body\ndrop-start\n", "double-panic-abort"),
        };

    private static readonly Dictionary<string, string> ExpectedOutputs =
        new(StringComparer.Ordinal)
        {
            ["drop-aggregate-fields"] = "body\naggregate\nfirst-field\nsecond-field\n",
            ["drop-assignment-replacement"] = "drop\nbody\ndrop\n",
            ["drop-temporary-scope"] = "drop\nafter\n",
        };
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
        ProcessEvidence? rustcProbe = null, compilerProbe = null, sdkProbe = null;
        string? rustcVersion = null, compilerVersion = null, sdkVersion = null;
        try
        {
            if (suite is not null)
            {
                Directory.CreateDirectory(runDirectory);
                var runner = new BoundedProcessRunner();
                rustcProbe = ProcessEvidence.From(await runner.RunAsync(new BoundedProcessRequest("rustc", ["+" + RustVersion, "--version"], root, timeout), cancellation.Token).ConfigureAwait(false));
                compilerProbe = ProcessEvidence.From(await runner.RunAsync(new BoundedProcessRequest("dotnet", BuildRustSharpArguments(root, "--version"), root, timeout), cancellation.Token).ConfigureAwait(false));
                // A runtime invocation is independent of the repository's SDK
                // pin. Probe SDK selection outside that pin and record its cwd;
                // this does not pretend to prove the compiler's build SDK.
                sdkProbe = ProcessEvidence.From(await runner.RunAsync(new BoundedProcessRequest("dotnet", ["--version"], Path.GetTempPath(), timeout), cancellation.Token).ConfigureAwait(false));
                rustcVersion = FirstLine(rustcProbe.StandardOutput);
                compilerVersion = FirstLine(compilerProbe.StandardOutput);
                sdkVersion = sdkProbe.Succeeded ? FirstLine(sdkProbe.StandardOutput) : null;
                bool toolsAvailable = rustcProbe.Succeeded && rustcVersion == suite.Oracle && compilerProbe.Succeeded && sdkProbe.Succeeded;
                foreach (P1ExpandedSuiteValidator.CaseSpec fixture in suite.Cases)
                {
                    Console.WriteLine($"P1 differential v3: {cases.Count + 1}/{suite.Denominator} {fixture.Id}");
                    if (cancellation.IsCancellationRequested)
                    {
                        cases.Add(BlockedCase(fixture, "P1 differential v3 deadline expired before this case started."));
                        continue;
                    }
                    if (!toolsAvailable)
                    {
                        cases.Add(BlockedCase(fixture, "The exact frozen rustc oracle, compiled CLI, or SDK probe was unavailable."));
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or OperationCanceledException)
        {
            harnessError = Trim(exception.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            cleanupDiagnostic = TryDeleteDirectory(runDirectory);
        }

        if (suite is not null)
        {
            for (int missing = cases.Count; missing < suite.Denominator; missing++)
                cases.Add(BlockedCase(suite.Cases[cases.Count], harnessError ?? "P1 differential v3 case did not start."));
        }
        harnessClock.Stop();
        int denominator = suite?.Denominator ?? 0;
        int passed = cases.Count(static item => item["status"]?.GetValue<string>() == "passed");
        int failed = cases.Count(static item => item["status"]?.GetValue<string>() == "failed");
        int blocked = cases.Count(static item => item["status"]?.GetValue<string>() == "blocked");
        int placeholderCases = suite?.Cases.Count(static item => IsPlaceholderCase(item.Id)) ?? 0;
        int semanticCases = denominator - placeholderCases;
        bool semanticClosureEligible = suite is not null && placeholderCases == 0;
        int borrowCaseCount = suite?.Cases.Count(static item => item.Id.StartsWith("borrow-", StringComparison.Ordinal)) ?? 0;
        int borrowPlaceholderCases = suite?.Cases.Count(static item =>
            item.Id.StartsWith("borrow-", StringComparison.Ordinal) && IsPlaceholderCase(item.Id)) ?? 0;
        bool borrowSemanticClosureEligible = suite is not null && borrowCaseCount > 0 && borrowPlaceholderCases == 0;
        string status = suite is null || harnessError is not null || cleanupDiagnostic is not null || cancellation.IsCancellationRequested || blocked > 0
            ? "blocked" : failed > 0 ? "failed" : passed == denominator ? "passed" : "blocked";
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["evidenceKind"] = "p1-source-borrow-drop-differential",
            ["profile"] = ProfileName,
            ["candidateSha"] = ResolveCandidateSha(),
            ["backend"] = suite?.Backend ?? "",
            ["compilerSha256"] = ComputeSha256IfPresent(CliPath(root)),
            ["declaredCompilerSha256"] = suite?.CompilerSha256 ?? "",
            ["semanticClosureEligible"] = semanticClosureEligible,
            ["manifest"] = new JsonObject
            {
                ["path"] = Path.GetRelativePath(root, manifestPath).Replace(Path.DirectorySeparatorChar, '/'),
                ["sha256"] = manifestBytes is null ? "" : Convert.ToHexString(SHA256.HashData(manifestBytes)),
                ["declaredSha256"] = suite?.ManifestSha256 ?? "",
                ["version"] = expanded?.Version ?? 0,
                ["denominator"] = denominator,
                ["validated"] = suite is not null,
            },
            ["compiler"] = new JsonObject
            {
                ["sha256"] = ComputeSha256IfPresent(CliPath(root)),
                ["declaredSha256"] = suite?.CompilerSha256 ?? "",
                ["cliPath"] = CliPath(root),
                ["profile"] = CompilerProfile,
            },
            ["platform"] = new JsonObject
            {
                ["name"] = OperatingSystem.IsWindows() ? "windows-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unknown",
                ["runtimeIdentifier"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                ["backend"] = suite?.Backend ?? "p1-differential-v3",
                ["oracle"] = rustcVersion,
                ["nativeHost"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 &&
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64,
                ["operatingSystem"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["osArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            },
            ["oracle"] = new JsonObject { ["version"] = rustcVersion, ["requestedVersion"] = suite?.Oracle, ["probe"] = rustcProbe?.ToJson() },
            ["toolProbes"] = new JsonObject { ["compiler"] = compilerProbe?.ToJson(), ["sdk"] = sdkProbe?.ToJson(), ["sdkWorkingDirectory"] = Path.GetTempPath() },
            ["semanticClosure"] = new JsonObject
            {
                ["eligible"] = semanticClosureEligible,
                ["eligibleCases"] = semanticCases,
                ["placeholderCases"] = placeholderCases,
                ["reason"] = semanticClosureEligible
                    ? null
                    : "The frozen v3 suite contains placeholder sources; passing process outcomes do not close their named ownership/Drop semantics.",
            },
            ["borrowSemanticClosureEligible"] = borrowSemanticClosureEligible,
            ["borrowSemanticClosure"] = new JsonObject
            {
                ["eligible"] = borrowSemanticClosureEligible,
                ["eligibleCases"] = borrowCaseCount - borrowPlaceholderCases,
                ["placeholderCases"] = borrowPlaceholderCases,
                ["reason"] = borrowSemanticClosureEligible
                    ? null
                    : "The frozen borrow subset contains placeholder sources; passing process outcomes do not close its named ownership semantics.",
            },
            ["toolVersions"] = new JsonObject
            {
                ["dotnet"] = Environment.Version.ToString(),
                ["sdkVersion"] = sdkVersion,
                ["rustc"] = rustcVersion,
                ["compiler"] = compilerVersion,
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
                ["caseTimeoutSeconds"] = timeout.TotalSeconds, ["deadlineSeconds"] = deadline.TotalSeconds,
                ["maximumCases"] = MaximumCases,
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
        bool expectedCompileFailure = ExpectedCompileFailures.TryGetValue(fixture.Id, out var expectedDiagnostics);
        bool expectedRuntimeFailure = ExpectedRuntimeFailures.TryGetValue(fixture.Id, out ExpectedRuntimeFailure? runtimeFailure);
        bool passed = expectedCompileFailure
            ? !oracleCompiles && ContainsDiagnostic(rustcCompile, expectedDiagnostics.Rustc) &&
              ContainsDiagnostic(rustSharpCheck, expectedDiagnostics.RustSharp) &&
              rustSharpCompile is not null && ContainsDiagnostic(rustSharpCompile, expectedDiagnostics.RustSharp)
            : expectedRuntimeFailure
                ? oracleCompiles && IsRuntimeFailure(rustcRun) &&
                  rustSharpCheck is { Succeeded: true } && rustSharpCompile is { Succeeded: true } &&
                  IsRuntimeFailure(rustSharpRun) &&
                  Normalize(rustcRun!.StandardOutput) == Normalize(runtimeFailure!.Output) &&
                  Normalize(rustSharpRun!.StandardOutput) == Normalize(runtimeFailure.Output)
                : oracleCompiles
                    ? rustcRun is { Succeeded: true } && rustSharpCheck is { Succeeded: true } && rustSharpCompile is { Succeeded: true } && rustSharpRun is { Succeeded: true } && Normalize(rustcRun.StandardOutput) == Normalize(rustSharpRun.StandardOutput) &&
                      (!ExpectedOutputs.TryGetValue(fixture.Id, out string? expectedOutput) || Normalize(rustcRun.StandardOutput) == Normalize(expectedOutput))
                    : false;
        string? difference = passed ? null : expectedCompileFailure
            ? $"Expected rustc {expectedDiagnostics.Rustc} and RustSharp {expectedDiagnostics.RustSharp} ownership diagnostics; observed compile/check output did not match."
            : expectedRuntimeFailure
                ? $"Expected matching {runtimeFailure!.TerminationKind} failures and output trace; observed compile/run evidence differed."
                : "rustc and RustSharp emitted different compile/run outcomes.";
        bool incompleteProcess = new[] { rustcCompile, rustcRun, rustSharpCheck, rustSharpCompile, rustSharpRun }
            .Any(static evidence => evidence is not null &&
                (evidence.Termination is "timedout" or "cancelled" || evidence.OutputTruncated || evidence.OutputReadTimedOut ||
                 evidence.OutputDrainTimedOut || evidence.OutputReadLimitReached || evidence.CleanupIncomplete));
        string? failureKind = passed ? null : incompleteProcess ? "incomplete-process-evidence" :
            rustSharpCheck.StandardError.Contains("RSC0009", StringComparison.Ordinal) ? "unsupported-lowering" :
            expectedRuntimeFailure ? "runtime-difference" : "semantic-difference";
        var result = new JsonObject
        {
            ["id"] = fixture.Id, ["source"] = fixture.Source, ["sourceSha256"] = fixture.SourceSha256,
            ["expectationSha256"] = fixture.ExpectationSha256, ["status"] = incompleteProcess ? "blocked" : passed ? "passed" : "failed", ["difference"] = difference,
            ["failureKind"] = failureKind,
            ["expectedOutcome"] = expectedCompileFailure ? "compile-fail" : expectedRuntimeFailure ? "run-fail" : "run-pass",
            ["semanticCoverage"] = "ownership-drop-scenario",
            ["semanticClosureEligible"] = true,
            ["expectedOutput"] = ExpectedOutputs.TryGetValue(fixture.Id, out string? output) ? output : runtimeFailure?.Output,
            ["expectedTermination"] = runtimeFailure?.TerminationKind,
        };
        if (expectedCompileFailure)
        {
            result["expectedRustcDiagnostic"] = expectedDiagnostics.Rustc;
            result["expectedRustSharpDiagnostic"] = expectedDiagnostics.RustSharp;
        }
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
        ["expectedOutcome"] = ExpectedCompileFailures.ContainsKey(fixture.Id) ? "compile-fail" : ExpectedRuntimeFailures.ContainsKey(fixture.Id) ? "run-fail" : "run-pass",
        ["semanticCoverage"] = "ownership-drop-scenario",
        ["semanticClosureEligible"] = true,
        ["expectedOutput"] = ExpectedOutputs.TryGetValue(fixture.Id, out string? output) ? output : ExpectedRuntimeFailures.TryGetValue(fixture.Id, out ExpectedRuntimeFailure? runtime) ? runtime.Output : null,
        ["expectedTermination"] = ExpectedRuntimeFailures.TryGetValue(fixture.Id, out ExpectedRuntimeFailure? expectedRuntime) ? expectedRuntime.TerminationKind : null,
    };

    private static bool ContainsDiagnostic(ProcessEvidence evidence, string diagnostic) =>
        evidence.Termination == "exited" && evidence.ExitCode == 1 && evidence.ProcessId > 0 &&
        !evidence.OutputTruncated && !evidence.OutputReadTimedOut && !evidence.OutputDrainTimedOut &&
        !evidence.OutputReadLimitReached && !evidence.CleanupIncomplete &&
        !evidence.StandardError.Contains("RSC0009", StringComparison.Ordinal) &&
        !evidence.StandardOutput.Contains("RSC0009", StringComparison.Ordinal) &&
        HasDiagnosticErrorLine(evidence.StandardError, diagnostic);

    private static bool HasDiagnosticErrorLine(string output, string diagnostic)
    {
        const int maximumLines = 8192;
        var clock = Stopwatch.StartNew();
        using var reader = new StringReader(output);
        // Captured output already has the BoundedProcessRunner byte bound.
        // Keep diagnostic parsing independently bounded too, and accept only
        // a real rustc error header or the CLI's source-span error format.
        for (int lineIndex = 0; lineIndex < maximumLines && clock.Elapsed < TimeSpan.FromMilliseconds(250); lineIndex++)
        {
            string? rawLine = reader.ReadLine();
            if (rawLine is null) return false;
            string line = rawLine.TrimStart();
            if (line.StartsWith("error[" + diagnostic + "]:", StringComparison.Ordinal) ||
                line.StartsWith("error " + diagnostic + ":", StringComparison.Ordinal)) return true;

            string marker = "]: error " + diagnostic + ":";
            int markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) continue;
            int spanStart = line.LastIndexOf('[', markerIndex);
            if (spanStart <= 0 || !Path.IsPathRooted(line.AsSpan(0, spanStart))) continue;
            ReadOnlySpan<char> span = line.AsSpan(spanStart + 1, markerIndex - spanStart - 1);
            int separator = span.IndexOf("..".AsSpan(), StringComparison.Ordinal);
            if (separator <= 0 || separator + 2 >= span.Length) continue;
            if (int.TryParse(span[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out int start) &&
                int.TryParse(span[(separator + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int end) &&
                start >= 0 && end >= start) return true;
        }
        return false;
    }

    internal static bool MatchesCompileFailure(BoundedProcessResult evidence, string diagnostic) => ContainsDiagnostic(ProcessEvidence.From(evidence), diagnostic);
    internal static bool IsPlaceholderCase(string id) => false;

    internal static bool MatchesRuntimeFailure(BoundedProcessResult evidence, string expectedOutput) =>
        IsRuntimeFailure(ProcessEvidence.From(evidence)) &&
        Normalize(evidence.StandardOutput) == Normalize(expectedOutput);

    private static bool IsRuntimeFailure(ProcessEvidence? evidence) => evidence is not null &&
        evidence.Termination == "exited" && evidence.ExitCode is not null and not 0 &&
        !evidence.OutputTruncated && !evidence.OutputReadTimedOut && !evidence.OutputDrainTimedOut &&
        !evidence.OutputReadLimitReached && !evidence.CleanupIncomplete;

    private static bool Succeeded(BoundedProcessResult result) => result.Succeeded && !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut && !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;

    private static List<string> BuildRustSharpArguments(string root, params string[] arguments)
    {
        var result = new List<string> { CliPath(root) };
        result.AddRange(arguments);
        return result;
    }

    private static string Normalize(string? value) => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string CliPath(string root) => Path.Combine(root, "src", "RustSharp.Cli", "bin", "Release", "net10.0", "rsc.dll");
    private static string? FirstLine(string value) => Normalize(value).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    private static string ComputeSha256IfPresent(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
    private static string? ResolveCandidateSha() =>
        Environment.GetEnvironmentVariable("GITHUB_SHA") ??
        Environment.GetEnvironmentVariable("CI_COMMIT_SHA");
    private static string Trim(string value) => value.Length <= 512 ? value : value[..512] + "...";

    private sealed record ProcessEvidence(
        string CommandLine, int ProcessId, int ParentProcessId, DateTimeOffset StartedAtUtc,
        int? ExitCode, string Termination, double ElapsedMilliseconds, string StandardOutput,
        string StandardError, bool OutputTruncated, bool OutputReadTimedOut, bool OutputDrainTimedOut,
        bool OutputReadLimitReached, bool CleanupAttempted, bool CleanupIncomplete, string? Diagnostic)
    {
        public bool Succeeded => Termination == "exited" && ExitCode == 0 && !OutputTruncated && !OutputReadTimedOut && !OutputDrainTimedOut && !OutputReadLimitReached && !CleanupIncomplete;
        public static ProcessEvidence From(BoundedProcessResult result) => new(result.StartedProcess.CommandLine, result.StartedProcess.ProcessId, result.StartedProcess.ParentProcessId, result.StartedProcess.StartedAt, result.ExitCode, result.Termination.ToString().ToLowerInvariant(), result.Elapsed.TotalMilliseconds, result.StandardOutput, result.StandardError, result.OutputTruncated, result.OutputReadTimedOut, result.OutputDrainTimedOut, result.OutputReadLimitReached, result.ProcessTreeCleanupAttempted, result.ProcessTreeCleanupIncomplete, result.OutputDiagnostic ?? result.ProcessTreeCleanupDiagnostic);
        public JsonObject ToJson() => new()
        {
            ["commandLine"] = CommandLine, ["processId"] = ProcessId, ["parentProcessId"] = ParentProcessId,
            ["startedAtUtc"] = StartedAtUtc, ["exitCode"] = ExitCode, ["termination"] = Termination,
            ["elapsedMilliseconds"] = ElapsedMilliseconds, ["standardOutput"] = StandardOutput, ["standardError"] = StandardError,
            ["outputTruncated"] = OutputTruncated, ["outputReadTimedOut"] = OutputReadTimedOut,
            ["outputDrainTimedOut"] = OutputDrainTimedOut, ["outputReadLimitReached"] = OutputReadLimitReached,
            ["cleanupAttempted"] = CleanupAttempted, ["cleanupIncomplete"] = CleanupIncomplete, ["diagnostic"] = Diagnostic,
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
