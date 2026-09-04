using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private const string SafeCoreLexingProfileName = "safe-core-lexing";
    private const string SafeCoreSyntaxProfileName = "safe-core-syntax";
    private const string SafeCoreNameResolutionProfileName = "safe-core-name-resolution";
    private const string OracleName = "rustc-1.98";
    private const string RustcToolchain = "1.98.0";

    public static async Task<int> Main(string[] args)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        var harnessClock = Stopwatch.StartNew();
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
        if (string.Equals(options.Profile, SafeCoreLexingProfileName, StringComparison.Ordinal))
        {
            try
            {
                string lexingReportPath = options.ReportPath is null
                    ? Path.Combine(repositoryRoot, "artifacts", "conformance", options.Profile + ".json")
                    : Path.GetFullPath(options.ReportPath, repositoryRoot);
                return await SafeCoreLexingProfileRunner.RunAsync(
                    repositoryRoot,
                    lexingReportPath,
                    options.Deadline,
                    startedAtUtc,
                    harnessClock).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"conformance: safe-core-lexing harness error: {TrimDiagnostic(exception.Message)}");
                return 2;
            }
        }

        if (string.Equals(options.Profile, SafeCoreSyntaxProfileName, StringComparison.Ordinal))
        {
            try
            {
                string syntaxReportPath = options.ReportPath is null
                    ? Path.Combine(repositoryRoot, "artifacts", "conformance", options.Profile + ".json")
                    : Path.GetFullPath(options.ReportPath, repositoryRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(syntaxReportPath)!);
                return await SafeCoreSyntaxProfileRunner.RunAsync(
                    repositoryRoot,
                    syntaxReportPath,
                    options.Deadline,
                    startedAtUtc,
                    harnessClock).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"conformance: safe-core-syntax harness error: {TrimDiagnostic(exception.Message)}");
                return 2;
            }
        }

        if (string.Equals(options.Profile, SafeCoreNameResolutionProfileName, StringComparison.Ordinal))
        {
            try
            {
                string nameResolutionReportPath = options.ReportPath is null
                    ? Path.Combine(repositoryRoot, "artifacts", "conformance", options.Profile + ".json")
                    : Path.GetFullPath(options.ReportPath, repositoryRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(nameResolutionReportPath)!);
                return await SafeCoreNameResolutionProfileRunner.RunAsync(
                    repositoryRoot,
                    nameResolutionReportPath,
                    options.Deadline,
                    startedAtUtc,
                    harnessClock).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"conformance: safe-core-name-resolution harness error: {TrimDiagnostic(exception.Message)}");
                return 2;
            }
        }

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
                [$"+{RustcToolchain}", "--version"],
                repositoryRoot,
                options.Timeout,
                deadline.Token).ConfigureAwait(false);
            string? rustcVersionText = FindRustcVersion(rustcVersion);
            bool oracleAvailable = rustcVersion.Succeeded && IsRustc198(rustcVersionText);
            string? blockedReason = oracleAvailable
                ? null
                : rustcVersion.Succeeded
                    ? $"Requested oracle rustc 1.98.x, found '{rustcVersionText ?? "unknown"}'."
                    : DescribeUnavailableProcess(
                        "Could not execute rustc --version",
                        rustcVersion,
                        options.Timeout);

            ProcessResult rustSharpVersionProbe = await ReadRustSharpVersionAsync(runner, repositoryRoot, options.Timeout, deadline.Token)
                .ConfigureAwait(false);
            string? rustSharpVersion = FindRustSharpVersion(rustSharpVersionProbe);
            bool rustSharpAvailable = rustSharpVersionProbe.Succeeded && rustSharpVersion is not null;
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
                            await RunCaseSafelyAsync(
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
            string status = !oracleAvailable || !rustSharpAvailable
                ? "blocked"
                : skipped > 0
                    ? "blocked"
                    : failed == 0
                        ? "passed"
                        : "failed";
            harnessClock.Stop();
            string? rustSharpVersionDiagnostic = rustSharpAvailable
                ? null
                : DescribeUnavailableProcess(
                    "Could not determine the RustSharp CLI version",
                    rustSharpVersionProbe,
                    options.Timeout);
            var report = new ConformanceReport(
                1,
                DateTimeOffset.UtcNow,
                options.Profile,
                new OracleReport(OracleName, "rustc", RustcToolchain, rustcVersionText, oracleAvailable, blockedReason, rustcVersion.ToEvidence()),
                new ToolReport("rustsharp", "dotnet run --project src/RustSharp.Cli -c Release", rustSharpVersion)
                {
                    Available = rustSharpAvailable,
                    Diagnostic = rustSharpVersionDiagnostic,
                    VersionProbe = rustSharpVersionProbe.ToEvidence(),
                },
                new LimitsReport(options.Timeout.TotalSeconds, options.Deadline.TotalSeconds, MaximumCases, BoundedProcessRunner.MaximumTotalOutputBytes),
                new SummaryReport(status, FixtureCatalog.Length, cases.Count - skipped, passed, failed, skipped),
                cases,
                null)
            {
                Host = HostReport.Current,
                Execution = new ExecutionReport(
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    harnessClock.Elapsed.TotalMilliseconds,
                    deadline.IsCancellationRequested,
                    deadline.IsCancellationRequested ? "Harness overall deadline expired." : null),
                BlockedReason = status == "blocked"
                    ? !oracleAvailable
                        ? blockedReason
                        : !rustSharpAvailable
                            ? rustSharpVersionDiagnostic
                            : deadline.IsCancellationRequested
                                ? "Harness overall deadline expired."
                                : skipped > 0
                                    ? "One or more profile cases were skipped."
                                    : null
                    : null,
            };
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
            [$"+{RustcToolchain}", sourcePath, "--edition", "2024", "-o", rustcOutput],
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
        ProcessResult? rustSharpCompile = null;
        ProcessResult? rustSharpRun = null;
        if (fixture.ExpectedSuccess)
        {
            if (rustcCompile.Succeeded)
            {
                rustcRun = await RunAsync(runner, rustcOutput, [], repositoryRoot, timeout, cancellationToken).ConfigureAwait(false);
            }

            if (rustSharpCheck.Succeeded)
            {
                string managedOutput = Path.Combine(caseDirectory, "rustsharp.dll");
                rustSharpCompile = await RunAsync(
                    runner,
                    "dotnet",
                    ["run", "--project", Path.Combine(repositoryRoot, "src", "RustSharp.Cli"), "-c", "Release", "--", "compile", sourcePath, "--output", managedOutput],
                    repositoryRoot,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                if (rustSharpCompile.Succeeded)
                {
                    rustSharpRun = await RunAsync(runner, "dotnet", [managedOutput], caseDirectory, timeout, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        bool outcomeMatches = fixture.ExpectedSuccess
            ? rustcCompile.Succeeded && rustSharpCheck.Succeeded && rustcRun?.Succeeded == true && rustSharpRun?.Succeeded == true && NormalizeOutput(rustcRun.StandardOutput) == NormalizeOutput(rustSharpRun.StandardOutput) && NormalizeOutput(rustSharpRun.StandardOutput) == NormalizeOutput(fixture.ExpectedOutput)
            : !rustcCompile.Succeeded && !rustSharpCheck.Succeeded;
        string? difference = outcomeMatches ? null : DescribeCaseDifference(
            fixture,
            rustcCompile,
            rustcRun,
            rustSharpCheck,
            rustSharpCompile,
            rustSharpRun);
        return new CaseReport(
            fixture.Id,
            fixture.FileName,
            fixture.ExpectedSuccess ? "run-pass" : "compile-fail",
            outcomeMatches ? "passed" : "failed",
            difference,
            rustcCompile.ToEvidence(),
            rustcRun?.ToEvidence(),
            rustSharpCheck.ToEvidence(),
            rustSharpRun?.ToEvidence())
        {
            ExpectedOutput = fixture.ExpectedOutput,
            RustSharpCompile = rustSharpCompile?.ToEvidence(),
        };
    }

    private static string DescribeCaseDifference(
        Fixture fixture,
        ProcessResult rustcCompile,
        ProcessResult? rustcRun,
        ProcessResult rustSharpCheck,
        ProcessResult? rustSharpCompile,
        ProcessResult? rustSharpRun)
    {
        if (!fixture.ExpectedSuccess)
        {
            if (rustcCompile.Succeeded)
            {
                return "rustc accepted a fixture expected to fail compilation.";
            }

            if (rustSharpCheck.Succeeded)
            {
                return "RustSharp accepted a fixture expected to fail compilation.";
            }

            return "Compile outcome differed between rustc and RustSharp.";
        }

        if (!rustcCompile.Succeeded)
        {
            return $"rustc compile failed (termination={rustcCompile.Termination}, exitCode={FormatExitCode(rustcCompile)}).";
        }

        if (!rustSharpCheck.Succeeded)
        {
            return $"RustSharp check failed (termination={rustSharpCheck.Termination}, exitCode={FormatExitCode(rustSharpCheck)}).";
        }

        if (rustcRun is not null && !rustcRun.Succeeded)
        {
            return $"rustc run failed (termination={rustcRun.Termination}, exitCode={FormatExitCode(rustcRun)}).";
        }

        if (rustSharpCompile is not null && !rustSharpCompile.Succeeded)
        {
            return $"RustSharp compile failed (termination={rustSharpCompile.Termination}, exitCode={FormatExitCode(rustSharpCompile)}).";
        }

        if (rustSharpRun is not null && !rustSharpRun.Succeeded)
        {
            return $"RustSharp run failed (termination={rustSharpRun.Termination}, exitCode={FormatExitCode(rustSharpRun)}).";
        }

        return "Run output differed from the fixture expectation or between rustc and RustSharp.";
    }

    private static string FormatExitCode(ProcessResult result) =>
        result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    private static async Task<CaseReport> RunCaseSafelyAsync(
        BoundedProcessRunner runner,
        string repositoryRoot,
        string runDirectory,
        Fixture fixture,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunCaseAsync(
                runner,
                repositoryRoot,
                runDirectory,
                fixture,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CaseReport.Skipped(fixture, "Harness overall deadline expired while executing this case.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return CaseReport.Failed(fixture, $"Case execution failed: {TrimDiagnostic(exception.Message)}");
        }
    }

    private static async Task<ProcessResult> RunAsync(BoundedProcessRunner runner, string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string commandLine = FormatCommandLine(fileName, arguments);
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

    private static async Task<ProcessResult> ReadRustSharpVersionAsync(BoundedProcessRunner runner, string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await RunAsync(runner, "dotnet", ["run", "--project", Path.Combine(root, "src", "RustSharp.Cli"), "-c", "Release", "--", "--version"], root, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static Options ParseOptions(string[] args)
    {
        if (args.Length > MaximumArgumentCount)
        {
            throw new ArgumentException($"The conformance harness accepts at most {MaximumArgumentCount} arguments.");
        }

        string profile = ProfileName;
        string oracle = OracleName;
        bool oracleSpecified = false;
        bool timeoutSpecified = false;
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
                else if (value == "--oracle")
                {
                    oracle = argument;
                    oracleSpecified = true;
                }
                else if (value == "--report") report = argument;
                else if (value == "--timeout")
                {
                    timeoutSpecified = true;
                    if (!int.TryParse(argument, out timeoutSeconds) || timeoutSeconds is < 1 or > MaximumTimeoutSeconds)
                    {
                        throw new ArgumentException("--timeout must be 1..300 seconds.");
                    }
                }
                else if (value == "--deadline" && (!int.TryParse(argument, out deadlineSeconds) || deadlineSeconds is < 1 or > MaximumDeadlineSeconds)) throw new ArgumentException("--deadline must be 1..900 seconds.");
            }
            else throw new ArgumentException($"Unknown option '{value}'.");
        }
        if (profile is not ProfileName and not SafeCoreLexingProfileName and
            not SafeCoreSyntaxProfileName and not SafeCoreNameResolutionProfileName)
        {
            throw new ArgumentException(
                $"Supported profiles are '{ProfileName}', '{SafeCoreLexingProfileName}', '{SafeCoreSyntaxProfileName}', and '{SafeCoreNameResolutionProfileName}'.");
        }

        bool inProcessAcceptanceProfile = profile is SafeCoreLexingProfileName or
            SafeCoreSyntaxProfileName or SafeCoreNameResolutionProfileName;
        if (inProcessAcceptanceProfile && oracleSpecified)
        {
            throw new ArgumentException($"Profile '{profile}' is in-process acceptance only and does not accept --oracle.");
        }

        if (inProcessAcceptanceProfile && timeoutSpecified)
        {
            throw new ArgumentException($"Profile '{profile}' runs in-process; --timeout is not applicable. Use --deadline to bound the harness.");
        }

        if (string.Equals(profile, ProfileName, StringComparison.Ordinal) && !string.Equals(oracle, OracleName, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Only oracle '{OracleName}' is supported for profile '{ProfileName}'.");
        }

        return new Options(profile, report, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(deadlineSeconds));
    }

    private static bool IsRustc198(string? version) => version?.StartsWith("rustc 1.98.", StringComparison.Ordinal) == true;

    private static string? FindRustcVersion(ProcessResult result) =>
        FindLineStartingWith(result.StandardOutput, "rustc ") ?? FindLineStartingWith(result.StandardError, "rustc ");

    private static string? FindRustSharpVersion(ProcessResult result) =>
        FindLineStartingWith(result.StandardOutput, "rsc ") ?? FindLineStartingWith(result.StandardError, "rsc ");

    private static string? FindLineStartingWith(string text, string prefix) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static string DescribeUnavailableProcess(string description, ProcessResult result, TimeSpan timeout)
    {
        string exitCode = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        string? detail = FirstDiagnosticLine(result.StandardError)
            ?? result.OutputDiagnostic
            ?? FirstDiagnosticLine(result.StandardOutput);
        string suffix = string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : $" detail='{TrimDiagnostic(detail!)}'.";
        return $"{description} (termination={result.Termination}, exitCode={exitCode}, timeoutSeconds={timeout.TotalSeconds:0.#}, elapsedMilliseconds={result.Elapsed.TotalMilliseconds:0.#}).{suffix}";
    }

    private static string? FirstDiagnosticLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length > 0);

    private static string FormatCommandLine(string fileName, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { fileName }.Concat(arguments).Select(QuoteCommandLineArgument));

    private static string QuoteCommandLineArgument(string value) =>
        value.Length != 0 && !value.Any(char.IsWhiteSpace) && value.IndexOf('"') < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string NormalizeOutput(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string TrimDiagnostic(string value)
    {
        const int maximumDiagnosticCharacters = 512;
        return value.Length <= maximumDiagnosticCharacters
            ? value
            : value[..maximumDiagnosticCharacters] + "...";
    }
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
    private sealed record ConformanceReport(int SchemaVersion, DateTimeOffset GeneratedAtUtc, string Profile, OracleReport Oracle, ToolReport RustSharp, LimitsReport Limits, SummaryReport Summary, IReadOnlyList<CaseReport> Cases, string? RunDirectoryCleanupDiagnostic)
    {
        // These additive fields make a report useful when it is collected from a CI runner
        // without changing the original positional schema consumed by existing tooling.
        public HostReport Host { get; init; } = HostReport.Current;
        public ExecutionReport Execution { get; init; } = ExecutionReport.Empty;
        public string? BlockedReason { get; init; }
    }
    private sealed record OracleReport(string Requested, string Executable, string Toolchain, string? Version, bool Available, string? BlockedReason, ProcessEvidence VersionProbe);
    private sealed record ToolReport(string Name, string Invocation, string? Version)
    {
        public bool Available { get; init; }
        public string? Diagnostic { get; init; }
        public ProcessEvidence VersionProbe { get; init; } = ProcessEvidence.Empty;
    }
    private sealed record LimitsReport(double TimeoutSeconds, double DeadlineSeconds, int MaximumCases, int MaximumOutputBytes);
    private sealed record SummaryReport(string Status, int Denominator, int Executed, int Passed, int Failed, int Skipped);
    private sealed record CaseReport(string Id, string Source, string Kind, string Status, string? Difference, ProcessEvidence RustcCompile, ProcessEvidence? RustcRun, ProcessEvidence RustSharpCheck, ProcessEvidence? RustSharpRun)
    {
        public string? ExpectedOutput { get; init; }
        public ProcessEvidence? RustSharpCompile { get; init; }

        public static CaseReport Skipped(Fixture fixture, string reason) => new(fixture.Id, fixture.FileName, fixture.ExpectedSuccess ? "run-pass" : "compile-fail", "skipped", reason, ProcessEvidence.Empty, null, ProcessEvidence.Empty, null)
        {
            ExpectedOutput = fixture.ExpectedOutput,
        };

        public static CaseReport Failed(Fixture fixture, string reason) => new(fixture.Id, fixture.FileName, fixture.ExpectedSuccess ? "run-pass" : "compile-fail", "failed", reason, ProcessEvidence.Empty, null, ProcessEvidence.Empty, null)
        {
            ExpectedOutput = fixture.ExpectedOutput,
        };
    }
    private sealed record ProcessEvidence(string CommandLine, int ProcessId, int ParentProcessId, DateTimeOffset? StartedAtUtc, int? ExitCode, string Termination, double ElapsedMilliseconds, string StandardOutput, string StandardError, bool OutputTruncated, bool OutputReadTimedOut, bool OutputDrainTimedOut, bool OutputReadLimitReached, string? OutputDiagnostic, bool CleanupAttempted, bool CleanupIncomplete, string? CleanupDiagnostic)
    {
        public static ProcessEvidence Empty => new("", 0, 0, null, null, "skipped", 0, "", "", false, false, false, false, null, false, false, null);
    }

    private sealed record HostReport(
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string Framework,
        string RuntimeIdentifier,
        string RuntimeVersion,
        bool ContinuousIntegration)
    {
        public static HostReport Current => new(
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription.Trim(),
            RuntimeInformation.RuntimeIdentifier,
            Environment.Version.ToString(),
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ExecutionReport(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        double ElapsedMilliseconds,
        bool DeadlineExpired,
        string? DeadlineDiagnostic)
    {
        public static ExecutionReport Empty => new(DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, false, null);
    }

    private sealed record ProcessResult(string CommandLine, int ProcessId, int? ExitCode, string Termination, TimeSpan Elapsed, string StandardOutput, string StandardError, bool OutputTruncated, bool OutputReadTimedOut, bool OutputDrainTimedOut, bool CleanupIncomplete, int ParentProcessId, DateTimeOffset? StartedAtUtc, bool OutputReadLimitReached, string? OutputDiagnostic, bool CleanupAttempted, string? CleanupDiagnostic)
    {
        public bool Succeeded => Termination == "exited" && ExitCode == 0;
        public static ProcessResult From(BoundedProcessResult result) => new(result.StartedProcess.CommandLine, result.StartedProcess.ProcessId, result.ExitCode, result.Termination.ToString().ToLowerInvariant(), result.Elapsed, result.StandardOutput, result.StandardError, result.OutputTruncated, result.OutputReadTimedOut, result.OutputDrainTimedOut, result.ProcessTreeCleanupIncomplete, result.StartedProcess.ParentProcessId, result.StartedProcess.StartedAt, result.OutputReadLimitReached, result.OutputDiagnostic, result.ProcessTreeCleanupAttempted, result.ProcessTreeCleanupDiagnostic);
        public ProcessEvidence ToEvidence() => new(CommandLine, ProcessId, ParentProcessId, StartedAtUtc, ExitCode, Termination, Elapsed.TotalMilliseconds, StandardOutput, StandardError, OutputTruncated, OutputReadTimedOut, OutputDrainTimedOut, OutputReadLimitReached, OutputDiagnostic, CleanupAttempted, CleanupIncomplete, CleanupDiagnostic);
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
