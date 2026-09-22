using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>
/// Runs the bounded, manifest-driven executable safe-core regression corpus.
/// </summary>
internal static partial class SafeCoreRegressionProfileRunner
{
    internal const string ProfileName = "safe-core-regression-v1";
    internal const string ManifestFileName = "safe-core-regression-manifest.json";
    internal const int ManifestVersion = 1;
    internal const string RustVersion = "1.98.0";
    internal const string Edition = "2024";
    internal const string CompilerProfile = "safe-core-primitives-v1";
    internal const int MaximumCases = 64;
    internal const int MaximumManifestBytes = 4 * 1024 * 1024;
    internal const int MaximumFixtureBytes = 1 * 1024 * 1024;
    internal const int MaximumIdLength = 96;
    internal const int MaximumDiagnosticLength = 512;
    internal const int MaximumArgumentCount = 32;
    internal const int MaximumCleanupAttempts = 40;
    internal const int MaximumTimeoutSeconds = 300;
    internal const int MaximumDeadlineSeconds = 900;
    private const int MaximumManifestDepth = 32;
    private const int MaximumJsonTokens = 8_192;
    private const string OracleExecutable = "rustc";
    private const string OracleToolchain = "1.98.0";
    private const string RustSharpExecutable = "dotnet";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static IReadOnlyList<string> Kinds { get; } =
        ["compile-pass", "compile-fail", "run-pass", "differential"];

    internal sealed record RegressionManifest(
        string Profile,
        int Version,
        string RustVersion,
        string Edition,
        string CompilerProfile,
        int Denominator,
        IReadOnlyList<RegressionFixture> Cases)
    {
        // These declarations are part of the versioned manifest contract. They
        // remain optional on the in-memory model so focused unit tests can
        // construct a minimal manifest without serializing JSON first.
        public RegressionManifestLimits? DeclaredLimits { get; init; }
        public IReadOnlyDictionary<string, int>? DeclaredCoverage { get; init; }
    }

    internal sealed record RegressionManifestLimits(
        int MaximumCases,
        int MaximumManifestBytes,
        int MaximumFixtureBytes,
        int CaseTimeoutSeconds,
        int DeadlineSeconds);

    internal sealed record RegressionFixture(
        string Id,
        string File,
        string Kind,
        string? ExpectedOutput,
        string? DiagnosticContains = null);

    internal sealed record ManifestValidation(
        bool Validated,
        RegressionManifest? Manifest,
        string? Error,
        string Sha256,
        long ByteLength,
        string RelativePath);

