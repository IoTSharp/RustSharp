using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace RustSharp.Syntax;

public sealed class SyntaxTree
{
    private SyntaxTree(
        string sourcePath,
        CompilationUnitSyntax? root,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        SourcePath = sourcePath;
        Root = root;
        Diagnostics = diagnostics;
    }

    public string SourcePath { get; }

    public CompilationUnitSyntax? Root { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Decodes one complete regular Rust string literal using the vertical-slice escape rules.</summary>
    public static bool TryDecodeStringLiteral(string literal, out string value)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return new Parser(literal).TryDecodeCompleteString(out value);
    }

    public static SyntaxTree Parse(string source, string sourcePath)
    {
        source ??= string.Empty;
        sourcePath ??= string.Empty;

        var parser = new Parser(source);
        CompilationUnitSyntax? root = parser.ParseCompilationUnit();
        ReadOnlyCollection<Diagnostic> diagnostics = Array.AsReadOnly(parser.Diagnostics.ToArray());

        if (diagnostics.Count != 0)
        {
            root = null;
        }

        return new SyntaxTree(sourcePath, root, diagnostics);
    }

    private sealed class Parser
    {
        private const string ExpectedTokenCode = "RSC1001";
        private const string UnexpectedInputCode = "RSC1002";
        private const string UnterminatedCommentCode = "RSC1003";
        private const string UnterminatedStringCode = "RSC1004";
        private const string InvalidEscapeCode = "RSC1005";
        private const string InvalidUnicodeEscapeCode = "RSC1006";
        private const string DiagnosticLimitCode = "RSC1007";
        private const int MaximumDiagnostics = 256;
        private const int MaximumRecoveries = 256;

        private readonly string _source;
        private readonly List<Diagnostic> _diagnostics = new(MaximumDiagnostics);
        private int _position;
        private int _recoveryCount;
        private bool _hasFatalTriviaError;
        private bool _isTerminated;
        private bool _diagnosticLimitReported;

        internal Parser(string source)
        {
            _source = source;
        }

        internal List<Diagnostic> Diagnostics => _diagnostics;

        internal bool TryDecodeCompleteString(out string value) =>
            TryParseString(out value) && IsAtEnd && _diagnostics.Count == 0;

        internal CompilationUnitSyntax? ParseCompilationUnit()
        {
            if (!ExpectIdentifier("fn") ||
                !ExpectIdentifier("main") ||
                !ExpectPunctuation('(') ||
                !ExpectPunctuation(')') ||
                !ExpectPunctuation('{'))
            {
                return null;
            }

            var statements = new List<PrintStatementSyntax>();
            bool closedBody = false;

            while (!_isTerminated && !IsAtEnd)
            {
                if (!SkipTrivia())
                {
                    return null;
                }

                if (IsAtEnd)
                {
                    AddExpected("'}'");
                    return null;
                }

                if (Current == '}')
                {
                    _position++;
                    closedBody = true;
                    break;
                }

                int statementStart = _position;
                PrintStatementSyntax? statement = ParsePrintStatement(statementStart);
                if (statement is not null)
                {
                    statements.Add(statement);
                    continue;
                }

                RecoverStatement(statementStart);
            }

            if (_isTerminated)
            {
                return null;
            }

            if (!closedBody)
            {
                AddExpected("'}'");
                return null;
            }

            if (!SkipTrivia())
            {
                return null;
            }

            if (!IsAtEnd)
            {
                int start = _position;
                int length = GetUnexpectedTokenLength();
                AddDiagnostic(
                    UnexpectedInputCode,
                    "Unexpected input after the end of 'main'.",
                    start,
                    length);
            }

            if (_isTerminated)
            {
                return null;
            }

            ReadOnlyCollection<PrintStatementSyntax> readOnlyStatements =
                Array.AsReadOnly(statements.ToArray());
            return new CompilationUnitSyntax(readOnlyStatements);
        }

        private PrintStatementSyntax? ParsePrintStatement(int statementStart)
        {
            if (!ExpectIdentifier("println") ||
                !ExpectPunctuation('!') ||
                !ExpectPunctuation('('))
            {
                return null;
            }

            if (!TryParseString(out string value))
            {
                return null;
            }

            if (!ExpectPunctuation(')') || !ExpectPunctuation(';'))
            {
                return null;
            }

            return new PrintStatementSyntax(
                value,
                new TextSpan(statementStart, _position - statementStart));
        }

        private bool ExpectIdentifier(string expected)
        {
            if (!SkipTrivia())
            {
                return false;
            }

            int start = _position;
            while (!_isTerminated && !IsAtEnd && IsIdentifierContinue(Current))
            {
                _position++;
            }

            int length = _position - start;
            if (length == expected.Length &&
                _source.AsSpan(start, length).SequenceEqual(expected.AsSpan()))
            {
                return true;
            }

            if (length == 0 && !IsAtEnd)
            {
                _position++;
                length = 1;
            }

            AddDiagnostic(
                ExpectedTokenCode,
                $"Expected '{expected}'.",
                start,
                length);
            return false;
        }

