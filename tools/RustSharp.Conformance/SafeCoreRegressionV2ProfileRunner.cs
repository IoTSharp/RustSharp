using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>
/// Versioned source regression runner for the bounded typed-MIR v2 profile.
/// The v1 manifest remains immutable; this runner owns the additive v2
/// denominator and preserves rustc/RustSharp process provenance for every
/// case.
/// </summary>
internal static class SafeCoreRegressionV2ProfileRunner
{
    internal const string ProfileName = "safe-core-regression-v2";
    internal const string ManifestFileName = "safe-core-regression-v2-manifest.json";
    internal const int ManifestVersion = 2;
    internal const string RustVersion = "1.98.0";
    internal const string Edition = "2024";
    internal const string CompilerProfile = "safe-core-mir-p1-v2";
    internal const int Denominator = 24;
    internal const int MaximumCases = 24;
    internal const int MaximumManifestBytes = 512 * 1024;
    internal const int MaximumFixtureBytes = 1024 * 1024;
    internal const int MaximumTimeoutSeconds = 300;
    internal const int MaximumDeadlineSeconds = 900;
    private const int MaximumJsonDepth = 32;
    private const int MaximumJsonTokens = 8_192;
    private const int MaximumIdLength = 96;
    private const int MaximumCleanupAttempts = 40;
    private const string Rustc = "rustc";
    private const string Dotnet = "dotnet";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] Kinds = ["compile-pass", "compile-fail", "run-pass", "differential"];
    private static readonly string[] Areas = ["legacy", "typed-mir", "borrow", "drop"];
    private static readonly string[] LegacyIds =
    [
        "compile-pass-baseline", "compile-fail-immutable", "compile-fail-type",
        "run-pass-functions", "run-pass-returns", "differential-short-circuit",
        "differential-integers", "differential-shadowing",
    ];
    private static readonly string[] V2Ids =
    [
        "typed-mir-match-guard", "typed-mir-or-pattern", "typed-mir-closure",
        "typed-mir-repeated-array", "typed-mir-shared-borrow", "typed-mir-mutable-reborrow",
        "typed-mir-drop-return", "typed-mir-drop-nested", "typed-mir-closure-escape",
        "typed-mir-binding-or-pattern", "typed-mir-mutable-capture", "typed-mir-borrow-conflict",
        "borrow-write-read", "borrow-mutable-reborrow-chain", "drop-reverse-locals", "drop-branch-return",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record Fixture(string Id, string File, string Kind, string? ExpectedOutput, string Area)
    {
        public string Expectation { get; init; } = "run-pass";
        public string? RustcDiagnostic { get; init; }
        public string? RustSharpDiagnostic { get; init; }
    }

    internal sealed record Limits(int MaximumCases, int MaximumManifestBytes, int MaximumFixtureBytes,
        int CaseTimeoutSeconds, int DeadlineSeconds);

    internal sealed record Manifest(string Profile, int Version, string RustVersion, string Edition,
        string CompilerProfile, int Denominator, IReadOnlyList<Fixture> Cases)
    {
        public Limits? DeclaredLimits { get; init; }
        public IReadOnlyDictionary<string, int>? DeclaredCoverage { get; init; }
    }

    internal sealed record ProcessEvidence(string CommandLine, int ProcessId, int ParentProcessId,
        DateTimeOffset? StartedAtUtc, int? ExitCode, string Termination, double ElapsedMilliseconds,
        string StandardOutput, string StandardError, bool OutputTruncated, bool OutputReadTimedOut,
        bool OutputDrainTimedOut, bool OutputReadLimitReached, bool CleanupAttempted,
        bool CleanupIncomplete, string? Diagnostic)
    {
        public static ProcessEvidence Empty => new("", 0, 0, null, null, "not-started", 0,
            "", "", false, false, false, false, false, false, null);
    }

    internal sealed record CaseReport(string Id, string File, string Kind, string Area, string Expectation,
        string Status, string? Difference, string? ExpectedOutput, ProcessEvidence RustcCompile,
        ProcessEvidence? RustcRun, ProcessEvidence RustSharpCheck, ProcessEvidence? RustSharpCompile,
        ProcessEvidence? RustSharpRun)
    {
        public string? SourceSha256 { get; init; }
        public string? RustcDiagnostic { get; init; }
        public string? RustSharpDiagnostic { get; init; }
    }

    internal sealed record Summary(string Status, int ExitCode, int Denominator, int Executed, int Passed,
        int Failed, int Blocked, int Skipped, IReadOnlyDictionary<string, int> Coverage);

    internal sealed record Report(int SchemaVersion, string EvidenceKind, string Profile,
        DateTimeOffset GeneratedAtUtc, string ManifestPath, string ManifestSha256, long ManifestBytes,
        bool ManifestValidated, string? ManifestError, ProcessEvidence RustcVersion,
        ProcessEvidence RustSharpVersion, double CaseTimeoutSeconds, double DeadlineSeconds,
        Summary Summary, IReadOnlyList<CaseReport> Cases, DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc, double ElapsedMilliseconds, bool DeadlineExpired,
        string? CleanupDiagnostic, string? HarnessError, HostReport Host);

    internal sealed record HostReport(string OperatingSystem, string Architecture, string ProcessArchitecture,
        string Framework, string RuntimeIdentifier);

    internal static Manifest ParseManifest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new ArgumentException("safe-core-regression-v2 manifest exceeds its byte bound.", nameof(json));
        ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(json));
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = MaximumJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Manifest root must be an object.", nameof(json));
        string profile = RequiredString(root, "profile");
        int version = RequiredInt(root, "version");
        string rustVersion = RequiredString(root, "rustVersion");
        string edition = RequiredString(root, "edition");
        string compilerProfile = RequiredString(root, "compilerProfile");
        int denominator = RequiredInt(root, "denominator");
        Limits limits = ParseLimits(root);
        Dictionary<string, int> coverage = ParseCoverage(root);
        if (!root.TryGetProperty("cases", out JsonElement casesElement) || casesElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Manifest must contain a cases array.", nameof(json));
        var cases = new List<Fixture>(Math.Min(Math.Max(denominator, 0), MaximumCases));
        int index = 0;
        foreach (JsonElement element in casesElement.EnumerateArray())
        {
            if (++index > MaximumCases) throw new ArgumentException("Manifest exceeds its case bound.", nameof(json));
            if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Case must be an object.", nameof(json));
            string id = RequiredString(element, "id");
            string file = RequiredString(element, "file");
            string kind = RequiredString(element, "kind");
            string area = RequiredString(element, "area");
            string? expectedOutput = OptionalString(element, "expectedOutput");
            string? expectation = OptionalString(element, "expectation") ?? (kind == "compile-fail" ? "compile-fail" : "run-pass");
            string? rustcDiagnostic = OptionalString(element, "rustcDiagnostic");
            string? rustSharpDiagnostic = OptionalString(element, "rustSharpDiagnostic");
            cases.Add(new Fixture(id, file, kind, expectedOutput, area)
            {
                Expectation = expectation,
                RustcDiagnostic = rustcDiagnostic,
                RustSharpDiagnostic = rustSharpDiagnostic,
            });
        }
        var manifest = new Manifest(profile, version, rustVersion, edition, compilerProfile, denominator, cases)
        {
            DeclaredLimits = limits,
            DeclaredCoverage = coverage,
        };
        ValidateManifest(manifest);
        return manifest;
    }

    internal static void ValidateManifest(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Profile != ProfileName || manifest.Version != ManifestVersion ||
            manifest.RustVersion != RustVersion || manifest.Edition != Edition ||
            manifest.CompilerProfile != CompilerProfile || manifest.Denominator != Denominator ||
            manifest.Cases.Count != Denominator)
            throw new ArgumentException("safe-core-regression-v2 version/profile contract is invalid.", nameof(manifest));
        Limits limits = manifest.DeclaredLimits ?? throw new ArgumentException("Manifest limits are required.", nameof(manifest));
        if (limits.MaximumCases != MaximumCases || limits.MaximumManifestBytes is < 1 or > MaximumManifestBytes ||
            limits.MaximumFixtureBytes is < 1 or > MaximumFixtureBytes || limits.CaseTimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
            limits.DeadlineSeconds is < 1 or > MaximumDeadlineSeconds)
            throw new ArgumentException("Manifest limits are outside the v2 bounds.", nameof(manifest));
        if (manifest.DeclaredCoverage is null || manifest.DeclaredCoverage.Count != 8)
            throw new ArgumentException("Manifest coverage must declare all v2 categories.", nameof(manifest));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var actualKinds = Kinds.ToDictionary(static kind => kind, static _ => 0, StringComparer.Ordinal);
        var actualAreas = Areas.ToDictionary(static area => area, static _ => 0, StringComparer.Ordinal);
        for (int index = 0; index < manifest.Cases.Count; index++)
        {
            Fixture fixture = manifest.Cases[index];
            if (!ids.Add(fixture.Id) || fixture.Id.Length > MaximumIdLength ||
                !fixture.Id.All(static c => c is >= 'a' and <= 'z' || char.IsAsciiDigit(c) || c == '-'))
                throw new ArgumentException("Fixture IDs must be unique, lowercase and bounded.", nameof(manifest));
            if (!files.Add(fixture.File) || Path.GetFileName(fixture.File) != fixture.File ||
                !fixture.File.EndsWith(".rs", StringComparison.Ordinal) || fixture.File.Length > MaximumIdLength)
                throw new ArgumentException("Fixture file names must be unique, flat .rs files.", nameof(manifest));
            if (!Kinds.Contains(fixture.Kind, StringComparer.Ordinal) || !Areas.Contains(fixture.Area, StringComparer.Ordinal))
                throw new ArgumentException("Fixture kind or area is outside the v2 contract.", nameof(manifest));
            if (fixture.Expectation is not ("compile-pass" or "run-pass" or "compile-fail" or "rustsharp-fail"))
                throw new ArgumentException("Fixture expectation is invalid.", nameof(manifest));
            if (fixture.Expectation is "compile-fail" or "rustsharp-fail" && fixture.Kind != "compile-fail")
                throw new ArgumentException("Only compile-fail fixtures may expect compilation failure.", nameof(manifest));
            if (fixture.Expectation == "compile-fail" && (fixture.RustcDiagnostic is null || fixture.RustSharpDiagnostic is null))
                throw new ArgumentException("Compile-fail fixtures require both diagnostic contracts.", nameof(manifest));
            if (fixture.Expectation == "rustsharp-fail" && fixture.RustSharpDiagnostic is null)
                throw new ArgumentException("RustSharp-only compile-fail fixtures require a diagnostic contract.", nameof(manifest));
            if (fixture.Kind is "run-pass" or "differential" && fixture.ExpectedOutput is null)
                throw new ArgumentException("Run and differential fixtures require expected output.", nameof(manifest));
            if (fixture.Kind is "compile-pass" or "compile-fail" && fixture.ExpectedOutput is not null)
                throw new ArgumentException("Compile fixtures cannot declare output.", nameof(manifest));
            actualKinds[fixture.Kind]++;
            actualAreas[fixture.Area]++;
            if (index < LegacyIds.Length && fixture.Id != LegacyIds[index])
                throw new ArgumentException("The first eight v2 cases must preserve the v1 IDs and order.", nameof(manifest));
            if (index >= LegacyIds.Length && fixture.Id != V2Ids[index - LegacyIds.Length])
                throw new ArgumentException("The additive v2 cases must preserve their fixed IDs and order.", nameof(manifest));
        }
        IReadOnlyDictionary<string, int> expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["compile-pass"] = 1, ["compile-fail"] = 6, ["run-pass"] = 13, ["differential"] = 4,
            ["legacy"] = 8, ["typed-mir"] = 12, ["borrow"] = 2, ["drop"] = 2,
        };
        foreach ((string key, int value) in expected)
        {
            if (!manifest.DeclaredCoverage.TryGetValue(key, out int declared) ||
                (actualKinds.TryGetValue(key, out int kind) ? kind : actualAreas[key]) != declared)
                throw new ArgumentException("Manifest coverage does not match its fixed denominator.", nameof(manifest));
        }
    }

    internal static IReadOnlyDictionary<string, int> CountCoverage(IEnumerable<Fixture> fixtures)
    {
        var result = Kinds.Concat(Areas).Distinct(StringComparer.Ordinal).ToDictionary(static key => key, static _ => 0, StringComparer.Ordinal);
        int count = 0;
        foreach (Fixture fixture in fixtures)
        {
            if (++count > MaximumCases) throw new ArgumentException("Fixture count exceeds its bound.", nameof(fixtures));
            if (!result.TryGetValue(fixture.Kind, out int kindCount) ||
                !result.TryGetValue(fixture.Area, out int areaCount))
                throw new ArgumentException("Unknown fixture category.", nameof(fixtures));
            result[fixture.Kind] = kindCount + 1;
            result[fixture.Area] = areaCount + 1;
        }
        return result;
    }

    public static async Task<int> RunAsync(string repositoryRoot, string reportPath, TimeSpan timeout,
        TimeSpan deadline, DateTimeOffset startedAtUtc, Stopwatch harnessClock)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(MaximumDeadlineSeconds))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        string root = Path.GetFullPath(repositoryRoot);
        string fullReport = Path.GetFullPath(reportPath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReport)!);
        string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", ManifestFileName);
        string relativeManifest = Path.GetRelativePath(root, manifestPath).Replace(Path.DirectorySeparatorChar, '/');
        Manifest? manifest = null;
        string? manifestError = null;
        byte[] manifestBytes = [];
        try
        {
            manifestBytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            manifest = ParseManifest(Encoding.UTF8.GetString(manifestBytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            manifestError = Trim(exception.Message);
        }
        TimeSpan effectiveTimeout = timeout;
        TimeSpan effectiveDeadline = deadline;
        if (manifest?.DeclaredLimits is Limits limits)
        {
            effectiveTimeout = TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, limits.CaseTimeoutSeconds));
            effectiveDeadline = TimeSpan.FromSeconds(Math.Min(deadline.TotalSeconds, limits.DeadlineSeconds));
        }
        using var cancellation = new CancellationTokenSource(effectiveDeadline);
        string runDirectory = Path.Combine(Path.GetDirectoryName(fullReport)!, ".run-safe-core-regression-v2-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        var cases = new List<CaseReport>(manifest?.Cases.Count ?? 0);
        string? cleanupDiagnostic = null;
        string? harnessError = manifestError;
        ProcessEvidence rustcVersion = ProcessEvidence.Empty;
        ProcessEvidence rustSharpVersion = ProcessEvidence.Empty;
        try
        {
            if (manifest is not null)
            {
                Directory.CreateDirectory(runDirectory);
                var runner = new BoundedProcessRunner();
                ProcessResult rustcProbe = await RunProcessAsync(runner, Rustc, ["+" + RustVersion, "--version"], root, effectiveTimeout, cancellation.Token).ConfigureAwait(false);
                rustcVersion = rustcProbe.ToEvidence();
                ProcessResult rustSharpProbe = await RunProcessAsync(runner, Dotnet, BuildRustSharpArguments(root, "--version"), root, effectiveTimeout, cancellation.Token).ConfigureAwait(false);
                rustSharpVersion = rustSharpProbe.ToEvidence();
                bool oracleAvailable = rustcProbe.Succeeded && IsRequestedRustcVersion(FirstLine(rustcProbe.StandardOutput, "rustc ") ?? FirstLine(rustcProbe.StandardError, "rustc "));
                bool rustSharpAvailable = rustSharpProbe.Succeeded;
                foreach (Fixture fixture in manifest.Cases)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        cases.Add(Blocked(fixture, "v2 deadline expired before this case started."));
                    }
                    else if (!oracleAvailable || !rustSharpAvailable)
                    {
                        cases.Add(Blocked(fixture, "rustc 1.98.0 or RustSharp release CLI is unavailable."));
                    }
                    else
                    {
                        cases.Add(await RunCaseAsync(runner, root, runDirectory, fixture, effectiveTimeout, cancellation.Token).ConfigureAwait(false));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            harnessError ??= "v2 deadline expired.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            harnessError ??= Trim(exception.Message);
        }
        finally
        {
            cleanupDiagnostic = TryDeleteDirectory(runDirectory);
        }
        if (manifest is not null)
            while (cases.Count < manifest.Cases.Count) cases.Add(Blocked(manifest.Cases[cases.Count], harnessError ?? "Case did not start."));
        harnessClock.Stop();
        int passed = cases.Count(static item => item.Status == "passed");
        int failed = cases.Count(static item => item.Status == "failed");
        int blocked = cases.Count(static item => item.Status == "blocked");
        int skipped = cases.Count(static item => item.Status == "skipped");
        string status = manifest is null || harnessError is not null || cleanupDiagnostic is not null || cancellation.IsCancellationRequested || blocked > 0
            ? "blocked" : failed > 0 ? "failed" : passed == Denominator && skipped == 0 ? "passed" : "blocked";
        int exitCode = status == "passed" ? 0 : status == "failed" ? 1 : 2;
        var report = new Report(2, "safe-core-typed-mir-regression", ProfileName, DateTimeOffset.UtcNow, relativeManifest,
            manifestBytes.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(manifestBytes)), manifestBytes.Length, manifest is not null,
            manifestError, rustcVersion, rustSharpVersion, effectiveTimeout.TotalSeconds, effectiveDeadline.TotalSeconds,
            new Summary(status, exitCode, manifest?.Denominator ?? 0, passed + failed, passed, failed, blocked, skipped,
                manifest is null ? new Dictionary<string, int>() : CountCoverage(manifest.Cases)), cases, startedAtUtc, DateTimeOffset.UtcNow,
            harnessClock.Elapsed.TotalMilliseconds, cancellation.IsCancellationRequested, cleanupDiagnostic, harnessError, CurrentHost());
        await File.WriteAllTextAsync(fullReport, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return exitCode;
    }

    internal static bool IsRequestedRustcVersion(string? version) => version?.StartsWith("rustc " + RustVersion + " (", StringComparison.Ordinal) == true;

    internal static bool MatchesCompileFailure(ProcessEvidence evidence, string diagnostic) =>
        evidence.Termination == "exited" && evidence.ExitCode == 1 && evidence.ProcessId > 0 &&
        !evidence.OutputTruncated && !evidence.OutputReadTimedOut && !evidence.OutputDrainTimedOut &&
        !evidence.OutputReadLimitReached && !evidence.CleanupIncomplete &&
        (evidence.StandardError.Contains("error[" + diagnostic + "]:", StringComparison.Ordinal) || evidence.StandardError.Contains("error " + diagnostic + ":", StringComparison.Ordinal));

    private static async Task<CaseReport> RunCaseAsync(BoundedProcessRunner runner, string root, string runDirectory,
        Fixture fixture, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string source = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", fixture.File);
        FileInfo info = new(source);
        if (info.Length is < 1 or > MaximumFixtureBytes) throw new InvalidOperationException("Fixture size exceeds its bound: " + fixture.File);
        string sourceSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false)));
        string caseDirectory = Path.Combine(runDirectory, fixture.Id);
        Directory.CreateDirectory(caseDirectory);
        string rustcOutput = Path.Combine(caseDirectory, "oracle.exe");
        ProcessResult rustcCompile = await RunProcessAsync(runner, Rustc, ["+" + RustVersion, source, "--edition", Edition, "-C", "overflow-checks=yes", "-o", rustcOutput], root, timeout, cancellationToken).ConfigureAwait(false);
        ProcessResult? rustcRun = fixture.Expectation == "run-pass" && rustcCompile.Succeeded ? await RunProcessAsync(runner, rustcOutput, [], caseDirectory, timeout, cancellationToken).ConfigureAwait(false) : null;
        ProcessResult rustSharpCheck = await RunProcessAsync(runner, Dotnet, BuildRustSharpArguments(root, "check", source, "--profile", CompilerProfile), root, timeout, cancellationToken).ConfigureAwait(false);
        ProcessResult? rustSharpCompile = null;
        ProcessResult? rustSharpRun = null;
        if (rustSharpCheck.Succeeded || fixture.Expectation is "compile-fail" or "rustsharp-fail")
        {
            string output = Path.Combine(caseDirectory, "rustsharp.dll");
            rustSharpCompile = await RunProcessAsync(runner, Dotnet, BuildRustSharpArguments(root, "compile", source, "--output", output, "--profile", CompilerProfile), root, timeout, cancellationToken).ConfigureAwait(false);
            if (rustSharpCompile.Succeeded && fixture.Expectation == "run-pass") rustSharpRun = await RunProcessAsync(runner, Dotnet, [output], caseDirectory, timeout, cancellationToken).ConfigureAwait(false);
        }
        bool passed = fixture.Expectation == "compile-fail"
            ? MatchesCompileFailure(rustcCompile.ToEvidence(), fixture.RustcDiagnostic!) && MatchesCompileFailure(rustSharpCheck.ToEvidence(), fixture.RustSharpDiagnostic!) && rustSharpCompile is not null && MatchesCompileFailure(rustSharpCompile.ToEvidence(), fixture.RustSharpDiagnostic!)
            : fixture.Expectation == "rustsharp-fail"
                ? rustcCompile.Succeeded && MatchesCompileFailure(rustSharpCheck.ToEvidence(), fixture.RustSharpDiagnostic!) && rustSharpCompile is not null && MatchesCompileFailure(rustSharpCompile.ToEvidence(), fixture.RustSharpDiagnostic!)
            : fixture.Kind == "compile-pass" ? rustcCompile.Succeeded && rustSharpCheck.Succeeded : rustcCompile.Succeeded && rustcRun?.Succeeded == true && rustSharpCheck.Succeeded && rustSharpCompile?.Succeeded == true && rustSharpRun?.Succeeded == true && string.IsNullOrEmpty(Normalize(rustcRun.StandardError)) && string.IsNullOrEmpty(Normalize(rustSharpRun.StandardError)) && Normalize(rustcRun.StandardOutput) == Normalize(rustSharpRun.StandardOutput) && Normalize(rustcRun.StandardOutput) == Normalize(fixture.ExpectedOutput);
        string? difference = passed ? null : fixture.Expectation == "compile-fail" ? "Expected rustc " + fixture.RustcDiagnostic + " and RustSharp " + fixture.RustSharpDiagnostic + "; rustc=" + Trim(rustcCompile.StandardError) + "; RustSharp=" + Trim(rustSharpCheck.StandardError + rustSharpCompile?.StandardError) : fixture.Expectation == "rustsharp-fail" ? "Expected RustSharp " + fixture.RustSharpDiagnostic + " while rustc accepted the source; RustSharp=" + Trim(rustSharpCheck.StandardError + rustSharpCompile?.StandardError) : "Compiler or runtime output differed from the fixed v2 contract.";
        return new(fixture.Id, fixture.File, fixture.Kind, fixture.Area, fixture.Expectation, passed ? "passed" : "failed", difference, fixture.ExpectedOutput, rustcCompile.ToEvidence(), rustcRun?.ToEvidence(), rustSharpCheck.ToEvidence(), rustSharpCompile?.ToEvidence(), rustSharpRun?.ToEvidence()) { SourceSha256 = sourceSha, RustcDiagnostic = fixture.RustcDiagnostic, RustSharpDiagnostic = fixture.RustSharpDiagnostic };
    }

    private static CaseReport Blocked(Fixture fixture, string reason) => new(fixture.Id, fixture.File, fixture.Kind, fixture.Area, fixture.Expectation, "blocked", reason, fixture.ExpectedOutput, ProcessEvidence.Empty, null, ProcessEvidence.Empty, null, null) { RustcDiagnostic = fixture.RustcDiagnostic, RustSharpDiagnostic = fixture.RustSharpDiagnostic };

    private static async Task<ProcessResult> RunProcessAsync(BoundedProcessRunner runner, string executable, List<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (arguments.Count > 32) throw new ArgumentException("Process argument bound exceeded.");
        return new(await runner.RunAsync(new BoundedProcessRequest(executable, arguments, workingDirectory, timeout), cancellationToken).ConfigureAwait(false));
    }

    private static List<string> BuildRustSharpArguments(string root, params string[] arguments)
    {
        string cli = Path.Combine(root, "src", "RustSharp.Cli", "bin", "Release", "net10.0", "rsc.dll");
        var result = new List<string> { cli };
        result.AddRange(arguments);
        return result;
    }

    private sealed record ProcessResult(BoundedProcessResult Result)
    {
        public bool Succeeded => Result.Succeeded && !Result.OutputTruncated && !Result.OutputReadTimedOut && !Result.OutputDrainTimedOut && !Result.OutputReadLimitReached && !Result.ProcessTreeCleanupIncomplete;
        public string StandardOutput => Result.StandardOutput;
        public string StandardError => Result.StandardError;
        public ProcessEvidence ToEvidence() => new(Result.StartedProcess.CommandLine, Result.StartedProcess.ProcessId, Result.StartedProcess.ParentProcessId, Result.StartedProcess.StartedAt, Result.ExitCode, Result.Termination.ToString().ToLowerInvariant(), Result.Elapsed.TotalMilliseconds, Result.StandardOutput, Result.StandardError, Result.OutputTruncated, Result.OutputReadTimedOut, Result.OutputDrainTimedOut, Result.OutputReadLimitReached, Result.ProcessTreeCleanupAttempted, Result.ProcessTreeCleanupIncomplete, Result.OutputDiagnostic ?? Result.ProcessTreeCleanupDiagnostic);
    }

    private static Limits ParseLimits(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out JsonElement value) || value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Manifest limits are required.");
        return new(RequiredInt(value, "maximumCases"), RequiredInt(value, "maximumManifestBytes"), RequiredInt(value, "maximumFixtureBytes"), RequiredInt(value, "caseTimeoutSeconds"), RequiredInt(value, "deadlineSeconds"));
    }

    private static Dictionary<string, int> ParseCoverage(JsonElement root)
    {
        if (!root.TryGetProperty("coverage", out JsonElement value) || value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Manifest coverage is required.");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject()) result.Add(property.Name, RequiredInt(value, property.Name));
        return result;
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var stack = new Stack<HashSet<string>>(MaximumJsonDepth);
        bool reachedEnd = false;
        for (int token = 0; token < MaximumJsonTokens; token++)
        {
            if (!reader.Read()) { reachedEnd = true; break; }
            if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.PropertyName && (stack.Count == 0 || !stack.Peek().Add(reader.GetString()!))) throw new JsonException("Duplicate or out-of-object JSON property.");
            else if (reader.TokenType == JsonTokenType.EndObject && stack.Count > 0) stack.Pop();
        }
        if (!reachedEnd || stack.Count != 0) throw new JsonException("Manifest JSON is incomplete or exceeds its token bound.");
    }

    private static string RequiredString(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new ArgumentException("Manifest property '" + name + "' must be a non-empty string.");
    private static string? OptionalString(JsonElement parent, string name) => !parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException("Manifest property '" + name + "' must be a string.");
    private static int RequiredInt(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : throw new ArgumentException("Manifest property '" + name + "' must be an integer.");
    private static string? FirstLine(string text, string prefix) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(static line => line.Trim()).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string Trim(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 512 ? value : value[..512] + "...";
    private static HostReport CurrentHost() => new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription, RuntimeInformation.RuntimeIdentifier);
    private static string? TryDeleteDirectory(string path)
    {
        var clock = Stopwatch.StartNew();
        Exception? last = null;
        for (int attempt = 0; attempt < MaximumCleanupAttempts && clock.Elapsed < CleanupTimeout; attempt++)
        {
            if (!Directory.Exists(path)) return null;
            try { Directory.Delete(path, true); } catch (IOException exception) { last = exception; } catch (UnauthorizedAccessException exception) { last = exception; }
            if (!Directory.Exists(path)) return null;
            Thread.Sleep(50);
        }
        return "v2 run directory cleanup failed: " + (last?.Message ?? "directory still exists");
    }
}
