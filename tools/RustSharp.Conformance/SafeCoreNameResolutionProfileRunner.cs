using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

internal static class SafeCoreNameResolutionProfileRunner
{
    private const string ProfileName = "safe-core-name-resolution";
    private const string ParserName = "RustSharp.Syntax.SafeCoreSyntax.Parse";
    private const string ResolverName = "RustSharp.Syntax.SafeCoreNameResolution.Resolve";
    private const string ManifestFileName = "safe-core-name-resolution-manifest.json";
    private const int ManifestVersion = 1;
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumCases = 32;
    private const int MaximumIdentifierLength = 128;
    private const int MaximumFixturePathLength = 512;
    private const int MaximumJsonTokens = 8_192;
    private const int MaximumSourceLength = 1_000_000;
    private const int MaximumTokens = 250_000;
    private const int MaximumNodes = 100_000;
    private const int MaximumParserDiagnostics = 128;
    private const int MaximumParserNestingDepth = 128;
    private const int MaximumParserOperations = 1_000_000;
    private const int MaximumSymbols = 100_000;
    private const int MaximumScopes = 50_000;
    private const int MaximumPathSegments = 128;
    private const int MaximumNameLength = 1_024;
    private const int MaximumPathLength = 4_096;
    private const int MaximumDiagnosticMessageLength = 1_024;
    private const int MaximumResolverDiagnostics = 128;
    private const int MaximumResolverNestingDepth = 128;
    private const int MaximumResolverOperations = 1_000_000;
    private const int MaximumExpectedResolutions = 64;
    private const int MaximumReportedDiagnostics = 128;
    private const int MaximumReportedResolutions = 256;
    private const int PassedExitCode = 0;
    private const int FailedExitCode = 1;
    private const int HarnessErrorExitCode = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 24,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly HashSet<string> KnownExpectedDiagnosticCodes = new(StringComparer.Ordinal)
    {
        SafeCoreNameResolutionDiagnosticCodes.InvalidSyntax,
        SafeCoreNameResolutionDiagnosticCodes.LimitReached,
        SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
        SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol,
        SafeCoreNameResolutionDiagnosticCodes.UnresolvedName,
        SafeCoreNameResolutionDiagnosticCodes.AmbiguousName,
        SafeCoreNameResolutionDiagnosticCodes.PrivateName,
        SafeCoreNameResolutionDiagnosticCodes.ImportCycle,
    };

