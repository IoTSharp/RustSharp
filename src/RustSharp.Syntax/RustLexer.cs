using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;

namespace RustSharp.Syntax;

/// <summary>
/// Performs a bounded, lossless lexical pass for the Rust 1.98 / Edition 2024
/// lexical profile and builds delimiter-grouped token trees.
/// </summary>
public static class RustLexer
{
    /// <summary>Default maximum number of UTF-16 source characters inspected.</summary>
    public const int DefaultMaximumSourceLength = 4_000_000;

    /// <summary>Default maximum number of non-trivia tokens retained.</summary>
    public const int DefaultMaximumTokens = 1_000_000;

    /// <summary>Default maximum number of trivia entries retained.</summary>
    public const int DefaultMaximumTrivia = 1_000_000;

    /// <summary>Default maximum number of diagnostics retained.</summary>
    public const int DefaultMaximumDiagnostics = 256;

    /// <summary>Default maximum delimiter nesting depth represented in a tree.</summary>
    public const int DefaultMaximumDelimiterDepth = 256;

    private const int AbsoluteMaximumSourceLength = 16_777_216;
    private const int AbsoluteMaximumTokens = 4_000_000;
    private const int AbsoluteMaximumTrivia = 4_000_000;
    private const int AbsoluteMaximumDiagnostics = 4096;
    private const int AbsoluteMaximumDelimiterDepth = 4096;
    private const int MaximumRawStringHashes = 255;

    private static readonly string[] PunctuationLexemes =
    [
        "<<=", ">>=", "..=", "...", "=>", "->", "::", "==", "!=", "<=", ">=", "&&", "||",
        "+=", "-=", "*=", "/=", "%=", "^=", "&=", "|=", "<<", ">>", "..", "+", "-", "*", "/",
        "%", "^", "!", "&", "|", "=", "<", ">", "@", "#", "$", "~", "?", ":", ";", ",", ".",
    ];

    private static readonly HashSet<string> Keywords =
    [
        "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum",
        "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod",
        "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super",
        "trait", "true", "type", "unsafe", "use", "where", "while", "abstract", "become", "box",
        "do", "final", "macro", "override", "priv", "typeof", "unsized", "virtual", "yield", "try",
        "union", "gen",
    ];

    /// <summary>Lexes source text with the default safety limits.</summary>
    public static RustLexResult Lex(string? source, string? sourcePath = null) =>
        Lex(source, sourcePath, null);

    /// <summary>Lexes source text with caller-supplied safety limits.</summary>
    public static RustLexResult Lex(
        string? source,
        string? sourcePath,
        RustLexerOptions? options) => Lex(source, sourcePath, options, CancellationToken.None);

    /// <summary>Lexes with cooperative cancellation; cancellation and deadline expiry throw before returning partial evidence.</summary>
    public static RustLexResult Lex(
        string? source,
        string? sourcePath,
        RustLexerOptions? options,
        CancellationToken cancellationToken)
    {
        source ??= string.Empty;
        sourcePath ??= string.Empty;
        var scanner = new Scanner(source, sourcePath, NormalizeOptions(options), cancellationToken);
        return scanner.Run();
    }

    /// <summary>Alias for <see cref="Lex(string?, string?)"/>.</summary>
    public static RustLexResult Tokenize(string? source, string? sourcePath = null) =>
        Lex(source, sourcePath, null);

    /// <summary>Lexes source text with explicit options.</summary>
    public static RustLexResult Tokenize(
        string? source,
        string? sourcePath,
        RustLexerOptions? options) =>
        Lex(source, sourcePath, options);

    private static RustLexerOptions NormalizeOptions(RustLexerOptions? options)
    {
        options ??= new RustLexerOptions();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Lexer timeout must be positive and at most one minute.");
        }

