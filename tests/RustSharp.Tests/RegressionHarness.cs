using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;

namespace RustSharp.Tests;

/// <summary>A fixed registration inventory, with process isolation for reviewable full-suite evidence.</summary>
internal static class RegressionHarness
{
    internal const int EvidenceSchemaVersion = 2;
    internal const int LegacyMaximumTestCount = 1024;
    internal const int MaximumTestCount = 4096;
    internal const int MinimumFullTestCount = 464;
    private const string WorkerSwitch = "--rustsharp-test-worker";
    private static readonly string AssemblyPath = Path.Combine(AppContext.BaseDirectory, "RustSharp.Tests.dll");

    internal sealed record Options(string? Filter, string? Report, string? CandidateSha, int TimeoutSeconds, int DeadlineSeconds);
    internal sealed record Summary(int RegisteredDenominator, int Selected, int Executed, int Passed, int Failed, int Skipped, int NotExecuted);
    internal sealed record CaseEvidence(string Id, string Status, double DurationMilliseconds, string? Error, BoundedProcessResult? Process);
    internal sealed record Bounds(int TimeoutSeconds, int DeadlineSeconds, int MaximumTests);
    internal sealed record RegistrationInventory(int SchemaVersion, string EvidenceKind, string RuntimeIdentifier,
        string BuildConfiguration, string AssemblySha256, int RegisteredDenominator, string[] RegisteredIds,
        string RegisteredIdsSha256);
    internal sealed record HarnessReport(int SchemaVersion, string EvidenceKind, string? CandidateSha,
        string RuntimeIdentifier, DateTimeOffset StartedAtUtc, DateTimeOffset FinishedAtUtc, bool FullSuite,
        bool SuiteSucceeded, CandidateSourceProvenance.Evidence? SourceProvenance, string BuildConfiguration,
        bool ProcessIsolated, string? Filter, string AssemblySha256, Bounds Bounds, string[] RegisteredIds,
        string RegisteredIdsSha256, Summary Summary, CaseEvidence[] Cases, bool DeadlineExpired,
        bool Cancelled, string? HarnessError, bool CleanupComplete);