    public static async Task<int> RunAsync(
        string repositoryRoot,
        string reportPath,
        TimeSpan deadline,
        DateTimeOffset startedAtUtc,
        Stopwatch harnessClock)
    {
        string fixturesDirectory = Path.GetFullPath(
            Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures"));
        EnsureDirectoryWithoutReparsePoint(fixturesDirectory, "fixtures directory");
        string manifestPath = GetContainedPath(fixturesDirectory, ManifestFileName, "manifest");
        EnsureReportOutsideFixtures(reportPath, fixturesDirectory);
        EnsureRegularFileWithoutReparsePoint(manifestPath, "manifest");
        string relativeManifestPath = ToRepositoryRelativePath(repositoryRoot, manifestPath);
        string? manifestSha256 = null;
        ValidatedManifest? validatedManifest = null;
        IReadOnlyList<NameResolutionCaseReport> cases = Array.Empty<NameResolutionCaseReport>();
        string? harnessError = null;
        int exitCode;

        using var deadlineSource = new CancellationTokenSource(deadline);
        try
        {
            BoundedFile manifestFile = await ReadBoundedFileAsync(
                manifestPath,
                MaximumManifestBytes,
                deadlineSource.Token).ConfigureAwait(false);
            manifestSha256 = manifestFile.Sha256;
            ValidateNoDuplicateProperties(manifestFile.Bytes);
            SafeCoreNameResolutionManifest? manifest = JsonSerializer.Deserialize<SafeCoreNameResolutionManifest>(
                manifestFile.Bytes,
                ManifestJsonOptions);
            validatedManifest = ValidateManifest(manifest, fixturesDirectory);

            var caseReports = new List<NameResolutionCaseReport>(validatedManifest.Cases.Count);
            for (int index = 0; index < validatedManifest.Cases.Count && index < MaximumCases; index++)
            {
                ValidatedCase fixture = validatedManifest.Cases[index];
                if (deadlineSource.IsCancellationRequested)
                {
                    caseReports.Add(NameResolutionCaseReport.Skipped(
                        fixture,
                        "Harness deadline expired before the case started."));
                }
                else
                {
                    caseReports.Add(await RunCaseAsync(
                        fixture,
                        validatedManifest.SyntaxOptions,
                        validatedManifest.ResolutionOptions,
                        deadlineSource.Token).ConfigureAwait(false));
                }
            }

            cases = caseReports;
            int failed = cases.Count(static item => item.Status == "failed");
            int errors = cases.Count(static item => item.Status == "error");
            int skipped = cases.Count(static item => item.Status == "skipped");
            exitCode = errors > 0 || skipped > 0 || deadlineSource.IsCancellationRequested
                ? HarnessErrorExitCode
                : failed > 0
                    ? FailedExitCode
                    : PassedExitCode;
        }
        catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
        {
            exitCode = HarnessErrorExitCode;
            harnessError = "The safe-core name-resolution harness deadline expired.";
        }
        catch (Exception exception) when (IsExpectedHarnessException(exception))
        {
            exitCode = HarnessErrorExitCode;
            harnessError = TrimDiagnostic(exception.Message);
        }

        harnessClock.Stop();
        int passedCount = cases.Count(static item => item.Status == "passed");
        int failedCount = cases.Count(static item => item.Status == "failed");
        int errorCount = cases.Count(static item => item.Status == "error");
        int skippedCount = cases.Count(static item => item.Status == "skipped");
        string? harnessErrorReason = harnessError;
        if (exitCode == HarnessErrorExitCode && harnessErrorReason is null)
        {
            harnessErrorReason = deadlineSource.IsCancellationRequested
                ? "The safe-core name-resolution harness deadline expired."
                : errorCount > 0
                    ? "One or more corpus cases could not be executed."
                    : skippedCount > 0
                        ? "One or more corpus cases were skipped."
                        : "The safe-core name-resolution harness was blocked.";
        }

        string status = exitCode switch
        {
            PassedExitCode => "passed",
            FailedExitCode => "failed",
            _ => "error",
        };
        var report = new SafeCoreNameResolutionReport(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Profile: ProfileName,
            EvidenceKind: "name-resolution-acceptance",
            Scope: new NameResolutionScopeReport(
                RustcConformance: false,
                RuntimeConformance: false,
                Statement: "This report measures only in-process RustSharp safe-core parser and name-resolution acceptance; it is not rustc differential or runtime conformance evidence."),
            Manifest: new NameResolutionManifestReport(
                Path: relativeManifestPath,
                Sha256: manifestSha256,
                Version: validatedManifest?.Version,
                Parser: validatedManifest?.Parser,
                Resolver: validatedManifest?.Resolver,
                Denominator: validatedManifest?.Denominator,
                CaseCount: validatedManifest?.Cases.Count,
                Validated: validatedManifest is not null,
                Error: harnessError),
            Pipeline: new NameResolutionPipelineReport(
                Parser: ParserName,
                Resolver: ResolverName,
                Invocation: "SafeCoreNameResolution.Resolve(SafeCoreSyntax.Parse(source, sourcePath, syntaxLimits), resolutionLimits)",
                AssemblyVersion: typeof(SafeCoreNameResolution).Assembly.GetName().Version?.ToString()),
            Limits: validatedManifest is null
                ? null
                : NameResolutionLimitsReport.From(
                    validatedManifest.SyntaxOptions,
                    validatedManifest.ResolutionOptions,
                    deadline,
                    MaximumCases,
                    MaximumManifestBytes),
            Summary: new NameResolutionSummaryReport(
                Status: status,
                Denominator: validatedManifest?.Denominator ?? 0,
                Executed: cases.Count(static item => item.ParserInvoked),
                ResolutionExecuted: cases.Count(static item => item.ResolverInvoked),
                Passed: passedCount,
                Failed: failedCount,
                Errors: errorCount,
                Skipped: skippedCount,
                ExitCode: exitCode),
            Cases: cases,
            Host: NameResolutionHostReport.Current,
            Execution: new NameResolutionExecutionReport(
                StartedAtUtc: startedAtUtc,
                FinishedAtUtc: DateTimeOffset.UtcNow,
                ElapsedMilliseconds: harnessClock.Elapsed.TotalMilliseconds,
                DeadlineSeconds: deadline.TotalSeconds,
                DeadlineExpired: deadlineSource.IsCancellationRequested),
            HarnessError: harnessErrorReason);

        await WriteReportAsync(reportPath, report).ConfigureAwait(false);
        if (exitCode == HarnessErrorExitCode)
        {
            Console.Error.WriteLine($"conformance: safe-core-name-resolution harness error: {harnessErrorReason}");
        }

        Console.WriteLine(JsonSerializer.Serialize(report, ReportJsonOptions));
        return exitCode;
    }

    private static ValidatedManifest ValidateManifest(
        SafeCoreNameResolutionManifest? manifest,
        string fixturesDirectory)
    {
        if (manifest is null)
        {
            throw new InvalidDataException("The safe-core name-resolution manifest is empty.");
        }

        if (!string.Equals(manifest.Profile, ProfileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Manifest profile must be '{ProfileName}'.");
        }

        if (manifest.Version != ManifestVersion)
        {
            throw new InvalidDataException($"Manifest version must be {ManifestVersion}.");
        }

        if (!string.Equals(manifest.Parser, ParserName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Manifest parser must be '{ParserName}'.");
        }

        if (!string.Equals(manifest.Resolver, ResolverName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Manifest resolver must be '{ResolverName}'.");
        }

        SyntaxLimits syntaxLimits = manifest.SyntaxLimits
            ?? throw new InvalidDataException("Manifest syntaxLimits are required.");
        var syntaxOptions = new SafeCoreSyntaxOptions
        {
            MaximumSourceLength = ValidateLimit(
                nameof(syntaxLimits.MaximumSourceLength),
                syntaxLimits.MaximumSourceLength,
                MaximumSourceLength),
            MaximumTokens = ValidateLimit(
                nameof(syntaxLimits.MaximumTokens),
                syntaxLimits.MaximumTokens,
                MaximumTokens),
            MaximumNodes = ValidateLimit(
                nameof(syntaxLimits.MaximumNodes),
                syntaxLimits.MaximumNodes,
                MaximumNodes),
            MaximumDiagnostics = ValidateLimit(
                nameof(syntaxLimits.MaximumDiagnostics),
                syntaxLimits.MaximumDiagnostics,
                MaximumParserDiagnostics),
            MaximumNestingDepth = ValidateLimit(
                nameof(syntaxLimits.MaximumNestingDepth),
                syntaxLimits.MaximumNestingDepth,
                MaximumParserNestingDepth),
            MaximumOperations = ValidateLimit(
                nameof(syntaxLimits.MaximumOperations),
                syntaxLimits.MaximumOperations,
                MaximumParserOperations),
        };

        ResolutionLimits resolutionLimits = manifest.ResolutionLimits
            ?? throw new InvalidDataException("Manifest resolutionLimits are required.");
        var resolutionOptions = new SafeCoreNameResolutionOptions
        {
            MaximumSymbols = ValidateLimit(
                nameof(resolutionLimits.MaximumSymbols),
                resolutionLimits.MaximumSymbols,
                MaximumSymbols),
            MaximumScopes = ValidateLimit(
                nameof(resolutionLimits.MaximumScopes),
                resolutionLimits.MaximumScopes,
                MaximumScopes),
            MaximumPathSegments = ValidateLimit(
                nameof(resolutionLimits.MaximumPathSegments),
                resolutionLimits.MaximumPathSegments,
                MaximumPathSegments),
            MaximumNameLength = ValidateLimit(
                nameof(resolutionLimits.MaximumNameLength),
                resolutionLimits.MaximumNameLength,
                MaximumNameLength),
            MaximumPathLength = ValidateLimit(
                nameof(resolutionLimits.MaximumPathLength),
                resolutionLimits.MaximumPathLength,
                MaximumPathLength),
            MaximumDiagnosticMessageLength = ValidateLimit(
                nameof(resolutionLimits.MaximumDiagnosticMessageLength),
                resolutionLimits.MaximumDiagnosticMessageLength,
                MaximumDiagnosticMessageLength),
            MaximumDiagnostics = ValidateLimit(
                nameof(resolutionLimits.MaximumDiagnostics),
                resolutionLimits.MaximumDiagnostics,
                MaximumResolverDiagnostics),
            MaximumNestingDepth = ValidateLimit(
                nameof(resolutionLimits.MaximumNestingDepth),
                resolutionLimits.MaximumNestingDepth,
                MaximumResolverNestingDepth),
            MaximumOperations = ValidateLimit(
                nameof(resolutionLimits.MaximumOperations),
                resolutionLimits.MaximumOperations,
                MaximumResolverOperations),
        };

        List<ManifestCase> manifestCases = manifest.Cases
            ?? throw new InvalidDataException("Manifest cases are required.");
        if (manifestCases.Count is < 1 or > MaximumCases)
        {
            throw new InvalidDataException($"Manifest case count must be 1..{MaximumCases}; found {manifestCases.Count}.");
        }

        if (manifest.Denominator != manifestCases.Count)
        {
            throw new InvalidDataException(
                $"Manifest denominator {manifest.Denominator} must exactly equal case count {manifestCases.Count}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var validatedCases = new List<ValidatedCase>(manifestCases.Count);
        for (int index = 0; index < manifestCases.Count && index < MaximumCases; index++)
        {
            ManifestCase item = manifestCases[index]
                ?? throw new InvalidDataException($"Manifest case at index {index} is null.");
            string id = ValidateIdentifier(item.Id, index);
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Manifest case id '{id}' is duplicated.");
            }

            string file = ValidateFixturePath(item.File, index);
            string fullPath = GetContainedPath(fixturesDirectory, file, $"case '{id}'");
            if (!File.Exists(fullPath))
            {
                throw new InvalidDataException($"Manifest case '{id}' fixture '{file}' does not exist.");
            }

            EnsureRegularFileWithoutReparsePoint(fullPath, $"case '{id}' fixture");
            if (!paths.Add(fullPath))
            {
                throw new InvalidDataException($"Manifest fixture '{file}' is referenced more than once.");
            }

            if (item.Expected is not "resolution-pass" and not "resolution-fail")
            {
                throw new InvalidDataException(
                    $"Manifest case '{id}' expected must be 'resolution-pass' or 'resolution-fail'.");
            }

            IReadOnlyList<ValidatedExpectedDiagnostic> expectedDiagnostics = ValidateExpectedDiagnostics(
                item.ExpectedDiagnostics,
                id,
                item.Expected,
                resolutionOptions.MaximumDiagnostics,
                syntaxOptions.MaximumSourceLength);
            IReadOnlyList<ValidatedExpectedResolution> expectedResolutions = ValidateExpectedResolutions(
                item.ExpectedResolutions,
                id,
                resolutionOptions);
            int minimumSymbols = ValidateMinimum(
                item.MinimumSymbols,
                resolutionOptions.MaximumSymbols,
                "minimumSymbols",
                id);
            int minimumScopes = ValidateMinimum(
                item.MinimumScopes,
                resolutionOptions.MaximumScopes,
                "minimumScopes",
                id);

            validatedCases.Add(new ValidatedCase(
                id,
                file,
                fullPath,
                item.Expected,
                expectedDiagnostics,
                expectedResolutions,
                minimumSymbols,
                minimumScopes));
        }

        return new ValidatedManifest(
            manifest.Version,
            manifest.Parser!,
            manifest.Resolver!,
            manifest.Denominator,
            syntaxOptions,
            resolutionOptions,
            validatedCases);
    }

    private static List<ValidatedExpectedDiagnostic> ValidateExpectedDiagnostics(
        List<ExpectedDiagnostic>? expectedDiagnostics,
        string caseId,
        string expectedOutcome,
        int maximumDiagnostics,
        int maximumSourceLength)
    {
        if (expectedDiagnostics is null)
        {
            throw new InvalidDataException($"Manifest case '{caseId}' expectedDiagnostics are required.");
        }

        if (expectedDiagnostics.Count > maximumDiagnostics)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' has more expected diagnostics than the configured diagnostic limit.");
        }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<ValidatedExpectedDiagnostic>(expectedDiagnostics.Count);
        int totalCount = 0;
        bool hasLocatedDiagnostics = false;
        bool hasLegacyDiagnostics = false;
        for (int index = 0; index < expectedDiagnostics.Count && index < maximumDiagnostics; index++)
        {
            ExpectedDiagnostic item = expectedDiagnostics[index]
                ?? throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected diagnostic at index {index} is null.");
            string code = item.Code ?? string.Empty;
            if (code.Length is < 1 or > MaximumIdentifierLength || !KnownExpectedDiagnosticCodes.Contains(code))
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected diagnostic at index {index} has an unknown code.");
            }

            if (!codes.Add(code))
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected diagnostic code '{code}' is duplicated.");
            }

            if (item.Count < 1 || item.Count > maximumDiagnostics)
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' diagnostic '{code}' count must be 1..{maximumDiagnostics}.");
            }

            totalCount = checked(totalCount + item.Count);
            if (totalCount > maximumDiagnostics)
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected diagnostic count exceeds the configured limit.");
            }

            IReadOnlyList<ValidatedExpectedDiagnosticOccurrence>? occurrences = null;
            if (item.Occurrences is null)
            {
                hasLegacyDiagnostics = true;
            }
            else
            {
                hasLocatedDiagnostics = true;
                if (item.Occurrences.Count != item.Count)
                {
                    throw new InvalidDataException(
                        $"Manifest case '{caseId}' diagnostic '{code}' must locate all {item.Count} occurrences.");
                }

                var occurrenceIndexes = new HashSet<int>();
                var validatedOccurrences = new List<ValidatedExpectedDiagnosticOccurrence>(item.Count);
                for (int occurrenceIndex = 0; occurrenceIndex < item.Occurrences.Count; occurrenceIndex++)
                {
                    ExpectedDiagnosticOccurrence occurrence = item.Occurrences[occurrenceIndex]
                        ?? throw new InvalidDataException(
                            $"Manifest case '{caseId}' diagnostic '{code}' occurrence at index {occurrenceIndex} is null.");
                    int occurrenceNumber = occurrence.Occurrence
                        ?? throw new InvalidDataException(
                            $"Manifest case '{caseId}' diagnostic '{code}' occurrence at index {occurrenceIndex} must specify occurrence.");
                    int start = occurrence.Start
                        ?? throw new InvalidDataException(
                            $"Manifest case '{caseId}' diagnostic '{code}' occurrence {occurrenceNumber} must specify start.");
                    int length = occurrence.Length
                        ?? throw new InvalidDataException(
                            $"Manifest case '{caseId}' diagnostic '{code}' occurrence {occurrenceNumber} must specify length.");
                    if (occurrenceNumber < 0 || occurrenceNumber >= item.Count ||
                        !occurrenceIndexes.Add(occurrenceNumber))
                    {
                        throw new InvalidDataException(
                            $"Manifest case '{caseId}' diagnostic '{code}' occurrences must uniquely cover 0..{item.Count - 1}.");
                    }

                    if (start < 0 || length < 0 || start > maximumSourceLength - length)
                    {
                        throw new InvalidDataException(
                            $"Manifest case '{caseId}' diagnostic '{code}' occurrence {occurrenceNumber} has an invalid source span.");
                    }

                    validatedOccurrences.Add(new(occurrenceNumber, start, length));
                }

                occurrences = validatedOccurrences;
            }

            validated.Add(new(code, item.Count, occurrences));
        }

        if (hasLocatedDiagnostics && hasLegacyDiagnostics)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' cannot mix located and legacy diagnostic expectations.");
        }