        private bool ExpectPunctuation(char expected)
        {
            if (!SkipTrivia())
            {
                return false;
            }

            if (!IsAtEnd && Current == expected)
            {
                _position++;
                return true;
            }

            int length = IsAtEnd ? 0 : 1;
            AddDiagnostic(
                ExpectedTokenCode,
                $"Expected '{expected}'.",
                _position,
                length);

            if (!IsAtEnd)
            {
                _position++;
            }

            return false;
        }

        private bool TryParseString(out string value)
        {
            value = string.Empty;
            if (!SkipTrivia())
            {
                return false;
            }

            if (IsAtEnd || Current != '"')
            {
                AddDiagnostic(
                    ExpectedTokenCode,
                    "Expected a string literal.",
                    _position,
                    IsAtEnd ? 0 : 1);
                return false;
            }

            int literalStart = _position;
            _position++;
            var builder = new StringBuilder();

            while (!_isTerminated && !IsAtEnd)
            {
                char current = Current;
                if (current == '"')
                {
                    _position++;
                    value = builder.ToString();
                    return true;
                }

                if (current == '\r')
                {
                    if (_position + 1 < _source.Length && _source[_position + 1] == '\n')
                    {
                        builder.Append('\n');
                        _position += 2;
                        continue;
                    }

                    AddDiagnostic(
                        UnterminatedStringCode,
                        "Unescaped carriage return is not allowed in a string literal.",
                        _position,
                        1);
                    return false;
                }

                if (current == '\n')
                {
                    builder.Append('\n');
                    _position++;
                    continue;
                }

                if (current != '\\')
                {
                    builder.Append(current);
                    _position++;
                    continue;
                }

                if (!TryParseEscape(builder))
                {
                    return false;
                }
            }

            AddDiagnostic(
                UnterminatedStringCode,
                "String literal is not terminated.",
                literalStart,
                _position - literalStart);
            return false;
        }

        private bool TryParseEscape(StringBuilder builder)
        {
            int escapeStart = _position;
            _position++;

            if (IsAtEnd)
            {
                AddDiagnostic(
                    UnterminatedStringCode,
                    "String literal is not terminated.",
                    escapeStart,
                    1);
                return false;
            }

            char escaped = Current;
            _position++;

            switch (escaped)
            {
                case 'n':
                    builder.Append('\n');
                    return true;
                case 'r':
                    builder.Append('\r');
                    return true;
                case 't':
                    builder.Append('\t');
                    return true;
                case '0':
                    builder.Append('\0');
                    return true;
                case '\\':
                    builder.Append('\\');
                    return true;
                case '"':
                    builder.Append('"');
                    return true;
                case '\'':
                    builder.Append('\'');
                    return true;
                case 'x':
                    return TryParseAsciiEscape(builder, escapeStart);
                case 'u':
                    return TryParseUnicodeEscape(builder, escapeStart);
                case '\r':
                    if (!IsAtEnd && Current == '\n')
                    {
                        _position++;
                    }

                    SkipStringContinuationWhitespace();
                    return true;
                case '\n':
                    SkipStringContinuationWhitespace();
                    return true;
                default:
                    AddDiagnostic(
                        InvalidEscapeCode,
                        $"Unknown string escape '\\{escaped}'.",
                        escapeStart,
                        _position - escapeStart);
                    return false;
            }
        }

        private bool TryParseAsciiEscape(StringBuilder builder, int escapeStart)
        {
            if (_position + 2 > _source.Length ||
                !TryGetHexValue(_source[_position], out int high) ||
                !TryGetHexValue(_source[_position + 1], out int low))
            {
                int length = Math.Min(4, _source.Length - escapeStart);
                AddDiagnostic(
                    InvalidEscapeCode,
                    "ASCII escape must contain exactly two hexadecimal digits.",
                    escapeStart,
                    length);
                return false;
            }

            int scalar = (high << 4) | low;
            _position += 2;
            if (scalar > 0x7f)
            {
                AddDiagnostic(
                    InvalidEscapeCode,
                    "ASCII escape must be in the range \\x00 through \\x7f.",
                    escapeStart,
                    _position - escapeStart);
                return false;
            }

            builder.Append((char)scalar);
            return true;
        }

        private bool TryParseUnicodeEscape(StringBuilder builder, int escapeStart)
        {
            if (IsAtEnd || Current != '{')
            {
                AddInvalidUnicodeEscape(escapeStart);
                return false;
            }

            _position++;
            int scalar = 0;
            int digitCount = 0;

            while (!_isTerminated && !IsAtEnd && Current != '}')
            {
                if (Current == '_' && digitCount != 0)
                {
                    _position++;
                    continue;
                }

                if (digitCount == 6 || !TryGetHexValue(Current, out int digit))
                {
                    AddInvalidUnicodeEscape(escapeStart);
                    return false;
                }

                scalar = (scalar << 4) | digit;
                digitCount++;
                _position++;
            }

            if (IsAtEnd || Current != '}' || digitCount == 0)
            {
                AddInvalidUnicodeEscape(escapeStart);
                return false;
            }

            _position++;
            if (scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff)
            {
                AddInvalidUnicodeEscape(escapeStart);
                return false;
            }

            builder.Append(char.ConvertFromUtf32(scalar));
            return true;
        }

