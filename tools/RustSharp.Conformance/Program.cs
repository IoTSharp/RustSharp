using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

internal static class Program
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 300;
    private const int DefaultDeadlineSeconds = 180;
    private const int MaximumDeadlineSeconds = 900;
    private const int MaximumArgumentCount = 32;
    private const int MaximumCleanupAttempts = 40;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumCases = 64;
    private const string ProfileName = "vertical-slice-v1";
    private const string OracleName = "rustc-1.98";

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = ParseOptions(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"conformance: {exception.Message}");
            return 2;
        }

        string repositoryRoot = FindRepositoryRoot();
        string reportPath = options.ReportPath is null
            ? Path.Combine(repositoryRoot, "artifacts", "conformance", options.Profile + ".json")
            : Path.GetFullPath(options.ReportPath, repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string runDirectory = Path.Combine(
            Path.GetDirectoryName(reportPath)!,
            $".run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        using var deadline = new CancellationTokenSource(options.Deadline);
        try
        {
            var runner = new BoundedProcessRunner();
            ProcessResult rustcVersion = await RunAsync(
                runner,
                "rustc",
                ["--version"],
                repositoryRoot,
                options.Timeout,
                deadline.Token).ConfigureAwait(false);
            string? rustcVersionText = FirstLine(rustcVersion.StandardOutput);
            bool oracleAvailable = rustcVersion.Succeeded && IsRustc198(rustcVersionText);
            string? blockedReason = oracleAvailable
                ? null
                : rustcVersion.Succeeded
                    ? $"Requested oracle rustc 1.98.x, found '{rustcVersionText ?? "unknown"}'."
                    : $"Could not execute rustc --version (termination={rustcVersion.Termination}, exitCode={rustcVersion.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}).";

            string? rustSharpVersion = await ReadRustSharpVersionAsync(runner, repositoryRoot, options.Timeout, deadline.Token)
                .ConfigureAwait(false);
            var cases = new List<CaseReport>(FixtureCatalog.Length);
            if (oracleAvailable)
            {
                foreach (Fixture fixture in FixtureCatalog)
                {
                    if (deadline.IsCancellationRequested)
                    {
                        cases.Add(CaseReport.Skipped(fixture, "Harness overall deadline expired before this case started."));
                    }
                    else
                    {
                        cases.Add(
                            await RunCaseAsync(
                                runner,
                                repositoryRoot,
                                runDirectory,
                                fixture,
                                options.Timeout,
                                deadline.Token).ConfigureAwait(false));
                    }
                }
            }
            else
            {
                foreach (Fixture fixture in FixtureCatalog)
                {
                    cases.Add(CaseReport.Skipped(fixture, blockedReason!));
                }
            }

            int passed = cases.Count(static item => item.Status == "passed");
            int failed = cases.Count(static item => item.Status == "failed");
            int skipped = cases.Count(static item => item.Status == "skipped");
            // A report with skipped cases is incomplete even when the oracle
            // itself is available (for example, an overall deadline expired).
            // Keep it blocked instead of allowing a partial run to look green.
            string status = !oracleAvailable
                ? "blocked"
                : skipped > 0
                    ? "blocked"
                    : failed == 0
                        ? "passed"
                        : "failed";
            var report = new ConformanceReport(
                1,
                DateTimeOffset.UtcNow,
                options.Profile,
                new OracleReport(OracleName, "rustc", rustcVersionText, oracleAvailable, blockedReason, rustcVersion.ToEvidence()),
                new ToolReport("rustsharp", "dotnet run --project src/RustSharp.Cli -c Release", rustSharpVersion),
                new LimitsReport(options.Timeout.TotalSeconds, options.Deadline.TotalSeconds, MaximumCases, BoundedProcessRunner.MaximumTotalOutputBytes),
                new SummaryReport(status, FixtureCatalog.Length, cases.Count - skipped, passed, failed, skipped),
                cases,
                null);
            string? cleanupDiagnostic = TryDeleteDirectory(runDirectory);
            report = report with { RunDirectoryCleanupDiagnostic = cleanupDiagnostic };
            await WriteReportAsync(reportPath, report).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return status switch
            {
                "failed" => 1,
                "blocked" => 2,
                _ => 0,
            };
        }
        finally
        {
            _ = TryDeleteDirectory(runDirectory);
        }
    }

    private static readonly Fixture[] FixtureCatalog =
    [
        new("hello", "hello.rs", true, "Hello from Rust#\n"),
        new("two-prints", "two_prints.rs", true, "first\nsecond\n"),
        new("malformed", "malformed.rs", false, null),
        new("syntax-error", "unsupported.rs", false, null),
    ];

    private static async Task<CaseReport> RunCaseAsync(
        BoundedProcessRunner runner,
        string repositoryRoot,
        string runDirectory,
        Fixture fixture,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string sourcePath = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures", fixture.FileName);
        string caseDirectory = Path.Combine(runDirectory, fixture.Id);
        Directory.CreateDirectory(caseDirectory);
        string rustcOutput = Path.Combine(caseDirectory, "oracle");
        ProcessResult rustcCompile = await RunAsync(
            runner,
            "rustc",
            [sourcePath, "--edition", "2024", "-o", rustcOutput],
            repositoryRoot,
            timeout,
            cancellationToken).ConfigureAwait(false);
        ProcessResult rustSharpCheck = await RunAsync(
            runner,
            "dotnet",
            ["run", "--project", Path.Combine(repositoryRoot, "src", "RustSharp.Cli"), "-c", "Release", "--", "check", sourcePath],
            repositoryRoot,
            timeout,
            cancellationToken).ConfigureAwait(false);

        ProcessResult? rustcRun = null;
        ProcessResult? rustSharpRun = null;
        if (fixture.ExpectedSuccess && rustcCompile.Succeeded && rustSharpCheck.Succeeded)
        {
            rustcRun = await RunAsync(runner, rustcOutput, [], repositoryRoot, timeout, cancellationToken).ConfigureAwait(false);
            string managedOutput = Path.Combine(caseDirectory, "rustsharp.dll");
            ProcessResult compile = await RunAsync(
                runner,
                "dotnet",
                ["run", "--project", Path.Combine(repositoryRoot, "src", "RustSharp.Cli"), "-c", "Release", "--", "compile", sourcePath, "--output", managedOutput],
                repositoryRoot,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (compile.Succeeded)
            {
                rustSharpRun = await RunAsync(runner, "dotnet", [managedOutput], caseDirectory, timeout, cancellationToken).ConfigureAwait(false);
            }
        }

        bool outcomeMatches = fixture.ExpectedSuccess
            ? rustcCompile.Succeeded && rustSharpCheck.Succeeded && rustcRun?.Succeeded == true && rustSharpRun?.Succeeded == true && NormalizeOutput(rustcRun.StandardOutput) == NormalizeOutput(rustSharpRun.StandardOutput) && NormalizeOutput(rustSharpRun.StandardOutput) == NormalizeOutput(fixture.ExpectedOutput)
            : !rustcCompile.Succeeded && !rustSharpCheck.Succeeded;
        string? difference = outcomeMatches ? null : fixture.ExpectedSuccess
            ? "Compile/check/run outcome or stdout differed from the fixture expectation."
            : "Compile outcome differed between rustc and RustSharp.";
        return new CaseReport(
            fixture.Id,
            fixture.FileName,
            fixture.ExpectedSuccess ? "run-pass" : "compile-fail",
            outcomeMatches ? "passed" : "failed",
            difference,
            rustcCompile.ToEvidence(),
            rustcRun?.ToEvidence(),
            rustSharpCheck.ToEvidence(),
            rustSharpRun?.ToEvidence());
    }

    private static async Task<ProcessResult> RunAsync(BoundedProcessRunner runner, string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string commandLine = string.Join(
            " ",
            new[] { fileName }.Concat(arguments).Select(static value => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value));
        try
        {
            return ProcessResult.From(
                await runner.RunAsync(new BoundedProcessRequest(fileName, arguments, workingDirectory, timeout), cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProcessResult(commandLine, 0, null, "cancelled", TimeSpan.Zero, string.Empty, "Process start cancelled by the harness deadline.", false, false, false, false, 0, null, false, null, false, null);
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new ProcessResult(
                commandLine,
                0,
                null,
                "error",
                TimeSpan.Zero,
                string.Empty,
                $"Process start failed: {exception.Message}",
                false,
                false,
                false,
                false,
                0,
                null,
                false,
                null,
                false,
                null);
        }
    }

    private static async Task<string?> ReadRustSharpVersionAsync(BoundedProcessRunner runner, string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(runner, "dotnet", ["run", "--project", Path.Combine(root, "src", "RustSharp.Cli"), "-c", "Release", "--", "--version"], root, timeout, cancellationToken).ConfigureAwait(false);
        return FindRustSharpVersion(result.StandardOutput);
    }

    private static Options ParseOptions(string[] args)
    {
        if (args.Length > MaximumArgumentCount)
        {
            throw new ArgumentException($"The conformance harness accepts at most {MaximumArgumentCount} arguments.");
        }

        string profile = ProfileName;
        string oracle = OracleName;
        string? report = null;
        int timeoutSeconds = DefaultTimeoutSeconds;
        int deadlineSeconds = DefaultDeadlineSeconds;
        for (int index = 0; index < args.Length; index++)
        {
            string value = args[index];
            if (value is "--profile" or "--oracle" or "--report" or "--timeout" or "--deadline")
            {
                if (++index >= args.Length) throw new ArgumentException($"Option '{value}' requires a value.");
                string argument = args[index];
                if (value == "--profile") profile = argument;
                else if (value == "--oracle") oracle = argument;
                else if (value == "--report") report = argument;
                else if (value == "--timeout" && (!int.TryParse(argument, out timeoutSeconds) || timeoutSeconds is < 1 or > MaximumTimeoutSeconds)) throw new ArgumentException("--timeout must be 1..300 seconds.");
                else if (value == "--deadline" && (!int.TryParse(argument, out deadlineSeconds) || deadlineSeconds is < 1 or > MaximumDeadlineSeconds)) throw new ArgumentException("--deadline must be 1..900 seconds.");
            }
            else throw new ArgumentException($"Unknown option '{value}'.");
        }
        if (!string.Equals(profile, ProfileName, StringComparison.Ordinal)) throw new ArgumentException($"Only profile '{ProfileName}' is supported.");
        if (!string.Equals(oracle, OracleName, StringComparison.Ordinal)) throw new ArgumentException($"Only oracle '{OracleName}' is supported.");
        return new Options(profile, report, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(deadlineSeconds));
    }

    private static bool IsRustc198(string? version) => version?.StartsWith("rustc 1.98.", StringComparison.Ordinal) == true;
    private static string? FirstLine(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

    private static string? FindRustSharpVersion(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith("rsc ", StringComparison.Ordinal));

    private static string NormalizeOutput(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(current, "RustSharp.slnx"))) return current;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent == current) break;
            current = parent;
        }
        throw new DirectoryNotFoundException("Could not locate RustSharp.slnx from the conformance tool.");
    }
    private static async Task WriteReportAsync(string path, ConformanceReport report)
    {
        string temporaryPath = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static string? TryDeleteDirectory(string path)
    {
        var clock = Stopwatch.StartNew();
        Exception? lastException = null;
        for (var attempt = 0; attempt < MaximumCleanupAttempts && clock.Elapsed < CleanupTimeout; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastException = exception;
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            Thread.Sleep(50);
        }

        return $"Run directory cleanup failed after {MaximumCleanupAttempts} attempts or {CleanupTimeout.TotalSeconds:0.#} seconds: {lastException?.Message ?? "directory still exists"}";
    }

    private sealed record Options(string Profile, string? ReportPath, TimeSpan Timeout, TimeSpan Deadline);
    private sealed record Fixture(string Id, string FileName, bool ExpectedSuccess, string? ExpectedOutput);
    private sealed record ConformanceReport(int SchemaVersion, DateTimeOffset GeneratedAtUtc, string Profile, OracleReport Oracle, ToolReport RustSharp, LimitsReport Limits, SummaryReport Summary, IReadOnlyList<CaseReport> Cases, string? RunDirectoryCleanupDiagnostic);
    private sealed record OracleReport(string Requested, string Executable, string? Version, bool Available, string? BlockedReason, ProcessEvidence VersionProbe);
    private sealed record ToolReport(string Name, string Invocation, string? Version);
    private sealed record LimitsReport(double TimeoutSeconds, double DeadlineSeconds, int MaximumCases, int MaximumOutputBytes);
    private sealed record SummaryReport(string Status, int Denominator, int Executed, int Passed, int Failed, int Skipped);
    private sealed record CaseReport(string Id, string Source, string Kind, string Status, string? Difference, ProcessEvidence RustcCompile, ProcessEvidence? RustcRun, ProcessEvidence RustSharpCheck, ProcessEvidence? RustSharpRun)
    {
        public static CaseReport Skipped(Fixture fixture, string reason) => new(fixture.Id, fixture.FileName, fixture.ExpectedSuccess ? "run-pass" : "compile-fail", "skipped", reason, ProcessEvidence.Empty, null, ProcessEvidence.Empty, null);
    }
    private sealed record ProcessEvidence(string CommandLine, int ProcessId, int ParentProcessId, DateTimeOffset? StartedAtUtc, int? ExitCode, string Termination, double ElapsedMilliseconds, string StandardOutput, string StandardError, bool OutputTruncated, bool OutputReadTimedOut, bool OutputDrainTimedOut, bool OutputReadLimitReached, string? OutputDiagnostic, bool CleanupAttempted, bool CleanupIncomplete, string? CleanupDiagnostic)
    {
        public static ProcessEvidence Empty => new("", 0, 0, null, null, "skipped", 0, "", "", false, false, false, false, null, false, false, null);
    }
    private sealed record ProcessResult(string CommandLine, int ProcessId, int? ExitCode, string Termination, TimeSpan Elapsed, string StandardOutput, string StandardError, bool OutputTruncated, bool OutputReadTimedOut, bool OutputDrainTimedOut, bool CleanupIncomplete, int ParentProcessId, DateTimeOffset? StartedAtUtc, bool OutputReadLimitReached, string? OutputDiagnostic, bool CleanupAttempted, string? CleanupDiagnostic)
    {
        public bool Succeeded => Termination == "exited" && ExitCode == 0;
        public static ProcessResult From(BoundedProcessResult result) => new(result.StartedProcess.CommandLine, result.StartedProcess.ProcessId, result.ExitCode, result.Termination.ToString().ToLowerInvariant(), result.Elapsed, result.StandardOutput, result.StandardError, result.OutputTruncated, result.OutputReadTimedOut, result.OutputDrainTimedOut, result.ProcessTreeCleanupIncomplete, result.StartedProcess.ParentProcessId, result.StartedProcess.StartedAt, result.OutputReadLimitReached, result.OutputDiagnostic, result.ProcessTreeCleanupAttempted, result.ProcessTreeCleanupDiagnostic);
        public ProcessEvidence ToEvidence() => new(CommandLine, ProcessId, ParentProcessId, StartedAtUtc, ExitCode, Termination, Elapsed.TotalMilliseconds, StandardOutput, StandardError, OutputTruncated, OutputReadTimedOut, OutputDrainTimedOut, OutputReadLimitReached, OutputDiagnostic, CleanupAttempted, CleanupIncomplete, CleanupDiagnostic);
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
