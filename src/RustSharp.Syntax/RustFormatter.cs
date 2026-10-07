using System.Diagnostics;
using System.Text;

namespace RustSharp.Syntax;

/// <summary>Formats the frozen safe-core profile after parsing and before verifying the complete output.</summary>
public static class RustFormatter
{
    public static FormatterResult Format(string source, string sourcePath,
        FormatterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourcePath);
        options ??= new FormatterOptions();
        Validate(options);
        var clock = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        FormatterResult Reject(string code, string message) => new(false, false, source,
            [new Diagnostic(code, message, new TextSpan(0, 0))]);
        void Guard()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= options.Timeout)
                throw new TimeoutException("Formatting exceeded its shared wall-clock budget.");
        }
        TimeSpan Remaining()
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Validate the same elapsed sample used to construct the parser budget.
            TimeSpan remaining = options.Timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Formatting exceeded its shared wall-clock budget.");
            return remaining;
        }
        try
        {
            if (source.Length > options.MaximumSourceLength)
                return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting source limit exceeded.");
            var parseOptions = new SafeCoreSyntaxOptions
            {
                Timeout = Remaining(), MaximumSourceLength = options.MaximumSourceLength,
                MaximumTokens = options.MaximumTokens, MaximumOperations = 1_000_000,
            };
            SafeCoreSyntaxResult before = SafeCoreSyntax.Parse(source, sourcePath, parseOptions, cancellationToken);
            Guard();
            if (before.IsTruncated)
                return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting parse limit exceeded.");
            if (!before.IsSuccessful)
                return new(false, false, source, before.Diagnostics);
            RustLexResult lex = before.LexResult;
            if (lex.Trivia.Count > options.MaximumTrivia ||
                (long)lex.Tokens.Count + lex.Trivia.Count > options.MaximumOperations)
                return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting trivia or layout operation limit exceeded.");
            var layout = new Layout(source, lex, options.MaximumOutputBytes, Guard);
            string formatted = layout.Run();
            Guard();
            if (formatted.Length > 1_000_000 || Encoding.UTF8.GetByteCount(formatted) > options.MaximumOutputBytes)
                return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting UTF-8 output limit exceeded.");
            // The output can be larger than the input due to indentation. It has its own frozen output bound.
            SafeCoreSyntaxResult after = SafeCoreSyntax.Parse(formatted, sourcePath,
                parseOptions with { Timeout = Remaining(), MaximumSourceLength = 1_000_000 }, cancellationToken);
            Guard();
            if (after.IsTruncated)
                return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting output parse limit exceeded.");
            if (!after.IsSuccessful || !SameLexicalMeaning(lex, after.LexResult, Guard))
                return Reject(FormatterDiagnosticCodes.PreservationFailed, "Formatting did not preserve tokens, comments, or supported syntax.");
            string firstAst = FormatterAstFingerprint.Write(before.Root!, Guard, cancellationToken);
            Guard();
            string secondAst = FormatterAstFingerprint.Write(after.Root!, Guard, cancellationToken);
            Guard();
            if (!string.Equals(firstAst, secondAst, StringComparison.Ordinal))
                return Reject(FormatterDiagnosticCodes.PreservationFailed, "Formatting changed the span-independent syntax tree.");
            bool changed = !string.Equals(source, formatted, StringComparison.Ordinal);
            if (options.CheckOnly && changed)
                return new(false, true, source, [new Diagnostic(FormatterDiagnosticCodes.FormattingDrift,
                    "Source requires formatting.", new TextSpan(0, 0))]);
            return new(true, changed, formatted, Array.Empty<Diagnostic>());
        }
        catch (TimeoutException)
        {
            return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting exceeded its shared wall-clock budget.");
        }
        catch (LayoutLimitException)
        {
            return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting output limit exceeded.");
        }
        catch (InvalidDataException)
        {
            return Reject(FormatterDiagnosticCodes.LimitReached, "Formatting AST verification limit exceeded.");
        }
    }

    private static void Validate(FormatterOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(10) ||
            options.MaximumSourceLength is < 1 or > 1_000_000 || options.MaximumTokens is < 1 or > 250_000 ||
            options.MaximumTrivia is < 1 or > 500_000 || options.MaximumOperations is < 1 or > 1_000_000 ||
            options.MaximumOutputBytes is < 1 or > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(options), "Formatter options must stay within the frozen tooling limits.");
    }

    private static bool SameLexicalMeaning(RustLexResult before, RustLexResult after, Action guard)
    {
        if (before.Tokens.Count != after.Tokens.Count) return false;
        for (int index = 0; index < before.Tokens.Count; index++)
        {
            guard();
            if (before.Tokens[index].Kind != after.Tokens[index].Kind || before.Tokens[index].Text != after.Tokens[index].Text)
                return false;
        }
        // Record each comment's token boundary as well as exact spelling. A comment cannot migrate across a token.
        return PreservedTrivia(before, guard).SequenceEqual(PreservedTrivia(after, guard));
    }

    private static List<(int TokenIndex, RustTriviaKind Kind, string Text, bool Documentation)> PreservedTrivia(
        RustLexResult lex, Action guard)
    {
        var result = new List<(int, RustTriviaKind, string, bool)>();
        int tokenIndex = 0;
        foreach (RustTrivia trivia in lex.Trivia)
        {
            guard();
            if (trivia.Kind == RustTriviaKind.Whitespace) continue;
            while (tokenIndex < lex.Tokens.Count && lex.Tokens[tokenIndex].Span.End <= trivia.Span.Start)
            {
                guard();
                tokenIndex++;
            }
            result.Add((tokenIndex, trivia.Kind, trivia.Text, trivia.IsDocumentation));
        }
        return result;
    }

    private sealed class LayoutLimitException : Exception;

    private sealed class Layout(string source, RustLexResult lex, int maximumOutputBytes, Action guard)
    {
        private readonly StringBuilder _text = new();
        private readonly Stack<string> _delimiters = new();
        private int _indent;
        private int _triviaIndex;
        private bool _lineStart = true;
        private bool _generatedSpace;

        public string Run()
        {
            for (int index = 0; index < lex.Tokens.Count; index++)
            {
                guard();
                RustToken token = lex.Tokens[index];
                TriviaBefore(token.Span.Start);
                if (token.Text == "#" && TryAttributeEnd(index, out int end))
                {
                    // Attribute ArgumentsText is an intentionally opaque parser payload. Keep its whole token tree exact.
                    Indent();
                    Append(source.Substring(token.Span.Start, lex.Tokens[end].Span.End - token.Span.Start));
                    while (_triviaIndex < lex.Trivia.Count && lex.Trivia[_triviaIndex].Span.Start < lex.Tokens[end].Span.End)
                    {
                        guard();
                        _triviaIndex++;
                    }
                    index = end;
                    Newline();
                    continue;
                }
                string? previous = index == 0 ? null : lex.Tokens[index - 1].Text;
                string? next = index + 1 < lex.Tokens.Count ? lex.Tokens[index + 1].Text : null;
                bool hadGap = index > 0 && token.Span.Start > lex.Tokens[index - 1].Span.End;
                switch (token.Text)
                {
                    case "{":
                        if (!_lineStart) Space();
                        Indent(); Append("{"); _delimiters.Push("{"); _indent++; Newline();
                        break;
                    case "}":
                        _indent = Math.Max(0, _indent - 1); Newline(); Indent(); Append("}");
                        if (_delimiters.Count > 0) _delimiters.Pop();
                        if (next == "else") Space();
                        else if (next is not (";" or "," or ")" or "]" or "." or "?" or "::")) Newline();
                        break;
                    case "(": case "[":
                        if (hadGap && !_lineStart) Space();
                        Indent(); Append(token.Text); _delimiters.Push(token.Text);
                        break;
                    case ")": case "]":
                        Indent(); Append(token.Text); if (_delimiters.Count > 0) _delimiters.Pop();
                        break;
                    case ";":
                        Indent(); Append(";");
                        if (_delimiters.Count == 0 || _delimiters.Peek() == "{") Newline(); else Space();
                        break;
                    case ",":
                        Indent(); Append(",");
                        if (_delimiters.Count > 0 && _delimiters.Peek() == "{") Newline(); else Space();
                        break;
                    case "=":
                        if (!_lineStart) Space(); Indent(); Append("="); Space();
                        break;
                    default:
                        if (!_lineStart && (hadGap || previous is "=" or "," or ":")) Space();
                        Indent(); Append(token.Text);
                        break;
                }
            }
            TriviaBefore(source.Length + 1);
            Newline();
            // Even an empty document has exactly one final LF. Literal/comment bytes are never trimmed.
            if (_text.Length == 0 || _text[^1] != '\n') Append("\n");
            return _text.ToString();
        }

        private bool TryAttributeEnd(int start, out int end)
        {
            int open = start + 1;
            if (open < lex.Tokens.Count && lex.Tokens[open].Text == "!") open++;
            if (open >= lex.Tokens.Count || lex.Tokens[open].Text != "[") { end = start; return false; }
            int depth = 0;
            for (int index = open; index < lex.Tokens.Count; index++)
            {
                guard();
                if (lex.Tokens[index].Text == "[") depth++;
                if (lex.Tokens[index].Text == "]" && --depth == 0) { end = index; return true; }
            }
            end = start;
            return false;
        }

        private void TriviaBefore(int tokenStart)
        {
            while (_triviaIndex < lex.Trivia.Count && lex.Trivia[_triviaIndex].Span.Start < tokenStart)
            {
                guard();
                RustTrivia trivia = lex.Trivia[_triviaIndex++];
                if (trivia.Kind == RustTriviaKind.Whitespace) continue;
                if (trivia.Kind == RustTriviaKind.ByteOrderMark) { Append(trivia.Text); _lineStart = true; continue; }
                if (trivia.Kind == RustTriviaKind.Shebang) { Append(trivia.Text); Newline(); continue; }
                if (!_lineStart) Space();
                Indent(); Append(trivia.Text);
                if (trivia.Kind == RustTriviaKind.LineComment || trivia.IsDocumentation) Newline(); else Space();
            }
        }

        private void Append(string text)
        {
            guard();
            if ((long)_text.Length + text.Length > Math.Min(maximumOutputBytes, 1_000_000)) throw new LayoutLimitException();
            _text.Append(text);
            _generatedSpace = false;
            if (text.Length > 0) _lineStart = text[^1] == '\n';
        }
        private void Indent()
        {
            if (_lineStart) { Append(new string(' ', _indent * 4)); _lineStart = false; }
        }
        private void Space()
        {
            if (_text.Length > 0 && !_lineStart && _text[^1] is not (' ' or '\t' or '\n'))
            {
                Append(" ");
                _generatedSpace = true;
            }
        }
        private void Newline()
        {
            // Remove only whitespace generated by Space/Indent, never literal or comment content.
            if (_lineStart) return;
            if (_generatedSpace && _text.Length > 0 && _text[^1] == ' ') _text.Length--;
            Append("\n"); _lineStart = true;
        }
    }
}