    internal static RegressionManifest ParseManifest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumManifestBytes)
        {
            throw new ArgumentException("Regression manifest exceeds " + MaximumManifestBytes + " bytes.", nameof(json));
        }

        ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(json));
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = MaximumManifestDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        return ParseManifest(document.RootElement);
    }

    internal static void ValidateManifest(RegressionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Profile, ProfileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Manifest profile must be '" + ProfileName + "'.", nameof(manifest));
        }

        if (manifest.Version != ManifestVersion ||
            !string.Equals(manifest.RustVersion, RustVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.Edition, Edition, StringComparison.Ordinal) ||
            !string.Equals(manifest.CompilerProfile, CompilerProfile, StringComparison.Ordinal))
        {
            throw new ArgumentException("Regression manifest version, Rust version, edition or compiler profile is unsupported.", nameof(manifest));
        }

        if (manifest.Denominator is < 1 or > MaximumCases || manifest.Cases.Count != manifest.Denominator)
        {
            throw new ArgumentException(
                "Regression manifest denominator must equal its 1.." + MaximumCases + " case count.",
                nameof(manifest));
        }

        if (manifest.DeclaredLimits is not null)
        {
            RegressionManifestLimits limits = manifest.DeclaredLimits;
            if (limits.MaximumCases is < 1 or > MaximumCases ||
                limits.MaximumManifestBytes is < 1 or > MaximumManifestBytes ||
                limits.MaximumFixtureBytes is < 1 or > MaximumFixtureBytes ||
                limits.CaseTimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
                limits.DeadlineSeconds is < 1 or > MaximumDeadlineSeconds ||
                manifest.Denominator > limits.MaximumCases)
            {
                throw new ArgumentException(
                    "Regression manifest limits must be positive, bounded, and cover the declared denominator.",
                    nameof(manifest));
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (RegressionFixture fixture in manifest.Cases)
        {
            if (fixture is null ||
                string.IsNullOrWhiteSpace(fixture.Id) || fixture.Id.Length > MaximumIdLength ||
                !fixture.Id.All(static character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '-') ||
                !ids.Add(fixture.Id))
            {
                throw new ArgumentException("Regression fixture IDs must be unique, lowercase, bounded file-safe names.", nameof(manifest));
            }

            if (string.IsNullOrWhiteSpace(fixture.File) ||
                fixture.File.Length > MaximumIdLength ||
                Path.GetFileName(fixture.File) != fixture.File ||
                !fixture.File.EndsWith(".rs", StringComparison.Ordinal) ||
                fixture.File.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                fixture.File.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ArgumentException("Fixture '" + fixture.Id + "' has an unsafe source file name.", nameof(manifest));
            }

            if (!Kinds.Contains(fixture.Kind, StringComparer.Ordinal))
            {
                throw new ArgumentException("Fixture '" + fixture.Id + "' has unsupported kind '" + fixture.Kind + "'.", nameof(manifest));
            }

            if (fixture.Kind is "run-pass" or "differential")
            {
                if (fixture.ExpectedOutput is null || fixture.ExpectedOutput.Length > MaximumFixtureBytes)
                {
                    throw new ArgumentException("Fixture '" + fixture.Id + "' must declare bounded expected output.", nameof(manifest));
                }
            }
            else if (fixture.ExpectedOutput is not null)
            {
                throw new ArgumentException(
                    "Fixture '" + fixture.Id + "' must not declare expected output for kind '" + fixture.Kind + "'.",
                    nameof(manifest));
            }

            if (fixture.DiagnosticContains is not null &&
                (string.IsNullOrWhiteSpace(fixture.DiagnosticContains) ||
                 fixture.DiagnosticContains.Length > MaximumDiagnosticLength))
            {
                throw new ArgumentException(
                    "Fixture '" + fixture.Id + "' diagnosticContains must be non-empty and bounded.",
                    nameof(manifest));
            }

            if (fixture.Kind != "compile-fail" && fixture.DiagnosticContains is not null)
            {
                throw new ArgumentException(
                    "Fixture '" + fixture.Id + "' may declare diagnosticContains only for compile-fail cases.",
                    nameof(manifest));
            }

            _ = kinds.Add(fixture.Kind);
        }

        if (Kinds.Any(kind => !kinds.Contains(kind)))
        {
            throw new ArgumentException(
                "The regression denominator must include compile-pass, compile-fail, run-pass and differential cases.",
                nameof(manifest));
        }

        if (manifest.DeclaredCoverage is not null)
        {
            if (manifest.DeclaredCoverage.Count != Kinds.Count ||
                manifest.DeclaredCoverage.Keys.Any(key => !Kinds.Contains(key, StringComparer.Ordinal)))
            {
                throw new ArgumentException(
                    "Regression manifest coverage must declare exactly the four supported outcome kinds.",
                    nameof(manifest));
            }

            IReadOnlyDictionary<string, int> actual = CountKinds(manifest.Cases);
            foreach (string kind in Kinds)
            {
                if (!manifest.DeclaredCoverage.TryGetValue(kind, out int declared) ||
                    declared != actual[kind])
                {
                    throw new ArgumentException(
                        "Regression manifest coverage does not match its cases.",
                        nameof(manifest));
                }
            }
        }
    }

    internal static IReadOnlyDictionary<string, int> CountKinds(IEnumerable<RegressionFixture> fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        var counts = Kinds.ToDictionary(static kind => kind, static _ => 0, StringComparer.Ordinal);
        int count = 0;
        foreach (RegressionFixture fixture in fixtures)
        {
            if (++count > MaximumCases)
            {
                throw new ArgumentException("Regression fixture count exceeds " + MaximumCases + ".", nameof(fixtures));
            }

            if (!counts.TryGetValue(fixture.Kind, out int current))
            {
                throw new ArgumentException("Unknown regression kind '" + fixture.Kind + "'.", nameof(fixtures));
            }

            counts[fixture.Kind] = current + 1;
        }

        return counts;
    }

    internal static (TimeSpan Timeout, TimeSpan Deadline) ApplyDeclaredLimits(
        RegressionManifest manifest,
        TimeSpan requestedTimeout,
        TimeSpan requestedDeadline)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.DeclaredLimits is not RegressionManifestLimits declared)
            return (requestedTimeout, requestedDeadline);

        TimeSpan declaredTimeout = TimeSpan.FromSeconds(declared.CaseTimeoutSeconds);
        TimeSpan declaredDeadline = TimeSpan.FromSeconds(declared.DeadlineSeconds);
        return (
            requestedTimeout <= declaredTimeout ? requestedTimeout : declaredTimeout,
            requestedDeadline <= declaredDeadline ? requestedDeadline : declaredDeadline);
    }

    private static void ArmCancellation(
        CancellationTokenSource cancellation,
        TimeSpan totalDeadline,
        Stopwatch harnessClock)
    {
        TimeSpan remaining = totalDeadline - harnessClock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            cancellation.Cancel();
            return;
        }

        cancellation.CancelAfter(remaining);
    }

    public static async Task<int> RunAsync(
        string repositoryRoot,
        string reportPath,
        TimeSpan timeout,
        TimeSpan deadline,
        DateTimeOffset startedAtUtc,
        Stopwatch harnessClock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentNullException.ThrowIfNull(harnessClock);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Regression case timeout must be 1..300 seconds.");
        }

        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(MaximumDeadlineSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), "Regression deadline must be 1..900 seconds.");
        }

        string root = Path.GetFullPath(repositoryRoot);
        string fullReportPath = Path.GetFullPath(reportPath, root);
        string reportDirectory = Path.GetDirectoryName(fullReportPath)
            ?? throw new ArgumentException("Regression report path must include a directory.", nameof(reportPath));
        Directory.CreateDirectory(reportDirectory);
        string fixturesDirectory = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures");
        string manifestPath = Path.Combine(fixturesDirectory, ManifestFileName);
        string relativeManifestPath = Path.GetRelativePath(root, manifestPath).Replace(Path.DirectorySeparatorChar, '/');

        TimeSpan effectiveTimeout = timeout;
        TimeSpan effectiveDeadline = deadline;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(deadline);
        ConsoleCancelEventHandler cancelHandler = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        ManifestValidation validation;
        try
        {
            validation = await ReadManifestAsync(
                root,
                manifestPath,
                relativeManifestPath,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            validation = new ManifestValidation(
                false,
                null,
                "Regression harness deadline or cancellation was requested before the manifest was read.",
                string.Empty,
                0,
                relativeManifestPath);
        }
        var cases = new List<RegressionCaseReport>(validation.Manifest?.Cases.Count ?? 0);
        string? runDirectory = null;
        string? cleanupDiagnostic = null;
        ToolVersionReport oracle = ToolVersionReport.Unavailable(
            "rustc",
            OracleExecutable,
            RustVersion,
            "Not probed because the manifest was invalid.");
        ToolVersionReport rustSharp = ToolVersionReport.Unavailable(
            "rustsharp",
            RustSharpExecutable,
            null,
            "Not probed because the manifest was invalid.");
        string? harnessError = validation.Error;
        try
        {
            if (validation.Validated && validation.Manifest is not null)
            {
                (effectiveTimeout, effectiveDeadline) = ApplyDeclaredLimits(
                    validation.Manifest,
                    timeout,
                    deadline);
                ArmCancellation(cancellation, effectiveDeadline, harnessClock);
                runDirectory = Path.Combine(
                    reportDirectory,
                    ".run-regressions-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(runDirectory);
                var runner = new BoundedProcessRunner();
                ProcessResult oracleProbe = await RunProcessAsync(
                    runner,
                    OracleExecutable,
                    ["+" + OracleToolchain, "--version"],
                    root,
                    effectiveTimeout,
                    cancellation.Token).ConfigureAwait(false);
                string? oracleVersion = FindLineStartingWith(oracleProbe.StandardOutput, "rustc ")
                    ?? FindLineStartingWith(oracleProbe.StandardError, "rustc ");
                bool oracleAvailable = oracleProbe.Succeeded &&
                    oracleVersion?.StartsWith("rustc 1.98.", StringComparison.Ordinal) == true;
                oracle = ToolVersionReport.FromProbe(
                    "rustc",
                    OracleExecutable,
                    RustVersion,
                    oracleVersion,
                    oracleAvailable,
                    oracleProbe,
                    oracleAvailable
                        ? null
                        : DescribeUnavailable("rustc 1.98.0 oracle is unavailable", oracleProbe, effectiveTimeout));

                ProcessResult rustSharpProbe = await RunProcessAsync(
                    runner,
                    RustSharpExecutable,
                    BuildRustSharpArguments(root, "--version"),
                    root,
                    effectiveTimeout,
                    cancellation.Token).ConfigureAwait(false);
                string? rustSharpVersion = FindLineStartingWith(rustSharpProbe.StandardOutput, "rsc ")
                    ?? FindLineStartingWith(rustSharpProbe.StandardError, "rsc ");
                bool rustSharpAvailable = rustSharpProbe.Succeeded && rustSharpVersion is not null;
                rustSharp = ToolVersionReport.FromProbe(
                    "rustsharp",
                    RustSharpExecutable,
                    null,
                    rustSharpVersion,
                    rustSharpAvailable,
                    rustSharpProbe,
                    rustSharpAvailable
                        ? null
                        : DescribeUnavailable("RustSharp CLI is unavailable", rustSharpProbe, effectiveTimeout));

                foreach (RegressionFixture fixture in validation.Manifest.Cases)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!rustSharpAvailable)
                    {
                        cases.Add(RegressionCaseReport.Skipped(
                            fixture,
                            rustSharp.Diagnostic ?? "RustSharp CLI is unavailable."));
                    }
                    else if (fixture.Kind == "differential" && !oracleAvailable)
                    {
                        cases.Add(RegressionCaseReport.Skipped(
                            fixture,
                            oracle.Diagnostic ?? "rustc 1.98.0 oracle is unavailable."));
                    }
                    else
                    {
                        cases.Add(await RunCaseSafelyAsync(
                            runner,
                            root,
                            runDirectory,
                            fixture,
                            oracleAvailable,
                            effectiveTimeout,
                            cancellation.Token).ConfigureAwait(false));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            harnessError ??= "Regression harness deadline or cancellation was requested.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException)
        {
            harnessError ??= TrimDiagnostic(exception.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (runDirectory is not null)
            {
                cleanupDiagnostic = TryDeleteDirectory(runDirectory);
            }
        }

        if (validation.Manifest is not null &&
            cases.Count < validation.Manifest.Cases.Count)
        {
            string reason = cancellation.IsCancellationRequested
                ? "Regression deadline or cancellation was requested before this case started."
                : harnessError ?? "Regression harness stopped before this case started.";
            for (int index = cases.Count;
                 index < validation.Manifest.Cases.Count && index < MaximumCases;
                 index++)
            {
                cases.Add(RegressionCaseReport.Skipped(
                    validation.Manifest.Cases[index],
                    reason));
            }
        }

        harnessClock.Stop();
        int denominator = validation.Manifest?.Denominator ?? 0;
        int executed = cases.Count(static item => item.Status is "passed" or "failed");
        int passed = cases.Count(static item => item.Status == "passed");
        int failed = cases.Count(static item => item.Status == "failed");
        int skipped = cases.Count(static item => item.Status == "skipped");
        IReadOnlyDictionary<string, int> expectedCoverage = validation.Manifest is null
            ? Kinds.ToDictionary(static kind => kind, static _ => 0, StringComparer.Ordinal)
            : CountKinds(validation.Manifest.Cases);
        IReadOnlyDictionary<string, int> actualCoverage = Kinds.ToDictionary(
            static kind => kind,
            kind => cases.Count(item =>
                string.Equals(item.Kind, kind, StringComparison.Ordinal) && item.Status == "passed"),
            StringComparer.Ordinal);
        string status = !validation.Validated
            ? "blocked"
            : harnessError is not null || cancellation.IsCancellationRequested
                ? "blocked"
                : skipped > 0
                    ? "blocked"
                    : failed > 0
                        ? "failed"
                        : passed == denominator && denominator > 0 ? "passed" : "blocked";
        int exitCode = status switch
        {
            "passed" => 0,
            "failed" => 1,
            _ => 2,
        };

        var report = new RegressionReport(
            1,
            "safe-core-regression",
            ProfileName,
            DateTimeOffset.UtcNow,
            new ManifestReport(
                validation.RelativePath,
                ManifestVersion,
                validation.Manifest?.Denominator ?? 0,
                validation.Manifest?.Cases.Count ?? 0,
                validation.Sha256,
                validation.ByteLength,
                validation.Validated,
                validation.Error),
            new VersionReport(RustVersion, Edition, CompilerProfile, oracle, rustSharp),
            new LimitsReport(
                effectiveTimeout.TotalSeconds,
                effectiveDeadline.TotalSeconds,
                MaximumCases,
                MaximumManifestBytes,
                MaximumFixtureBytes,
                BoundedProcessRunner.MaximumTotalOutputBytes,
                MaximumCleanupAttempts,
                CleanupTimeout.TotalSeconds),
            new RegressionSummary(
                status,
                exitCode,
                denominator,
                executed,
                passed,
                failed,
                skipped,
                expectedCoverage,
                actualCoverage),
            cases,
            new ExecutionReport(
                startedAtUtc,
                DateTimeOffset.UtcNow,
                harnessClock.Elapsed.TotalMilliseconds,
                cancellation.IsCancellationRequested,
                cancellation.IsCancellationRequested
                    ? "Regression deadline or cancellation was requested."
                    : null),
            new CleanupReport(runDirectory is not null, cleanupDiagnostic is null, cleanupDiagnostic),
            harnessError)
        {
            RustVersion = SafeCoreRegressionProfileRunner.RustVersion,
            Edition = SafeCoreRegressionProfileRunner.Edition,
            Coverage = expectedCoverage,
        };

        await WriteReportAsync(fullReportPath, report).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return exitCode;
    }

    private static RegressionManifest ParseManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Regression manifest root must be an object.");
        }

        string profile = RequiredString(root, "profile");
        int version = RequiredInt(root, "version");
        string rustVersion = RequiredString(root, "rustVersion");
        string edition = RequiredString(root, "edition");
        string compilerProfile = RequiredString(root, "compilerProfile");
        int denominator = RequiredInt(root, "denominator");
        RegressionManifestLimits declaredLimits = ParseLimits(root);
        IReadOnlyDictionary<string, int> declaredCoverage = ParseCoverage(root);
        if (!root.TryGetProperty("cases", out JsonElement casesElement) ||
            casesElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Regression manifest must contain a cases array.");
        }

        var cases = new List<RegressionFixture>(Math.Min(
            denominator > 0 ? denominator : 0,
            MaximumCases));
        int index = 0;
        foreach (JsonElement element in casesElement.EnumerateArray())
        {
            if (++index > MaximumCases)
            {
                throw new ArgumentException(
                    "Regression manifest exceeds the " + MaximumCases + "-case bound.");
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Regression case " + index + " must be an object.");
            }

            string id = RequiredString(element, "id");
            string file = RequiredString(element, "file");
            string kind = RequiredString(element, "kind");
            string? expectedOutput = null;
            string? diagnosticContains = null;
            if (element.TryGetProperty("expectedOutput", out JsonElement output))
            {
                if (output.ValueKind != JsonValueKind.Null &&
                    output.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException(
                        "Regression case '" + id + "' expectedOutput must be a string or null.");
                }

                expectedOutput = output.ValueKind == JsonValueKind.Null
                    ? null
                    : output.GetString();
            }

            if (element.TryGetProperty("diagnosticContains", out JsonElement diagnostic))
            {
                if (diagnostic.ValueKind != JsonValueKind.Null &&
                    diagnostic.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException(
                        "Regression case '" + id + "' diagnosticContains must be a string or null.");
                }

                diagnosticContains = diagnostic.ValueKind == JsonValueKind.Null
                    ? null
                    : diagnostic.GetString();
            }

            cases.Add(new RegressionFixture(id, file, kind, expectedOutput, diagnosticContains));
        }

        var manifest = new RegressionManifest(
            profile,
            version,
            rustVersion,
            edition,
            compilerProfile,
            denominator,
            cases);
        manifest = manifest with
        {
            DeclaredLimits = declaredLimits,
            DeclaredCoverage = declaredCoverage,
        };
        ValidateManifest(manifest);
        return manifest;
    }

    private static RegressionManifestLimits ParseLimits(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out JsonElement limits) ||
            limits.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Regression manifest must contain a limits object.");
        }

        return new(
            RequiredInt(limits, "maximumCases"),
            RequiredInt(limits, "maximumManifestBytes"),
            RequiredInt(limits, "maximumFixtureBytes"),
            RequiredInt(limits, "caseTimeoutSeconds"),
            RequiredInt(limits, "deadlineSeconds"));
    }

    private static Dictionary<string, int> ParseCoverage(JsonElement root)
    {
        if (!root.TryGetProperty("coverage", out JsonElement coverage) ||
            coverage.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Regression manifest must contain a coverage object.");
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty property in coverage.EnumerateObject())
        {
            if (result.Count >= Kinds.Count || !result.TryAdd(property.Name, RequiredInt(coverage, property.Name)))
            {
                throw new ArgumentException("Regression manifest coverage contains duplicate or unsupported entries.");
            }
        }

        return result;
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumManifestDepth,
        });
        var objectProperties = new Stack<HashSet<string>>();
        bool reachedEnd = false;
        for (int tokenIndex = 0; tokenIndex < MaximumJsonTokens; tokenIndex++)
        {
            if (!reader.Read())
            {
                reachedEnd = true;
                break;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (objectProperties.Count == 0)
                    throw new JsonException("A JSON property was found outside an object.");

                string propertyName = reader.GetString() ?? string.Empty;
                if (!objectProperties.Peek().Add(propertyName))
                    throw new JsonException($"Duplicate JSON property '{propertyName}' is not allowed.");
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (objectProperties.Count == 0)
                    throw new JsonException("The JSON object structure is invalid.");

                objectProperties.Pop();
            }
        }

        if (!reachedEnd)
            throw new JsonException($"The manifest exceeds the {MaximumJsonTokens}-token JSON limit.");
        if (objectProperties.Count != 0)
            throw new JsonException("The JSON object structure is incomplete.");
    }

    private static async Task<ManifestValidation> ReadManifestAsync(
        string repositoryRoot,
        string manifestPath,
        string relativeManifestPath,
        CancellationToken cancellationToken)
    {
        string sha256 = string.Empty;
        long byteLength = 0;
        try
        {
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException(
                    "Regression manifest was not found.",
                    manifestPath);
            }

            var fileInfo = new FileInfo(manifestPath);
            byteLength = fileInfo.Length;
            if (byteLength <= 0 || byteLength > MaximumManifestBytes)
            {
                throw new ArgumentException(
                    "Regression manifest size must be 1.." + MaximumManifestBytes + " bytes.");
            }

            await using (FileStream hashStream = new(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                byte[] digest = await SHA256.HashDataAsync(
                    hashStream,
                    cancellationToken).ConfigureAwait(false);
                sha256 = Convert.ToHexString(digest);
            }

            string json = await File.ReadAllTextAsync(
                manifestPath,
                cancellationToken).ConfigureAwait(false);
            RegressionManifest manifest = ParseManifest(json);
            string fixturesDirectory = Path.Combine(
                repositoryRoot,
                "tools",
                "RustSharp.Conformance",
                "fixtures");
            foreach (RegressionFixture fixture in manifest.Cases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = Path.Combine(fixturesDirectory, fixture.File);
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException(
                        "Fixture '" + fixture.File + "' was not found.",
                        sourcePath);
                }

                long sourceLength = new FileInfo(sourcePath).Length;
                if (sourceLength > MaximumFixtureBytes)
                {
                    throw new ArgumentException(
                        "Fixture '" + fixture.Id + "' exceeds " + MaximumFixtureBytes + " bytes.");
                }
            }

            return new ManifestValidation(
                true,
                manifest,
                null,
                sha256,
                byteLength,
                relativeManifestPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            JsonException or NotSupportedException)
        {
            return new ManifestValidation(
                false,
                null,
                TrimDiagnostic(exception.Message),
                sha256,
                byteLength,
                relativeManifestPath);
        }
    }

    private static async Task<RegressionCaseReport> RunCaseSafelyAsync(
        BoundedProcessRunner runner,
        string repositoryRoot,
        string runDirectory,
        RegressionFixture fixture,
        bool oracleAvailable,
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
                oracleAvailable,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RegressionCaseReport.Skipped(
                fixture,
                "Regression deadline or cancellation was requested.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException)
        {
            return RegressionCaseReport.Failed(
                fixture,
                "Case execution failed: " + TrimDiagnostic(exception.Message));
        }
    }

    private static async Task<RegressionCaseReport> RunCaseAsync(
        BoundedProcessRunner runner,
        string repositoryRoot,
        string runDirectory,
        RegressionFixture fixture,
        bool oracleAvailable,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string fixturesDirectory = Path.Combine(
            repositoryRoot,
            "tools",
            "RustSharp.Conformance",
            "fixtures");
        string sourcePath = Path.Combine(fixturesDirectory, fixture.File);
        string caseDirectory = Path.Combine(runDirectory, fixture.Id);
        Directory.CreateDirectory(caseDirectory);
        ProcessResult rustSharpCheck = await RunProcessAsync(
            runner,
            RustSharpExecutable,
            BuildRustSharpArguments(repositoryRoot, "check", sourcePath, "--profile", CompilerProfile),
            repositoryRoot,
            timeout,
            cancellationToken).ConfigureAwait(false);

        ProcessResult? rustcCompile = null;
        ProcessResult? rustcRun = null;
        ProcessResult? rustSharpCompile = null;
        ProcessResult? rustSharpRun = null;
        bool oracleCompared = false;
        if ((fixture.Kind is "compile-pass" or "compile-fail" or "differential") &&
            (fixture.Kind != "compile-pass" || oracleAvailable))
        {
            string oracleOutput = Path.Combine(caseDirectory, "oracle.exe");
            rustcCompile = await RunProcessAsync(
                runner,
                OracleExecutable,
                [
                    "+" + OracleToolchain,
                    sourcePath,
                    "--edition",
                    Edition,
                    "-C",
                    "overflow-checks=yes",
                    "-o",
                    oracleOutput,
                ],
                repositoryRoot,
                timeout,
                cancellationToken).ConfigureAwait(false);
            oracleCompared = oracleAvailable;
            if (fixture.Kind == "differential" && rustcCompile.Succeeded)
            {
                rustcRun = await RunProcessAsync(
                    runner,
                    oracleOutput,
                    [],
                    caseDirectory,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (fixture.Kind is "compile-pass" or "run-pass" or "differential" &&
            rustSharpCheck.Succeeded)
        {
            string managedOutput = Path.Combine(caseDirectory, "rustsharp.dll");
            rustSharpCompile = await RunProcessAsync(
                runner,
                RustSharpExecutable,
                BuildRustSharpArguments(
                    repositoryRoot,
                    "compile",
                    sourcePath,
                    "--output",
                    managedOutput,
                    "--profile",
                    CompilerProfile),
                repositoryRoot,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (rustSharpCompile.Succeeded &&
                (fixture.Kind is "run-pass" or "differential"))
            {
                rustSharpRun = await RunProcessAsync(
                    runner,
                    RustSharpExecutable,
                    [managedOutput],
                    caseDirectory,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        bool expectedSuccess = fixture.Kind switch
        {
            "compile-pass" => rustSharpCheck.Succeeded &&
                rustSharpCompile?.Succeeded == true &&
                (!oracleCompared ||
                 rustcCompile is not null && rustcCompile.Succeeded),
            "compile-fail" => IsCompileFailure(rustSharpCheck) &&
                DiagnosticMatches(rustSharpCheck.StandardOutput, rustSharpCheck.StandardError, fixture.DiagnosticContains) &&
                (!oracleCompared ||
                 rustcCompile is not null && IsCompileFailure(rustcCompile) &&
                 DiagnosticMatches(rustcCompile.StandardOutput, rustcCompile.StandardError, fixture.DiagnosticContains)),
            "run-pass" => rustSharpCheck.Succeeded &&
                rustSharpCompile?.Succeeded == true &&
                rustSharpRun?.Succeeded == true &&
                NormalizeOutput(rustSharpRun.StandardOutput) ==
                NormalizeOutput(fixture.ExpectedOutput),
            "differential" => rustSharpCheck.Succeeded &&
                rustSharpCompile?.Succeeded == true &&
                rustSharpRun?.Succeeded == true &&
                rustcCompile?.Succeeded == true &&
                rustcRun?.Succeeded == true &&
                NormalizeOutput(rustSharpRun.StandardOutput) ==
                NormalizeOutput(rustcRun.StandardOutput) &&
                NormalizeOutput(rustSharpRun.StandardOutput) ==
                NormalizeOutput(fixture.ExpectedOutput),
            _ => false,
        };
        if (fixture.Kind == "differential" && !oracleCompared)
        {
            expectedSuccess = false;
        }

        string? difference = expectedSuccess
            ? null
            : DescribeDifference(
                fixture,
                rustcCompile,
                rustcRun,
                rustSharpCheck,
                rustSharpCompile,
                rustSharpRun);
        return new RegressionCaseReport(
            fixture.Id,
            fixture.File,
            fixture.Kind,
            expectedSuccess ? "passed" : "failed",
            fixture.ExpectedOutput,
            oracleCompared,
            difference,
            rustcCompile?.ToEvidence() ?? ProcessEvidence.Empty,
            rustcRun?.ToEvidence(),
            rustSharpCheck.ToEvidence(),
            rustSharpCompile?.ToEvidence(),
            rustSharpRun?.ToEvidence())
        {
            DiagnosticContains = fixture.DiagnosticContains,
        };
    }

    private static bool IsCompileFailure(ProcessResult result) =>
        result.Termination == "exited" &&
        result.ExitCode == 1 &&
        !result.CleanupIncomplete &&
        !result.OutputTruncated &&
        !result.OutputReadTimedOut &&
        !result.OutputDrainTimedOut &&
        !result.OutputReadLimitReached;

    internal static bool DiagnosticMatches(
        string standardOutput,
        string standardError,
        string? expected)
    {
        if (expected is null) return true;
        return standardOutput.Contains(expected, StringComparison.Ordinal) ||
            standardError.Contains(expected, StringComparison.Ordinal);
    }

    private static string DescribeDifference(
        RegressionFixture fixture,
        ProcessResult? rustcCompile,
        ProcessResult? rustcRun,
        ProcessResult rustSharpCheck,
        ProcessResult? rustSharpCompile,
        ProcessResult? rustSharpRun)
    {
        if (fixture.Kind == "compile-pass" && !rustSharpCheck.Succeeded)
        {
            return "RustSharp check did not succeed (termination=" +
                rustSharpCheck.Termination +
                ", exitCode=" +
                FormatExitCode(rustSharpCheck) +
                ").";
        }

        if (fixture.Kind == "compile-pass" &&
            rustcCompile is not null &&
            !rustcCompile.Succeeded)
        {
            return "rustc compile baseline failed (termination=" +
                rustcCompile.Termination +
                ", exitCode=" +
                FormatExitCode(rustcCompile) +
                ").";
        }

        if (fixture.Kind == "compile-pass" &&
            rustSharpCompile is not null &&
            !rustSharpCompile.Succeeded)
        {
            return "RustSharp compile failed (termination=" +
                rustSharpCompile.Termination +
                ", exitCode=" +
                FormatExitCode(rustSharpCompile) +
                ").";
        }

        if (fixture.Kind == "compile-fail")
        {
            if (rustSharpCheck.Succeeded)
            {
                return "RustSharp accepted a fixture expected to fail compilation.";
            }

            if (rustcCompile is not null && rustcCompile.Succeeded)
            {
                return "rustc accepted a fixture expected to fail compilation.";
            }

            if (fixture.DiagnosticContains is not null &&
                IsCompileFailure(rustSharpCheck) &&
                !DiagnosticMatches(
                    rustSharpCheck.StandardOutput,
                    rustSharpCheck.StandardError,
                    fixture.DiagnosticContains))
            {
                return "RustSharp compile-fail diagnostics did not contain '" +
                    TrimDiagnostic(fixture.DiagnosticContains) + "'.";
            }

            if (fixture.DiagnosticContains is not null &&
                rustcCompile is not null &&
                IsCompileFailure(rustcCompile) &&
                !DiagnosticMatches(
                    rustcCompile.StandardOutput,
                    rustcCompile.StandardError,
                    fixture.DiagnosticContains))
            {
                return "rustc compile-fail diagnostics did not contain '" +
                    TrimDiagnostic(fixture.DiagnosticContains) + "'.";
            }

            return "Compile-fail outcome differed from the expected exit contract.";
        }

        if (rustcCompile is not null && !rustcCompile.Succeeded)
        {
            return "rustc compile failed (termination=" +
                rustcCompile.Termination +
                ", exitCode=" +
                FormatExitCode(rustcCompile) +
                ").";
        }

        if (!rustSharpCheck.Succeeded)
        {
            return "RustSharp check failed (termination=" +
                rustSharpCheck.Termination +
                ", exitCode=" +
                FormatExitCode(rustSharpCheck) +
                ").";
        }

        if (rustcRun is not null && !rustcRun.Succeeded)
        {
            return "rustc run failed (termination=" +
                rustcRun.Termination +
                ", exitCode=" +
                FormatExitCode(rustcRun) +
                ").";
        }

        if (rustSharpCompile is not null && !rustSharpCompile.Succeeded)
        {
            return "RustSharp compile failed (termination=" +
                rustSharpCompile.Termination +
                ", exitCode=" +
                FormatExitCode(rustSharpCompile) +
                ").";
        }

        if (rustSharpRun is not null && !rustSharpRun.Succeeded)
        {
            return "RustSharp run failed (termination=" +
                rustSharpRun.Termination +
                ", exitCode=" +
                FormatExitCode(rustSharpRun) +
                ").";
        }

        return "Runtime output differed from the expected or differential oracle output.";
    }

    private static List<string> BuildRustSharpArguments(
        string repositoryRoot,
        params string[] cliArguments)
    {
        ArgumentNullException.ThrowIfNull(cliArguments);
        var arguments = new List<string>(MaximumArgumentCount);
        string cliDll = Path.Combine(
            repositoryRoot,
            "src",
            "RustSharp.Cli",
            "bin",
            "Release",
            "net10.0",
            "rsc.dll");
        if (File.Exists(cliDll))
        {
            arguments.Add(cliDll);
        }
        else
        {
            arguments.Add("run");
            arguments.Add("--project");
            arguments.Add(Path.Combine(repositoryRoot, "src", "RustSharp.Cli"));
            arguments.Add("-c");
            arguments.Add("Release");
            arguments.Add("--no-build");
            arguments.Add("--no-restore");
            arguments.Add("--");
        }

        if (arguments.Count + cliArguments.Length > MaximumArgumentCount)
        {
            throw new ArgumentException(
                "A RustSharp invocation exceeds the " + MaximumArgumentCount + "-argument bound.",
                nameof(cliArguments));
        }

        arguments.AddRange(cliArguments);
        return arguments;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        BoundedProcessRunner runner,
        string fileName,
        List<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (arguments.Count > MaximumArgumentCount)
        {
            throw new ArgumentException(
                "A regression process accepts at most " + MaximumArgumentCount + " arguments.",
                nameof(arguments));
        }

        string commandLine = FormatCommandLine(fileName, arguments);
        try
        {
            return ProcessResult.From(await runner.RunAsync(
                new BoundedProcessRequest(fileName, arguments, workingDirectory, timeout),
                cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProcessResult.Cancelled(commandLine);
        }
        catch (Exception exception) when (
            exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return ProcessResult.Error(commandLine, exception.Message);
        }
    }

    private static async Task WriteReportAsync(string path, RegressionReport report)
    {
        string temporaryPath = path + ".tmp-" + Environment.ProcessId + "-" +
            Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    report,
                    JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? TryDeleteDirectory(string path)
    {
        var clock = Stopwatch.StartNew();
        Exception? lastException = null;
        for (int attempt = 0;
             attempt < MaximumCleanupAttempts && clock.Elapsed < CleanupTimeout;
             attempt++)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            try
            {
                Directory.Delete(path, recursive: true);
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

        return "Regression run directory cleanup failed after " +
            MaximumCleanupAttempts +
            " attempts or " +
            CleanupTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
            " seconds: " +
            (lastException?.Message ?? "directory still exists");
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                "Regression manifest property '" + property + "' must be a string.");
        }

        return value.GetString() ??
            throw new ArgumentException(
                "Regression manifest property '" + property + "' cannot be null.");
    }

    private static int RequiredInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
        {
            throw new ArgumentException(
                "Regression manifest property '" + property + "' must be a bounded integer.");
        }

        return result;
    }

    private static string NormalizeOutput(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string FormatExitCode(ProcessResult result) =>
        result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    private static string? FindLineStartingWith(string text, string prefix) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static string DescribeUnavailable(
        string description,
        ProcessResult result,
        TimeSpan timeout)
    {
        string exitCode = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        string detail = FirstDiagnosticLine(result.StandardError) ??
            FirstDiagnosticLine(result.StandardOutput) ??
            result.OutputDiagnostic ??
            string.Empty;
        return description +
            " (termination=" +
            result.Termination +
            ", exitCode=" +
            exitCode +
            ", timeoutSeconds=" +
            timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
            ", elapsedMilliseconds=" +
            result.Elapsed.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) +
            ")" +
            (detail.Length == 0 ? string.Empty : ": " + TrimDiagnostic(detail));
    }

    private static string? FirstDiagnosticLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length > 0);

    private static string FormatCommandLine(
        string fileName,
        IReadOnlyList<string> arguments) =>
        string.Join(
            " ",
            new[] { fileName }.Concat(arguments).Select(QuoteCommandLineArgument));

    private static string QuoteCommandLineArgument(string value) =>
        value.Length != 0 &&
        !value.Any(char.IsWhiteSpace) &&
        value.IndexOf('"') < 0
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string TrimDiagnostic(string value) =>
        value.Length <= MaximumDiagnosticLength
            ? value
            : value[..MaximumDiagnosticLength] + "...";

    internal sealed record RegressionReport(
        int SchemaVersion,
        string EvidenceKind,
        string Profile,
        DateTimeOffset GeneratedAtUtc,
        ManifestReport Manifest,
        VersionReport Versions,
        LimitsReport Limits,
        RegressionSummary Summary,
        IReadOnlyList<RegressionCaseReport> Cases,
        ExecutionReport Execution,
        CleanupReport Cleanup,
        string? HarnessError)
    {
        // Additive top-level fields keep the report shape aligned with the
        // existing syntax/name-resolution evidence consumers.
        public string RustVersion { get; init; } = SafeCoreRegressionProfileRunner.RustVersion;
        public string Edition { get; init; } = SafeCoreRegressionProfileRunner.Edition;
        public IReadOnlyDictionary<string, int> Coverage { get; init; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }

    internal sealed record ManifestReport(
        string Path,
        int Version,
        int Denominator,
        int CaseCount,
        string Sha256,
        long ByteLength,
        bool Validated,
        string? Error);

    internal sealed record VersionReport(
        string RustVersion,
        string Edition,
        string CompilerProfile,
        ToolVersionReport Oracle,
        ToolVersionReport RustSharp);

    internal sealed record ToolVersionReport(
        string Name,
        string Executable,
        string? Requested,
        string? Version,
        bool Available,
        string? Diagnostic,
        ProcessEvidence Probe)
    {
        internal static ToolVersionReport Unavailable(
            string name,
            string executable,
            string? requested,
            string diagnostic) =>
            new(name, executable, requested, null, false, diagnostic, ProcessEvidence.Empty);

        internal static ToolVersionReport FromProbe(
            string name,
            string executable,
            string? requested,
            string? version,
            bool available,
            ProcessResult probe,
            string? diagnostic) =>
            new(
                name,
                executable,
                requested,
                version,
                available,
                diagnostic,
                probe.ToEvidence());
    }

    internal sealed record LimitsReport(
        double CaseTimeoutSeconds,
        double DeadlineSeconds,
        int MaximumCases,
        int MaximumManifestBytes,
        int MaximumFixtureBytes,
        int MaximumOutputBytes,
        int MaximumCleanupAttempts,
        double CleanupTimeoutSeconds);

    internal sealed record RegressionSummary(
        string Status,
        int ExitCode,
        int Denominator,
        int Executed,
        int Passed,
        int Failed,
        int Skipped,
        IReadOnlyDictionary<string, int> ExpectedByKind,
        IReadOnlyDictionary<string, int> PassedByKind);

    internal sealed record RegressionCaseReport(
        string Id,
        string Source,
        string Kind,
        string Status,
        string? ExpectedOutput,
        bool OracleCompared,
        string? Difference,
        ProcessEvidence RustcCompile,
        ProcessEvidence? RustcRun,
        ProcessEvidence RustSharpCheck,
        ProcessEvidence? RustSharpCompile,
        ProcessEvidence? RustSharpRun)
    {
        public string? DiagnosticContains { get; init; }

        internal static RegressionCaseReport Skipped(
            RegressionFixture fixture,
            string reason) =>
            new(
                fixture.Id,
                fixture.File,
                fixture.Kind,
                "skipped",
                fixture.ExpectedOutput,
                false,
                reason,
                ProcessEvidence.Empty,
                null,
                ProcessEvidence.Empty,
                null,
                null)
            {
                DiagnosticContains = fixture.DiagnosticContains,
            };

        internal static RegressionCaseReport Failed(
            RegressionFixture fixture,
            string reason) =>
            new(
                fixture.Id,
                fixture.File,
                fixture.Kind,
                "failed",
                fixture.ExpectedOutput,
                false,
                reason,
                ProcessEvidence.Empty,
                null,
                ProcessEvidence.Empty,
                null,
                null)
            {
                DiagnosticContains = fixture.DiagnosticContains,
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
        string? OutputDiagnostic,
        bool CleanupAttempted,
        bool CleanupIncomplete,
        string? CleanupDiagnostic)
    {
        internal static ProcessEvidence Empty => new(
            "",
            0,
            0,
            null,
            null,
            "skipped",
            0,
            "",
            "",
            false,
            false,
            false,
            false,
            null,
            false,
            false,
            null);
    }

    internal sealed record ExecutionReport(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        double ElapsedMilliseconds,
        bool DeadlineExpired,
        string? DeadlineDiagnostic);

    internal sealed record CleanupReport(
        bool Attempted,
        bool Completed,
        string? Diagnostic);

    internal sealed record ProcessResult(
        string CommandLine,
        int ProcessId,
        int? ExitCode,
        string Termination,
        TimeSpan Elapsed,
        string StandardOutput,
        string StandardError,
        bool OutputTruncated,
        bool OutputReadTimedOut,
        bool OutputDrainTimedOut,
        bool CleanupIncomplete,
        int ParentProcessId,
        DateTimeOffset? StartedAtUtc,
        bool OutputReadLimitReached,
        string? OutputDiagnostic,
        bool CleanupAttempted,
        string? CleanupDiagnostic)
    {
        internal bool Succeeded =>
            Termination == "exited" &&
            ExitCode == 0 &&
            !CleanupIncomplete &&
            !OutputTruncated &&
            !OutputReadTimedOut &&
            !OutputDrainTimedOut &&
            !OutputReadLimitReached;

        internal static ProcessResult From(BoundedProcessResult result) =>
            new(
                result.StartedProcess.CommandLine,
                result.StartedProcess.ProcessId,
                result.ExitCode,
                result.Termination.ToString().ToLowerInvariant(),
                result.Elapsed,
                result.StandardOutput,
                result.StandardError,
                result.OutputTruncated,
                result.OutputReadTimedOut,
                result.OutputDrainTimedOut,
                result.ProcessTreeCleanupIncomplete,
                result.StartedProcess.ParentProcessId,
                result.StartedProcess.StartedAt,
                result.OutputReadLimitReached,
                result.OutputDiagnostic,
                result.ProcessTreeCleanupAttempted,
                result.ProcessTreeCleanupDiagnostic);

        internal static ProcessResult Cancelled(string commandLine) =>
            new(
                commandLine,
                0,
                null,
                "cancelled",
                TimeSpan.Zero,
                "",
                "",
                false,
                false,
                false,
                false,
                0,
                null,
                false,
                "Process start cancelled by the harness deadline.",
                false,
                null);

        internal static ProcessResult Error(
            string commandLine,
            string diagnostic) =>
            new(
                commandLine,
                0,
                null,
                "error",
                TimeSpan.Zero,
                "",
                diagnostic,
                false,
                false,
                false,
                false,
                0,
                null,
                false,
                diagnostic,
                false,
                null);

        internal ProcessEvidence ToEvidence() =>
            new(
                CommandLine,
                ProcessId,
                ParentProcessId,
                StartedAtUtc,
                ExitCode,
                Termination,
                Elapsed.TotalMilliseconds,
                StandardOutput,
                StandardError,
                OutputTruncated,
                OutputReadTimedOut,
                OutputDrainTimedOut,
                OutputReadLimitReached,
                OutputDiagnostic,
                CleanupAttempted,
                CleanupIncomplete,
                CleanupDiagnostic);
    }
}
