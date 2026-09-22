using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>Fixed, bounded generic checks and executions compared with rustc.</summary>
internal static class SafeCoreGenericProfileRunner
{
    internal const string ProfileName = "safe-core-generics-v1";
    internal const int CatalogVersion = 2;
    private const int MaximumCases = 32;
    private const int MaximumFixtureSourceLength = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static IReadOnlyList<string> RequiredCategories { get; } =
        Array.AsReadOnly(new[] { "calls", "bodies", "bounds", "coherence", "names", "boundaries", "execution" });
    private static readonly Dictionary<string, string[]> RequiredCategoryCases = new(StringComparer.Ordinal)
    {
        ["calls"] = ["explicit-identity", "inferred-identity", "call-value-arity", "call-type-arity"],
        ["bodies"] = ["generic-forward", "generic-branches", "unused-body-mismatch", "branch-mismatch", "parameter-operator"],
        ["bounds"] = ["inline-bound", "where-bound", "missing-closed-bound", "missing-caller-bound"],
        ["coherence"] = ["disjoint-impls", "nominal-impl-head", "duplicate-impl", "blanket-overlap"],
        ["names"] = ["module-call", "unresolved-type"],
        ["boundaries"] = ["default-parameter", "lifetime-parameter", "const-parameter", "associated-item", "enum-item"],
        ["execution"] = ["run-scalars", "run-bound-chain", "run-tuple-return", "run-record", "run-tuple-struct",
            "run-nested-copy", "run-record-order", "run-unit-struct"],
    };
    private static readonly HashSet<string> KnownFailureCodes = new(StringComparer.Ordinal)
    {
        "RSG1001", "RSG1002", "RSG1003", "RSG1004", "RSG1005", "RSG1006",
        "RSN1002", "RSN1003", "RSN1004", "RSN1005", "RSN1007",
    };
    private static readonly HashSet<string> RequiredPassCases = new(StringComparer.Ordinal)
    {
        "explicit-identity", "inferred-identity", "generic-forward", "generic-branches",
        "inline-bound", "where-bound", "disjoint-impls", "nominal-impl-head", "module-call",
        "run-scalars", "run-bound-chain", "run-tuple-return", "run-record", "run-tuple-struct",
        "run-nested-copy", "run-record-order", "run-unit-struct",
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<BoundedProcessTermination>() },
    };

    internal sealed record Fixture(string Id, string Source, bool ExpectedSuccess,
        string? ExpectedDiagnosticCode = null, string? ExpectedDiagnosticText = null)
    {
        public string Category { get; init; } = string.Empty;
        public int? ExpectedDiagnosticStart { get; init; }
        public bool ExpectedRustcSuccess { get; init; }
        public string? ExpectedStandardOutput { get; init; }
        public bool Executes => Category == "execution";
        public string Kind => Executes ? "run-pass" : ExpectedSuccess ? "compile-pass" : ExpectedRustcSuccess ? "profile-reject" : "compile-fail";
    }

    internal sealed record CatalogData(IReadOnlyList<Fixture> Fixtures, string ManifestSha256);

    internal static CatalogData LoadCatalog(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        string manifestPath = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "manifests", ProfileName + ".json");
        byte[] bytes = ReadBounded(manifestPath, 262_144, cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Properties(root,
                ["schemaVersion", "profile", "catalogVersion", "rustVersion", "edition", "denominator", "cases"],
                ["schemaVersion", "profile", "catalogVersion", "rustVersion", "edition", "denominator", "cases"]);
            if (root.GetProperty("schemaVersion").GetInt32() != 2 || root.GetProperty("profile").GetString() != ProfileName ||
                root.GetProperty("catalogVersion").GetInt32() != CatalogVersion || root.GetProperty("rustVersion").GetString() != "1.98.0" ||
                root.GetProperty("edition").GetString() != "2024" || root.GetProperty("denominator").GetInt32() != MaximumCases)
                throw new ArgumentException("The generic manifest version, baseline or denominator is invalid.");
            JsonElement entries = root.GetProperty("cases");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != MaximumCases)
                throw new ArgumentException("The generic manifest must contain all 32 fixed fixtures.");
            var fixtures = new List<Fixture>(MaximumCases);
            string fixtureRoot = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures", "generics");
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("Generic catalog loading exceeded five seconds.");
                Properties(entry,
                    ["id", "category", "path", "expectedSuccess", "expectedRustcSuccess",
                        "expectedDiagnosticCode", "expectedDiagnosticText", "expectedDiagnosticStart", "expectedStandardOutput"],
                    ["id", "category", "path", "expectedSuccess", "expectedRustcSuccess"]);
                string id = entry.GetProperty("id").GetString() ?? string.Empty;
                if (id.Length is < 1 or > 96 || !id.All(static c => c is >= 'a' and <= 'z' || char.IsAsciiDigit(c) || c == '-'))
                    throw new ArgumentException("Generic fixture IDs must be bounded safe file names.");
                if (entry.GetProperty("path").GetString() != id + ".rs")
                    throw new ArgumentException("Generic fixture paths must equal their fixed ID plus .rs.");
                string source = StrictUtf8.GetString(ReadBounded(Path.Combine(fixtureRoot, id + ".rs"),
                    MaximumFixtureSourceLength, cancellationToken));
                fixtures.Add(new(id, source, entry.GetProperty("expectedSuccess").GetBoolean(),
                    entry.TryGetProperty("expectedDiagnosticCode", out var code) ? code.GetString() : null,
                    entry.TryGetProperty("expectedDiagnosticText", out var text) ? text.GetString() : null)
                {
                    Category = entry.GetProperty("category").GetString() ?? string.Empty,
                    ExpectedRustcSuccess = entry.GetProperty("expectedRustcSuccess").GetBoolean(),
                    ExpectedDiagnosticStart = entry.TryGetProperty("expectedDiagnosticStart", out var start) ? start.GetInt32() : null,
                    ExpectedStandardOutput = entry.TryGetProperty("expectedStandardOutput", out var stdout) ? stdout.GetString() : null,
                });
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Generic catalog loading exceeded five seconds.");
            ValidateCatalog(fixtures, cancellationToken);
            return new(fixtures.AsReadOnly(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new ArgumentException("The generic manifest contains invalid JSON or property types.", exception);
        }
    }

    private static byte[] ReadBounded(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
            throw new ArgumentException("Generic evidence input exceeds its file-size bound.");
        var bytes = new byte[(int)stream.Length];
        int offset = 0;
        var clock = Stopwatch.StartNew();
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Generic evidence read exceeded five seconds.");
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new IOException("Generic evidence input changed while reading.");
            offset += read;
        }
        if (stream.ReadByte() != -1) throw new IOException("Generic evidence input grew while reading.");
        return bytes;
    }

    private static void Properties(JsonElement value, string[] allowed, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Generic evidence requires JSON objects.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new ArgumentException("Generic evidence contains an unknown or duplicate JSON property.");
        foreach (string property in required)
            if (!seen.Contains(property))
                throw new ArgumentException($"Generic evidence is missing required property '{property}'.");
    }

    internal static void ValidateCatalog(IReadOnlyList<Fixture> catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog.Count != MaximumCases) throw new ArgumentException("The generic catalog requires exactly 32 fixtures.", nameof(catalog));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Fixture fixture in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture is null || fixture.Id is null || fixture.Category is null ||
                !ids.Add(fixture.Id) ||
                !RequiredCategoryCases.TryGetValue(fixture.Category, out string[]? required) ||
                !required.Contains(fixture.Id, StringComparer.Ordinal))
                throw new ArgumentException("A generic fixture is duplicated or has an invalid ID/category.", nameof(catalog));
            if (string.IsNullOrWhiteSpace(fixture.Source) || fixture.Source.Length > MaximumFixtureSourceLength)
                throw new ArgumentException("Generic fixture source exceeds its bound.", nameof(catalog));
            if (fixture.ExpectedSuccess && (!fixture.ExpectedRustcSuccess || fixture.ExpectedDiagnosticCode is not null ||
                fixture.ExpectedDiagnosticText is not null || fixture.ExpectedDiagnosticStart is not null) ||
                !fixture.ExpectedSuccess && (fixture.ExpectedDiagnosticCode is null ||
                    !KnownFailureCodes.Contains(fixture.ExpectedDiagnosticCode) || fixture.ExpectedDiagnosticText is null ||
                    fixture.ExpectedDiagnosticStart is null))
                throw new ArgumentException("A generic fixture has an invalid expected outcome or diagnostic.", nameof(catalog));
            if (fixture.ExpectedSuccess != RequiredPassCases.Contains(fixture.Id))
                throw new ArgumentException("A fixed generic fixture has changed its required outcome.", nameof(catalog));
            if (fixture.Executes != (fixture.ExpectedStandardOutput is not null) ||
                fixture.ExpectedStandardOutput is { Length: > 4096 } ||
                fixture.ExpectedStandardOutput?.Contains('\r') == true)
                throw new ArgumentException("Only execution fixtures require bounded LF-normalized stdout.", nameof(catalog));
            if ((fixture.Category == "boundaries") != (!fixture.ExpectedSuccess && fixture.ExpectedRustcSuccess))
                throw new ArgumentException("Only declared boundary cases may intentionally differ from rustc acceptance.", nameof(catalog));
            if (fixture.ExpectedDiagnosticStart is int start)
            {
                string? expectedText = fixture.ExpectedDiagnosticText;
                if (expectedText is null || expectedText.Length == 0 || start < 0 ||
                    start > fixture.Source.Length - expectedText.Length ||
                    fixture.Source.Substring(start, expectedText.Length) != expectedText)
                    throw new ArgumentException("A generic diagnostic expectation must identify exact source text.", nameof(catalog));
            }
        }
        foreach ((string category, string[] required) in RequiredCategoryCases)
            foreach (string id in required)
                if (!catalog.Any(f => f.Id == id && f.Category == category))
                    throw new ArgumentException("The fixed generic catalog is missing a required fixture.", nameof(catalog));
    }

    internal static bool Matches(Fixture fixture, CompilationResult result)
    {
        if (result.Success != fixture.ExpectedSuccess || result.Output is not null) return false;
        if (fixture.ExpectedSuccess) return result.Diagnostics.Count == 0;
        return result.Diagnostics.Count == 1 && result.Diagnostics.All(diagnostic =>
            diagnostic.Code == fixture.ExpectedDiagnosticCode && diagnostic.Span.Start >= 0 &&
            diagnostic.Span.Start == fixture.ExpectedDiagnosticStart &&
            diagnostic.Span.End <= fixture.Source.Length && diagnostic.Span.Length > 0 &&
            (fixture.ExpectedDiagnosticText is null ||
                fixture.Source.Substring(diagnostic.Span.Start, diagnostic.Span.Length) == fixture.ExpectedDiagnosticText));
    }

    public static async Task<int> RunAsync(string repositoryRoot, string reportPath,
        TimeSpan timeout, TimeSpan deadline, DateTimeOffset startedAtUtc, Stopwatch harnessClock)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30) ||
            deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(180))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Generic conformance requires a case timeout <=30s and overall deadline <=180s.");
        using var cancellation = new CancellationTokenSource(deadline);
        CatalogData data = LoadCatalog(repositoryRoot, cancellation.Token);
        IReadOnlyList<Fixture> catalog = data.Fixtures;
        string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        string runDirectory = Path.Combine(reportDirectory, $".run-generics-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var cases = new List<CaseReport>(catalog.Count);
        ProcessProbe? versionProbe = null;
        string? blockedReason = null;
        string? cleanupDiagnostic = null;
        bool oracleAvailable = false;
        try
        {
            var runner = new BoundedProcessRunner();
            versionProbe = await ProbeAsync(runner, ["+1.98.0", "--version"], repositoryRoot,
                timeout, cancellation.Token).ConfigureAwait(false);
            oracleAvailable = versionProbe.Result is { } version && CleanExit(version) && version.ExitCode == 0 &&
                version.StandardOutput.StartsWith("rustc 1.98.0", StringComparison.Ordinal);
            if (!oracleAvailable) blockedReason = versionProbe.Error ?? "The rustc +1.98.0 oracle is unavailable or reports a different version.";
            foreach (Fixture fixture in catalog)
            {
                if (!oracleAvailable || cancellation.IsCancellationRequested)
                {
                    cases.Add(new(fixture.Id, fixture.Kind, "skipped",
                        blockedReason ?? "The overall deadline expired or execution was cancelled.", SourceHash(fixture),
                        fixture.ExpectedDiagnosticCode, fixture.ExpectedDiagnosticText, null, [], null)
                    { Category = fixture.Category, ExpectedDiagnosticStart = fixture.ExpectedDiagnosticStart, ExpectedRustcSuccess = fixture.ExpectedRustcSuccess });
                    continue;
                }
                cases.Add(await RunCaseAsync(runner, runDirectory, fixture, timeout, cancellation.Token).ConfigureAwait(false));
                Console.Error.WriteLine($"Generic conformance {cases.Count}/{catalog.Count}: {fixture.Id} {cases[^1].Status}.");
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            cleanupDiagnostic = await CleanupAsync(runDirectory, reportDirectory).ConfigureAwait(false);
        }

        int passed = cases.Count(static result => result.Status == "passed");
        int failed = cases.Count(static result => result.Status == "failed");
        int skipped = cases.Count(static result => result.Status == "skipped");
        string status = ReportStatus(catalog.Count, passed, failed, skipped, oracleAvailable,
            cancellation.IsCancellationRequested, cleanupDiagnostic);
        var report = new
        {
            SchemaVersion = 2,
            Profile = ProfileName,
            CatalogVersion,
            CatalogValidated = true,
            data.ManifestSha256,
            EvidenceScope = "generic-check-execution-differential-and-profile-boundaries",
            ExecutableConformance = true,
            RequiredCategories,
            CategoryCoverage = RequiredCategories.Select(category => new
            {
                Category = category,
                RequiredCases = RequiredCategoryCases[category],
                Cases = catalog.Where(fixture => fixture.Category == category).Select(static fixture => fixture.Id).ToArray(),
                CompilePass = catalog.Count(fixture => fixture.Category == category && fixture.Kind == "compile-pass"),
                CompileFail = catalog.Count(fixture => fixture.Category == category && fixture.Kind == "compile-fail"),
                ProfileReject = catalog.Count(fixture => fixture.Category == category && fixture.Kind == "profile-reject"),
                RunPass = catalog.Count(fixture => fixture.Category == category && fixture.Kind == "run-pass"),
            }).ToArray(),
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = startedAtUtc,
            ElapsedMilliseconds = harnessClock.Elapsed.TotalMilliseconds,
            Host = new
            {
                Runtime = Environment.Version.ToString(),
                RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString()
            },
            Oracle = new { Requested = "rustc-1.98", Toolchain = "1.98.0", Available = oracleAvailable, VersionProbe = versionProbe },
            Compiler = new
            {
                Api = "CompilerDriver.Check/Compile",
                Profile = nameof(CompilationProfile.SafeCoreGenerics),
                Version = typeof(CompilerDriver).Assembly.GetName().Version?.ToString()
            },
            Limits = new
            {
                MaximumCases,
                TimeoutSeconds = timeout.TotalSeconds,
                DeadlineSeconds = deadline.TotalSeconds,
                MaximumOutputBytes = BoundedProcessRunner.MaximumTotalOutputBytes
            },
            Summary = new
            {
                Status = status,
                Denominator = catalog.Count,
                Total = catalog.Count,
                Executed = cases.Count - skipped,
                Passed = passed,
                Failed = failed,
                Skipped = skipped
            },
            BlockedReason = status == "blocked" ? blockedReason ?? "Execution did not finish every declared fixture." : null,
            CancelledOrDeadlineExpired = cancellation.IsCancellationRequested,
            Cases = cases,
            RunDirectoryCleanupDiagnostic = cleanupDiagnostic,
        };
        string temporaryReport = reportPath + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using var reportDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using (var stream = new FileStream(temporaryReport, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, reportDeadline.Token).ConfigureAwait(false);
            File.Move(temporaryReport, reportPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryReport)) File.Delete(temporaryReport); }
        Console.WriteLine($"Generic conformance: {status}; {passed}/{catalog.Count} passed, {failed} failed, {skipped} skipped. Report: {reportPath}");
        return status == "passed" ? 0 : status == "blocked" ? 2 : 1;
    }

    private static async Task<CaseReport> RunCaseAsync(BoundedProcessRunner runner, string directory,
        Fixture fixture, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var caseDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        caseDeadline.CancelAfter(timeout);
        string sourcePath = Path.Combine(directory, fixture.Id + ".rs");
        string metadataPath = Path.Combine(directory, fixture.Id + ".rmeta");
        string oraclePath = fixture.Executes ? Path.Combine(directory, fixture.Id + (OperatingSystem.IsWindows() ? ".exe" : "")) : metadataPath;
        string assemblyPath = Path.Combine(directory, fixture.Id + ".dll");
        CompilationResult? actual = null;
        CompilationResult? compiled = null;
        ProcessProbe? oracle = null;
        ProcessProbe? managedRun = null;
        ProcessProbe? oracleRun = null;
        string? assemblySha256 = null;
        string? difference = null;
        IReadOnlyList<DiagnosticEvidence> diagnostics = [];
        try
        {
            await File.WriteAllTextAsync(sourcePath, fixture.Source, caseDeadline.Token).ConfigureAwait(false);
            actual = CompilerDriver.Check(fixture.Source, sourcePath, CompilationProfile.SafeCoreGenerics, caseDeadline.Token);
            diagnostics = actual.Diagnostics.Select(diagnostic => new DiagnosticEvidence(diagnostic.Code,
                diagnostic.Span.Start, diagnostic.Span.Length, diagnostic.SourcePath,
                diagnostic.Span.Start >= 0 && diagnostic.Span.End <= fixture.Source.Length
                    ? fixture.Source.Substring(diagnostic.Span.Start, diagnostic.Span.Length) : null)).ToArray();
            IReadOnlyList<string> oracleArguments = fixture.Executes
                ? ["+1.98.0", "--edition=2024", "--crate-name", "generic_fixture", sourcePath, "-o", oraclePath]
                : ["+1.98.0", "--edition=2024", "--crate-type=lib", "--emit=metadata", "--crate-name", "generic_fixture", sourcePath, "-o", oraclePath];
            oracle = await ProbeAsync(runner, oracleArguments, directory, timeout, caseDeadline.Token).ConfigureAwait(false);
            if (!Matches(fixture, actual)) difference = "RustSharp outcome, diagnostic code or diagnostic span differs from the declared fixture.";
            else if (oracle.Result is not { } process || !CleanExit(process))
                difference = oracle.Error ?? "The oracle process did not exit cleanly within its bounds.";
            else if (process.ExitCode != (fixture.ExpectedRustcSuccess ? 0 : 1))
                difference = "rustc outcome differs from its independently declared fixture outcome.";
            else if (fixture.ExpectedRustcSuccess && !File.Exists(oraclePath))
                difference = "rustc reported success without producing its requested artifact.";
            else if (!fixture.ExpectedRustcSuccess && !process.StandardError.Contains("error", StringComparison.Ordinal))
                difference = "rustc failed without a compiler error diagnostic.";
            if (difference is null && fixture.Executes)
            {
                compiled = CompilerDriver.Compile(fixture.Source, sourcePath, assemblyPath,
                    profile: CompilationProfile.SafeCoreGenerics, cancellationToken: caseDeadline.Token);
                if (!compiled.Success || compiled.Diagnostics.Count != 0 || compiled.Output is null || !File.Exists(assemblyPath))
                    difference = "RustSharp did not compile the executable fixture: " + string.Join("; ", compiled.Diagnostics);
                else
                {
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath, caseDeadline.Token).ConfigureAwait(false)));
                    managedRun = await ProbeAsync(runner, [assemblyPath], directory, timeout, caseDeadline.Token, "dotnet").ConfigureAwait(false);
                    oracleRun = await ProbeAsync(runner, [], directory, timeout, caseDeadline.Token, oraclePath).ConfigureAwait(false);
                    if (managedRun.Result is not { } managed || oracleRun.Result is not { } native || !CleanExit(managed) || !CleanExit(native))
                        difference = "An executable process did not exit cleanly within its bounds.";
                    else if (managed.ExitCode != 0 || native.ExitCode != 0 || managed.ExitCode != native.ExitCode ||
                        NormalizeOutput(managed.StandardOutput) != fixture.ExpectedStandardOutput ||
                        NormalizeOutput(native.StandardOutput) != fixture.ExpectedStandardOutput ||
                        NormalizeOutput(managed.StandardOutput) != NormalizeOutput(native.StandardOutput) ||
                        managed.StandardError.Length != 0 || native.StandardError.Length != 0)
                        difference = "Executable stdout, stderr or exit code differs from the declared output or rustc execution.";
                }
            }
        }
        catch (OperationCanceledException) { difference = "The case or overall deadline expired, or execution was cancelled."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        { difference = exception.Message; }
        return new(fixture.Id, fixture.Kind,
            difference is null ? "passed" : "failed", difference, SourceHash(fixture), fixture.ExpectedDiagnosticCode,
            fixture.ExpectedDiagnosticText, actual?.Success, diagnostics, oracle)
        {
            Category = fixture.Category,
            ExpectedDiagnosticStart = fixture.ExpectedDiagnosticStart,
            ExpectedRustcSuccess = fixture.ExpectedRustcSuccess,
            ExpectedStandardOutput = fixture.ExpectedStandardOutput,
            RustSharpCompiled = compiled?.Success,
            AssemblySha256 = assemblySha256,
            ManagedExecution = managedRun,
            RustcExecution = oracleRun
        };
    }

    internal static string ReportStatus(int total, int passed, int failed, int skipped, bool oracleAvailable,
        bool cancelledOrDeadlineExpired, string? cleanupDiagnostic)
    {
        if (total != MaximumCases || passed < 0 || failed < 0 || skipped < 0 ||
            (long)passed + failed + skipped != total) return "failed";
        if (!oracleAvailable || skipped != 0 || cancelledOrDeadlineExpired) return "blocked";
        return failed != 0 || cleanupDiagnostic is not null ? "failed" : "passed";
    }

    private static async Task<ProcessProbe> ProbeAsync(BoundedProcessRunner runner, IReadOnlyList<string> arguments,
        string directory, TimeSpan timeout, CancellationToken cancellationToken, string executable = "rustc")
    {
        try
        {
            BoundedProcessResult result = await runner.RunAsync(new(executable, arguments, directory, timeout,
                static process => Console.Error.WriteLine($"Started PID {process.ProcessId} at {process.StartedAt:O}; " +
                    $"parent PID {process.ParentProcessId}; command: {process.CommandLine}")), cancellationToken).ConfigureAwait(false);
            return new(result, null);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or OperationCanceledException)
        { return new(null, exception.Message); }
    }

    private static bool CleanExit(BoundedProcessResult result) => result.Termination == BoundedProcessTermination.Exited &&
        !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut &&
        !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;

    private static async Task<string?> CleanupAsync(string directory, string parent)
    {
        string resolved = Path.GetFullPath(directory);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(parent), pathComparison) ||
            !Path.GetFileName(resolved).StartsWith($".run-generics-{Environment.ProcessId}-", StringComparison.Ordinal))
            return "Cleanup ownership verification failed.";
        var clock = Stopwatch.StartNew();
        string? diagnostic = null;
        for (int attempt = 0; attempt < 8 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true); return null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostic = exception.Message; }
            await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
        }
        return "Owned run directory could not be removed: " + diagnostic;
    }

    private static string SourceHash(Fixture fixture) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Source)));
    private static string NormalizeOutput(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal);
    private sealed record ProcessProbe(BoundedProcessResult? Result, string? Error);
    private sealed record DiagnosticEvidence(string Code, int Start, int Length, string? SourcePath, string? SourceText);
    private sealed record CaseReport(string Id, string Kind, string Status, string? Difference, string SourceSha256,
        string? ExpectedDiagnosticCode, string? ExpectedDiagnosticText, bool? RustSharpSuccess,
        IReadOnlyList<DiagnosticEvidence> RustSharpDiagnostics, ProcessProbe? Rustc)
    {
        public string Category { get; init; } = string.Empty;
        public int? ExpectedDiagnosticStart { get; init; }
        public bool ExpectedRustcSuccess { get; init; }
        public string? ExpectedStandardOutput { get; init; }
        public bool? RustSharpCompiled { get; init; }
        public string? AssemblySha256 { get; init; }
        public ProcessProbe? ManagedExecution { get; init; }
        public ProcessProbe? RustcExecution { get; init; }
    }
}