    internal static Options ParseOptions(IReadOnlyList<string> args)
    {
        if (args.Count > 10 || args.Count % 2 != 0) throw new ArgumentException("Options must have values (maximum five options).");
        string? filter = null, report = null, sha = null;
        int timeout = 120, deadline = 900;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index += 2)
        {
            string key = args[index], value = args[index + 1];
            if (!seen.Add(key) || value.Length is 0 or > 4096) throw new ArgumentException("Options must be unique and bounded.");
            switch (key)
            {
                case "--filter" when value.Length <= 256: filter = value; break;
                case "--report": report = Path.GetFullPath(value); break;
                case "--candidate-sha" when IsCommitSha(value): sha = value.ToLowerInvariant(); break;
                case "--timeout" when int.TryParse(value, out int caseSeconds) && caseSeconds is >= 1 and <= 300: timeout = caseSeconds; break;
                case "--deadline" when int.TryParse(value, out int deadlineSeconds) && deadlineSeconds is >= 1 and <= 1800: deadline = deadlineSeconds; break;
                default: throw new ArgumentException($"Unsupported or invalid option '{key}'.");
            }
        }
        if (sha is not null && report is null) throw new ArgumentException("Candidate evidence requires --report.");
        if (report is not null && sha is null) throw new ArgumentException("--report requires --candidate-sha.");
        return new(filter, report, sha, timeout, deadline);
    }

    internal static bool IsCommitSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    internal static string InventoryHash(IEnumerable<string> ids) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids))));
    internal static RegistrationInventory CreateInventory(IReadOnlyList<TestCase> registered)
    {
        if (registered.Count > MaximumTestCount) throw new ArgumentException("Registration inventory exceeds its fixed bound.");
        if (new FileInfo(AssemblyPath).Length > 32 * 1024 * 1024) throw new InvalidOperationException("Test assembly exceeds the inventory's 32 MiB bound.");
        string[] ids = RegistrationIds(registered);
        return new(EvidenceSchemaVersion, "p1-regression-registration-inventory", RuntimeInformation.RuntimeIdentifier,
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AssemblyPath))), registered.Count, ids, InventoryHash(ids));
    }
    internal static string[] RegistrationIds(IReadOnlyList<TestCase> registered)
    {
        Dictionary<string, int> nameCounts = registered.GroupBy(static item => item.Name, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        return registered.Select((item, index) => nameCounts[item.Name] == 1 ? item.Name : FormattableString.Invariant($"{item.Name} [registration {index + 1:D4}]")).ToArray();
    }
    internal static bool IsFullSuccess(bool fullSuite, Summary summary, bool provenanceValid, bool release, string? error) =>
        fullSuite && release && provenanceValid && error is null && summary.RegisteredDenominator >= MinimumFullTestCount &&
        summary.Selected == summary.RegisteredDenominator && summary.Executed == summary.RegisteredDenominator &&
        summary.RegisteredDenominator <= MaximumTestCount && summary.Passed == summary.RegisteredDenominator &&
        summary.Failed == 0 && summary.Skipped == 0 && summary.NotExecuted == 0;

    internal static async Task<int> RunAsync(TestCase[] registered, string[] args)
    {
        if (registered.Length > MaximumTestCount)
        {
            Console.Error.WriteLine($"Test registration {registered.Length} exceeds its {MaximumTestCount} bound.");
            return 2;
        }
        string[] registeredIds = RegistrationIds(registered);
        if (args.Length == 1 && args[0] == "--list")
        {
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(CreateInventory(registered), RegressionHarnessJsonContext.Default.RegistrationInventory));
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }
        if (args.Length == 2 && args[0] == WorkerSwitch)
        {
            if (!int.TryParse(args[1], out int registration) || registration < 0 || registration >= registered.Length) return 2;
            TestCase test = registered[registration];
            try { await test.ExecuteAsync().ConfigureAwait(false); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        Options options;
        try { options = ParseOptions(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Usage: RustSharp.Tests --list | [--filter fragment] [--report path --candidate-sha SHA] [--timeout 1..300] [--deadline 1..1800]");
            return 2;
        }
        int[] selected = Enumerable.Range(0, registered.Length).Where(index => options.Filter is null || registered[index].Name.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var clock = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.DeadlineSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, deadline.Token);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var cases = new List<CaseEvidence>(selected.Length);
        CandidateSourceProvenance.Evidence? provenance = null;
        string? harnessError = selected.Length == 0 ? "The test filter matched no cases." : null;
        bool isolated = options.Report is not null;
        string configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        try
        {
            if (options.CandidateSha is not null)
            {
                provenance = await CandidateSourceProvenance.VerifyAsync(options.CandidateSha, Directory.GetCurrentDirectory(), linked.Token).ConfigureAwait(false);
                if (!provenance.CandidateMatchesWorkingTree) harnessError = "Candidate source provenance did not match the working tree.";
            }
            if (harnessError is null)
            {
                for (int index = 0; index < selected.Length && index < MaximumTestCount && clock.Elapsed.TotalSeconds < options.DeadlineSeconds; index++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    int registrationIndex = selected[index];
                    TestCase test = registered[registrationIndex];
                    var caseClock = Stopwatch.StartNew();
                    BoundedProcessResult? process = null;
                    string? error = null;
                    try
                    {
                        if (isolated)
                        {
                            var arguments = new List<string>();
                            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable.");
                            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) arguments.Add(AssemblyPath);
                            arguments.Add(WorkerSwitch);
                            arguments.Add(registrationIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            process = await new BoundedProcessRunner().RunAsync(new(executable, arguments, Directory.GetCurrentDirectory(),
                                TimeSpan.FromSeconds(options.TimeoutSeconds)), linked.Token).ConfigureAwait(false);
                            if (!process.Succeeded || process.OutputTruncated || process.OutputReadTimedOut || process.OutputDrainTimedOut ||
                                process.OutputReadLimitReached || process.ProcessTreeCleanupIncomplete)
                                error = $"Worker {process.Termination}, exit {process.ExitCode}: {process.StandardError}";
                        }
                        else
                        {
                            await Task.Run(test.ExecuteAsync, linked.Token).WaitAsync(TimeSpan.FromSeconds(options.TimeoutSeconds), linked.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) { error = exception.ToString(); }
                    string status = isolated && process is null ? "not-executed" : error is null ? "passed" : "failed";
                    cases.Add(new(registeredIds[registrationIndex], status, caseClock.Elapsed.TotalMilliseconds, error, process));
                    if (error is null) Console.WriteLine($"PASS {test.Name}");
                    else Console.Error.WriteLine($"FAIL {test.Name}: {error}");
                    if (linked.IsCancellationRequested || process?.Termination is BoundedProcessTermination.TimedOut or BoundedProcessTermination.Cancelled ||
                        (!isolated && error is not null && caseClock.Elapsed.TotalSeconds >= options.TimeoutSeconds))
                    {
                        harnessError = "A case timeout or cancellation stopped the harness.";
                        break;
                    }
                }
            }
            if (options.CandidateSha is not null && provenance?.CandidateMatchesWorkingTree == true && !linked.IsCancellationRequested)
            {
                CandidateSourceProvenance.Evidence finalVerification = await CandidateSourceProvenance.VerifyAsync(options.CandidateSha,
                    Directory.GetCurrentDirectory(), linked.Token).ConfigureAwait(false);
                provenance = finalVerification with { Processes = [.. provenance.Processes, .. finalVerification.Processes] };
                if (!provenance.CandidateMatchesWorkingTree) harnessError ??= "Candidate source changed during the full harness run.";
            }
        }
        catch (Exception exception) { harnessError ??= exception.ToString(); }
        finally { Console.CancelKeyPress -= cancelHandler; }

        if (deadline.IsCancellationRequested || clock.Elapsed.TotalSeconds >= options.DeadlineSeconds) harnessError ??= "The full harness deadline expired.";
        if (cancellation.IsCancellationRequested) harnessError ??= "The full harness was cancelled.";
        int recorded = cases.Count;
        int executed = cases.Count(static item => item.Status != "not-executed");
        for (int index = recorded; index < selected.Length && index < MaximumTestCount; index++)
            cases.Add(new(registeredIds[selected[index]], "not-executed", 0, harnessError ?? "The harness stopped before this case.", null));
        int passed = cases.Count(static item => item.Status == "passed");
        int failed = cases.Count(static item => item.Status == "failed");
        var summary = new Summary(registered.Length, selected.Length, executed, passed, failed, 0, selected.Length - executed);
        bool succeeded = IsFullSuccess(options.Filter is null, summary, provenance?.CandidateMatchesWorkingTree == true,
            configuration == "Release", harnessError);
        if (options.Report is not null)
        {
            var report = new HarnessReport(EvidenceSchemaVersion, "p1-full-regression-harness", options.CandidateSha,
                RuntimeInformation.RuntimeIdentifier, startedAt, DateTimeOffset.UtcNow, options.Filter is null, succeeded, provenance,
                configuration, isolated, options.Filter, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AssemblyPath))),
                new(options.TimeoutSeconds, options.DeadlineSeconds, MaximumTestCount),
                registeredIds, InventoryHash(registeredIds),
                summary, cases.ToArray(), deadline.IsCancellationRequested || clock.Elapsed.TotalSeconds >= options.DeadlineSeconds,
                cancellation.IsCancellationRequested, harnessError, cases.All(static item => item.Process?.ProcessTreeCleanupIncomplete != true));
            await WriteAtomicAsync(options.Report, report).ConfigureAwait(false);
        }
        Console.WriteLine($"Executed {executed}/{registered.Length} registered tests: {passed} passed, {failed} failed, {summary.NotExecuted} not executed.");
        return harnessError is not null || failed != 0 || summary.NotExecuted != 0 || (options.Report is not null && options.Filter is null && !succeeded) ? 1 : 0;
    }

    private static async Task WriteAtomicAsync(string path, HarnessReport report)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Report path must have a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".p1-harness-" + Guid.NewGuid().ToString("N") + ".json.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, RegressionHarnessJsonContext.Default.HarnessReport), new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
