using System.Collections.ObjectModel;

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
        RustLexerOptions? options)
    {
        source ??= string.Empty;
        sourcePath ??= string.Empty;
        var scanner = new Scanner(source, sourcePath, NormalizeOptions(options));
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
        private readonly List<RustToken> _tokens = [];
        private readonly List<RustTrivia> _trivia = [];
        private readonly List<RustTrivia> _pendingTrivia = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly List<MutableTreeNode> _roots = [];
        private readonly Stack<MutableGroup> _groups = new();
        private int _position;
        private bool _stopped;
        private bool _limitReported;
        private bool _delimiterDepthReported;
        private bool _sourceWasTruncated;

        internal Scanner(string source, string sourcePath, RustLexerOptions options)
        {
            _source = source;
            _sourcePath = sourcePath;
            _options = options;
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
            while (!_stopped && _position < _scanLength)
            {
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
            IReadOnlyList<RustToken> tokens = Array.AsReadOnly(_tokens.ToArray());
            IReadOnlyList<RustTrivia> trivia = Array.AsReadOnly(_trivia.ToArray());
            IReadOnlyList<RustTrivia> trailing = Array.AsReadOnly(_pendingTrivia.ToArray());
            IReadOnlyList<Diagnostic> diagnostics = Array.AsReadOnly(_diagnostics.ToArray());

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
            if (_position == 0 && IsShebangStart())
            {
                _position += 2;
                while (_position < _scanLength && _source[_position] is not ('\r' or '\n'))
                {
                    _position++;
                }

                AddTrivia(RustTriviaKind.Shebang, start, _position, true);
                return true;
            }

            if (char.IsWhiteSpace(_source[_position]))
            {
                _position++;
                while (_position < _scanLength && char.IsWhiteSpace(_source[_position]))
                {
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
                while (_position < _scanLength && _source[_position] is not ('\r' or '\n'))
                {
                    _position++;
                }

                bool documentation = _position - start >= 3 &&
                    (_source[start + 2] is '/' or '!');
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
                (_source[start + 2] is '*' or '!');
            AddTrivia(RustTriviaKind.BlockComment, start, _position, documentationBlock);
            return true;
        }

        private RustToken ScanToken()
        {
            int start = _position;

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

            if (IsIdentifierStart(_source[_position]))
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

            _position++;
            AddDiagnostic(
                RustLexDiagnosticCodes.UnknownCharacter,
                $"Unknown character U+{(int)_source[start]:X4}.",
                start,
                1);
            return CreateToken(RustTokenKind.Unknown, start, _position, false, null);
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
                quote++;
            }

            if (quote >= _scanLength || _source[quote] != '"')
            {
                return false;
            }

            int hashCount = quote - hashStart;
            int start = _position;
            int cursor = quote + 1;
            while (cursor < _scanLength)
            {
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
                }

                if (closingHashEnd - closingHashStart == hashCount)
                {
                    _position = closingHashEnd;
                    ReportRawLiteralRestrictions(kind, quote + 1, cursor);

                    token = CreateToken(kind, start, _position, false, null);
                    return true;
                }

                cursor++;
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
                    if (_source[position] <= 0x7f)
                    {
                        continue;
                    }

                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLiteral,
                        "Raw byte string literals must contain ASCII text.",
                        position,
                        1);
                    break;
                }
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
            if (_position + 1 < _scanLength && IsIdentifierStart(_source[_position + 1]) &&
                !HasNearbyClosingApostrophe(_position + 1))
            {
                _position += 2;
                while (_position < _scanLength && IsIdentifierContinue(_source[_position]))
                {
                    _position++;
                }

                return CreateToken(RustTokenKind.Lifetime, start, _position, false, null);
            }

            return ScanQuotedLiteral(start, 0, '\'', RustTokenKind.CharacterLiteral);
        }

        private bool HasNearbyClosingApostrophe(int contentStart)
        {
            int cursor = contentStart;
            int inspected = 0;
            while (cursor < _scanLength && inspected < 256)
            {
                char current = _source[cursor];
                if (current == '\'')
                {
                    return true;
                }

                if (current is '\r' or '\n' || char.IsWhiteSpace(current))
                {
                    return false;
                }

                cursor++;
                inspected++;
            }

            return false;
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
                char current = _source[_position];
                if (current == quote && !escapePending)
                {
                    _position++;
                    closed = true;
                    break;
                }

                if (current is '\r' or '\n')
                {
                    if (quote == '\'')
                    {
                        invalid = true;
                    }

                    if (current == '\r' && _position + 1 < _scanLength && _source[_position + 1] == '\n')
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
                        bool validUnicodeEscape = ConsumeUnicodeEscape(ref invalid, out scalar);
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

                if (byteLiteral && current > 0x7f)
                {
                    invalid = true;
                    AddDiagnostic(
                        RustLexDiagnosticCodes.InvalidLiteral,
                        "Byte literals must contain ASCII text.",
                        _position,
                        1);
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
                if (char.IsHighSurrogate(current) &&
                    _position + 1 < _scanLength &&
                    char.IsLowSurrogate(_source[_position + 1]))
                {
                    _position += 2;
                }
                else
                {
                    _position++;
                }
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

            return CreateToken(kind, start, _position, false, null);
        }

        private bool ConsumeUnicodeEscape(ref bool invalid, out int scalar)
        {
            int escapeStart = _position - 1;
            _position += 2;
            int digits = 0;
            scalar = 0;
            while (_position < _scanLength && _source[_position] != '}')
            {
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
                    while (_position < _scanLength && _source[_position] != '}')
                    {
                        _position++;
                    }

                    if (_position < _scanLength)
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

            if (_source[_position] == '0' && _position + 1 < _scanLength)
            {
                char prefix = _source[_position + 1];
                int radix = prefix switch
                {
                    'b' or 'B' => 2,
                    'o' or 'O' => 8,
                    'x' or 'X' => 16,
                    _ => 0,
                };

                if (radix != 0)
                {
                    _position += 2;
                    int digitCount = 0;
                    while (_position < _scanLength)
                    {
                        char current = _source[_position];
                        if (current == '_')
                        {
                            _position++;
                            continue;
                        }

                        if (IsDigitForRadix(current, radix))
                        {
                            digitCount++;
                            _position++;
                            continue;
                        }

                        break;
                    }

                    if (digitCount == 0)
                    {
                        invalid = true;
                    }

                    if (_position < _scanLength && IsIdentifierContinue(_source[_position]))
                    {
                        int tailStart = _position;
                        while (_position < _scanLength && IsIdentifierContinue(_source[_position]))
                        {
                            _position++;
                        }

                        if (!IsIntegerSuffix(_source.AsSpan(tailStart, _position - tailStart)))
                        {
                            invalid = true;
                        }
                    }

                    if (invalid)
                    {
                        AddDiagnostic(
                            RustLexDiagnosticCodes.InvalidNumber,
                            "Numeric literal contains an invalid digit or suffix.",
                            start,
                            _position - start);
                    }

                    return CreateToken(RustTokenKind.IntegerLiteral, start, _position, false, null);
                }
            }

            while (_position < _scanLength)
            {
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

            if (_position < _scanLength && _source[_position] == '.' &&
                !(_position + 1 < _scanLength && _source[_position + 1] == '.') &&
                (_position + 1 >= _scanLength ||
                    !IsIdentifierStart(_source[_position + 1]) ||
                    IsAsciiDigit(_source[_position + 1])))
            {
                floating = true;
                _position++;
                while (_position < _scanLength && (IsAsciiDigit(_source[_position]) || _source[_position] == '_'))
                {
                    _position++;
                }
            }

            if (_position < _scanLength && _source[_position] is 'e' or 'E')
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

            if (_position < _scanLength && IsIdentifierStart(_source[_position]))
            {
                int suffixStart = _position;
                while (_position < _scanLength && IsIdentifierContinue(_source[_position]))
                {
                    _position++;
                }

                ReadOnlySpan<char> suffix = _source.AsSpan(suffixStart, _position - suffixStart);
                if (IsFloatSuffix(suffix))
                {
                    floating = true;
                }

                if (floating ? !IsFloatSuffix(suffix) : !IsIntegerSuffix(suffix))
                {
                    invalid = true;
                }
            }

            if (invalid)
            {
                AddDiagnostic(
                    RustLexDiagnosticCodes.InvalidNumber,
                    "Numeric literal contains an invalid digit or suffix.",
                    start,
                    _position - start);
            }

            RustTokenKind kind = floating ? RustTokenKind.FloatLiteral : RustTokenKind.IntegerLiteral;
            return CreateToken(kind, start, _position, false, null);
        }

        private RustToken ScanIdentifier(int start)
        {
            _position++;
            while (_position < _scanLength && IsIdentifierContinue(_source[_position]))
            {
                _position++;
            }

            string text = _source.Substring(start, _position - start);
            bool keyword = Keywords.Contains(text);
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
                !IsIdentifierStart(_source[_position + 2]))
            {
                return false;
            }

            int start = _position;
            _position += 3;
            while (_position < _scanLength && IsIdentifierContinue(_source[_position]))
            {
                _position++;
            }

            token = CreateToken(RustTokenKind.RawIdentifier, start, _position, false, null);
            return true;
        }

        private RustToken CreateToken(
            RustTokenKind kind,
            int start,
            int end,
            bool isKeyword,
            RustDelimiterKind? delimiter)
        {
            IReadOnlyList<RustTrivia> leadingTrivia = _pendingTrivia.Count == 0
                ? Array.Empty<RustTrivia>()
                : Array.AsReadOnly(_pendingTrivia.ToArray());
            _pendingTrivia.Clear();
            string text = _source.Substring(start, end - start);
            return new RustToken(kind, new TextSpan(start, end - start), text, isKeyword, delimiter, leadingTrivia);
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
            for (int index = 0; index < _roots.Count; index++)
            {
                trees[index] = _roots[index].Freeze(_scanLength);
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

        private bool IsShebangStart() =>
            _scanLength >= 2 && _source[0] == '#' && _source[1] == '!' &&
            (_scanLength == 2 || _source[2] != '[');

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

        private static bool IsIdentifierStart(char value) =>
            RustIdentifierFacts.IsIdentifierStart(value);

        private static bool IsIdentifierContinue(char value) =>
            RustIdentifierFacts.IsIdentifierContinue(value);

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

        private static bool IsIntegerSuffix(ReadOnlySpan<char> suffix)
        {
            if (suffix.IsEmpty)
            {
                return true;
            }

            return suffix.SequenceEqual("u8") || suffix.SequenceEqual("u16") || suffix.SequenceEqual("u32") ||
                suffix.SequenceEqual("u64") || suffix.SequenceEqual("u128") || suffix.SequenceEqual("usize") ||
                suffix.SequenceEqual("i8") || suffix.SequenceEqual("i16") || suffix.SequenceEqual("i32") ||
                suffix.SequenceEqual("i64") || suffix.SequenceEqual("i128") || suffix.SequenceEqual("isize");
        }

        private static bool IsFloatSuffix(ReadOnlySpan<char> suffix) =>
            suffix.IsEmpty || suffix.SequenceEqual("f32") || suffix.SequenceEqual("f64");

        private abstract class MutableTreeNode
        {
            internal abstract RustTokenTree Freeze(int sourceLength);
        }

        private sealed class MutableLeaf : MutableTreeNode
        {
            internal MutableLeaf(RustToken token)
            {
                Token = token;
            }

            internal RustToken Token { get; }

            internal override RustTokenTree Freeze(int sourceLength) => new RustLeafTokenTree(Token);
        }

        private sealed class MutableGroup(RustDelimiterKind delimiter, RustToken openToken) : MutableTreeNode
        {
            internal RustDelimiterKind Delimiter { get; } = delimiter;
            internal RustToken OpenToken { get; } = openToken;
            internal RustToken? CloseToken { get; set; }
            internal List<MutableTreeNode> Children { get; } = [];

            internal override RustTokenTree Freeze(int sourceLength)
            {
                var children = new RustTokenTree[Children.Count];
                for (int index = 0; index < Children.Count; index++)
                {
                    children[index] = Children[index].Freeze(sourceLength);
                }

                int end = CloseToken?.Span.End ?? sourceLength;
                return new RustDelimitedTokenTree(
                    Delimiter,
                    OpenToken,
                    Array.AsReadOnly(children),
                    CloseToken,
                    new TextSpan(OpenToken.Span.Start, Math.Max(0, end - OpenToken.Span.Start)));
            }
        }
    }
}
