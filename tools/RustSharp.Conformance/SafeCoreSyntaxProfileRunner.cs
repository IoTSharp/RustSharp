using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

internal static class SafeCoreSyntaxProfileRunner
{
    private const string ProfileName = "safe-core-syntax";
    private const string ParserName = "RustSharp.Syntax.SafeCoreSyntax.Parse";
    private const string ManifestFileName = "safe-core-syntax-manifest.json";
    private const int ManifestVersion = 1;
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumCases = 64;
    private const int MaximumIdentifierLength = 128;
    private const int MaximumFixturePathLength = 512;
    private const int MaximumReportedDiagnostics = 128;
    private const int MaximumJsonTokens = 8_192;
    private const int MaximumSourceLength = 1_000_000;
    private const int MaximumTokens = 250_000;
    private const int MaximumNodes = 100_000;
    private const int MaximumDiagnostics = 128;
    private const int MaximumNestingDepth = 128;
    private const int MaximumOperations = 1_000_000;
    private const int PassedExitCode = 0;
    private const int FailedExitCode = 1;
    private const int HarnessErrorExitCode = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly HashSet<string> KnownExpectedDiagnosticCodes = new(StringComparer.Ordinal)
    {
        SafeCoreSyntaxDiagnosticCodes.ExpectedToken,
        SafeCoreSyntaxDiagnosticCodes.UnexpectedToken,
        SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax,
        SafeCoreSyntaxDiagnosticCodes.UnterminatedConstruct,
        SafeCoreSyntaxDiagnosticCodes.InvalidLiteralSuffix,
        SafeCoreSyntaxDiagnosticCodes.LimitReached,
        SafeCoreSyntaxDiagnosticCodes.LexicalTruncation,
        RustLexDiagnosticCodes.SourceTooLong,
        RustLexDiagnosticCodes.LimitReached,
        RustLexDiagnosticCodes.UnknownCharacter,
        RustLexDiagnosticCodes.UnmatchedClosingDelimiter,
        RustLexDiagnosticCodes.UnterminatedDelimiter,
        RustLexDiagnosticCodes.MismatchedDelimiter,
        RustLexDiagnosticCodes.UnterminatedLiteral,
        RustLexDiagnosticCodes.UnterminatedComment,
        RustLexDiagnosticCodes.InvalidNumber,
        RustLexDiagnosticCodes.InvalidLiteral,
        RustLexDiagnosticCodes.InvalidLifetime,
        RustLexDiagnosticCodes.ReservedPrefix,
        RustLexDiagnosticCodes.ReservedGuardedString,
        RustLexDiagnosticCodes.ReservedPounds,
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
        IReadOnlyList<SyntaxCaseReport> cases = Array.Empty<SyntaxCaseReport>();
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
            SafeCoreSyntaxManifest? manifest = JsonSerializer.Deserialize<SafeCoreSyntaxManifest>(
                manifestFile.Bytes,
                ManifestJsonOptions);
            validatedManifest = ValidateManifest(manifest, fixturesDirectory);

            var caseReports = new List<SyntaxCaseReport>(validatedManifest.Cases.Count);
            for (int index = 0; index < validatedManifest.Cases.Count && index < MaximumCases; index++)
            {
                ValidatedCase fixture = validatedManifest.Cases[index];
                if (deadlineSource.IsCancellationRequested)
                {
                    caseReports.Add(SyntaxCaseReport.Skipped(
                        fixture,
                        "Harness deadline expired before the case started."));
                }
                else
                {
                    caseReports.Add(await RunCaseAsync(
                        fixture,
                        validatedManifest.Options,
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
            harnessError = "The safe-core syntax harness deadline expired.";
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
                ? "The safe-core syntax harness deadline expired."
                : errorCount > 0
                    ? "One or more corpus cases could not be executed."
                    : skippedCount > 0
                        ? "One or more corpus cases were skipped."
                        : "The safe-core syntax harness was blocked.";
        }

        string status = exitCode switch
        {
            PassedExitCode => "passed",
            FailedExitCode => "failed",
            _ => "error",
        };
        var report = new SafeCoreSyntaxReport(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Profile: ProfileName,
            EvidenceKind: "parser-acceptance",
            Scope: new SyntaxScopeReport(
                RustcConformance: false,
                RuntimeConformance: false,
                Statement: "This report measures only RustSharp safe-core parser acceptance; it is not rustc differential or runtime conformance evidence."),
            Manifest: new SyntaxManifestReport(
                Path: relativeManifestPath,
                Sha256: manifestSha256,
                Version: validatedManifest?.Version,
                Parser: validatedManifest?.Parser,
                Validated: validatedManifest is not null,
                CaseCount: validatedManifest?.Cases.Count,
                Error: harnessError),
            Parser: new SyntaxParserReport(
                Name: ParserName,
                Invocation: "RustSharp.Syntax.SafeCoreSyntax.Parse(source, sourcePath, manifestLimits)",
                AssemblyVersion: typeof(SafeCoreSyntax).Assembly.GetName().Version?.ToString()),
            Limits: validatedManifest is null
                ? null
                : SyntaxLimitsReport.From(validatedManifest.Options, deadline, MaximumCases, MaximumManifestBytes),
            Summary: new SyntaxSummaryReport(
                Status: status,
                Denominator: validatedManifest?.Cases.Count ?? 0,
                Executed: cases.Count(static item => item.ParserInvoked),
                Passed: passedCount,
                Failed: failedCount,
                Errors: errorCount,
                Skipped: skippedCount,
                ExitCode: exitCode),
            Cases: cases,
            Host: SyntaxHostReport.Current,
            Execution: new SyntaxExecutionReport(
                StartedAtUtc: startedAtUtc,
                FinishedAtUtc: DateTimeOffset.UtcNow,
                ElapsedMilliseconds: harnessClock.Elapsed.TotalMilliseconds,
                DeadlineSeconds: deadline.TotalSeconds,
                DeadlineExpired: deadlineSource.IsCancellationRequested),
            HarnessError: harnessErrorReason);

        await WriteReportAsync(reportPath, report).ConfigureAwait(false);
        if (exitCode == HarnessErrorExitCode)
        {
            Console.Error.WriteLine($"conformance: safe-core-syntax harness error: {harnessErrorReason}");
        }

        Console.WriteLine(JsonSerializer.Serialize(report, ReportJsonOptions));
        return exitCode;
    }

    private static ValidatedManifest ValidateManifest(
        SafeCoreSyntaxManifest? manifest,
        string fixturesDirectory)
    {
        if (manifest is null)
        {
            throw new InvalidDataException("The safe-core syntax manifest is empty.");
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

        ManifestLimits limits = manifest.Limits
            ?? throw new InvalidDataException("Manifest limits are required.");
        var options = new SafeCoreSyntaxOptions
        {
            MaximumSourceLength = ValidateLimit(
                nameof(limits.MaximumSourceLength),
                limits.MaximumSourceLength,
                MaximumSourceLength),
            MaximumTokens = ValidateLimit(
                nameof(limits.MaximumTokens),
                limits.MaximumTokens,
                MaximumTokens),
            MaximumNodes = ValidateLimit(
                nameof(limits.MaximumNodes),
                limits.MaximumNodes,
                MaximumNodes),
            MaximumDiagnostics = ValidateLimit(
                nameof(limits.MaximumDiagnostics),
                limits.MaximumDiagnostics,
                MaximumDiagnostics),
            MaximumNestingDepth = ValidateLimit(
                nameof(limits.MaximumNestingDepth),
                limits.MaximumNestingDepth,
                MaximumNestingDepth),
            MaximumOperations = ValidateLimit(
                nameof(limits.MaximumOperations),
                limits.MaximumOperations,
                MaximumOperations),
        };

        List<ManifestCase> manifestCases = manifest.Cases
            ?? throw new InvalidDataException("Manifest cases are required.");
        if (manifestCases.Count is < 1 or > MaximumCases)
        {
            throw new InvalidDataException($"Manifest case count must be 1..{MaximumCases}; found {manifestCases.Count}.");
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

            if (item.Expected is not "parse-pass" and not "parse-fail")
            {
                throw new InvalidDataException($"Manifest case '{id}' expected must be 'parse-pass' or 'parse-fail'.");
            }

            int minimumItems = ValidateMinimum(item.MinimumItems, options.MaximumNodes, "minimumItems", id);
            int minimumFunctions = ValidateMinimum(item.MinimumFunctions, options.MaximumNodes, "minimumFunctions", id);
            string? diagnosticCode = item.DiagnosticCode;
            if (item.Expected == "parse-fail")
            {
                if (string.IsNullOrWhiteSpace(diagnosticCode) || diagnosticCode.Length > MaximumIdentifierLength ||
                    !KnownExpectedDiagnosticCodes.Contains(diagnosticCode))
                {
                    throw new InvalidDataException($"Manifest parse-fail case '{id}' requires a known stable diagnosticCode.");
                }

                if (item.MinimumItems is not null || item.MinimumFunctions is not null)
                {
                    throw new InvalidDataException($"Manifest parse-fail case '{id}' cannot specify minimumItems or minimumFunctions.");
                }
            }
            else if (diagnosticCode is not null)
            {
                throw new InvalidDataException($"Manifest parse-pass case '{id}' cannot specify diagnosticCode.");
            }

            validatedCases.Add(new ValidatedCase(
                id,
                file,
                fullPath,
                item.Expected,
                diagnosticCode,
                minimumItems,
                minimumFunctions));
        }

        return new ValidatedManifest(manifest.Version, manifest.Parser!, options, validatedCases);
    }

    private static async Task<SyntaxCaseReport> RunCaseAsync(
        ValidatedCase fixture,
        SafeCoreSyntaxOptions options,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        bool parserInvoked = false;
        try
        {
            EnsureRegularFileWithoutReparsePoint(fixture.FullPath, $"case '{fixture.Id}' fixture");
            long maximumSourceBytes = checked((long)options.MaximumSourceLength * 4 + 4);
            BoundedFile sourceFile = await ReadBoundedFileAsync(
                fixture.FullPath,
                maximumSourceBytes,
                cancellationToken).ConfigureAwait(false);
            string source = DecodeUtf8(sourceFile.Bytes, fixture.File);
            if (source.Length > options.MaximumSourceLength)
            {
                throw new InvalidDataException(
                    $"Fixture '{fixture.File}' has {source.Length} UTF-16 characters; manifest limit is {options.MaximumSourceLength}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            parserInvoked = true;
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, fixture.File, options);
            cancellationToken.ThrowIfCancellationRequested();
            int itemCount = result.Root?.Items.Count ?? 0;
            int functionCount = CountFunctions(result.Root, options.MaximumNodes, cancellationToken);
            bool diagnosticMatched = fixture.DiagnosticCode is null || result.Diagnostics.Any(
                diagnostic => string.Equals(diagnostic.Code, fixture.DiagnosticCode, StringComparison.Ordinal));
            var differences = new List<string>(3);
            if (fixture.Expected == "parse-pass")
            {
                if (!result.IsSuccessful)
                {
                    differences.Add("Parser rejected a fixture expected to pass.");
                }

                if (itemCount < fixture.MinimumItems)
                {
                    differences.Add($"Parsed item count {itemCount} is below minimum {fixture.MinimumItems}.");
                }

                if (functionCount < fixture.MinimumFunctions)
                {
                    differences.Add($"Parsed function count {functionCount} is below minimum {fixture.MinimumFunctions}.");
                }
            }
            else
            {
                if (result.IsSuccessful)
                {
                    differences.Add("Parser accepted a fixture expected to fail.");
                }

                if (result.IsTruncated)
                {
                    differences.Add("Parser rejection was caused by a configured limit.");
                }

                if (!diagnosticMatched)
                {
                    differences.Add($"Expected diagnostic '{fixture.DiagnosticCode}' was not emitted.");
                }
            }

            clock.Stop();
            IReadOnlyList<SyntaxDiagnosticReport> diagnostics = result.Diagnostics
                .Take(MaximumReportedDiagnostics)
                .Select(ToDiagnosticReport)
                .ToArray();
            return new SyntaxCaseReport(
                Id: fixture.Id,
                Source: fixture.File,
                SourceSha256: sourceFile.Sha256,
                Expected: fixture.Expected,
                Actual: result.IsSuccessful ? "parse-pass" : "parse-fail",
                Status: differences.Count == 0 ? "passed" : "failed",
                Difference: differences.Count == 0 ? null : string.Join(" ", differences),
                ExpectedDiagnosticCode: fixture.DiagnosticCode,
                SourceLength: source.Length,
                TokenCount: result.LexResult.Tokens.Count,
                TopLevelItemCount: itemCount,
                RecursiveFunctionCount: functionCount,
                DiagnosticCount: result.Diagnostics.Count,
                DiagnosticsTruncated: result.Diagnostics.Count > diagnostics.Count,
                IsTruncated: result.IsTruncated,
                ParserInvoked: parserInvoked,
                ElapsedMilliseconds: clock.Elapsed.TotalMilliseconds,
                Diagnostics: diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            clock.Stop();
            return SyntaxCaseReport.Skipped(
                fixture,
                "Harness deadline expired while executing the case.",
                clock.Elapsed,
                parserInvoked);
        }
        catch (Exception exception) when (IsExpectedHarnessException(exception))
        {
            clock.Stop();
            return SyntaxCaseReport.Error(fixture, clock.Elapsed, TrimDiagnostic(exception.Message), parserInvoked);
        }
    }

    private static SyntaxDiagnosticReport ToDiagnosticReport(Diagnostic diagnostic)
    {
        string message = TrimDiagnostic(diagnostic.Message);
        return new SyntaxDiagnosticReport(
            diagnostic.Code,
            message,
            message.Length != diagnostic.Message.Length,
            diagnostic.Span.Start,
            diagnostic.Span.Length);
    }

    private static int CountFunctions(
        SafeCoreCompilationUnitSyntax? root,
        int maximumNodes,
        CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return 0;
        }

        var pending = new Stack<SafeCoreItemSyntax>();
        for (int index = root.Items.Count - 1; index >= 0 && pending.Count < maximumNodes; index--)
        {
            pending.Push(root.Items[index]);
        }

        int inspected = 0;
        int functions = 0;
        while (pending.Count > 0 && inspected < maximumNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeCoreItemSyntax item = pending.Pop();
            inspected++;
            if (item is SafeCoreFunctionSyntax)
            {
                functions++;
            }
            else if (item is SafeCoreModuleSyntax module)
            {
                for (int index = module.Items.Count - 1; index >= 0 && pending.Count < maximumNodes; index--)
                {
                    pending.Push(module.Items[index]);
                }
            }
        }

        if (pending.Count > 0)
        {
            throw new InvalidDataException($"Function counting exceeded the manifest maximumNodes limit of {maximumNodes}.");
        }

        return functions;
    }

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
        string relative = Path.GetRelativePath(fixturesDirectory, reportPath);
        bool isOutside = Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        if (!isOutside)
        {
            throw new ArgumentException("The safe-core syntax report cannot be written inside the fixtures directory.");
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
            MaxDepth = 16,
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

        return new BoundedFile(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
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

    private static async Task WriteReportAsync(string path, SafeCoreSyntaxReport report)
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

    private sealed class SafeCoreSyntaxManifest
    {
        public string? Profile { get; init; }
        public int Version { get; init; }
        public string? Parser { get; init; }
        public ManifestLimits? Limits { get; init; }
        public List<ManifestCase>? Cases { get; init; }
    }

    private sealed class ManifestLimits
    {
        public int MaximumSourceLength { get; init; }
        public int MaximumTokens { get; init; }
        public int MaximumNodes { get; init; }
        public int MaximumDiagnostics { get; init; }
        public int MaximumNestingDepth { get; init; }
        public int MaximumOperations { get; init; }
    }

    private sealed class ManifestCase
    {
        public string? Id { get; init; }
        public string? File { get; init; }
        public string? Expected { get; init; }
        public string? DiagnosticCode { get; init; }
        public int? MinimumItems { get; init; }
        public int? MinimumFunctions { get; init; }
    }

    private sealed record ValidatedManifest(
        int Version,
        string Parser,
        SafeCoreSyntaxOptions Options,
        IReadOnlyList<ValidatedCase> Cases);

    private sealed record ValidatedCase(
        string Id,
        string File,
        string FullPath,
        string Expected,
        string? DiagnosticCode,
        int MinimumItems,
        int MinimumFunctions);

    private sealed record BoundedFile(byte[] Bytes, string Sha256);

    private sealed record SafeCoreSyntaxReport(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string Profile,
        string EvidenceKind,
        SyntaxScopeReport Scope,
        SyntaxManifestReport Manifest,
        SyntaxParserReport Parser,
        SyntaxLimitsReport? Limits,
        SyntaxSummaryReport Summary,
        IReadOnlyList<SyntaxCaseReport> Cases,
        SyntaxHostReport Host,
        SyntaxExecutionReport Execution,
        string? HarnessError);

    private sealed record SyntaxScopeReport(
        bool RustcConformance,
        bool RuntimeConformance,
        string Statement);

    private sealed record SyntaxManifestReport(
        string Path,
        string? Sha256,
        int? Version,
        string? Parser,
        bool Validated,
        int? CaseCount,
        string? Error);

    private sealed record SyntaxParserReport(
        string Name,
        string Invocation,
        string? AssemblyVersion);

    private sealed record SyntaxLimitsReport(
        int MaximumSourceLength,
        int MaximumTokens,
        int MaximumNodes,
        int MaximumDiagnostics,
        int MaximumNestingDepth,
        int MaximumOperations,
        int MaximumCases,
        int MaximumManifestBytes,
        double DeadlineSeconds)
    {
        public static SyntaxLimitsReport From(
            SafeCoreSyntaxOptions options,
            TimeSpan deadline,
            int maximumCases,
            int maximumManifestBytes) => new(
                options.MaximumSourceLength,
                options.MaximumTokens,
                options.MaximumNodes,
                options.MaximumDiagnostics,
                options.MaximumNestingDepth,
                options.MaximumOperations,
                maximumCases,
                maximumManifestBytes,
                deadline.TotalSeconds);
    }

    private sealed record SyntaxSummaryReport(
        string Status,
        int Denominator,
        int Executed,
        int Passed,
        int Failed,
        int Errors,
        int Skipped,
        int ExitCode);

    private sealed record SyntaxCaseReport(
        string Id,
        string Source,
        string? SourceSha256,
        string Expected,
        string? Actual,
        string Status,
        string? Difference,
        string? ExpectedDiagnosticCode,
        int? SourceLength,
        int? TokenCount,
        int? TopLevelItemCount,
        int? RecursiveFunctionCount,
        int? DiagnosticCount,
        bool DiagnosticsTruncated,
        bool? IsTruncated,
        bool ParserInvoked,
        double ElapsedMilliseconds,
        IReadOnlyList<SyntaxDiagnosticReport> Diagnostics)
    {
        public static SyntaxCaseReport Error(
            ValidatedCase fixture,
            TimeSpan elapsed,
            string difference,
            bool parserInvoked) => new(
            fixture.Id,
            fixture.File,
            null,
            fixture.Expected,
            null,
            "error",
            difference,
            fixture.DiagnosticCode,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            parserInvoked,
            elapsed.TotalMilliseconds,
            Array.Empty<SyntaxDiagnosticReport>());

        public static SyntaxCaseReport Skipped(
            ValidatedCase fixture,
            string difference,
            TimeSpan elapsed = default,
            bool parserInvoked = false) => new(
                fixture.Id,
                fixture.File,
                null,
                fixture.Expected,
                null,
                "skipped",
                difference,
                fixture.DiagnosticCode,
                null,
                null,
                null,
                null,
                null,
                false,
                null,
                parserInvoked,
                elapsed.TotalMilliseconds,
                Array.Empty<SyntaxDiagnosticReport>());
    }

    private sealed record SyntaxDiagnosticReport(
        string Code,
        string Message,
        bool MessageTruncated,
        int Start,
        int Length);

    private sealed record SyntaxHostReport(
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string Framework,
        string RuntimeIdentifier,
        string RuntimeVersion,
        bool ContinuousIntegration)
    {
        public static SyntaxHostReport Current => new(
            System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim(),
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Trim(),
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            Environment.Version.ToString(),
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SyntaxExecutionReport(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        double ElapsedMilliseconds,
        double DeadlineSeconds,
        bool DeadlineExpired);
}
