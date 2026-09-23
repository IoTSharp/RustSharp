using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>
/// Executes the fixed source-level borrow/Drop differential corpus.  This is
/// deliberately separate from the in-process P1 probes: every case goes
/// through rustc and the RustSharp CLI and retains process provenance.
/// </summary>
internal static class P1DifferentialProfileRunner
{
    internal const string ProfileName = "p1-differential-v1";
    internal const string ProfileV2Name = "p1-differential-v2";
    internal const string ManifestFileName = "p1-differential-manifest.json";
    internal const string ManifestV2FileName = "p1-differential-v2-manifest.json";
    internal const int ManifestVersion = 1;
    internal const string RustVersion = "1.98.0";
    internal const string Edition = "2024";
    internal const string CompilerProfile = "safe-core-mir-p1-v2";
    internal const int MaximumCases = 16;
    internal const int MaximumManifestBytes = 256 * 1024;
    internal const int MaximumFixtureBytes = 1024 * 1024;
    internal const int MaximumTimeoutSeconds = 300;
    internal const int MaximumDeadlineSeconds = 900;
    private const int MaximumJsonDepth = 24;
    private const int MaximumJsonTokens = 4096;
    private const int MaximumIdLength = 96;
    private const int MaximumArgumentCount = 32;
    private const string Rustc = "rustc";
    private const string Dotnet = "dotnet";
    private static readonly string[] V2CaseIds =
    [
        "borrow-shared", "borrow-mutable-reborrow", "drop-return-order", "drop-early-return",
        "borrow-write-read", "borrow-shared-after-update", "borrow-mutable-reborrow-chain", "borrow-shared-reborrow",
        "borrow-fail-mut-alias", "borrow-fail-shared-write", "borrow-fail-moved-mut-ref", "borrow-fail-escape",
        "drop-reverse-locals", "drop-nested-scopes", "drop-return-reverse", "drop-branch-return",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal sealed record Fixture(string Id, string File, string Kind, string? ExpectedOutput)
    {
        public string Expectation { get; init; } = "run-pass";
        public string? RustcDiagnostic { get; init; }
        public string? RustSharpDiagnostic { get; init; }
    }

    internal sealed record Limits(
        int MaximumCases,
        int MaximumManifestBytes,
        int MaximumFixtureBytes,
        int CaseTimeoutSeconds,
        int DeadlineSeconds);

    internal sealed record Manifest(
        string Profile,
        int Version,
        string RustVersion,
        string Edition,
        string CompilerProfile,
        int Denominator,
        IReadOnlyList<Fixture> Cases)
    {
        public Limits? DeclaredLimits { get; init; }
        public int BorrowCount => Cases.Count(static item => item.Kind == "borrow");
        public int DropCount => Cases.Count(static item => item.Kind == "drop");
    }

    internal sealed record CaseReport(
        string Id,
        string Source,
        string Kind,
        string Status,
        string? Difference,
        string? ExpectedOutput,
        ProcessEvidence RustcCompile,
        ProcessEvidence? RustcRun,
        ProcessEvidence RustSharpCheck,
        ProcessEvidence? RustSharpCompile,
        ProcessEvidence? RustSharpRun)
    {
        public string Expectation { get; init; } = "run-pass";
        public string? SourceSha256 { get; init; }
        public string? RustcDiagnostic { get; init; }
        public string? RustSharpDiagnostic { get; init; }
        public static CaseReport Blocked(Fixture fixture, string reason) => new(
            fixture.Id, fixture.File, fixture.Kind, "blocked", reason, fixture.ExpectedOutput,
            ProcessEvidence.Empty, null, ProcessEvidence.Empty, null, null)
        {
            Expectation = fixture.Expectation,
            RustcDiagnostic = fixture.RustcDiagnostic,
            RustSharpDiagnostic = fixture.RustSharpDiagnostic,
        };
    }

    internal sealed record ProcessEvidence(
        string CommandLine,
        int ProcessId,
        int ParentProcessId,
        DateTimeOffset? StartedAtUtc,
        int? ExitCode,
        string Termination,
        double ElapsedMilliseconds,
        string StandardOutput,
        string StandardError,
        bool OutputTruncated,
        bool OutputReadTimedOut,
        bool OutputDrainTimedOut,
        bool OutputReadLimitReached,
        bool CleanupAttempted,
        bool CleanupIncomplete,
        string? Diagnostic)
    {
        public static ProcessEvidence Empty => new(
            "", 0, 0, null, null, "not-started", 0, "", "", false, false, false, false, false, false, null);
    }

    internal sealed record ToolReport(
        string Name,
        string Executable,
        string RequestedVersion,
        string? Version,
        bool Available,
        string? Diagnostic,
        ProcessEvidence Probe);

    internal sealed record Summary(
        string Status,
        int ExitCode,
        int Denominator,
        int Executed,
        int Passed,
        int Failed,
        int Blocked,
        int Skipped,
        int BorrowDenominator,
        int DropDenominator);

    internal sealed record HostReport(
        string OperatingSystem,
        string Architecture,
        string ProcessArchitecture,
        string Framework,
        string RuntimeIdentifier);

    internal sealed record Report(
        int SchemaVersion,
        string EvidenceKind,
        string Profile,
        DateTimeOffset GeneratedAtUtc,
        string ManifestPath,
        string ManifestSha256,
        long ManifestBytes,
        bool ManifestValidated,
        string? ManifestError,
        ToolReport Oracle,
        ToolReport RustSharp,
        HostReport Host,
        double CaseTimeoutSeconds,
        double DeadlineSeconds,
        Summary Summary,
        IReadOnlyList<CaseReport> Cases,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        double ElapsedMilliseconds,
        bool DeadlineExpired,
        string? CleanupDiagnostic,
        string? HarnessError);

    internal static Manifest ParseManifest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new ArgumentException("P1 differential manifest exceeds its byte bound.", nameof(json));
        ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(json));
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = MaximumJsonDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("P1 differential manifest root must be an object.", nameof(json));
        string profile = RequiredString(root, "profile");
        int version = RequiredInt(root, "version");
        string rustVersion = RequiredString(root, "rustVersion");
        string edition = RequiredString(root, "edition");
        string compilerProfile = RequiredString(root, "compilerProfile");
        int denominator = RequiredInt(root, "denominator");
        Limits? limits = null;
        if (root.TryGetProperty("limits", out JsonElement limitsElement))
        {
            if (limitsElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("P1 differential manifest limits must be an object.", nameof(json));
            limits = new(
                RequiredInt(limitsElement, "maximumCases"),
                RequiredInt(limitsElement, "maximumManifestBytes"),
                RequiredInt(limitsElement, "maximumFixtureBytes"),
                RequiredInt(limitsElement, "caseTimeoutSeconds"),
                RequiredInt(limitsElement, "deadlineSeconds"));
        }
        if (!root.TryGetProperty("cases", out JsonElement casesElement) || casesElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("P1 differential manifest must contain a cases array.", nameof(json));
        var cases = new List<Fixture>();
        foreach (JsonElement item in casesElement.EnumerateArray())
        {
            if (cases.Count >= MaximumCases) throw new ArgumentException("P1 differential manifest exceeds its case bound.", nameof(json));
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("P1 differential case must be an object.", nameof(json));
            cases.Add(new(
                RequiredString(item, "id"),
                RequiredString(item, "file"),
                RequiredString(item, "kind"),
                OptionalString(item, "expectedOutput"))
            {
                Expectation = OptionalString(item, "expectation") ?? "run-pass",
                RustcDiagnostic = OptionalString(item, "rustcDiagnostic"),
                RustSharpDiagnostic = OptionalString(item, "rustSharpDiagnostic"),
            });
        }
        var manifest = new Manifest(profile, version, rustVersion, edition, compilerProfile, denominator, cases)
        {
            DeclaredLimits = limits,
        };
        ValidateManifest(manifest);
        if (manifest.Version == 2)
        {
            if (!root.TryGetProperty("coverage", out JsonElement coverage) || coverage.ValueKind != JsonValueKind.Object ||
                RequiredInt(coverage, "borrow") != 10 || RequiredInt(coverage, "drop") != 6 ||
                RequiredInt(coverage, "compile-fail") != 4 || RequiredInt(coverage, "run-pass") != 12 ||
                coverage.EnumerateObject().Count() != 4)
                throw new ArgumentException("P1 differential v2 coverage must match its fixed denominator.", nameof(json));
        }
        return manifest;
    }

    internal static void ValidateManifest(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!(manifest.Profile == ProfileName && manifest.Version == ManifestVersion ||
              manifest.Profile == ProfileV2Name && manifest.Version == 2) ||
            manifest.RustVersion != RustVersion || manifest.Edition != Edition ||
            manifest.CompilerProfile != CompilerProfile)
            throw new ArgumentException("P1 differential manifest version or profile contract is unsupported.", nameof(manifest));
        if (manifest.Denominator is < 1 or > MaximumCases || manifest.Cases.Count != manifest.Denominator)
            throw new ArgumentException("P1 differential denominator must equal its bounded case count.", nameof(manifest));
        if (manifest.DeclaredLimits is Limits limits &&
            (limits.MaximumCases is < 1 or > MaximumCases ||
             limits.MaximumManifestBytes is < 1 or > MaximumManifestBytes ||
             limits.MaximumFixtureBytes is < 1 or > MaximumFixtureBytes ||
             limits.CaseTimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
             limits.DeadlineSeconds is < 1 or > MaximumDeadlineSeconds ||
             manifest.Denominator > limits.MaximumCases))
            throw new ArgumentException("P1 differential limits must be positive, bounded and cover the denominator.", nameof(manifest));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Fixture fixture in manifest.Cases)
        {
            if (fixture.Id.Length is 0 or > MaximumIdLength ||
                !fixture.Id.All(static c => c is >= 'a' and <= 'z' || char.IsAsciiDigit(c) || c == '-') || !ids.Add(fixture.Id))
                throw new ArgumentException("P1 differential case IDs must be unique lowercase file-safe names.", nameof(manifest));
            if (Path.GetFileName(fixture.File) != fixture.File || !fixture.File.EndsWith(".rs", StringComparison.Ordinal) ||
                fixture.File.Length > MaximumIdLength || fixture.File.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException("P1 differential fixture file name is unsafe.", nameof(manifest));
            if (fixture.Kind is not ("borrow" or "drop"))
                throw new ArgumentException("P1 differential case kind must be borrow or drop.", nameof(manifest));
            if (fixture.Expectation is not ("run-pass" or "compile-fail") ||
                manifest.Version == 1 && fixture.Expectation != "run-pass")
                throw new ArgumentException("P1 differential expectation is unsupported by its version.", nameof(manifest));
            if (fixture.Expectation == "compile-fail" &&
                (fixture.Kind != "borrow" || fixture.ExpectedOutput is not null ||
                 fixture.RustcDiagnostic is not ("E0499" or "E0506" or "E0382" or "E0597") ||
                 fixture.RustSharpDiagnostic is not ("RSO1001" or "RSO1002" or "RSO1005")))
                throw new ArgumentException("Compile-fail cases require exact ownership diagnostic contracts and no runtime output.", nameof(manifest));
            if (fixture.Expectation == "run-pass" &&
                (fixture.ExpectedOutput is null || fixture.RustcDiagnostic is not null || fixture.RustSharpDiagnostic is not null))
                throw new ArgumentException("Run-pass cases require expected output and no compile-fail diagnostics.", nameof(manifest));
            if (Encoding.UTF8.GetByteCount(fixture.ExpectedOutput ?? "") > MaximumFixtureBytes)
                throw new ArgumentException("P1 differential expected output exceeds its bound.", nameof(manifest));
        }
        if (manifest.BorrowCount == 0 || manifest.DropCount == 0)
            throw new ArgumentException("P1 differential denominator must contain both borrow and drop cases.", nameof(manifest));
        if (manifest.Version == 2 && (manifest.DeclaredLimits is null || manifest.Denominator != 16 || manifest.BorrowCount != 10 ||
            manifest.DropCount != 6 || manifest.Cases.Count(static item => item.Expectation == "compile-fail") != 4 ||
            !manifest.Cases.Select(static item => item.Id).SequenceEqual(V2CaseIds, StringComparer.Ordinal)))
            throw new ArgumentException("P1 differential v2 fixes 16 cases: 10 borrow, 6 Drop, including 4 compile-fail cases.", nameof(manifest));
    }

