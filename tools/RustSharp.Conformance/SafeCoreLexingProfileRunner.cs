using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

internal static class SafeCoreLexingProfileRunner
{
    private const string ProfileName = "safe-core-lexing";
    private const string LexerName = "RustSharp.Syntax.RustLexer.Lex";
    private const string ManifestFileName = "safe-core-lexing-manifest.json";
    private const int ManifestVersion = 1;
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumCases = 32;
    private const int MaximumIdentifierLength = 128;
    private const int MaximumFixturePathLength = 512;
    private const int MaximumTreePathLength = 512;
    private const int MaximumJsonTokens = 16_384;
    private const int MaximumSourceLength = 65_536;
    private const int MaximumTokens = 4_096;
    private const int MaximumTrivia = 4_096;
    private const int MaximumDiagnostics = 128;
    private const int MaximumDelimiterDepth = 128;
    private const int MaximumDiagnosticMessageLength = 1_024;
    private const int PassedExitCode = 0;
    private const int FailedExitCode = 1;
    private const int HarnessErrorExitCode = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly HashSet<string> KnownDiagnosticCodes = new(StringComparer.Ordinal)
    {
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
        RustLexDiagnosticCodes.DelimiterDepthLimit,
    };
    private static readonly HashSet<string> KnownTokenKinds = Enum.GetValues<RustTokenKind>()
        .Select(ToTokenKind)
        .ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> KnownTriviaKinds = Enum.GetValues<RustTriviaKind>()
        .Select(ToTriviaKind)
        .ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> KnownDelimiters = Enum.GetValues<RustDelimiterKind>()
        .Select(ToDelimiter)
        .ToHashSet(StringComparer.Ordinal);

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
        PrepareReportTarget(reportPath);
        EnsureRegularFileWithoutReparsePoint(manifestPath, "manifest");
        string relativeManifestPath = ToRepositoryRelativePath(repositoryRoot, manifestPath);
        string? manifestSha256 = null;
        ValidatedManifest? validatedManifest = null;
        IReadOnlyList<LexCaseReport> cases = Array.Empty<LexCaseReport>();
        string? harnessError = null;