        private void AddInvalidUnicodeEscape(int escapeStart)
        {
            AddDiagnostic(
                InvalidUnicodeEscapeCode,
                "Unicode escape must contain one to six hexadecimal digits and a valid Unicode scalar value.",
                escapeStart,
                Math.Max(1, _position - escapeStart));
        }

        private void SkipStringContinuationWhitespace()
        {
            while (!_isTerminated && !IsAtEnd && char.IsWhiteSpace(Current))
            {
                _position++;
            }
        }

        private bool SkipTrivia()
        {
            if (_hasFatalTriviaError)
            {
                return false;
            }

            while (!_isTerminated && !IsAtEnd)
            {
                if (char.IsWhiteSpace(Current))
                {
                    _position++;
                    continue;
                }

                if (Current != '/' || _position + 1 >= _source.Length)
                {
                    return true;
                }

                char next = _source[_position + 1];
                if (next == '/')
                {
                    _position += 2;
                    while (!_isTerminated && !IsAtEnd && Current is not ('\r' or '\n'))
                    {
                        _position++;
                    }

                    continue;
                }

                if (next != '*')
                {
                    return true;
                }

                int commentStart = _position;
                _position += 2;
                int depth = 1;

                while (!_isTerminated && !IsAtEnd && depth != 0)
                {
                    if (Current == '/' &&
                        _position + 1 < _source.Length &&
                        _source[_position + 1] == '*')
                    {
                        depth++;
                        _position += 2;
                    }
                    else if (Current == '*' &&
                             _position + 1 < _source.Length &&
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
                        UnterminatedCommentCode,
                        "Block comment is not terminated.",
                        commentStart,
                        _position - commentStart);
                    _hasFatalTriviaError = true;
                    return false;
                }
            }

            return true;
        }

        private void RecoverStatement(int statementStart)
        {
            if (_isTerminated)
            {
                return;
            }

            if (_recoveryCount >= MaximumRecoveries)
            {
                AddDiagnosticLimit(statementStart);
                return;
            }

            _recoveryCount++;

            if (_position <= statementStart && !IsAtEnd)
            {
                _position++;
            }

            while (!_isTerminated && !IsAtEnd)
            {
                if (Current == ';')
                {
                    _position++;
                    return;
                }

                if (Current == '}')
                {
                    return;
                }

                _position++;
            }
        }

        private int GetUnexpectedTokenLength()
        {
            if (IsIdentifierContinue(Current))
            {
                int start = _position;
                while (!_isTerminated && !IsAtEnd && IsIdentifierContinue(Current))
                {
                    _position++;
                }

                int length = _position - start;
                _position = start;
                return length;
            }

            return 1;
        }

        private void AddExpected(string expected)
        {
            AddDiagnostic(ExpectedTokenCode, $"Expected {expected}.", _position, 0);
        }

        private void AddDiagnostic(string code, string message, int start, int length)
        {
            if (_isTerminated)
            {
                return;
            }

            // Reserve the final slot for the single truncation diagnostic. This
            // keeps malformed, very large inputs bounded at 256 diagnostics.
            if (_diagnostics.Count >= MaximumDiagnostics - 1)
            {
                AddDiagnosticLimit(start);
                return;
            }

            int safeStart = Math.Clamp(start, 0, _source.Length);
            int safeLength = Math.Clamp(length, 0, _source.Length - safeStart);
            _diagnostics.Add(new Diagnostic(code, message, new TextSpan(safeStart, safeLength)));
        }

        private void AddDiagnosticLimit(int start)
        {
            if (_diagnosticLimitReported)
            {
                _isTerminated = true;
                return;
            }

            _diagnosticLimitReported = true;
            _isTerminated = true;

            int safeStart = Math.Clamp(start, 0, _source.Length);
            _diagnostics.Add(new Diagnostic(
                DiagnosticLimitCode,
                "Too many syntax errors; parsing stopped.",
                new TextSpan(safeStart, 0)));
        }

        private static bool IsIdentifierContinue(char value) =>
            value == '_' || char.IsLetterOrDigit(value);

        private static bool TryGetHexValue(char value, out int result)
        {
            if (value is >= '0' and <= '9')
            {
                result = value - '0';
                return true;
            }

            if (value is >= 'a' and <= 'f')
            {
                result = value - 'a' + 10;
                return true;
            }

            if (value is >= 'A' and <= 'F')
            {
                result = value - 'A' + 10;
                return true;
            }

            result = 0;
            return false;
        }

        private bool IsAtEnd => _position >= _source.Length;

        private char Current => _source[_position];
    }
}