    public static async Task<int> RunAsync(
        string repositoryRoot,
        string reportPath,
        TimeSpan timeout,
        TimeSpan deadline,
        DateTimeOffset startedAtUtc,
        Stopwatch harnessClock,
        string profile = ProfileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(MaximumDeadlineSeconds))
            throw new ArgumentOutOfRangeException(nameof(deadline));

        string root = Path.GetFullPath(repositoryRoot);
        string fullReportPath = Path.GetFullPath(reportPath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath)!);
        if (profile is not (ProfileName or ProfileV2Name)) throw new ArgumentException("Unknown P1 differential profile.", nameof(profile));
        string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", profile == ProfileName ? ManifestFileName : ManifestV2FileName);
        string relativeManifestPath = Path.GetRelativePath(root, manifestPath).Replace(Path.DirectorySeparatorChar, '/');
        Manifest? manifest = null;
        string? manifestError = null;
        string manifestSha = "";
        long manifestBytes = 0;
        try
        {
            var fileInfo = new FileInfo(manifestPath);
            if (fileInfo.Length is < 1 or > MaximumManifestBytes) throw new ArgumentException("P1 differential manifest size exceeds its bound.");
            byte[] bytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            manifestBytes = bytes.Length;
            manifestSha = Convert.ToHexString(SHA256.HashData(bytes));
            manifest = ParseManifest(Encoding.UTF8.GetString(bytes));
            if (manifest.Profile != profile) throw new ArgumentException("P1 differential manifest does not match the requested profile.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            manifestError = Trim(exception.Message);
            manifest = null;
        }

        TimeSpan effectiveTimeout = timeout;
        TimeSpan effectiveDeadline = deadline;
        if (manifest?.DeclaredLimits is Limits declaredLimits)
        {
            effectiveTimeout = TimeSpan.FromSeconds(Math.Min(effectiveTimeout.TotalSeconds, declaredLimits.CaseTimeoutSeconds));
            effectiveDeadline = TimeSpan.FromSeconds(Math.Min(effectiveDeadline.TotalSeconds, declaredLimits.DeadlineSeconds));
        }
        using var cancellation = new CancellationTokenSource(effectiveDeadline);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        string runDirectory = Path.Combine(Path.GetDirectoryName(fullReportPath)!, ".run-p1-differential-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        var cases = new List<CaseReport>(manifest?.Cases.Count ?? 0);
        string? harnessError = manifestError;
        string? cleanupDiagnostic = null;
        ToolReport oracle = Unavailable("rustc", Rustc, RustVersion, manifestError ?? "Not probed.");
        ToolReport rustSharp = Unavailable("rustsharp", Dotnet, "release CLI", manifestError ?? "Not probed.");
        try
        {
            if (manifest is not null)
            {
                Directory.CreateDirectory(runDirectory);
                var runner = new BoundedProcessRunner();
                ProcessResult oracleProbe = await RunProcessAsync(runner, Rustc, ["+" + RustVersion, "--version"], root, effectiveTimeout, cancellation.Token).ConfigureAwait(false);
                string? oracleVersion = FirstLine(oracleProbe.StandardOutput, "rustc ") ?? FirstLine(oracleProbe.StandardError, "rustc ");
                bool oracleAvailable = oracleProbe.Succeeded && IsRequestedRustcVersion(oracleVersion);
                oracle = new("rustc", Rustc, RustVersion, oracleVersion, oracleAvailable, oracleAvailable ? null : "rustc 1.98.0 oracle is unavailable: " + Trim(oracleProbe.StandardError), oracleProbe.ToEvidence());
                ProcessResult rustSharpProbe = await RunProcessAsync(runner, Dotnet, BuildRustSharpArguments(root, "--version"), root, effectiveTimeout, cancellation.Token).ConfigureAwait(false);
                string? rustSharpVersion = FirstLine(rustSharpProbe.StandardOutput, "rsc ") ?? FirstLine(rustSharpProbe.StandardError, "rsc ");
                bool rustSharpAvailable = rustSharpProbe.Succeeded && rustSharpVersion is not null;
                rustSharp = new("rustsharp", Dotnet, "release CLI", rustSharpVersion, rustSharpAvailable, rustSharpAvailable ? null : "RustSharp CLI is unavailable: " + Trim(rustSharpProbe.StandardError), rustSharpProbe.ToEvidence());
                foreach (Fixture fixture in manifest.Cases)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        cases.Add(CaseReport.Blocked(fixture, "P1 differential deadline expired before this case started."));
                    }
                    else if (!oracleAvailable || !rustSharpAvailable)
                    {
                        cases.Add(CaseReport.Blocked(fixture, oracle.Diagnostic ?? rustSharp.Diagnostic ?? "Required tool unavailable."));
                    }
                    else
                    {
                        try
                        {
                            cases.Add(await RunCaseAsync(runner, root, runDirectory, fixture, effectiveTimeout, cancellation.Token).ConfigureAwait(false));
                        }
                        catch (OperationCanceledException)
                        {
                            cases.Add(CaseReport.Blocked(fixture, "P1 differential case deadline or cancellation prevented complete evidence."));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            harnessError ??= "P1 differential deadline expired.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            harnessError ??= Trim(exception.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            cleanupDiagnostic = TryDeleteDirectory(runDirectory);
        }
        if (manifest is not null)
        {
            for (int index = cases.Count; index < manifest.Cases.Count && index < MaximumCases; index++)
                cases.Add(CaseReport.Blocked(manifest.Cases[cases.Count], harnessError ?? "P1 differential case did not start."));
        }
        harnessClock.Stop();
        int denominator = manifest?.Denominator ?? 0;
        int passed = cases.Count(static item => item.Status == "passed");
        int failed = cases.Count(static item => item.Status == "failed");
        int blocked = cases.Count(static item => item.Status == "blocked");
        int skipped = cases.Count(static item => item.Status == "skipped");
        string status = manifest is null || harnessError is not null || cleanupDiagnostic is not null || cancellation.IsCancellationRequested || blocked > 0
            ? "blocked"
            : failed > 0 ? "failed" : passed == denominator && skipped == 0 ? "passed" : "blocked";
        var summary = new Summary(status, status == "passed" ? 0 : status == "failed" ? 1 : 2, denominator, passed + failed, passed, failed, blocked, skipped, manifest?.BorrowCount ?? 0, manifest?.DropCount ?? 0);
        var report = new Report(profile == ProfileV2Name ? 2 : 1, "p1-source-borrow-drop-differential", profile, DateTimeOffset.UtcNow, relativeManifestPath, manifestSha, manifestBytes, manifest is not null, manifestError, oracle, rustSharp, CurrentHost(), effectiveTimeout.TotalSeconds, effectiveDeadline.TotalSeconds, summary, cases, startedAtUtc, DateTimeOffset.UtcNow, harnessClock.Elapsed.TotalMilliseconds, cancellation.IsCancellationRequested, cleanupDiagnostic, harnessError);
        await WriteReportAsync(fullReportPath, report).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return summary.ExitCode;
    }

    private static async Task<CaseReport> RunCaseAsync(BoundedProcessRunner runner, string root, string runDirectory, Fixture fixture, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string source = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", fixture.File);
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length is < 1 or > MaximumFixtureBytes)
            throw new InvalidOperationException("P1 differential fixture size exceeds its bound: " + fixture.File);
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false)));
        using var caseDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        caseDeadline.CancelAfter(timeout);
        cancellationToken = caseDeadline.Token;
        string directory = Path.Combine(runDirectory, fixture.Id);
        Directory.CreateDirectory(directory);
        string oracleOutput = Path.Combine(directory, "oracle.exe");
        ProcessResult rustcCompile = await RunProcessAsync(runner, Rustc, ["+" + RustVersion, source, "--edition", Edition, "-C", "overflow-checks=yes", "-o", oracleOutput], root, timeout, cancellationToken).ConfigureAwait(false);
        ProcessResult? rustcRun = fixture.Expectation == "run-pass" && rustcCompile.Succeeded ? await RunProcessAsync(runner, oracleOutput, [], directory, timeout, cancellationToken).ConfigureAwait(false) : null;
        ProcessResult rustSharpCheck = await RunProcessAsync(runner, Dotnet, BuildRustSharpArguments(root, "check", source, "--profile", CompilerProfile), root, timeout, cancellationToken).ConfigureAwait(false);
        ProcessResult? rustSharpCompile = null;
        ProcessResult? rustSharpRun = null;
        if (rustSharpCheck.Succeeded || fixture.Expectation == "compile-fail")
        {
            string output = Path.Combine(directory, "rustsharp.dll");
            rustSharpCompile = await RunProcessAsync(runner, Dotnet, BuildRustSharpArguments(root, "compile", source, "--output", output, "--profile", CompilerProfile), root, timeout, cancellationToken).ConfigureAwait(false);
            if (rustSharpCompile.Succeeded && fixture.Expectation == "run-pass")
                rustSharpRun = await RunProcessAsync(runner, Dotnet, [output], directory, timeout, cancellationToken).ConfigureAwait(false);
        }
        bool passed = fixture.Expectation == "compile-fail"
            ? MatchesCompileFailure(rustcCompile.ToEvidence(), fixture.RustcDiagnostic!) &&
              MatchesCompileFailure(rustSharpCheck.ToEvidence(), fixture.RustSharpDiagnostic!) &&
              rustSharpCompile is not null && MatchesCompileFailure(rustSharpCompile.ToEvidence(), fixture.RustSharpDiagnostic!)
            : rustcCompile.Succeeded && rustcRun?.Succeeded == true && rustSharpCheck.Succeeded && rustSharpCompile?.Succeeded == true && rustSharpRun?.Succeeded == true &&
            string.IsNullOrEmpty(Normalize(rustcRun.StandardError)) && string.IsNullOrEmpty(Normalize(rustSharpRun.StandardError)) &&
            Normalize(rustcRun.StandardOutput) == Normalize(rustSharpRun.StandardOutput) && Normalize(rustcRun.StandardOutput) == Normalize(fixture.ExpectedOutput);
        string? difference = passed ? null : fixture.Expectation == "compile-fail"
            ? "Expected rustc error " + fixture.RustcDiagnostic + " and RustSharp check/compile error " + fixture.RustSharpDiagnostic +
              "; rustc=" + Trim(rustcCompile.StandardError) + "; RustSharp check=" + Trim(rustSharpCheck.StandardError) +
              "; RustSharp compile=" + Trim(rustSharpCompile?.StandardError ?? "not started")
            : DescribeDifference(rustcCompile, rustcRun, rustSharpCheck, rustSharpCompile, rustSharpRun, fixture.ExpectedOutput!);
        return new(fixture.Id, fixture.File, fixture.Kind, passed ? "passed" : "failed", difference, fixture.ExpectedOutput,
            rustcCompile.ToEvidence(), rustcRun?.ToEvidence(), rustSharpCheck.ToEvidence(), rustSharpCompile?.ToEvidence(), rustSharpRun?.ToEvidence())
        {
            Expectation = fixture.Expectation,
            SourceSha256 = sourceSha256,
            RustcDiagnostic = fixture.RustcDiagnostic,
            RustSharpDiagnostic = fixture.RustSharpDiagnostic,
        };
    }

    internal static bool IsRequestedRustcVersion(string? version) =>
        version?.StartsWith("rustc " + RustVersion + " (", StringComparison.Ordinal) == true;

    internal static bool MatchesCompileFailure(ProcessEvidence evidence, string diagnostic) =>
        evidence.Termination == "exited" && evidence.ExitCode == 1 && evidence.ProcessId > 0 &&
        !evidence.OutputTruncated && !evidence.OutputReadTimedOut && !evidence.OutputDrainTimedOut &&
        !evidence.OutputReadLimitReached && !evidence.CleanupIncomplete &&
        (evidence.StandardError.Contains("error[" + diagnostic + "]:", StringComparison.Ordinal) ||
         evidence.StandardError.Contains("error " + diagnostic + ":", StringComparison.Ordinal));

    private static string DescribeDifference(ProcessResult rustcCompile, ProcessResult? rustcRun, ProcessResult rustSharpCheck, ProcessResult? rustSharpCompile, ProcessResult? rustSharpRun, string expected)
    {
        if (!rustcCompile.Succeeded) return "rustc compile failed: " + Trim(rustcCompile.StandardError);
        if (rustcRun is not null && !rustcRun.Succeeded) return "rustc run failed: " + Trim(rustcRun.StandardError);
        if (!rustSharpCheck.Succeeded) return "RustSharp check failed: " + Trim(rustSharpCheck.StandardError + rustSharpCheck.StandardOutput);
        if (rustSharpCompile is not null && !rustSharpCompile.Succeeded) return "RustSharp compile failed: " + Trim(rustSharpCompile.StandardError + rustSharpCompile.StandardOutput);
        if (rustSharpRun is not null && !rustSharpRun.Succeeded) return "RustSharp run failed: " + Trim(rustSharpRun.StandardError);
        if (rustcRun is not null && Normalize(rustcRun.StandardOutput) != Normalize(expected)) return "rustc output did not match manifest expected output.";
        return "RustSharp output or stderr differed from rustc 1.98.0.";
    }

    private sealed record ProcessResult(BoundedProcessResult Result)
    {
        public bool Succeeded => Result.Succeeded && !Result.OutputTruncated && !Result.OutputReadTimedOut && !Result.OutputDrainTimedOut && !Result.OutputReadLimitReached && !Result.ProcessTreeCleanupIncomplete;
        public string StandardOutput => Result.StandardOutput;
        public string StandardError => Result.StandardError;
        public ProcessEvidence ToEvidence() => new(Result.StartedProcess.CommandLine, Result.StartedProcess.ProcessId, Result.StartedProcess.ParentProcessId, Result.StartedProcess.StartedAt, Result.ExitCode, Result.Termination.ToString().ToLowerInvariant(), Result.Elapsed.TotalMilliseconds, Result.StandardOutput, Result.StandardError, Result.OutputTruncated, Result.OutputReadTimedOut, Result.OutputDrainTimedOut, Result.OutputReadLimitReached, Result.ProcessTreeCleanupAttempted, Result.ProcessTreeCleanupIncomplete, Result.OutputDiagnostic ?? Result.ProcessTreeCleanupDiagnostic);
    }

    private static async Task<ProcessResult> RunProcessAsync(BoundedProcessRunner runner, string executable, List<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (arguments.Count > MaximumArgumentCount) throw new ArgumentException("Process argument bound exceeded.");
        try
        {
            return new(await runner.RunAsync(new BoundedProcessRequest(executable, arguments, workingDirectory, timeout), cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(executable + " could not be started: " + exception.Message, exception);
        }
    }

    private static List<string> BuildRustSharpArguments(string root, params string[] arguments)
    {
        string cli = Path.Combine(root, "src", "RustSharp.Cli", "bin", "Release", "net10.0", "rsc.dll");
        var result = File.Exists(cli) ? new List<string> { cli } : new List<string> { "run", "--project", Path.Combine(root, "src", "RustSharp.Cli"), "-c", "Release", "--no-build", "--no-restore", "--" };
        result.AddRange(arguments);
        return result;
    }

    private static ToolReport Unavailable(string name, string executable, string requested, string diagnostic) => new(name, executable, requested, null, false, diagnostic, ProcessEvidence.Empty);
    private static HostReport CurrentHost() => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
    private static string? FirstLine(string output, string prefix) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
    private static string Normalize(string? value) => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string Trim(string value) => value.Length <= 512 ? value : value[..512] + "...";

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new ArgumentException("Manifest property '" + name + "' must be a non-empty string.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Manifest property '" + name + "' must be a string.");
        return value.GetString();
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)) throw new ArgumentException("Manifest property '" + name + "' must be an integer.");
        return result;
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var objects = new Stack<HashSet<string>>(MaximumJsonDepth);
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > MaximumJsonTokens) throw new JsonException("Manifest exceeds its JSON token bound.");
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.PropertyName && (objects.Count == 0 || !objects.Peek().Add(reader.GetString() ?? ""))) throw new JsonException("Manifest contains a duplicate property.");
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
        }
    }

    private static async Task WriteReportAsync(string path, Report report)
    {
        string temporary = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return null;
        Exception? last = null;
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { Directory.Delete(path, true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { last = exception; }
            if (!Directory.Exists(path)) return null;
            Thread.Sleep(50);
        }
        return "P1 differential run directory cleanup failed: " + (last?.Message ?? "directory still exists");
    }
}