        if (expectedOutcome == "resolution-pass" && validated.Count != 0)
        {
            throw new InvalidDataException(
                $"Manifest resolution-pass case '{caseId}' cannot expect resolver diagnostics.");
        }

        if (expectedOutcome == "resolution-fail" && validated.Count == 0)
        {
            throw new InvalidDataException(
                $"Manifest resolution-fail case '{caseId}' must expect at least one resolver diagnostic.");
        }

        return validated;
    }

    private static List<ValidatedExpectedResolution> ValidateExpectedResolutions(
        List<ExpectedResolution>? expectedResolutions,
        string caseId,
        SafeCoreNameResolutionOptions options)
    {
        if (expectedResolutions is null || expectedResolutions.Count is < 1 or > MaximumExpectedResolutions)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' expectedResolutions count must be 1..{MaximumExpectedResolutions}.");
        }

        var selectors = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<ValidatedExpectedResolution>(expectedResolutions.Count);
        for (int index = 0; index < expectedResolutions.Count && index < MaximumExpectedResolutions; index++)
        {
            ExpectedResolution item = expectedResolutions[index]
                ?? throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected resolution at index {index} is null.");
            string path = ValidateExpectedPath(item.Path, options.MaximumPathLength, caseId, "path", index);
            string scopePath = ValidateExpectedPath(
                item.ScopePath,
                options.MaximumPathLength,
                caseId,
                "scopePath",
                index);
            int occurrence = item.Occurrence
                ?? throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected resolution at index {index} must specify occurrence.");
            if (occurrence is < 0 or >= MaximumExpectedResolutions)
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected resolution occurrence must be 0..{MaximumExpectedResolutions - 1}.");
            }

            string selector = path + "\0" + scopePath + "\0" + occurrence;
            if (!selectors.Add(selector))
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' repeats expected resolution '{path}' in '{scopePath}' occurrence {occurrence}.");
            }

            string status = ValidateResolutionStatus(item.Status, caseId, index);
            string? symbolKind = ValidateSymbolKind(item.SymbolKind, status, caseId, index);
            string? symbolQualifiedName = item.SymbolQualifiedName;
            if (status == "resolved")
            {
                symbolQualifiedName = ValidateExpectedPath(
                    symbolQualifiedName,
                    options.MaximumPathLength,
                    caseId,
                    "symbolQualifiedName",
                    index);
            }
            else if (symbolQualifiedName is not null)
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' non-resolved expectation at index {index} cannot specify symbolQualifiedName.");
            }

            int candidateCount = item.CandidateCount
                ?? throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected resolution at index {index} must specify candidateCount.");
            if (candidateCount < 0 || candidateCount > options.MaximumSymbols)
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' expected resolution candidateCount must be 0..{options.MaximumSymbols}.");
            }

            validated.Add(new(
                path,
                scopePath,
                occurrence,
                status,
                symbolKind,
                symbolQualifiedName,
                candidateCount));
        }

        return validated;
    }

    private static async Task<NameResolutionCaseReport> RunCaseAsync(
        ValidatedCase fixture,
        SafeCoreSyntaxOptions syntaxOptions,
        SafeCoreNameResolutionOptions resolutionOptions,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        bool parserInvoked = false;
        bool resolverInvoked = false;
        try
        {
            EnsureRegularFileWithoutReparsePoint(fixture.FullPath, $"case '{fixture.Id}' fixture");
            long maximumSourceBytes = checked((long)syntaxOptions.MaximumSourceLength * 4 + 4);
            BoundedFile sourceFile = await ReadBoundedFileAsync(
                fixture.FullPath,
                maximumSourceBytes,
                cancellationToken).ConfigureAwait(false);
            string source = DecodeUtf8(sourceFile.Bytes, fixture.File);
            if (source.Length > syntaxOptions.MaximumSourceLength)
            {
                throw new InvalidDataException(
                    $"Fixture '{fixture.File}' has {source.Length} UTF-16 characters; manifest limit is {syntaxOptions.MaximumSourceLength}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            parserInvoked = true;
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, fixture.File, syntaxOptions);
            cancellationToken.ThrowIfCancellationRequested();

            SafeCoreNameResolutionResult? resolution = null;
            var differences = new List<string>(fixture.ExpectedResolutions.Count + 4);
            if (!syntax.IsSuccessful)
            {
                differences.Add("Parser rejected a fixture that must reach name resolution.");
            }
            else
            {
                resolverInvoked = true;
                resolution = SafeCoreNameResolution.Resolve(syntax, resolutionOptions);
                cancellationToken.ThrowIfCancellationRequested();
            }

            string actualOutcome = resolution is null
                ? "parse-fail"
                : resolution.IsSuccessful
                    ? "resolution-pass"
                    : "resolution-fail";
            if (!string.Equals(actualOutcome, fixture.Expected, StringComparison.Ordinal))
            {
                differences.Add($"Expected outcome '{fixture.Expected}', actual '{actualOutcome}'.");
            }

            if (resolution is not null)
            {
                if (resolution.IsTruncated)
                {
                    differences.Add("Name resolution reached a configured safety limit.");
                }

                if (resolution.Symbols.Count < fixture.MinimumSymbols)
                {
                    differences.Add(
                        $"Resolved symbol count {resolution.Symbols.Count} is below minimum {fixture.MinimumSymbols}.");
                }

                if (resolution.Scopes.Count < fixture.MinimumScopes)
                {
                    differences.Add(
                        $"Resolved scope count {resolution.Scopes.Count} is below minimum {fixture.MinimumScopes}.");
                }

                CompareDiagnostics(fixture.ExpectedDiagnostics, resolution.Diagnostics, differences);
                CompareResolutions(
                    fixture.ExpectedResolutions,
                    resolution.Resolutions,
                    differences,
                    cancellationToken);
            }

            clock.Stop();
            IReadOnlyList<NameResolutionDiagnosticReport> syntaxDiagnostics = syntax.Diagnostics
                .Take(MaximumReportedDiagnostics)
                .Select(ToDiagnosticReport)
                .ToArray();
            IReadOnlyList<NameResolutionDiagnosticReport> resolutionDiagnostics = resolution?.Diagnostics
                .Take(MaximumReportedDiagnostics)
                .Select(ToDiagnosticReport)
                .ToArray()
                ?? Array.Empty<NameResolutionDiagnosticReport>();
            IReadOnlyList<NameResolutionPathReport> resolutions = resolution?.Resolutions
                .Take(MaximumReportedResolutions)
                .Select(ToPathReport)
                .ToArray()
                ?? Array.Empty<NameResolutionPathReport>();
            return new NameResolutionCaseReport(
                Id: fixture.Id,
                Source: fixture.File,
                SourceSha256: sourceFile.Sha256,
                Expected: fixture.Expected,
                Actual: actualOutcome,
                Status: differences.Count == 0 ? "passed" : "failed",
                Difference: differences.Count == 0 ? null : TrimDiagnostic(string.Join(" ", differences)),
                ExpectedDiagnostics: fixture.ExpectedDiagnostics,
                ExpectedResolutions: fixture.ExpectedResolutions,
                SourceLength: source.Length,
                TokenCount: syntax.LexResult.Tokens.Count,
                SymbolCount: resolution?.Symbols.Count,
                ScopeCount: resolution?.Scopes.Count,
                ResolutionCount: resolution?.Resolutions.Count,
                SyntaxDiagnosticCount: syntax.Diagnostics.Count,
                SyntaxDiagnosticsTruncated: syntax.Diagnostics.Count > syntaxDiagnostics.Count,
                ResolutionDiagnosticCount: resolution?.Diagnostics.Count,
                ResolutionDiagnosticsTruncated: resolution is not null && resolution.Diagnostics.Count > resolutionDiagnostics.Count,
                ResolutionsTruncated: resolution is not null && resolution.Resolutions.Count > resolutions.Count,
                ResolutionWasTruncated: resolution?.IsTruncated,
                ParserInvoked: parserInvoked,
                ResolverInvoked: resolverInvoked,
                ElapsedMilliseconds: clock.Elapsed.TotalMilliseconds,
                SyntaxDiagnostics: syntaxDiagnostics,
                ResolutionDiagnostics: resolutionDiagnostics,
                Resolutions: resolutions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            clock.Stop();
            return NameResolutionCaseReport.Skipped(
                fixture,
                "Harness deadline expired while executing the case.",
                clock.Elapsed,
                parserInvoked,
                resolverInvoked);
        }
        catch (Exception exception) when (IsExpectedHarnessException(exception))
        {
            clock.Stop();
            return NameResolutionCaseReport.Error(
                fixture,
                clock.Elapsed,
                TrimDiagnostic(exception.Message),
                parserInvoked,
                resolverInvoked);
        }
    }

    private static void CompareDiagnostics(
        IReadOnlyList<ValidatedExpectedDiagnostic> expected,
        IReadOnlyList<Diagnostic> actual,
        List<string> differences)
    {
        Dictionary<string, List<Diagnostic>> actualByCode = actual
            .GroupBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        Dictionary<string, int> actualCounts = actualByCode.ToDictionary(
            static item => item.Key,
            static item => item.Value.Count,
            StringComparer.Ordinal);
        bool matched = actualCounts.Count == expected.Count;
        for (int index = 0; index < expected.Count; index++)
        {
            ValidatedExpectedDiagnostic item = expected[index];
            matched &= actualCounts.TryGetValue(item.Code, out int count) && count == item.Count;
        }

        if (!matched)
        {
            differences.Add(
                $"Expected resolver diagnostics [{FormatDiagnosticCounts(expected)}], actual [{FormatDiagnosticCounts(actualCounts)}].");
        }

        for (int index = 0; index < expected.Count; index++)
        {
            ValidatedExpectedDiagnostic item = expected[index];
            if (item.Occurrences is null ||
                !actualByCode.TryGetValue(item.Code, out List<Diagnostic>? actualOccurrences))
            {
                continue;
            }

            for (int occurrenceIndex = 0; occurrenceIndex < item.Occurrences.Count; occurrenceIndex++)
            {
                ValidatedExpectedDiagnosticOccurrence occurrence = item.Occurrences[occurrenceIndex];
                if (occurrence.Occurrence >= actualOccurrences.Count)
                {
                    continue;
                }

                Diagnostic diagnostic = actualOccurrences[occurrence.Occurrence];
                if (diagnostic.Span.Start != occurrence.Start || diagnostic.Span.Length != occurrence.Length)
                {
                    differences.Add(
                        $"Diagnostic '{item.Code}' occurrence {occurrence.Occurrence} expected span [{occurrence.Start},{occurrence.Length}], actual [{diagnostic.Span.Start},{diagnostic.Span.Length}].");
                }
            }
        }
    }

    private static void CompareResolutions(
        IReadOnlyList<ValidatedExpectedResolution> expected,
        IReadOnlyList<SafeCorePathResolution> actual,
        List<string> differences,
        CancellationToken cancellationToken)
    {
        if (actual.Count != expected.Count)
        {
            differences.Add($"Expected exactly {expected.Count} recorded resolutions, actual {actual.Count}.");
        }

        var byPathAndScope = new Dictionary<(string Path, string ScopePath), List<SafeCorePathResolution>>();
        for (int index = 0; index < actual.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            SafeCorePathResolution resolution = actual[index];
            var key = (resolution.Path, resolution.ScopePath);
            if (!byPathAndScope.TryGetValue(key, out List<SafeCorePathResolution>? matches))
            {
                matches = [];
                byPathAndScope.Add(key, matches);
            }

            matches.Add(resolution);
        }

        for (int index = 0; index < expected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatedExpectedResolution item = expected[index];
            if (!byPathAndScope.TryGetValue((item.Path, item.ScopePath), out List<SafeCorePathResolution>? matches) ||
                item.Occurrence >= matches.Count)
            {
                differences.Add(
                    $"Expected resolution '{item.Path}' in '{item.ScopePath}' occurrence {item.Occurrence} was not recorded.");
                continue;
            }

            SafeCorePathResolution resolution = matches[item.Occurrence];
            string actualStatus = ToWireStatus(resolution.Status);
            if (!string.Equals(actualStatus, item.Status, StringComparison.Ordinal))
            {
                differences.Add(
                    $"Resolution '{item.Path}' occurrence {item.Occurrence} expected status '{item.Status}', actual '{actualStatus}'.");
            }

            string? actualKind = resolution.Symbol is null ? null : ToWireSymbolKind(resolution.Symbol.Kind);
            if (!string.Equals(actualKind, item.SymbolKind, StringComparison.Ordinal))
            {
                differences.Add(
                    $"Resolution '{item.Path}' occurrence {item.Occurrence} expected symbol kind '{item.SymbolKind ?? "null"}', actual '{actualKind ?? "null"}'.");
            }

            string? actualQualifiedName = resolution.Symbol?.QualifiedName;
            if (!string.Equals(actualQualifiedName, item.SymbolQualifiedName, StringComparison.Ordinal))
            {
                differences.Add(
                    $"Resolution '{item.Path}' occurrence {item.Occurrence} expected symbol '{item.SymbolQualifiedName ?? "null"}', actual '{actualQualifiedName ?? "null"}'.");
            }

            if (resolution.Candidates.Count != item.CandidateCount)
            {
                differences.Add(
                    $"Resolution '{item.Path}' occurrence {item.Occurrence} expected {item.CandidateCount} candidates, actual {resolution.Candidates.Count}.");
            }
        }
    }

    private static string FormatDiagnosticCounts(IReadOnlyList<ValidatedExpectedDiagnostic> diagnostics) =>
        string.Join(", ", diagnostics.Select(static item => item.Code + "=" + item.Count));

    private static string FormatDiagnosticCounts(IReadOnlyDictionary<string, int> diagnostics) =>
        string.Join(", ", diagnostics.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Key + "=" + item.Value));

    private static NameResolutionDiagnosticReport ToDiagnosticReport(Diagnostic diagnostic)
    {
        string message = TrimDiagnostic(diagnostic.Message);
        return new(
            diagnostic.Code,
            message,
            message.Length != diagnostic.Message.Length,
            diagnostic.Span.Start,
            diagnostic.Span.Length);
    }

    private static NameResolutionPathReport ToPathReport(SafeCorePathResolution resolution) => new(
        resolution.Path,
        resolution.ScopePath,
        ToWireStatus(resolution.Status),
        resolution.Symbol is null ? null : ToWireSymbolKind(resolution.Symbol.Kind),
        resolution.Symbol?.QualifiedName,
        resolution.Candidates.Count,
        resolution.Span.Start,
        resolution.Span.Length);

    private static int ValidateLimit(string name, int value, int maximum)
    {
        if (value is < 1 || value > maximum)
        {
            throw new InvalidDataException($"Manifest limit '{name}' must be 1..{maximum}; found {value}.");
        }

        return value;
    }

    private static int ValidateMinimum(int? value, int maximum, string propertyName, string caseId)
    {
        int normalized = value ?? 0;
        if (normalized < 0 || normalized > maximum)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' {propertyName} must be 0..{maximum}; found {normalized}.");
        }

        return normalized;
    }

    private static string ValidateIdentifier(string? id, int index)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumIdentifierLength ||
            id.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidDataException($"Manifest case at index {index} has an invalid id.");
        }

        return id;
    }

    private static string ValidateFixturePath(string? file, int index)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > MaximumFixturePathLength ||
            !string.Equals(file, file.Trim(), StringComparison.Ordinal) || Path.IsPathFullyQualified(file) ||
            file.IndexOfAny(['/', '\\']) >= 0 || !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal) ||
            file.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidDataException($"Manifest case at index {index} has an invalid fixture path.");
        }

        if (!string.Equals(Path.GetExtension(file), ".rs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest fixture '{file}' must have the .rs extension.");
        }

        return file;
    }

    private static string ValidateExpectedPath(
        string? path,
        int maximumLength,
        string caseId,
        string propertyName,
        int index)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > maximumLength ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            path.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' expected resolution at index {index} has an invalid {propertyName}.");
        }

        return path;
    }

    private static string ValidateResolutionStatus(string? status, string caseId, int index)
    {
        if (status is "resolved" or "unresolved" or "ambiguous" or "private" or "invalid" or "limit-exceeded")
        {
            return status;
        }

        throw new InvalidDataException(
            $"Manifest case '{caseId}' expected resolution at index {index} has an invalid status.");
    }

    private static string? ValidateSymbolKind(string? symbolKind, string status, string caseId, int index)
    {
        if (status != "resolved")
        {
            if (symbolKind is not null)
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' non-resolved expectation at index {index} cannot specify symbolKind.");
            }

            return null;
        }

        if (symbolKind is "module" or "import" or "function" or "struct" or "enum" or "type-alias" or
            "const" or "generic-parameter" or "parameter" or "local" or "field" or "enum-variant")
        {
            return symbolKind;
        }

        throw new InvalidDataException(
            $"Manifest case '{caseId}' resolved expectation at index {index} requires a known symbolKind.");
    }

    private static string ToWireStatus(SafeCoreNameResolutionStatus status) => status switch
    {
        SafeCoreNameResolutionStatus.Resolved => "resolved",
        SafeCoreNameResolutionStatus.Unresolved => "unresolved",
        SafeCoreNameResolutionStatus.Ambiguous => "ambiguous",
        SafeCoreNameResolutionStatus.Private => "private",
        SafeCoreNameResolutionStatus.Invalid => "invalid",
        SafeCoreNameResolutionStatus.LimitExceeded => "limit-exceeded",
        _ => throw new InvalidDataException($"Unknown name-resolution status '{status}'."),
    };

    private static string ToWireSymbolKind(SafeCoreSymbolKind kind) => kind switch
    {
        SafeCoreSymbolKind.Module => "module",
        SafeCoreSymbolKind.Import => "import",
        SafeCoreSymbolKind.Function => "function",
        SafeCoreSymbolKind.Struct => "struct",
        SafeCoreSymbolKind.Enum => "enum",
        SafeCoreSymbolKind.TypeAlias => "type-alias",
        SafeCoreSymbolKind.Const => "const",
        SafeCoreSymbolKind.GenericParameter => "generic-parameter",
        SafeCoreSymbolKind.Parameter => "parameter",
        SafeCoreSymbolKind.Local => "local",
        SafeCoreSymbolKind.Field => "field",
        SafeCoreSymbolKind.EnumVariant => "enum-variant",
        _ => throw new InvalidDataException($"Unknown symbol kind '{kind}'."),
    };

    private static string GetContainedPath(string root, string relativePath, string description)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(relativePath, fullRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"The {description} path is invalid: {exception.Message}", exception);
        }

        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative is "." or ".." || Path.IsPathFullyQualified(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {description} path '{relativePath}' escapes the fixtures directory.");
        }

        return fullPath;
    }

    private static void EnsureReportOutsideFixtures(string reportPath, string fixturesDirectory)
    {
        string fullReportPath = Path.GetFullPath(reportPath);
        string relative = Path.GetRelativePath(fixturesDirectory, fullReportPath);
        bool isOutside = Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        if (!isOutside)
        {
            throw new ArgumentException(
                "The safe-core name-resolution report cannot be written inside the fixtures directory.");
        }
    }

    private static void EnsureDirectoryWithoutReparsePoint(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException($"The {description} path must name a directory.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {description} path cannot be a symbolic link or reparse point.");
        }
    }

    private static void EnsureRegularFileWithoutReparsePoint(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"The {description} path must name a regular file.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {description} path cannot be a symbolic link or reparse point.");
        }
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 24,
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
                {
                    throw new JsonException("A JSON property was found outside an object.");
                }

                string propertyName = reader.GetString() ?? string.Empty;
                if (!objectProperties.Peek().Add(propertyName))
                {
                    throw new JsonException($"Duplicate JSON property '{propertyName}' is not allowed.");
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (objectProperties.Count == 0)
                {
                    throw new JsonException("The JSON object structure is invalid.");
                }

                objectProperties.Pop();
            }
        }

        if (!reachedEnd)
        {
            throw new JsonException($"The manifest exceeds the {MaximumJsonTokens}-token JSON limit.");
        }

        if (objectProperties.Count != 0)
        {
            throw new JsonException("The JSON object structure is incomplete.");
        }
    }

    private static async Task<BoundedFile> ReadBoundedFileAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > maximumBytes || length > int.MaxValue)
        {
            throw new InvalidDataException($"File '{path}' exceeds the {maximumBytes}-byte harness limit.");
        }

        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException($"File '{path}' changed while it was being read.");
        }

        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string DecodeUtf8(byte[] bytes, string file)
    {
        try
        {
            string source = StrictUtf8.GetString(bytes);
            return source.Length > 0 && source[0] == '\uFEFF' ? source[1..] : source;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Fixture '{file}' is not valid UTF-8.", exception);
        }
    }

    private static string ToRepositoryRelativePath(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsExpectedHarnessException(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
        ArgumentException or NotSupportedException;

    private static string TrimDiagnostic(string value)
    {
        const int maximumCharacters = 1024;
        return value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "...";
    }

    private static async Task WriteReportAsync(string path, SafeCoreNameResolutionReport report)
    {
        string temporaryPath = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, report, ReportJsonOptions).ConfigureAwait(false);
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class SafeCoreNameResolutionManifest
    {
        public string? Profile { get; init; }
        public int Version { get; init; }
        public string? Parser { get; init; }
        public string? Resolver { get; init; }
        public int Denominator { get; init; }
        public SyntaxLimits? SyntaxLimits { get; init; }
        public ResolutionLimits? ResolutionLimits { get; init; }
        public List<ManifestCase>? Cases { get; init; }
    }

    private sealed class SyntaxLimits
    {
        public int MaximumSourceLength { get; init; }
        public int MaximumTokens { get; init; }
        public int MaximumNodes { get; init; }
        public int MaximumDiagnostics { get; init; }
        public int MaximumNestingDepth { get; init; }
        public int MaximumOperations { get; init; }
    }

    private sealed class ResolutionLimits
    {
        public int MaximumSymbols { get; init; }
        public int MaximumScopes { get; init; }
        public int MaximumPathSegments { get; init; }
        public int MaximumNameLength { get; init; }
        public int MaximumPathLength { get; init; }
        public int MaximumDiagnosticMessageLength { get; init; }
        public int MaximumDiagnostics { get; init; }
        public int MaximumNestingDepth { get; init; }
        public int MaximumOperations { get; init; }
    }

    private sealed class ManifestCase
    {
        public string? Id { get; init; }
        public string? File { get; init; }
        public string? Expected { get; init; }
        public List<ExpectedDiagnostic>? ExpectedDiagnostics { get; init; }
        public List<ExpectedResolution>? ExpectedResolutions { get; init; }
        public int? MinimumSymbols { get; init; }
        public int? MinimumScopes { get; init; }
    }

    private sealed class ExpectedDiagnostic
    {
        public string? Code { get; init; }
        public int Count { get; init; }
        public List<ExpectedDiagnosticOccurrence>? Occurrences { get; init; }
    }

    private sealed class ExpectedDiagnosticOccurrence
    {
        public int? Occurrence { get; init; }
        public int? Start { get; init; }
        public int? Length { get; init; }
    }

    private sealed class ExpectedResolution
    {
        public string? Path { get; init; }
        public string? ScopePath { get; init; }
        public int? Occurrence { get; init; }
        public string? Status { get; init; }
        public string? SymbolKind { get; init; }
        public string? SymbolQualifiedName { get; init; }
        public int? CandidateCount { get; init; }
    }

    private sealed record ValidatedManifest(
        int Version,
        string Parser,
        string Resolver,
        int Denominator,
        SafeCoreSyntaxOptions SyntaxOptions,
        SafeCoreNameResolutionOptions ResolutionOptions,
        IReadOnlyList<ValidatedCase> Cases);

    private sealed record ValidatedCase(
        string Id,
        string File,
        string FullPath,
        string Expected,
        IReadOnlyList<ValidatedExpectedDiagnostic> ExpectedDiagnostics,
        IReadOnlyList<ValidatedExpectedResolution> ExpectedResolutions,
        int MinimumSymbols,
        int MinimumScopes);

    private sealed record ValidatedExpectedDiagnostic(
        string Code,
        int Count,
        IReadOnlyList<ValidatedExpectedDiagnosticOccurrence>? Occurrences);

    private sealed record ValidatedExpectedDiagnosticOccurrence(
        int Occurrence,
        int Start,
        int Length);

    private sealed record ValidatedExpectedResolution(
        string Path,
        string ScopePath,
        int Occurrence,
        string Status,
        string? SymbolKind,
        string? SymbolQualifiedName,
        int CandidateCount);

    private sealed record BoundedFile(byte[] Bytes, string Sha256);

    private sealed record SafeCoreNameResolutionReport(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string Profile,
        string EvidenceKind,
        NameResolutionScopeReport Scope,
        NameResolutionManifestReport Manifest,
        NameResolutionPipelineReport Pipeline,
        NameResolutionLimitsReport? Limits,
        NameResolutionSummaryReport Summary,
        IReadOnlyList<NameResolutionCaseReport> Cases,
        NameResolutionHostReport Host,
        NameResolutionExecutionReport Execution,
        string? HarnessError);

    private sealed record NameResolutionScopeReport(
        bool RustcConformance,
        bool RuntimeConformance,
        string Statement);

    private sealed record NameResolutionManifestReport(
        string Path,
        string? Sha256,
        int? Version,
        string? Parser,
        string? Resolver,
        int? Denominator,
        int? CaseCount,
        bool Validated,
        string? Error);

    private sealed record NameResolutionPipelineReport(
        string Parser,
        string Resolver,
        string Invocation,
        string? AssemblyVersion);

    private sealed record NameResolutionLimitsReport(
        int MaximumSourceLength,
        int MaximumTokens,
        int MaximumNodes,
        int MaximumParserDiagnostics,
        int MaximumParserNestingDepth,
        int MaximumParserOperations,
        int MaximumSymbols,
        int MaximumScopes,
        int MaximumPathSegments,
        int MaximumNameLength,
        int MaximumPathLength,
        int MaximumDiagnosticMessageLength,
        int MaximumResolverDiagnostics,
        int MaximumResolverNestingDepth,
        int MaximumResolverOperations,
        int MaximumCases,
        int MaximumManifestBytes,
        int MaximumExpectedResolutions,
        int MaximumReportedDiagnostics,
        int MaximumReportedResolutions,
        double DeadlineSeconds)
    {
        public static NameResolutionLimitsReport From(
            SafeCoreSyntaxOptions syntax,
            SafeCoreNameResolutionOptions resolution,
            TimeSpan deadline,
            int maximumCases,
            int maximumManifestBytes) => new(
                syntax.MaximumSourceLength,
                syntax.MaximumTokens,
                syntax.MaximumNodes,
                syntax.MaximumDiagnostics,
                syntax.MaximumNestingDepth,
                syntax.MaximumOperations,
                resolution.MaximumSymbols,
                resolution.MaximumScopes,
                resolution.MaximumPathSegments,
                resolution.MaximumNameLength,
                resolution.MaximumPathLength,
                resolution.MaximumDiagnosticMessageLength,
                resolution.MaximumDiagnostics,
                resolution.MaximumNestingDepth,
                resolution.MaximumOperations,
                maximumCases,
                maximumManifestBytes,
                SafeCoreNameResolutionProfileRunner.MaximumExpectedResolutions,
                SafeCoreNameResolutionProfileRunner.MaximumReportedDiagnostics,
                SafeCoreNameResolutionProfileRunner.MaximumReportedResolutions,
                deadline.TotalSeconds);
    }

    private sealed record NameResolutionSummaryReport(
        string Status,
        int Denominator,
        int Executed,
        int ResolutionExecuted,
        int Passed,
        int Failed,
        int Errors,
        int Skipped,
        int ExitCode);

    private sealed record NameResolutionCaseReport(
        string Id,
        string Source,
        string? SourceSha256,
        string Expected,
        string? Actual,
        string Status,
        string? Difference,
        IReadOnlyList<ValidatedExpectedDiagnostic> ExpectedDiagnostics,
        IReadOnlyList<ValidatedExpectedResolution> ExpectedResolutions,
        int? SourceLength,
        int? TokenCount,
        int? SymbolCount,
        int? ScopeCount,
        int? ResolutionCount,
        int? SyntaxDiagnosticCount,
        bool SyntaxDiagnosticsTruncated,
        int? ResolutionDiagnosticCount,
        bool ResolutionDiagnosticsTruncated,
        bool ResolutionsTruncated,
        bool? ResolutionWasTruncated,
        bool ParserInvoked,
        bool ResolverInvoked,
        double ElapsedMilliseconds,
        IReadOnlyList<NameResolutionDiagnosticReport> SyntaxDiagnostics,
        IReadOnlyList<NameResolutionDiagnosticReport> ResolutionDiagnostics,
        IReadOnlyList<NameResolutionPathReport> Resolutions)
    {
        public static NameResolutionCaseReport Error(
            ValidatedCase fixture,
            TimeSpan elapsed,
            string difference,
            bool parserInvoked,
            bool resolverInvoked) => Empty(
                fixture,
                "error",
                difference,
                elapsed,
                parserInvoked,
                resolverInvoked);

        public static NameResolutionCaseReport Skipped(
            ValidatedCase fixture,
            string difference,
            TimeSpan elapsed = default,
            bool parserInvoked = false,
            bool resolverInvoked = false) => Empty(
                fixture,
                "skipped",
                difference,
                elapsed,
                parserInvoked,
                resolverInvoked);

        private static NameResolutionCaseReport Empty(
            ValidatedCase fixture,
            string status,
            string difference,
            TimeSpan elapsed,
            bool parserInvoked,
            bool resolverInvoked) => new(
                fixture.Id,
                fixture.File,
                null,
                fixture.Expected,
                null,
                status,
                difference,
                fixture.ExpectedDiagnostics,
                fixture.ExpectedResolutions,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null,
                false,
                false,
                null,
                parserInvoked,
                resolverInvoked,
                elapsed.TotalMilliseconds,
                Array.Empty<NameResolutionDiagnosticReport>(),
                Array.Empty<NameResolutionDiagnosticReport>(),
                Array.Empty<NameResolutionPathReport>());
    }

    private sealed record NameResolutionDiagnosticReport(
        string Code,
        string Message,
        bool MessageTruncated,
        int Start,
        int Length);

    private sealed record NameResolutionPathReport(
        string Path,
        string ScopePath,
        string Status,
        string? SymbolKind,
        string? SymbolQualifiedName,
        int CandidateCount,
        int Start,
        int Length);

    private sealed record NameResolutionHostReport(
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string Framework,
        string RuntimeIdentifier,
        string RuntimeVersion,
        bool ContinuousIntegration)
    {
        public static NameResolutionHostReport Current => new(
            System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim(),
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Trim(),
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            Environment.Version.ToString(),
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record NameResolutionExecutionReport(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        double ElapsedMilliseconds,
        double DeadlineSeconds,
        bool DeadlineExpired);
}