        using var deadlineSource = new CancellationTokenSource(deadline);
        try
        {
            BoundedFile manifestFile = await ReadBoundedFileAsync(
                manifestPath,
                MaximumManifestBytes,
                deadlineSource.Token).ConfigureAwait(false);
            manifestSha256 = manifestFile.Sha256;
            ValidateNoDuplicateProperties(manifestFile.Bytes);
            LexManifest? manifest = JsonSerializer.Deserialize<LexManifest>(
                manifestFile.Bytes,
                ManifestJsonOptions);
            validatedManifest = ValidateManifest(manifest, fixturesDirectory);

            var caseReports = new List<LexCaseReport>(validatedManifest.Cases.Count);
            for (int index = 0; index < validatedManifest.Cases.Count && index < MaximumCases; index++)
            {
                ValidatedCase fixture = validatedManifest.Cases[index];
                if (deadlineSource.IsCancellationRequested)
                {
                    caseReports.Add(LexCaseReport.Skipped(
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
        }
        catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
        {
            harnessError = "The safe-core lexing harness deadline expired.";
        }
        catch (Exception exception) when (IsExpectedHarnessException(exception))
        {
            harnessError = TrimDiagnostic(exception.Message);
        }

        bool deadlineExpired = FreezeDeadline(deadlineSource);
        harnessClock.Stop();
        int passedCount = cases.Count(static item => item.Status == "passed");
        int failedCount = cases.Count(static item => item.Status == "failed");
        int errorCount = cases.Count(static item => item.Status == "error");
        int skippedCount = cases.Count(static item => item.Status == "skipped");
        int exitCode = harnessError is not null || errorCount > 0 || skippedCount > 0 || deadlineExpired
            ? HarnessErrorExitCode
            : failedCount > 0
                ? FailedExitCode
                : PassedExitCode;
        string? harnessErrorReason = harnessError;
        if (exitCode == HarnessErrorExitCode && harnessErrorReason is null)
        {
            harnessErrorReason = deadlineExpired
                ? "The safe-core lexing harness deadline expired."
                : errorCount > 0
                    ? "One or more corpus cases could not be executed."
                    : skippedCount > 0
                        ? "One or more corpus cases were skipped."
                        : "The safe-core lexing harness was blocked.";
        }

        string status = exitCode switch
        {
            PassedExitCode => "passed",
            FailedExitCode => "failed",
            _ => "error",
        };
        var report = new LexReport(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Profile: ProfileName,
            EvidenceKind: "lexer-acceptance",
            Scope: new LexScopeReport(
                RustcConformance: false,
                RuntimeConformance: false,
                Statement: "This report measures only RustSharp safe-core lexer acceptance; it is not rustc differential or runtime conformance evidence."),
            Manifest: new LexManifestReport(
                Path: relativeManifestPath,
                Sha256: manifestSha256,
                Version: validatedManifest?.Version,
                Lexer: validatedManifest?.Lexer,
                Denominator: validatedManifest?.Denominator,
                Validated: validatedManifest is not null,
                CaseCount: validatedManifest?.Cases.Count,
                Error: harnessError),
            Lexer: new LexerReport(
                Name: LexerName,
                Invocation: "RustSharp.Syntax.RustLexer.Lex(source, sourcePath, manifestLimits)",
                AssemblyVersion: typeof(RustLexer).Assembly.GetName().Version?.ToString()),
            Limits: validatedManifest is null
                ? null
                : LexLimitsReport.From(
                    validatedManifest.Options,
                    deadline,
                    MaximumCases,
                    MaximumManifestBytes,
                    MaximumJsonTokens),
            Summary: new LexSummaryReport(
                Status: status,
                Denominator: validatedManifest?.Denominator ?? 0,
                Executed: cases.Count(static item => item.LexerInvoked),
                Passed: passedCount,
                Failed: failedCount,
                Errors: errorCount,
                Skipped: skippedCount,
                ExitCode: exitCode),
            Cases: cases,
            Host: LexHostReport.Current,
            Execution: new LexExecutionReport(
                StartedAtUtc: startedAtUtc,
                FinishedAtUtc: DateTimeOffset.UtcNow,
                ElapsedMilliseconds: harnessClock.Elapsed.TotalMilliseconds,
                DeadlineSeconds: deadline.TotalSeconds,
                DeadlineExpired: deadlineExpired),
            HarnessError: harnessErrorReason);

        await WriteReportAsync(reportPath, report).ConfigureAwait(false);
        if (exitCode == HarnessErrorExitCode)
        {
            Console.Error.WriteLine($"conformance: safe-core-lexing harness error: {harnessErrorReason}");
        }

        Console.WriteLine(JsonSerializer.Serialize(report, ReportJsonOptions));
        return exitCode;
    }

    private static ValidatedManifest ValidateManifest(LexManifest? manifest, string fixturesDirectory)
    {
        if (manifest is null)
        {
            throw new InvalidDataException("The safe-core lexing manifest is empty.");
        }

        if (!string.Equals(manifest.Profile, ProfileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Manifest profile must be '{ProfileName}'.");
        }

        if (manifest.Version != ManifestVersion)
        {
            throw new InvalidDataException($"Manifest version must be {ManifestVersion}.");
        }

        if (!string.Equals(manifest.Lexer, LexerName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Manifest lexer must be '{LexerName}'.");
        }

        ManifestLimits limits = manifest.Limits
            ?? throw new InvalidDataException("Manifest limits are required.");
        var options = new RustLexerOptions
        {
            MaximumSourceLength = ValidateLimit(
                nameof(limits.MaximumSourceLength),
                limits.MaximumSourceLength,
                MaximumSourceLength),
            MaximumTokens = ValidateLimit(nameof(limits.MaximumTokens), limits.MaximumTokens, MaximumTokens),
            MaximumTrivia = ValidateLimit(nameof(limits.MaximumTrivia), limits.MaximumTrivia, MaximumTrivia),
            MaximumDiagnostics = ValidateLimit(
                nameof(limits.MaximumDiagnostics),
                limits.MaximumDiagnostics,
                MaximumDiagnostics),
            MaximumDelimiterDepth = ValidateLimit(
                nameof(limits.MaximumDelimiterDepth),
                limits.MaximumDelimiterDepth,
                MaximumDelimiterDepth),
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

            if (item.Expected is not "lex-pass" and not "lex-fail")
            {
                throw new InvalidDataException($"Manifest case '{id}' expected must be 'lex-pass' or 'lex-fail'.");
            }

            List<LexTokenReport> tokens = item.Tokens
                ?? throw new InvalidDataException($"Manifest case '{id}' tokens are required.");
            List<LexTriviaReport> trivia = item.Trivia
                ?? throw new InvalidDataException($"Manifest case '{id}' trivia are required.");
            List<LexTreeReport> trees = item.Trees
                ?? throw new InvalidDataException($"Manifest case '{id}' trees are required.");
            List<LexDiagnosticReport> diagnostics = item.Diagnostics
                ?? throw new InvalidDataException($"Manifest case '{id}' diagnostics are required.");
            List<int> trailingTriviaIndices = item.TrailingTriviaIndices
                ?? throw new InvalidDataException($"Manifest case '{id}' trailingTriviaIndices are required.");

            ValidateTokens(id, tokens, trivia.Count, options);
            ValidateTrivia(id, trivia, options);
            ValidateTrees(id, trees, tokens.Count, options);
            ValidateDiagnostics(id, diagnostics, options);
            ValidateTriviaIndices(id, "trailingTriviaIndices", trailingTriviaIndices, trivia.Count);
            if (item.Expected == "lex-pass" && diagnostics.Count != 0)
            {
                throw new InvalidDataException($"Manifest lex-pass case '{id}' cannot expect diagnostics.");
            }

            if (item.Expected == "lex-fail" && diagnostics.Count == 0)
            {
                throw new InvalidDataException($"Manifest lex-fail case '{id}' must expect at least one diagnostic.");
            }

            validatedCases.Add(new ValidatedCase(
                id,
                file,
                fullPath,
                item.Expected,
                tokens,
                trivia,
                trees,
                diagnostics,
                trailingTriviaIndices));
        }

        return new ValidatedManifest(
            manifest.Version,
            manifest.Lexer!,
            manifest.Denominator,
            options,
            validatedCases);
    }

    private static void ValidateTokens(
        string caseId,
        IReadOnlyList<LexTokenReport> tokens,
        int triviaCount,
        RustLexerOptions options)
    {
        if (tokens.Count > options.MaximumTokens)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' token count exceeds maximumTokens {options.MaximumTokens}.");
        }

        for (int index = 0; index < tokens.Count && index < options.MaximumTokens; index++)
        {
            LexTokenReport token = tokens[index]
                ?? throw new InvalidDataException($"Manifest case '{caseId}' token at index {index} is null.");
            if (token.Index != index)
            {
                throw new InvalidDataException($"Manifest case '{caseId}' token indices must be contiguous from zero.");
            }

            if (!KnownTokenKinds.Contains(token.Kind ?? string.Empty))
            {
                throw new InvalidDataException($"Manifest case '{caseId}' token {index} has unknown kind '{token.Kind}'.");
            }

            bool delimiterToken = token.Kind is "open-delimiter" or "close-delimiter";
            if (delimiterToken != (token.Delimiter is not null) ||
                (token.Delimiter is not null && !KnownDelimiters.Contains(token.Delimiter)))
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' token {index} has invalid delimiter metadata.");
            }

            if (token.IsKeyword != string.Equals(token.Kind, "keyword", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' token {index} has invalid keyword metadata.");
            }

            ValidateTextAndSpan(caseId, $"token {index}", token.Text, token.Start, token.Length, options.MaximumSourceLength);
            ValidateTriviaIndices(caseId, $"token {index} leadingTriviaIndices", token.LeadingTriviaIndices, triviaCount);
        }
    }

    private static void ValidateTrivia(
        string caseId,
        IReadOnlyList<LexTriviaReport> trivia,
        RustLexerOptions options)
    {
        if (trivia.Count > options.MaximumTrivia)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' trivia count exceeds maximumTrivia {options.MaximumTrivia}.");
        }

        for (int index = 0; index < trivia.Count && index < options.MaximumTrivia; index++)
        {
            LexTriviaReport item = trivia[index]
                ?? throw new InvalidDataException($"Manifest case '{caseId}' trivia at index {index} is null.");
            if (item.Index != index)
            {
                throw new InvalidDataException($"Manifest case '{caseId}' trivia indices must be contiguous from zero.");
            }

            if (!KnownTriviaKinds.Contains(item.Kind ?? string.Empty))
            {
                throw new InvalidDataException($"Manifest case '{caseId}' trivia {index} has unknown kind '{item.Kind}'.");
            }

            ValidateTextAndSpan(caseId, $"trivia {index}", item.Text, item.Start, item.Length, options.MaximumSourceLength);
        }
    }

    private static void ValidateTrees(
        string caseId,
        IReadOnlyList<LexTreeReport> trees,
        int tokenCount,
        RustLexerOptions options)
    {
        if (trees.Count > options.MaximumTokens)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' tree node count exceeds maximumTokens {options.MaximumTokens}.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < trees.Count && index < options.MaximumTokens; index++)
        {
            LexTreeReport tree = trees[index]
                ?? throw new InvalidDataException($"Manifest case '{caseId}' tree at index {index} is null.");
            ValidateTreePath(caseId, tree.Path, options.MaximumDelimiterDepth);
            if (!paths.Add(tree.Path))
            {
                throw new InvalidDataException($"Manifest case '{caseId}' tree path '{tree.Path}' is duplicated.");
            }

            ValidateSpan(caseId, $"tree '{tree.Path}'", tree.Start, tree.Length, options.MaximumSourceLength);
            if (tree.ChildCount < 0 || tree.ChildCount > options.MaximumTokens)
            {
                throw new InvalidDataException($"Manifest case '{caseId}' tree '{tree.Path}' has an invalid childCount.");
            }

            if (tree.NodeKind == "leaf")
            {
                ValidateTokenIndex(caseId, tree.Path, tree.TokenIndex, tokenCount, "tokenIndex");
                if (tree.Delimiter is not null || tree.OpenTokenIndex is not null ||
                    tree.CloseTokenIndex is not null || tree.IsClosed is not null || tree.ChildCount != 0)
                {
                    throw new InvalidDataException($"Manifest case '{caseId}' leaf tree '{tree.Path}' has group fields.");
                }
            }
            else if (tree.NodeKind == "delimited")
            {
                if (!KnownDelimiters.Contains(tree.Delimiter ?? string.Empty) || tree.IsClosed is null)
                {
                    throw new InvalidDataException($"Manifest case '{caseId}' group tree '{tree.Path}' has invalid delimiter fields.");
                }

                ValidateTokenIndex(caseId, tree.Path, tree.OpenTokenIndex, tokenCount, "openTokenIndex");
                if (tree.IsClosed.Value)
                {
                    ValidateTokenIndex(caseId, tree.Path, tree.CloseTokenIndex, tokenCount, "closeTokenIndex");
                }
                else if (tree.CloseTokenIndex is not null)
                {
                    throw new InvalidDataException($"Manifest case '{caseId}' open tree '{tree.Path}' has a closeTokenIndex.");
                }

                if (tree.TokenIndex is not null)
                {
                    throw new InvalidDataException($"Manifest case '{caseId}' group tree '{tree.Path}' has a leaf tokenIndex.");
                }
            }
            else
            {
                throw new InvalidDataException($"Manifest case '{caseId}' tree '{tree.Path}' has invalid nodeKind '{tree.NodeKind}'.");
            }
        }
    }

    private static void ValidateDiagnostics(
        string caseId,
        IReadOnlyList<LexDiagnosticReport> diagnostics,
        RustLexerOptions options)
    {
        if (diagnostics.Count > options.MaximumDiagnostics)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' diagnostic count exceeds maximumDiagnostics {options.MaximumDiagnostics}.");
        }

        for (int index = 0; index < diagnostics.Count && index < options.MaximumDiagnostics; index++)
        {
            LexDiagnosticReport diagnostic = diagnostics[index]
                ?? throw new InvalidDataException($"Manifest case '{caseId}' diagnostic at index {index} is null.");
            if (!KnownDiagnosticCodes.Contains(diagnostic.Code ?? string.Empty))
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' diagnostic {index} has unknown code '{diagnostic.Code}'.");
            }

            if (string.IsNullOrWhiteSpace(diagnostic.Message) || diagnostic.Message.Length > MaximumDiagnosticMessageLength)
            {
                throw new InvalidDataException($"Manifest case '{caseId}' diagnostic {index} has an invalid message.");
            }

            ValidateSpan(
                caseId,
                $"diagnostic {index}",
                diagnostic.Start,
                diagnostic.Length,
                options.MaximumSourceLength);
        }
    }

    private static async Task<LexCaseReport> RunCaseAsync(
        ValidatedCase fixture,
        RustLexerOptions options,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        bool lexerInvoked = false;
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
            lexerInvoked = true;
            RustLexResult result = RustLexer.Lex(source, fixture.File, options);
            cancellationToken.ThrowIfCancellationRequested();

            LexTriviaReport[] trivia = BuildTriviaReport(result);
            LexTokenReport[] tokens = BuildTokenReport(result);
            List<LexTreeReport> trees = BuildTreeReport(result, cancellationToken);
            IReadOnlyList<LexDiagnosticReport> diagnostics = result.Diagnostics
                .Select(ToDiagnosticReport)
                .ToArray();
            int[] trailingTriviaIndices = ResolveTriviaIndices(result.Trivia, result.TrailingTrivia);
            bool sourceRoundTrips = string.Equals(result.ToSourceText(), source, StringComparison.Ordinal);
            bool lexicalCoverageExact = ReconstructSource(result, options, cancellationToken, out string reconstruction) &&
                string.Equals(reconstruction, source, StringComparison.Ordinal);
            bool spansExact = ValidateActualSpans(result, source);
            string actual = result.IsSuccessful ? "lex-pass" : "lex-fail";
            var differences = new List<string>(8);
            if (!string.Equals(fixture.Expected, actual, StringComparison.Ordinal))
            {
                differences.Add($"Lexer produced {actual}; expected {fixture.Expected}.");
            }

            if (result.IsTruncated)
            {
                differences.Add("Lexer result was truncated by a configured limit.");
            }

            if (!sourceRoundTrips)
            {
                differences.Add("RustLexResult.ToSourceText() did not preserve the source.");
            }

            if (!lexicalCoverageExact)
            {
                differences.Add("Tokens and trivia did not reconstruct the source exactly.");
            }

            if (!spansExact)
            {
                differences.Add("One or more token, trivia, or diagnostic spans were invalid.");
            }

            CompareExpected("tokens", fixture.Tokens, tokens, differences);
            CompareExpected("trivia", fixture.Trivia, trivia, differences);
            CompareExpected("trees", fixture.Trees, trees, differences);
            CompareExpected("diagnostics", fixture.Diagnostics, diagnostics, differences);
            if (!fixture.TrailingTriviaIndices.SequenceEqual(trailingTriviaIndices))
            {
                differences.Add("Trailing trivia indices differed from the manifest.");
            }

            clock.Stop();
            return new LexCaseReport(
                Id: fixture.Id,
                Source: fixture.File,
                SourceSha256: sourceFile.Sha256,
                Expected: fixture.Expected,
                Actual: actual,
                Status: differences.Count == 0 ? "passed" : "failed",
                Difference: differences.Count == 0 ? null : string.Join(" ", differences),
                SourceLength: source.Length,
                ExpectedCounts: LexCountsReport.From(
                    fixture.Tokens,
                    fixture.Trivia,
                    fixture.Trees,
                    fixture.Diagnostics,
                    fixture.TrailingTriviaIndices),
                ActualCounts: LexCountsReport.From(tokens, trivia, trees, diagnostics, trailingTriviaIndices),
                IsTruncated: result.IsTruncated,
                SourceRoundTrips: sourceRoundTrips,
                LexicalCoverageExact: lexicalCoverageExact,
                SpansExact: spansExact,
                ExpectationsMatched: differences.Count == 0,
                LexerInvoked: lexerInvoked,
                ElapsedMilliseconds: clock.Elapsed.TotalMilliseconds,
                Tokens: tokens,
                Trivia: trivia,
                TrailingTriviaIndices: trailingTriviaIndices,
                Trees: trees,
                Diagnostics: diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            clock.Stop();
            return LexCaseReport.Skipped(
                fixture,
                "Harness deadline expired while executing the case.",
                clock.Elapsed,
                lexerInvoked);
        }
        catch (Exception exception) when (IsExpectedHarnessException(exception))
        {
            clock.Stop();
            return LexCaseReport.Error(
                fixture,
                clock.Elapsed,
                TrimDiagnostic(exception.Message),
                lexerInvoked);
        }
    }

    private static LexTriviaReport[] BuildTriviaReport(RustLexResult result)
    {
        var report = new LexTriviaReport[result.Trivia.Count];
        for (int index = 0; index < result.Trivia.Count; index++)
        {
            RustTrivia trivia = result.Trivia[index];
            report[index] = new LexTriviaReport(
                index,
                ToTriviaKind(trivia.Kind),
                trivia.Text,
                trivia.Span.Start,
                trivia.Span.Length,
                trivia.IsDocumentation);
        }

        return report;
    }

    private static LexTokenReport[] BuildTokenReport(RustLexResult result)
    {
        var triviaIndices = new Dictionary<RustTrivia, int>();
        for (int index = 0; index < result.Trivia.Count; index++)
        {
            if (!triviaIndices.TryAdd(result.Trivia[index], index))
            {
                throw new InvalidDataException("Lexer produced duplicate trivia records that cannot be indexed unambiguously.");
            }
        }

        var report = new LexTokenReport[result.Tokens.Count];
        for (int index = 0; index < result.Tokens.Count; index++)
        {
            RustToken token = result.Tokens[index];
            var leading = new int[token.LeadingTrivia.Count];
            for (int triviaIndex = 0; triviaIndex < token.LeadingTrivia.Count; triviaIndex++)
            {
                if (!triviaIndices.TryGetValue(token.LeadingTrivia[triviaIndex], out leading[triviaIndex]))
                {
                    throw new InvalidDataException("A token referenced trivia absent from the global trivia list.");
                }
            }

            report[index] = new LexTokenReport(
                index,
                ToTokenKind(token.Kind),
                token.Text,
                token.Span.Start,
                token.Span.Length,
                token.Delimiter is null ? null : ToDelimiter(token.Delimiter.Value),
                token.IsKeyword,
                leading);
        }

        return report;
    }

    private static List<LexTreeReport> BuildTreeReport(
        RustLexResult result,
        CancellationToken cancellationToken)
    {
        var tokenIndices = new Dictionary<RustToken, int>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < result.Tokens.Count; index++)
        {
            tokenIndices.Add(result.Tokens[index], index);
        }

        var pending = new Stack<(RustTokenTree Node, string Path)>();
        for (int index = result.TokenTrees.Count - 1; index >= 0; index--)
        {
            pending.Push((result.TokenTrees[index], index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var report = new List<LexTreeReport>(Math.Min(result.Tokens.Count, MaximumTokens));
        while (pending.Count > 0 && report.Count < MaximumTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (RustTokenTree node, string path) = pending.Pop();
            if (node is RustLeafTokenTree leaf)
            {
                report.Add(new LexTreeReport(
                    path,
                    "leaf",
                    node.Span.Start,
                    node.Span.Length,
                    0,
                    GetTokenIndex(tokenIndices, leaf.TokenValue),
                    null,
                    null,
                    null,
                    null));
                continue;
            }

            if (node is not RustDelimitedTokenTree group)
            {
                throw new InvalidDataException($"Lexer produced unsupported token tree node '{node.GetType().Name}'.");
            }

            int openTokenIndex = GetTokenIndex(tokenIndices, group.OpenToken);
            int? closeTokenIndex = group.CloseToken is null
                ? null
                : GetTokenIndex(tokenIndices, group.CloseToken);
            report.Add(new LexTreeReport(
                path,
                "delimited",
                group.Span.Start,
                group.Span.Length,
                group.Children.Count,
                null,
                ToDelimiter(group.Delimiter),
                openTokenIndex,
                closeTokenIndex,
                group.IsClosed));
            for (int index = group.Children.Count - 1; index >= 0; index--)
            {
                pending.Push((group.Children[index], path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        if (pending.Count != 0)
        {
            throw new InvalidDataException($"Token tree reporting exceeded the {MaximumTokens}-node safety limit.");
        }

        return report;
    }

    private static int GetTokenIndex(Dictionary<RustToken, int> indices, RustToken token)
    {
        if (!indices.TryGetValue(token, out int index))
        {
            throw new InvalidDataException("A token tree referenced a token absent from the flat token list.");
        }

        return index;
    }

    private static int[] ResolveTriviaIndices(
        IReadOnlyList<RustTrivia> allTrivia,
        IReadOnlyList<RustTrivia> selectedTrivia)
    {
        var indices = new Dictionary<RustTrivia, int>();
        for (int index = 0; index < allTrivia.Count; index++)
        {
            if (!indices.TryAdd(allTrivia[index], index))
            {
                throw new InvalidDataException("Lexer produced duplicate trivia records that cannot be indexed unambiguously.");
            }
        }

        var selected = new int[selectedTrivia.Count];
        for (int index = 0; index < selectedTrivia.Count; index++)
        {
            if (!indices.TryGetValue(selectedTrivia[index], out selected[index]))
            {
                throw new InvalidDataException("Trailing trivia was absent from the global trivia list.");
            }
        }

        return selected;
    }

    private static bool ReconstructSource(
        RustLexResult result,
        RustLexerOptions options,
        CancellationToken cancellationToken,
        out string reconstruction)
    {
        int pieceCount = checked(result.Tokens.Count + result.Trivia.Count);
        if (pieceCount > options.MaximumTokens + options.MaximumTrivia)
        {
            reconstruction = string.Empty;
            return false;
        }

        var pieces = new List<(TextSpan Span, string Text)>(pieceCount);
        for (int index = 0; index < result.Tokens.Count; index++)
        {
            pieces.Add((result.Tokens[index].Span, result.Tokens[index].Text));
        }

        for (int index = 0; index < result.Trivia.Count; index++)
        {
            pieces.Add((result.Trivia[index].Span, result.Trivia[index].Text));
        }

        pieces.Sort(static (left, right) => left.Span.Start.CompareTo(right.Span.Start));
        var builder = new StringBuilder(result.Source.Length);
        int expectedStart = 0;
        for (int index = 0; index < pieces.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (TextSpan span, string text) = pieces[index];
            if (span.Start != expectedStart || span.Length != text.Length)
            {
                reconstruction = builder.ToString();
                return false;
            }

            builder.Append(text);
            expectedStart = span.End;
        }

        reconstruction = builder.ToString();
        return expectedStart == result.Source.Length;
    }

    private static bool ValidateActualSpans(RustLexResult result, string source)
    {
        for (int index = 0; index < result.Tokens.Count; index++)
        {
            RustToken token = result.Tokens[index];
            if (!SpanMatches(source, token.Span, token.Text))
            {
                return false;
            }
        }

        for (int index = 0; index < result.Trivia.Count; index++)
        {
            RustTrivia trivia = result.Trivia[index];
            if (!SpanMatches(source, trivia.Span, trivia.Text))
            {
                return false;
            }
        }

        for (int index = 0; index < result.Diagnostics.Count; index++)
        {
            TextSpan span = result.Diagnostics[index].Span;
            if (span.Start < 0 || span.Length < 0 || span.Start > source.Length - span.Length)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SpanMatches(string source, TextSpan span, string text) =>
        span.Start >= 0 && span.Length >= 0 && span.Start <= source.Length - span.Length &&
        span.Length == text.Length &&
        source.AsSpan(span.Start, span.Length).SequenceEqual(text.AsSpan());

    private static LexDiagnosticReport ToDiagnosticReport(Diagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Span.Start,
        diagnostic.Span.Length);

    private static void CompareExpected<T>(
        string name,
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        List<string> differences)
    {
        string expectedJson = JsonSerializer.Serialize(expected, ReportJsonOptions);
        string actualJson = JsonSerializer.Serialize(actual, ReportJsonOptions);
        if (!string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
        {
            differences.Add($"Exact {name} differed from the manifest.");
        }
    }

    private static int ValidateLimit(string name, int value, int maximum)
    {
        if (value is < 1 || value > maximum)
        {
            throw new InvalidDataException($"Manifest limit '{name}' must be 1..{maximum}; found {value}.");
        }

        return value;
    }

    private static void ValidateTextAndSpan(
        string caseId,
        string description,
        string? text,
        int start,
        int length,
        int maximumSourceLength)
    {
        if (text is null || text.Length != length)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' {description} text length must equal its UTF-16 span length.");
        }

        ValidateSpan(caseId, description, start, length, maximumSourceLength);
    }

    private static void ValidateSpan(
        string caseId,
        string description,
        int start,
        int length,
        int maximumSourceLength)
    {
        if (start < 0 || length < 0 || start > maximumSourceLength - length)
        {
            throw new InvalidDataException($"Manifest case '{caseId}' {description} has an invalid span.");
        }
    }

    private static void ValidateTriviaIndices(
        string caseId,
        string description,
        IReadOnlyList<int>? indices,
        int triviaCount)
    {
        if (indices is null || indices.Count > triviaCount)
        {
            throw new InvalidDataException($"Manifest case '{caseId}' {description} is invalid.");
        }

        int previous = -1;
        for (int index = 0; index < indices.Count && index < triviaCount; index++)
        {
            int value = indices[index];
            if (value < 0 || value >= triviaCount || value <= previous)
            {
                throw new InvalidDataException(
                    $"Manifest case '{caseId}' {description} must contain unique ascending valid indices.");
            }

            previous = value;
        }
    }

    private static void ValidateTreePath(string caseId, string? path, int maximumDepth)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumTreePathLength ||
            path[0] == '/' || path[^1] == '/' ||
            path.Any(static character => !char.IsAsciiDigit(character) && character != '/'))
        {
            throw new InvalidDataException($"Manifest case '{caseId}' has invalid tree path '{path}'.");
        }

        string[] segments = path.Split('/', maximumDepth + 2, StringSplitOptions.None);
        if (segments.Length > maximumDepth + 1 || segments.Any(static segment =>
                segment.Length == 0 ||
                !int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            throw new InvalidDataException($"Manifest case '{caseId}' has invalid tree path '{path}'.");
        }
    }

    private static void ValidateTokenIndex(
        string caseId,
        string treePath,
        int? index,
        int tokenCount,
        string propertyName)
    {
        if (index is null || index < 0 || index >= tokenCount)
        {
            throw new InvalidDataException(
                $"Manifest case '{caseId}' tree '{treePath}' has invalid {propertyName}.");
        }
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
            throw new ArgumentException("The safe-core lexing report cannot be written inside the fixtures directory.");
        }
    }

    private static void PrepareReportTarget(string reportPath)
    {
        string fullReportPath = Path.GetFullPath(reportPath);
        string? directory = Path.GetDirectoryName(fullReportPath);
        if (directory is null)
        {
            throw new ArgumentException("The safe-core lexing report path must have a parent directory.");
        }

        EnsurePathHasNoReparsePoints(directory, "report directory");
        Directory.CreateDirectory(directory);
        EnsurePathHasNoReparsePoints(directory, "report directory");

        try
        {
            FileAttributes attributes = File.GetAttributes(fullReportPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new ArgumentException("The safe-core lexing report path must name a file.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("The safe-core lexing report path cannot be a symbolic link or reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsurePathHasNoReparsePoints(string path, string description)
    {
        string? current = Path.GetFullPath(path);
        for (int depth = 0; depth < 64 && current is not null; depth++)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException($"The {description} cannot contain a symbolic link or reparse point.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return;
            }

            current = parent;
        }

        throw new ArgumentException($"The {description} exceeds the 64-component validation limit.");
    }

    private static bool FreezeDeadline(CancellationTokenSource deadlineSource)
    {
        if (deadlineSource.IsCancellationRequested)
        {
            return true;
        }

        return !deadlineSource.TryReset() || deadlineSource.IsCancellationRequested;
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
            MaxDepth = 32,
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
            bufferSize: 4_096,
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
        ArgumentException or NotSupportedException or OverflowException;

    private static string TrimDiagnostic(string value) =>
        value.Length <= MaximumDiagnosticMessageLength
            ? value
            : value[..MaximumDiagnosticMessageLength] + "...";

    private static async Task WriteReportAsync(string path, LexReport report)
    {
        string temporaryPath = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
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

    private static string ToTokenKind(RustTokenKind kind) => kind switch
    {
        RustTokenKind.Identifier => "identifier",
        RustTokenKind.RawIdentifier => "raw-identifier",
        RustTokenKind.Keyword => "keyword",
        RustTokenKind.Lifetime => "lifetime",
        RustTokenKind.IntegerLiteral => "integer-literal",
        RustTokenKind.FloatLiteral => "float-literal",
        RustTokenKind.StringLiteral => "string-literal",
        RustTokenKind.RawStringLiteral => "raw-string-literal",
        RustTokenKind.ByteStringLiteral => "byte-string-literal",
        RustTokenKind.RawByteStringLiteral => "raw-byte-string-literal",
        RustTokenKind.CStringLiteral => "c-string-literal",
        RustTokenKind.RawCStringLiteral => "raw-c-string-literal",
        RustTokenKind.CharacterLiteral => "character-literal",
        RustTokenKind.ByteCharacterLiteral => "byte-character-literal",
        RustTokenKind.OpenDelimiter => "open-delimiter",
        RustTokenKind.CloseDelimiter => "close-delimiter",
        RustTokenKind.Punctuation => "punctuation",
        RustTokenKind.Unknown => "unknown",
        _ => throw new InvalidDataException($"Unknown lexer token kind '{kind}'."),
    };

    private static string ToTriviaKind(RustTriviaKind kind) => kind switch
    {
        RustTriviaKind.Whitespace => "whitespace",
        RustTriviaKind.LineComment => "line-comment",
        RustTriviaKind.BlockComment => "block-comment",
        RustTriviaKind.Shebang => "shebang",
        _ => throw new InvalidDataException($"Unknown lexer trivia kind '{kind}'."),
    };

    private static string ToDelimiter(RustDelimiterKind delimiter) => delimiter switch
    {
        RustDelimiterKind.Parenthesis => "parenthesis",
        RustDelimiterKind.Bracket => "bracket",
        RustDelimiterKind.Brace => "brace",
        _ => throw new InvalidDataException($"Unknown lexer delimiter kind '{delimiter}'."),
    };

    private sealed class LexManifest
    {
        public string? Profile { get; init; }
        public int Version { get; init; }
        public string? Lexer { get; init; }
        public int Denominator { get; init; }
        public ManifestLimits? Limits { get; init; }
        public List<ManifestCase>? Cases { get; init; }
    }

    private sealed class ManifestLimits
    {
        public int MaximumSourceLength { get; init; }
        public int MaximumTokens { get; init; }
        public int MaximumTrivia { get; init; }
        public int MaximumDiagnostics { get; init; }
        public int MaximumDelimiterDepth { get; init; }
    }

    private sealed class ManifestCase
    {
        public string? Id { get; init; }
        public string? File { get; init; }
        public string? Expected { get; init; }
        public List<LexTokenReport>? Tokens { get; init; }
        public List<LexTriviaReport>? Trivia { get; init; }
        public List<int>? TrailingTriviaIndices { get; init; }
        public List<LexTreeReport>? Trees { get; init; }
        public List<LexDiagnosticReport>? Diagnostics { get; init; }
    }

    private sealed record ValidatedManifest(
        int Version,
        string Lexer,
        int Denominator,
        RustLexerOptions Options,
        IReadOnlyList<ValidatedCase> Cases);

    private sealed record ValidatedCase(
        string Id,
        string File,
        string FullPath,
        string Expected,
        IReadOnlyList<LexTokenReport> Tokens,
        IReadOnlyList<LexTriviaReport> Trivia,
        IReadOnlyList<LexTreeReport> Trees,
        IReadOnlyList<LexDiagnosticReport> Diagnostics,
        IReadOnlyList<int> TrailingTriviaIndices);

    private sealed record BoundedFile(byte[] Bytes, string Sha256);

    private sealed record LexTokenReport(
        int Index,
        string Kind,
        string Text,
        int Start,
        int Length,
        string? Delimiter,
        bool IsKeyword,
        IReadOnlyList<int> LeadingTriviaIndices);

    private sealed record LexTriviaReport(
        int Index,
        string Kind,
        string Text,
        int Start,
        int Length,
        bool IsDocumentation);

    private sealed record LexTreeReport(
        string Path,
        string NodeKind,
        int Start,
        int Length,
        int ChildCount,
        int? TokenIndex,
        string? Delimiter,
        int? OpenTokenIndex,
        int? CloseTokenIndex,
        bool? IsClosed);

    private sealed record LexDiagnosticReport(string Code, string Message, int Start, int Length);

    private sealed record LexReport(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string Profile,
        string EvidenceKind,
        LexScopeReport Scope,
        LexManifestReport Manifest,
        LexerReport Lexer,
        LexLimitsReport? Limits,
        LexSummaryReport Summary,
        IReadOnlyList<LexCaseReport> Cases,
        LexHostReport Host,
        LexExecutionReport Execution,
        string? HarnessError);

    private sealed record LexScopeReport(bool RustcConformance, bool RuntimeConformance, string Statement);

    private sealed record LexManifestReport(
        string Path,
        string? Sha256,
        int? Version,
        string? Lexer,
        int? Denominator,
        bool Validated,
        int? CaseCount,
        string? Error);

    private sealed record LexerReport(string Name, string Invocation, string? AssemblyVersion);

    private sealed record LexLimitsReport(
        int MaximumSourceLength,
        int MaximumTokens,
        int MaximumTrivia,
        int MaximumDiagnostics,
        int MaximumDelimiterDepth,
        int MaximumCases,
        int MaximumManifestBytes,
        int MaximumJsonTokens,
        double DeadlineSeconds)
    {
        public static LexLimitsReport From(
            RustLexerOptions options,
            TimeSpan deadline,
            int maximumCases,
            int maximumManifestBytes,
            int maximumJsonTokens) => new(
                options.MaximumSourceLength,
                options.MaximumTokens,
                options.MaximumTrivia,
                options.MaximumDiagnostics,
                options.MaximumDelimiterDepth,
                maximumCases,
                maximumManifestBytes,
                maximumJsonTokens,
                deadline.TotalSeconds);
    }

    private sealed record LexSummaryReport(
        string Status,
        int Denominator,
        int Executed,
        int Passed,
        int Failed,
        int Errors,
        int Skipped,
        int ExitCode);

    private sealed record LexCountsReport(int Tokens, int Trivia, int Trees, int Diagnostics, int TrailingTrivia)
    {
        public static LexCountsReport From<TToken, TTrivia, TTree, TDiagnostic>(
            IReadOnlyCollection<TToken> tokens,
            IReadOnlyCollection<TTrivia> trivia,
            IReadOnlyCollection<TTree> trees,
            IReadOnlyCollection<TDiagnostic> diagnostics,
            IReadOnlyCollection<int> trailingTrivia) => new(
                tokens.Count,
                trivia.Count,
                trees.Count,
                diagnostics.Count,
                trailingTrivia.Count);
    }

    private sealed record LexCaseReport(
        string Id,
        string Source,
        string? SourceSha256,
        string Expected,
        string? Actual,
        string Status,
        string? Difference,
        int? SourceLength,
        LexCountsReport ExpectedCounts,
        LexCountsReport? ActualCounts,
        bool? IsTruncated,
        bool? SourceRoundTrips,
        bool? LexicalCoverageExact,
        bool? SpansExact,
        bool ExpectationsMatched,
        bool LexerInvoked,
        double ElapsedMilliseconds,
        IReadOnlyList<LexTokenReport> Tokens,
        IReadOnlyList<LexTriviaReport> Trivia,
        IReadOnlyList<int> TrailingTriviaIndices,
        IReadOnlyList<LexTreeReport> Trees,
        IReadOnlyList<LexDiagnosticReport> Diagnostics)
    {
        public static LexCaseReport Error(
            ValidatedCase fixture,
            TimeSpan elapsed,
            string difference,
            bool lexerInvoked) => Empty(fixture, "error", difference, elapsed, lexerInvoked);

        public static LexCaseReport Skipped(
            ValidatedCase fixture,
            string difference,
            TimeSpan elapsed = default,
            bool lexerInvoked = false) => Empty(fixture, "skipped", difference, elapsed, lexerInvoked);

        private static LexCaseReport Empty(
            ValidatedCase fixture,
            string status,
            string difference,
            TimeSpan elapsed,
            bool lexerInvoked) => new(
                fixture.Id,
                fixture.File,
                null,
                fixture.Expected,
                null,
                status,
                difference,
                null,
                LexCountsReport.From(
                    fixture.Tokens,
                    fixture.Trivia,
                    fixture.Trees,
                    fixture.Diagnostics,
                    fixture.TrailingTriviaIndices),
                null,
                null,
                null,
                null,
                null,
                false,
                lexerInvoked,
                elapsed.TotalMilliseconds,
                Array.Empty<LexTokenReport>(),
                Array.Empty<LexTriviaReport>(),
                Array.Empty<int>(),
                Array.Empty<LexTreeReport>(),
                Array.Empty<LexDiagnosticReport>());
    }

    private sealed record LexHostReport(
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string Framework,
        string RuntimeIdentifier,
        string RuntimeVersion,
        bool ContinuousIntegration)
    {
        public static LexHostReport Current => new(
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription.Trim(),
            RuntimeInformation.RuntimeIdentifier,
            Environment.Version.ToString(),
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LexExecutionReport(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        double ElapsedMilliseconds,
        double DeadlineSeconds,
        bool DeadlineExpired);
}