        return options with
        {
            MaximumSourceLength = Math.Clamp(
                options.MaximumSourceLength,
                1,
                AbsoluteMaximumSourceLength),
            MaximumTokens = Math.Clamp(options.MaximumTokens, 1, AbsoluteMaximumTokens),
            MaximumTrivia = Math.Clamp(options.MaximumTrivia, 1, AbsoluteMaximumTrivia),
            MaximumDiagnostics = Math.Clamp(options.MaximumDiagnostics, 1, AbsoluteMaximumDiagnostics),
            MaximumDelimiterDepth = Math.Clamp(
                options.MaximumDelimiterDepth,
                1,
                AbsoluteMaximumDelimiterDepth),
        };
    }

    private sealed class Scanner
    {
        private readonly string _source;
        private readonly string _sourcePath;
        private readonly RustLexerOptions _options;
        private readonly int _scanLength;
        private readonly CancellationToken _cancellationToken;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private int _workSinceBudgetCheck;
        private readonly List<RustToken> _tokens = [];
        private readonly List<RustTrivia> _trivia = [];
        private readonly List<RustTrivia> _pendingTrivia = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly List<MutableTreeNode> _roots = [];
        private readonly Stack<MutableGroup> _groups = new();
        private int _position;
        private int _reservedPoundsEnd;
        private bool _stopped;
        private bool _limitReported;
        private bool _delimiterDepthReported;
        private bool _sourceWasTruncated;

        internal Scanner(string source, string sourcePath, RustLexerOptions options, CancellationToken cancellationToken)
        {
            _source = source;
            _sourcePath = sourcePath;
            _options = options;
            _cancellationToken = cancellationToken;
            _scanLength = Math.Min(source.Length, options.MaximumSourceLength);

            if (_scanLength < source.Length)
            {
                _sourceWasTruncated = true;
                AddDiagnostic(
                    RustLexDiagnosticCodes.SourceTooLong,
                    $"Source exceeds the maximum length of {_scanLength} characters.",
                    _scanLength,
                    0);
            }
        }

        internal RustLexResult Run()
        {
            CheckBudgetNow();
            ValidateUnicodeScalars();
            while (!_stopped && _position < _scanLength)
            {
                CheckBudget();
                if (TryScanTrivia())
                {
                    continue;
                }

                if (_tokens.Count >= _options.MaximumTokens)
                {
                    AddLimitDiagnostic(_position);
                    break;
                }

                RustToken token = ScanToken();
                AddToken(token);
            }

            if (!_stopped)
            {
                ReportUnterminatedGroups();
            }

            IReadOnlyList<RustTokenTree> trees = FreezeRoots();
            CheckBudgetNow();
            IReadOnlyList<RustToken> tokens = Array.AsReadOnly(_tokens.ToArray());
            IReadOnlyList<RustTrivia> trivia = Array.AsReadOnly(_trivia.ToArray());
            IReadOnlyList<RustTrivia> trailing = Array.AsReadOnly(_pendingTrivia.ToArray());
            IReadOnlyList<Diagnostic> diagnostics = Array.AsReadOnly(_diagnostics.ToArray());
            CheckBudgetNow();

            return new RustLexResult(
                _source,
                _sourcePath,
                tokens,
                trivia,
                trailing,
                trees,
                diagnostics,
                _sourceWasTruncated || _stopped);
        }

        private bool TryScanTrivia()
        {
            if (_position >= _scanLength)
            {
                return false;
            }

            int start = _position;
            if (_position == 0 && _source[0] == '\uFEFF')
            {
                _position++;
                AddTrivia(RustTriviaKind.ByteOrderMark, start, _position, false);
                return true;
            }

            if ((_position == 0 || (_position == 1 && _source[0] == '\uFEFF')) && IsShebangStart())
            {
                _position += 2;
                while (_position < _scanLength && _source[_position] != '\n')
                {
                    CheckBudget();
                    _position++;
                }

                if (_position < _scanLength && _position > start && _source[_position - 1] == '\r')
                {
                    _position--;
                }

                AddTrivia(RustTriviaKind.Shebang, start, _position, false);
                return true;
            }

            if (IsRustWhitespace(_source[_position]))
            {
                _position++;
                while (_position < _scanLength && IsRustWhitespace(_source[_position]))
                {
                    CheckBudget();
                    _position++;
                }

                AddTrivia(RustTriviaKind.Whitespace, start, _position, false);
                return true;
            }

            if (_source[_position] != '/' || _position + 1 >= _scanLength)
            {
                return false;
            }

            char next = _source[_position + 1];
            if (next == '/')
            {
                _position += 2;
                while (_position < _scanLength && _source[_position] != '\n')
                {
                    CheckBudget();
                    _position++;
                }

                bool documentation = _position - start >= 3 &&
                    (_source[start + 2] == '!' ||
                        (_source[start + 2] == '/' &&
                            (_position - start == 3 || _source[start + 3] != '/')));
                // Keep the original CRLF in whitespace, while a lone CR remains comment content.
                if (_position < _scanLength && _position > start && _source[_position - 1] == '\r')
                {
                    _position--;
                }

                if (documentation)
                {
                    ReportBareCarriageReturns(start, _position, RustLexDiagnosticCodes.InvalidDocumentationComment);
                }

                AddTrivia(RustTriviaKind.LineComment, start, _position, documentation);
                return true;
            }

            if (next != '*')
            {
                return false;
            }

            _position += 2;
            int depth = 1;
            bool depthReported = false;
            while (_position < _scanLength && depth > 0)
            {
                CheckBudget();
                if (_source[_position] == '/' &&
                    _position + 1 < _scanLength &&
                    _source[_position + 1] == '*')
                {
                    depth++;
                    _position += 2;
                    if (depth > _options.MaximumDelimiterDepth && !depthReported)
                    {
                        depthReported = true;
                        AddDiagnostic(
                            RustLexDiagnosticCodes.DelimiterDepthLimit,
                            "Nested block comment depth exceeds the configured limit.",
                            start,
                            _position - start);
                    }
                }
                else if (_source[_position] == '*' &&
                    _position + 1 < _scanLength &&
                    _source[_position + 1] == '/')
                {
                    depth--;
                    _position += 2;
                }
                else
                {
                    _position++;
                }
            }

            if (depth != 0)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.UnterminatedComment,
                    "Block comment is not terminated.",
                    start,
                    _position - start);
            }

            bool documentationBlock = _position - start >= 3 &&
                (_source[start + 2] == '!' ||
                    (_source[start + 2] == '*' &&
                        (_position - start == 3 || _source[start + 3] != '*')));
            documentationBlock &= !MatchesAt(start, "/**/");
            if (documentationBlock)
            {
                ReportBareCarriageReturns(start, _position, RustLexDiagnosticCodes.InvalidDocumentationComment);
            }

            AddTrivia(RustTriviaKind.BlockComment, start, _position, documentationBlock);
            return true;
        }

        private RustToken ScanToken()
        {
            int start = _position;

            if (TryScanReservedGuardedSyntax(out RustToken? reservedGuardedSyntax))
            {
                return reservedGuardedSyntax!;
            }

            if (TryScanRawString(out RustToken? rawString))
            {
                return rawString!;
            }

            if (TryScanRawIdentifier(out RustToken? rawIdentifier))
            {
                return rawIdentifier!;
            }

            if (TryScanPrefixedLiteral(out RustToken? prefixedLiteral))
            {
                return prefixedLiteral!;
            }

            if (_source[_position] == '"')
            {
                return ScanQuotedLiteral(start, 0, '"', RustTokenKind.StringLiteral);
            }

            if (_source[_position] == '\'')
            {
                return ScanApostropheToken(start);
            }

            if (IsAsciiDigit(_source[_position]))
            {
                return ScanNumber(start);
            }

            if (IsIdentifierStartAt(_position, out _))
            {
                return ScanIdentifier(start);
            }

            if (TryGetDelimiter(_source[_position], out RustDelimiterKind delimiter))
            {
                _position++;
                RustTokenKind kind = IsOpeningDelimiter(_source[start])
                    ? RustTokenKind.OpenDelimiter
                    : RustTokenKind.CloseDelimiter;
                return CreateToken(kind, start, _position, false, delimiter);
            }

            foreach (string punctuation in PunctuationLexemes)
            {
                if (!MatchesAt(_position, punctuation))
                {
                    continue;
                }

                _position += punctuation.Length;
                return CreateToken(RustTokenKind.Punctuation, start, _position, false, null);
            }

            int scalar = _source[start];
            int width = 1;
            if (TryReadRune(start, out Rune unknownRune, out int runeWidth))
            {
                scalar = unknownRune.Value;
                width = runeWidth;
            }

            _position += width;
            if (!char.IsSurrogate(_source[start]) || width == 2)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.UnknownCharacter,
                    $"Unknown character U+{scalar:X4}.",
                    start,
                    width);
            }

            return CreateToken(RustTokenKind.Unknown, start, _position, false, null);
        }

        private bool TryScanReservedGuardedSyntax(out RustToken? token)
        {
            token = null;
            if (!MatchesAt(_position, "#\"") && !MatchesAt(_position, "##"))
            {
                return false;
            }

            int start = _position;
            // Reuse a known non-string pound run instead of rescanning its tail for each ## token.
            int quote = Math.Max(start, _reservedPoundsEnd);
            while (quote < _scanLength && _source[quote] == '#')
            {
                CheckBudget();
                quote++;
            }

            if (quote >= _scanLength || _source[quote] != '"')
            {
                _reservedPoundsEnd = quote;
                _position = start + 2;
                AddDiagnostic(
                    RustLexDiagnosticCodes.ReservedPounds,
                    "Two adjacent pound characters are reserved in Edition 2024.",
                    start,
                    2);
                token = CreateToken(RustTokenKind.ReservedPounds, start, _position, false, null);
                return true;
            }

            int hashCount = quote - start;
            _position = quote + 1;
            bool terminated = false;
            while (_position < _scanLength)
            {
                CheckBudget();
                char current = _source[_position];
                if (current == '"')
                {
                    _position++;
                    terminated = true;
                    break;
                }

                if (current == '\\' && _position + 1 < _scanLength &&
                    _source[_position + 1] is '\\' or '"')
                {
                    _position += 2;
                    continue;
                }

                _position += TryReadRune(_position, out _, out int width) ? width : 1;
            }

            if (!terminated)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.UnterminatedLiteral,
                    "Guarded string literal is not terminated.",
                    start,
                    _position - start);
                token = CreateToken(
                    RustTokenKind.ReservedGuardedStringLiteral,
                    start,
                    _position,
                    false,
                    null);
                return true;
            }

            int closingHashes = 0;
            while (_position < _scanLength && _source[_position] == '#' && closingHashes < hashCount)
            {
                CheckBudget();
                closingHashes++;
                _position++;
            }

            int? suffixStart = ScanLiteralSuffix(reportBareUnderscore: false);
            AddDiagnostic(
                RustLexDiagnosticCodes.ReservedGuardedString,
                "Guarded string literals are reserved in Edition 2024.",
                start,
                _position - start);
            token = CreateToken(
                RustTokenKind.ReservedGuardedStringLiteral,
                start,
                _position,
                false,
                null,
                suffixStart);
            return true;
        }

        private bool TryScanRawString(out RustToken? token)
        {
            token = null;
            int prefixLength;
            RustTokenKind kind;

            if (MatchesAt(_position, "br"))
            {
                prefixLength = 2;
                kind = RustTokenKind.RawByteStringLiteral;
            }
            else if (MatchesAt(_position, "cr"))
            {
                prefixLength = 2;
                kind = RustTokenKind.RawCStringLiteral;
            }
            else if (_source[_position] == 'r')
            {
                prefixLength = 1;
                kind = RustTokenKind.RawStringLiteral;
            }
            else
            {
                return false;
            }

            int hashStart = _position + prefixLength;
            int quote = hashStart;
            while (quote < _scanLength && _source[quote] == '#')
            {
                CheckBudget();
                quote++;
            }

            if (quote >= _scanLength || _source[quote] != '"')
            {
                return false;
            }

            int hashCount = quote - hashStart;
            int start = _position;
            if (hashCount > MaximumRawStringHashes)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.InvalidLiteral,
                    $"Raw string literals may use at most {MaximumRawStringHashes} delimiter hashes.",
                    hashStart,
                    hashCount);
            }

            int cursor = quote + 1;
            while (cursor < _scanLength)
            {
                CheckBudget();
                if (_source[cursor] != '"')
                {
                    cursor++;
                    continue;
                }

                int closingHashStart = cursor + 1;
                int closingHashEnd = closingHashStart;
                while (closingHashEnd < _scanLength &&
                    _source[closingHashEnd] == '#' &&
                    closingHashEnd - closingHashStart < hashCount)
                {
                    closingHashEnd++;
                    CheckBudget();
                }

                if (closingHashEnd - closingHashStart == hashCount)
                {
                    _position = closingHashEnd;
                    ReportRawLiteralRestrictions(kind, quote + 1, cursor);

                    int? suffixStart = ScanLiteralSuffix(reportBareUnderscore: true);

                    token = CreateToken(kind, start, _position, false, null, suffixStart);
                    return true;
                }

                cursor = closingHashEnd;
            }

            _position = _scanLength;
            ReportRawLiteralRestrictions(kind, quote + 1, _scanLength);

            AddDiagnostic(
                RustLexDiagnosticCodes.UnterminatedLiteral,
                "Raw string literal is not terminated.",
                start,
                _position - start);
            token = CreateToken(kind, start, _position, false, null);
            return true;
        }

        private void ReportRawLiteralRestrictions(
            RustTokenKind kind,
            int contentStart,
            int contentEnd)
        {
            ReportBareCarriageReturns(contentStart, contentEnd);

            if (kind == RustTokenKind.RawCStringLiteral)
            {
                int nul = _source.IndexOf('\0', contentStart, Math.Max(0, contentEnd - contentStart));
                if (nul >= 0)
                {
                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLiteral,
                        "C string literals must not contain NUL characters.",
                        nul,
                        1);
                }
            }

            if (kind == RustTokenKind.RawByteStringLiteral)
            {
                for (int position = contentStart; position < contentEnd; position++)
                {
                    CheckBudget();
                    if (_source[position] <= 0x7f)
                    {
                        continue;
                    }

                    int scalarWidth = TryReadRune(position, out _, out int width) ? width : 1;

                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLiteral,
                        "Raw byte string literals must contain ASCII text.",
                        position,
                        scalarWidth);
                    break;
                }
            }
        }

        private void ReportBareCarriageReturns(
            int contentStart,
            int contentEnd,
            string code = RustLexDiagnosticCodes.InvalidLiteral)
        {
            for (int position = contentStart; position < contentEnd; position++)
            {
                CheckBudget();
                if (_source[position] != '\r' ||
                    (position + 1 < contentEnd && _source[position + 1] == '\n'))
                {
                    continue;
                }

                AddDiagnostic(
                    code,
                    code == RustLexDiagnosticCodes.InvalidDocumentationComment
                        ? "Bare carriage returns are not allowed in documentation comments."
                        : "Bare carriage returns are not allowed in literal content; use \\r or CRLF.",
                    position,
                    1);
            }
        }

        private bool TryScanPrefixedLiteral(out RustToken? token)
        {
            token = null;
            int start = _position;

            if (MatchesAt(_position, "b\""))
            {
                token = ScanQuotedLiteral(start, 1, '"', RustTokenKind.ByteStringLiteral);
                return true;
            }

            if (MatchesAt(_position, "c\""))
            {
                token = ScanQuotedLiteral(start, 1, '"', RustTokenKind.CStringLiteral);
                return true;
            }

            if (MatchesAt(_position, "b'"))
            {
                token = ScanQuotedLiteral(start, 1, '\'', RustTokenKind.ByteCharacterLiteral);
                return true;
            }

            return false;
        }

        private RustToken ScanApostropheToken(int start)
        {
            int contentStart = _position + 1;
            if (MatchesAt(contentStart, "r#") &&
                IsIdentifierStartAt(contentStart + 2, out int rawFirstWidth))
            {
                _position = contentStart + 2 + rawFirstWidth;
                while (IsIdentifierContinueAt(_position, out int width))
                {
                    CheckBudget();
                    _position += width;
                }

                ReadOnlySpan<char> rawName = _source.AsSpan(
                    contentStart + 2,
                    _position - contentStart - 2);
                if (IsReservedRawLifetime(rawName))
                {
                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLifetime,
                        "This name cannot be used as a raw lifetime.",
                        start,
                        _position - start);
                }

                return CreateToken(RustTokenKind.RawLifetime, start, _position, false, null);
            }

            bool startsWithNumber = contentStart < _scanLength && IsAsciiDigit(_source[contentStart]);
            bool startsWithIdentifier = IsIdentifierStartAt(contentStart, out int identifierFirstWidth);
            if (contentStart < _scanLength && (startsWithNumber || startsWithIdentifier))
            {
                int firstWidth = startsWithNumber
                    ? 1
                    : identifierFirstWidth;
                int identifierEnd = contentStart + firstWidth;
                while (IsIdentifierContinueAt(identifierEnd, out int width))
                {
                    CheckBudget();
                    identifierEnd += width;
                }

                if (identifierEnd >= _scanLength || _source[identifierEnd] != '\'')
                {
                    _position = identifierEnd;
                    if (startsWithNumber)
                    {
                        AddDiagnostic(
                            RustLexDiagnosticCodes.InvalidLifetime,
                            "Lifetimes cannot start with a number.",
                            start,
                            _position - start);
                    }
                    else if (_position < _scanLength && _source[_position] == '#')
                    {
                        AddDiagnostic(
                            RustLexDiagnosticCodes.ReservedPrefix,
                            "Lifetime prefixes followed by '#' are reserved in Edition 2021 and later.",
                            start,
                            _position - start);
                    }

                    return CreateToken(RustTokenKind.Lifetime, start, _position, false, null);
                }
            }

            return ScanQuotedLiteral(start, 0, '\'', RustTokenKind.CharacterLiteral);
        }

        private RustToken ScanQuotedLiteral(
            int start,
            int prefixLength,
            char quote,
            RustTokenKind kind)
        {
            _position = start + prefixLength + 1;
            int scalarCount = 0;
            bool escapePending = false;
            bool invalid = false;
            bool closed = false;
            bool byteLiteral = kind is RustTokenKind.ByteStringLiteral or RustTokenKind.ByteCharacterLiteral;
            bool cStringLiteral = kind == RustTokenKind.CStringLiteral;

            while (_position < _scanLength)
            {
                CheckBudget();
                char current = _source[_position];
                if (current == quote && !escapePending)
                {
                    _position++;
                    closed = true;
                    break;
                }

                if (current is '\r' or '\n')
                {
                    bool carriageReturnLineFeed = current == '\r' &&
                        _position + 1 < _scanLength && _source[_position + 1] == '\n';
                    if (current == '\r' && !carriageReturnLineFeed)
                    {
                        invalid = true;
                        AddDiagnostic(
                            RustLexDiagnosticCodes.InvalidLiteral,
                            "Bare carriage returns are not allowed in literal content; use \\r or CRLF.",
                            _position,
                            1);
                    }

                    if (quote == '\'')
                    {
                        invalid = true;
                    }

                    if (carriageReturnLineFeed)
                    {
                        _position += 2;
                    }
                    else
                    {
                        _position++;
                    }

                    scalarCount++;
                    escapePending = false;
                    continue;
                }

                if (escapePending)
                {
                    escapePending = false;
                    if (current == 'u' && _position + 1 < _scanLength && _source[_position + 1] == '{')
                    {
                        int escapeStart = _position - 1;
                        int scalar;
                        bool validUnicodeEscape = ConsumeUnicodeEscape(quote, ref invalid, out scalar);
                        if (byteLiteral)
                        {
                            invalid = true;
                            AddDiagnostic(
                                RustLexDiagnosticCodes.InvalidLiteral,
                                "Unicode escapes are not allowed in byte literals.",
                                escapeStart,
                                Math.Max(2, _position - escapeStart));
                        }
                        else if (cStringLiteral && validUnicodeEscape && scalar == 0)
                        {
                            invalid = true;
                            AddDiagnostic(
                                RustLexDiagnosticCodes.InvalidLiteral,
                                "C string literals must not contain NUL characters.",
                                escapeStart,
                                Math.Max(2, _position - escapeStart));
                        }

                        if (!validUnicodeEscape)
                        {
                            continue;
                        }

                        scalarCount++;
                        continue;
                    }

                    if (current == 'x')
                    {
                        int escapeStart = _position - 1;
                        _position++;
                        bool high = _position < _scanLength && IsHex(_source[_position]);
                        int hexValue = 0;
                        if (high)
                        {
                            hexValue = HexValue(_source[_position]);
                            _position++;
                        }

                        bool low = _position < _scanLength && IsHex(_source[_position]);
                        if (low)
                        {
                            hexValue = (hexValue << 4) | HexValue(_source[_position]);
                            _position++;
                        }

                        if (!high || !low)
                        {
                            invalid = true;
                            AddDiagnostic(
                                RustLexDiagnosticCodes.InvalidLiteral,
                                "Hex escape must contain exactly two hexadecimal digits.",
                                escapeStart,
                                Math.Max(1, _position - escapeStart));
                        }
                        else if (hexValue > 0x7f &&
                            kind is RustTokenKind.StringLiteral or RustTokenKind.CharacterLiteral)
                        {
                            invalid = true;
                            AddDiagnostic(
                                RustLexDiagnosticCodes.InvalidLiteral,
                                "Hex escapes in string and character literals must be in the ASCII range \\x00..\\x7F.",
                                escapeStart,
                                _position - escapeStart);
                        }
                        else if (cStringLiteral && hexValue == 0)
                        {
                            invalid = true;
                            AddDiagnostic(
                                RustLexDiagnosticCodes.InvalidLiteral,
                                "C string literals must not contain NUL characters.",
                                escapeStart,
                                _position - escapeStart);
                        }

                        scalarCount++;
                        continue;
                    }

                    if (cStringLiteral && current == '0')
                    {
                        invalid = true;
                        AddDiagnostic(
                            RustLexDiagnosticCodes.InvalidLiteral,
                            "C string literals must not contain NUL characters.",
                            Math.Max(start, _position - 1),
                            2);
                    }
                    else if (byteLiteral && current == 'u')
                    {
                        invalid = true;
                        AddDiagnostic(
                            RustLexDiagnosticCodes.InvalidLiteral,
                            "Unicode escapes are not allowed in byte literals.",
                            Math.Max(start, _position - 1),
                            2);
                    }
                    else if (!IsSimpleEscape(current) && current is not ('\r' or '\n'))
                    {
                        invalid = true;
                        AddDiagnostic(
                            RustLexDiagnosticCodes.InvalidLiteral,
                            $"Unknown escape '\\{current}'.",
                            Math.Max(start, _position - 1),
                            2);
                    }

                    scalarCount++;
                    _position++;
                    continue;
                }

                if (current == '\\')
                {
                    escapePending = true;
                    _position++;
                    continue;
                }

                if (quote == '\'' && current == '\t')
                {
                    invalid = true;
                }

                int scalarWidth = TryReadRune(_position, out _, out int width) ? width : 1;
                if (byteLiteral && current > 0x7f)
                {
                    invalid = true;
                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLiteral,
                        "Byte literals must contain ASCII text.",
                        _position,
                        scalarWidth);
                }

                if (cStringLiteral && current == '\0')
                {
                    invalid = true;
                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLiteral,
                        "C string literals must not contain NUL characters.",
                        _position,
                        1);
                }

                scalarCount++;
                _position += scalarWidth;
            }

            if (!closed)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.UnterminatedLiteral,
                    "Quoted literal is not terminated.",
                    start,
                    _position - start);
            }
            else if (quote == '\'' && (scalarCount != 1 || invalid))
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.InvalidLiteral,
                    "Character literal must contain exactly one character or escape.",
                    start,
                    _position - start);
            }

            int? suffixStart = closed ? ScanLiteralSuffix(reportBareUnderscore: true) : null;
            return CreateToken(kind, start, _position, false, null, suffixStart);
        }

        private bool ConsumeUnicodeEscape(char literalQuote, ref bool invalid, out int scalar)
        {
            int escapeStart = _position - 1;
            _position += 2;
            int digits = 0;
            scalar = 0;
            while (_position < _scanLength && _source[_position] != '}')
            {
                CheckBudget();
                char current = _source[_position];
                if (current == '_' && digits > 0)
                {
                    _position++;
                    continue;
                }

                if (!IsHex(current) || digits == 6)
                {
                    invalid = true;
                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLiteral,
                        "Unicode escape must contain one to six hexadecimal digits.",
                        escapeStart,
                        Math.Max(1, _position - escapeStart));
                    while (_position < _scanLength &&
                        _source[_position] != '}' &&
                        _source[_position] != literalQuote)
                    {
                        _position++;
                        CheckBudget();
                    }

                    if (_position < _scanLength && _source[_position] == '}')
                    {
                        _position++;
                    }

                    return false;
                }

                scalar = (scalar << 4) | HexValue(current);
                digits++;
                _position++;
            }

            if (_position >= _scanLength || _source[_position] != '}' || digits == 0 ||
                scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff)
            {
                invalid = true;
                AddDiagnostic(
                    RustLexDiagnosticCodes.InvalidLiteral,
                    "Unicode escape must contain one to six hexadecimal digits and a valid scalar.",
                    escapeStart,
                    Math.Max(1, _position - escapeStart));
                if (_position < _scanLength)
                {
                    _position++;
                }

                return false;
            }

            _position++;
            return true;
        }

        private RustToken ScanNumber(int start)
        {
            bool floating = false;
            bool invalid = false;
            int? suffixStart = null;
            int radix = 10;
            bool emptyRadixBody = false;

            if (_source[_position] == '0' && _position + 1 < _scanLength)
            {
                char prefix = _source[_position + 1];
                int prefixRadix = prefix switch
                {
                    'b' => 2,
                    'o' => 8,
                    'x' => 16,
                    _ => 0,
                };

                if (prefixRadix != 0)
                {
                    radix = prefixRadix;
                    _position += 2;
                    int digitCount = 0;
                    while (_position < _scanLength)
                    {
                        CheckBudget();
                        char current = _source[_position];
                        if (current == '_')
                        {
                            _position++;
                            continue;
                        }

                        bool numericBodyCharacter = radix == 16 ? IsHex(current) : IsAsciiDigit(current);
                        if (numericBodyCharacter)
                        {
                            if (!IsDigitForRadix(current, radix))
                            {
                                invalid = true;
                            }

                            digitCount++;
                            _position++;
                            continue;
                        }

                        break;
                    }

                    if (digitCount == 0)
                    {
                        invalid = true;
                        emptyRadixBody = true;
                    }
                }
            }

            while (radix == 10 && _position < _scanLength)
            {
                CheckBudget();
                char current = _source[_position];
                if (current == '_')
                {
                    _position++;
                    continue;
                }

                if (!IsAsciiDigit(current))
                {
                    break;
                }

                _position++;
            }

            if (!emptyRadixBody && _position < _scanLength && _source[_position] == '.' &&
                !(_position + 1 < _scanLength && _source[_position + 1] == '.') &&
                (_position + 1 >= _scanLength ||
                    !IsIdentifierStartAt(_position + 1, out _) ||
                    IsAsciiDigit(_source[_position + 1])))
            {
                floating = true;
                _position++;
                while (_position < _scanLength && (IsAsciiDigit(_source[_position]) || _source[_position] == '_'))
                {
                    CheckBudget();
                    _position++;
                }
            }

            if (!emptyRadixBody && _position < _scanLength && _source[_position] is 'e' or 'E')
            {
                floating = true;
                _position++;
                if (_position < _scanLength && _source[_position] is '+' or '-')
                {
                    _position++;
                }

                int exponentDigits = 0;
                while (_position < _scanLength && (IsAsciiDigit(_source[_position]) || _source[_position] == '_'))
                {
                    CheckBudget();
                    if (IsAsciiDigit(_source[_position]))
                    {
                        exponentDigits++;
                    }

                    _position++;
                }

                if (exponentDigits == 0)
                {
                    invalid = true;
                }
            }

            suffixStart = ScanLiteralSuffix(reportBareUnderscore: true);

            invalid |= floating && radix != 10;
            if (invalid)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.InvalidNumber,
                    floating && radix != 10
                        ? "Floating-point literals must use decimal notation."
                        : "Numeric literal contains an invalid digit or incomplete numeric body.",
                    start,
                    _position - start);
            }

            RustTokenKind kind = floating ? RustTokenKind.FloatLiteral : RustTokenKind.IntegerLiteral;
            return CreateToken(kind, start, _position, false, null, suffixStart);
        }

        private RustToken ScanIdentifier(int start)
        {
            _ = IsIdentifierStartAt(_position, out int firstWidth);
            _position += firstWidth;
            while (IsIdentifierContinueAt(_position, out int width))
            {
                CheckBudget();
                _position += width;
            }

            string text = _source.Substring(start, _position - start);
            bool keyword = Keywords.Contains(text);
            if (_position < _scanLength && _source[_position] is '#' or '"' or '\'')
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.ReservedPrefix,
                    $"Prefix '{text}' is reserved in Edition 2021 and later.",
                    start,
                    _position - start);
            }

            return CreateToken(
                keyword ? RustTokenKind.Keyword : RustTokenKind.Identifier,
                start,
                _position,
                keyword,
                null);
        }

        private bool TryScanRawIdentifier(out RustToken? token)
        {
            token = null;
            if (!MatchesAt(_position, "r#") ||
                _position + 2 >= _scanLength ||
                !IsIdentifierStartAt(_position + 2, out int firstWidth))
            {
                return false;
            }

            int start = _position;
            _position += 2 + firstWidth;
            while (IsIdentifierContinueAt(_position, out int width))
            {
                CheckBudget();
                _position += width;
            }

            token = CreateToken(RustTokenKind.RawIdentifier, start, _position, false, null);
            return true;
        }

        private int? ScanLiteralSuffix(bool reportBareUnderscore)
        {
            if (!IsIdentifierStartAt(_position, out int firstWidth))
            {
                return null;
            }

            int start = _position;
            _position += firstWidth;
            while (IsIdentifierContinueAt(_position, out int width))
            {
                CheckBudget();
                _position += width;
            }

            if (reportBareUnderscore && _position - start == 1 && _source[start] == '_')
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.InvalidLiteral,
                    "Underscore literal suffix is not allowed.",
                    start,
                    1);
            }

            return start;
        }

        private static bool IsReservedRawLifetime(ReadOnlySpan<char> name) =>
            name.SequenceEqual("_") ||
            name.SequenceEqual("crate") ||
            name.SequenceEqual("self") ||
            name.SequenceEqual("Self") ||
            name.SequenceEqual("super");

        private RustToken CreateToken(
            RustTokenKind kind,
            int start,
            int end,
            bool isKeyword,
            RustDelimiterKind? delimiter,
            int? literalSuffixStart = null)
        {
            IReadOnlyList<RustTrivia> leadingTrivia = _pendingTrivia.Count == 0
                ? Array.Empty<RustTrivia>()
                : Array.AsReadOnly(_pendingTrivia.ToArray());
            _pendingTrivia.Clear();
            string text = _source.Substring(start, end - start);
            string? literalSuffix = literalSuffixStart is int suffixStart
                ? _source.Substring(suffixStart, end - suffixStart)
                : null;
            return new RustToken(
                kind,
                new TextSpan(start, end - start),
                text,
                isKeyword,
                delimiter,
                leadingTrivia,
                literalSuffix);
        }

        private void AddToken(RustToken token)
        {
            _tokens.Add(token);
            var leaf = new MutableLeaf(token);

            if (_groups.Count == 0)
            {
                _roots.Add(leaf);
            }
            else
            {
                _groups.Peek().Children.Add(leaf);
            }

            if (token.Kind == RustTokenKind.OpenDelimiter)
            {
                if (_groups.Count >= _options.MaximumDelimiterDepth)
                {
                    if (!_delimiterDepthReported)
                    {
                        _delimiterDepthReported = true;
                        AddDiagnostic(
                            RustLexDiagnosticCodes.DelimiterDepthLimit,
                            "Delimiter nesting exceeds the configured limit.",
                            token.Span.Start,
                            token.Span.Length);
                    }

                    _stopped = true;
                    return;
                }

                var group = new MutableGroup(token.Delimiter!.Value, token);
                if (_groups.Count == 0)
                {
                    _roots[^1] = group;
                }
                else
                {
                    _groups.Peek().Children[^1] = group;
                }

                _groups.Push(group);
            }
            else if (token.Kind == RustTokenKind.CloseDelimiter)
            {
                if (_groups.Count == 0)
                {
                    AddDiagnostic(
                        RustLexDiagnosticCodes.UnmatchedClosingDelimiter,
                        "Closing delimiter has no matching opening delimiter.",
                        token.Span.Start,
                        token.Span.Length);
                }
                else
                {
                    MutableGroup group = _groups.Peek();
                    if (group.Delimiter != token.Delimiter)
                    {
                        AddDiagnostic(
                            RustLexDiagnosticCodes.MismatchedDelimiter,
                            "Closing delimiter does not match the innermost opening delimiter.",
                            token.Span.Start,
                            token.Span.Length);
                    }
                    else
                    {
                        // Delimiters are represented by OpenToken/CloseToken on
                        // a group; keep only interior nodes in Children.
                        if (group.Children.Count > 0 &&
                            group.Children[^1] is MutableLeaf closingLeaf &&
                            ReferenceEquals(closingLeaf.Token, token))
                        {
                            group.Children.RemoveAt(group.Children.Count - 1);
                        }

                        group.CloseToken = token;
                        _groups.Pop();
                    }
                }
            }
        }

        private void AddTrivia(RustTriviaKind kind, int start, int end, bool documentation)
        {
            if (_trivia.Count >= _options.MaximumTrivia)
            {
                AddLimitDiagnostic(start);
                return;
            }

            var trivia = new RustTrivia(
                kind,
                new TextSpan(start, end - start),
                _source.Substring(start, end - start),
                documentation);
            _trivia.Add(trivia);
            _pendingTrivia.Add(trivia);
        }

        private void ReportUnterminatedGroups()
        {
            if (_groups.Count == 0)
            {
                return;
            }

            foreach (MutableGroup group in _groups)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.UnterminatedDelimiter,
                    "Opening delimiter is not terminated.",
                    group.OpenToken.Span.Start,
                    _scanLength - group.OpenToken.Span.Start);
                if (_stopped)
                {
                    break;
                }
            }
        }

        private ReadOnlyCollection<RustTokenTree> FreezeRoots()
        {
            var trees = new RustTokenTree[_roots.Count];
            var pending = new Stack<(MutableTreeNode Node, RustTokenTree[] Target, int Index, RustTokenTree[]? Children)>();
            for (int index = 0; index < _roots.Count; index++)
            {
                CheckBudget();
                pending.Push((_roots[index], trees, index, null));
                // Each node is visited at most twice; CLR stack depth is independent of source nesting.
                while (pending.TryPop(out var work))
                {
                    CheckBudget();
                    if (work.Node is MutableLeaf leaf)
                    {
                        work.Target[work.Index] = new RustLeafTokenTree(leaf.Token);
                    }
                    else if (work.Node is MutableGroup group)
                    {
                        if (work.Children is not null)
                        {
                            int end = group.CloseToken?.Span.End ?? _scanLength;
                            work.Target[work.Index] = new RustDelimitedTokenTree(
                                group.Delimiter, group.OpenToken, Array.AsReadOnly(work.Children),
                                group.CloseToken, new TextSpan(group.OpenToken.Span.Start, end - group.OpenToken.Span.Start));
                            continue;
                        }

                        var children = new RustTokenTree[group.Children.Count];
                        pending.Push((group, work.Target, work.Index, children));
                        for (int child = group.Children.Count - 1; child >= 0; child--)
                        {
                            CheckBudget();
                            pending.Push((group.Children[child], children, child, null));
                        }
                    }
                }
            }

            return Array.AsReadOnly(trees);
        }

        private void AddDiagnostic(string code, string message, int start, int length)
        {
            if (_stopped)
            {
                return;
            }

            if (_diagnostics.Count >= _options.MaximumDiagnostics - 1)
            {
                AddLimitDiagnostic(start);
                return;
            }

            int safeStart = Math.Clamp(start, 0, _source.Length);
            int safeLength = Math.Clamp(length, 0, _source.Length - safeStart);
            _diagnostics.Add(new Diagnostic(code, message, new TextSpan(safeStart, safeLength)));
        }

        private void AddLimitDiagnostic(int start)
        {
            if (_limitReported)
            {
                _stopped = true;
                return;
            }

            _limitReported = true;
            _stopped = true;
            int safeStart = Math.Clamp(start, 0, _source.Length);
            _diagnostics.Add(new Diagnostic(
                RustLexDiagnosticCodes.LimitReached,
                "Lexing stopped after reaching a configured safety limit.",
                new TextSpan(safeStart, 0)));
        }

        private bool IsShebangStart()
        {
            if (!MatchesAt(_position, "#!"))
            {
                return false;
            }

            int cursor = _position + 2;
            // Rust skips whitespace and non-doc comments before deciding whether this is an inner attribute.
            while (cursor < _scanLength)
            {
                CheckBudget();
                if (IsRustWhitespace(_source[cursor]))
                {
                    cursor++;
                }
                else if (MatchesAt(cursor, "//") && !MatchesAt(cursor, "//!") &&
                    (!MatchesAt(cursor, "///") || MatchesAt(cursor, "////")))
                {
                    cursor += 2;
                    while (cursor < _scanLength && _source[cursor] != '\n')
                    {
                        CheckBudget();
                        cursor++;
                    }
                }
                else if (MatchesAt(cursor, "/*") && !MatchesAt(cursor, "/*!") &&
                    (!MatchesAt(cursor, "/**") || MatchesAt(cursor, "/***") || MatchesAt(cursor, "/**/")))
                {
                    cursor += 2;
                    int depth = 1;
                    while (cursor < _scanLength && depth > 0)
                    {
                        CheckBudget();
                        if (MatchesAt(cursor, "/*"))
                        {
                            depth++;
                            cursor += 2;
                        }
                        else if (MatchesAt(cursor, "*/"))
                        {
                            depth--;
                            cursor += 2;
                        }
                        else
                        {
                            cursor++;
                        }
                    }
                }
                else
                {
                    return _source[cursor] != '[';
                }
            }

            return true;
        }

        private void ValidateUnicodeScalars()
        {
            for (int position = 0; position < _scanLength && !_stopped; position++)
            {
                CheckBudget();
                if (!char.IsSurrogate(_source[position]))
                {
                    continue;
                }

                if (TryReadRune(position, out _, out int width))
                {
                    position += width - 1;
                }
                else
                {
                    AddDiagnostic(RustLexDiagnosticCodes.UnknownCharacter,
                        $"Unknown character U+{(int)_source[position]:X4}.", position, 1);
                }
            }
        }

        private void CheckBudget()
        {
            if (++_workSinceBudgetCheck >= 1024)
            {
                _workSinceBudgetCheck = 0;
                CheckBudgetNow();
            }
        }

        private void CheckBudgetNow()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(_startedAt) >= _options.Timeout)
            {
                throw new TimeoutException("Lexer wall-clock budget expired.");
            }
        }

        private bool MatchesAt(int position, string text)
        {
            return position >= 0 && position <= _scanLength - text.Length &&
                _source.AsSpan(position, text.Length).SequenceEqual(text.AsSpan());
        }

        private static bool IsOpeningDelimiter(char value) => value is '(' or '[' or '{';

        private static bool TryGetDelimiter(char value, out RustDelimiterKind delimiter)
        {
            delimiter = value switch
            {
                '(' or ')' => RustDelimiterKind.Parenthesis,
                '[' or ']' => RustDelimiterKind.Bracket,
                '{' or '}' => RustDelimiterKind.Brace,
                _ => default,
            };
            return value is '(' or ')' or '[' or ']' or '{' or '}';
        }

        private bool IsIdentifierStartAt(int position, out int width) =>
            TryReadRune(position, out Rune value, out width) && RustIdentifierFacts.IsIdentifierStart(value);

        private bool IsIdentifierContinueAt(int position, out int width) =>
            TryReadRune(position, out Rune value, out width) && RustIdentifierFacts.IsIdentifierContinue(value);

        private bool TryReadRune(int position, out Rune value, out int width)
        {
            value = default;
            width = 0;
            if (position < 0 || position >= _scanLength)
            {
                return false;
            }

            char first = _source[position];
            if (!char.IsSurrogate(first))
            {
                value = new Rune(first);
                width = 1;
                return true;
            }

            if (char.IsHighSurrogate(first) && position + 1 < _scanLength &&
                Rune.TryCreate(first, _source[position + 1], out value))
            {
                width = 2;
                return true;
            }

            return false;
        }

        private static bool IsRustWhitespace(char value) => value is
            '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or
            '\u0085' or '\u200E' or '\u200F' or '\u2028' or '\u2029';

        private static bool IsSimpleEscape(char value) => value is '\\' or '\'' or '"' or 'n' or 'r' or 't' or '0';

        private static bool IsHex(char value) =>
            value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        private static int HexValue(char value) => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => value - 'A' + 10,
        };

        private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

        private static bool IsDigitForRadix(char value, int radix) => radix switch
        {
            2 => value is '0' or '1',
            8 => value is >= '0' and <= '7',
            10 => IsAsciiDigit(value),
            16 => IsHex(value),
            _ => false,
        };

        private abstract class MutableTreeNode
        {
        }

        private sealed class MutableLeaf : MutableTreeNode
        {
            internal MutableLeaf(RustToken token)
            {
                Token = token;
            }

            internal RustToken Token { get; }
        }

        private sealed class MutableGroup(RustDelimiterKind delimiter, RustToken openToken) : MutableTreeNode
        {
            internal RustDelimiterKind Delimiter { get; } = delimiter;
            internal RustToken OpenToken { get; } = openToken;
            internal RustToken? CloseToken { get; set; }
            internal List<MutableTreeNode> Children { get; } = [];
        }
    }
}
