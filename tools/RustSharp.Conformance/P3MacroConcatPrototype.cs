using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated candidate concat: cooked unsuffixed strings only, without production macro expansion.</summary>
public static class P3MacroConcatPrototype
{
    public static P3MacroConcatResult Concat(string invocation, P3MacroBuiltinLimits? limits = null,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new();
        if (!limits.IsWithinCandidate()) return Failure("RSMAC1090", "Invalid candidate resource limits.", 0);
        if (invocation.Length > limits.MaximumInputBytes || Encoding.UTF8.GetByteCount(invocation) > limits.MaximumInputBytes)
            return Failure("RSMAC1005", "Input byte budget exceeded.", 0);
        var state = new State(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            RustLexResult lex = RustLexer.Lex(invocation, "concat-prototype.rs", new RustLexerOptions
            {
                MaximumSourceLength = limits.MaximumInputBytes, MaximumTokens = limits.MaximumInputTokens,
                MaximumTrivia = limits.MaximumInputBytes, MaximumDelimiterDepth = limits.MaximumDepth,
                MaximumDiagnostics = 16, Timeout = TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds),
            }, cancellationToken);
            state.Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static item => item.Code is RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Lexer resource budget exceeded.", state.Steps);
            if (!lex.IsSuccessful) return Failure("RSMAC1002", "Malformed lexical token or delimiter group.", state.Steps);
            IReadOnlyList<RustTokenTree> trees = lex.TokenTrees;
            if (trees.Count != 3 || trees[0].Token is not { Kind: RustTokenKind.Identifier, Text: "concat" }
                || trees[1].Token is not { Kind: RustTokenKind.Punctuation, Text: "!" }
                || trees[2] is not RustDelimitedTokenTree { Delimiter: RustDelimiterKind.Parenthesis, IsClosed: true } group)
                return Failure("RSMAC1003", "Expected exactly one concat!(...) invocation.", state.Steps);
            var output = new StringBuilder();
            var spans = new List<TextSpan>();
            bool expectLiteral = true;
            for (int index = 0; index < group.Children.Count && index < limits.MaximumInputTokens; index++)
            {
                state.Step();
                RustToken? token = group.Children[index].Token;
                if (expectLiteral)
                {
                    if (token is not { Kind: RustTokenKind.StringLiteral, LiteralSuffix: null })
                        return Failure("RSMAC1003", "Only unsuffixed cooked UTF-8 string literals are included.", state.Steps);
                    string normalized = state.RemoveContinuations(token.Text);
                    if (!SyntaxTree.TryDecodeStringLiteral(normalized, out string decoded))
                        return Failure("RSMAC1002", "Invalid cooked Rust string escape.", state.Steps);
                    state.Step();
                    output.Append(decoded);
                    spans.Add(token.Span);
                    expectLiteral = false;
                }
                else
                {
                    if (token is not { Kind: RustTokenKind.Punctuation, Text: "," })
                        return Failure("RSMAC1003", "Comma required between cooked string literals.", state.Steps);
                    expectLiteral = true;
                }
            }
            state.Step();
            int start = group.OpenToken.Span.End;
            return new(true, string.Empty, string.Empty, output.ToString(), new(start, group.CloseToken!.Span.Start - start), spans.AsReadOnly(), state.Steps);
        }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, state.Steps); }
    }

    private static P3MacroConcatResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, null, Array.Empty<TextSpan>(), steps);

    private sealed class State(P3MacroBuiltinLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        internal int Steps { get; private set; }
        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds)
                throw new TimeoutException("Concat prototype step/deadline budget exceeded.");
            Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested();
        }

        // Rust Reference expr.literal.continuation restricts skipped whitespace to HT, LF, CR and SPACE.
        // Remove these escapes before reusing SyntaxTree's decoder, whose continuation helper uses broader char.IsWhiteSpace.
        internal string RemoveContinuations(string literal)
        {
            var normalized = new StringBuilder(literal.Length);
            for (int index = 0; index < literal.Length && index < limits.MaximumInputBytes; index++)
            {
                Step();
                char value = literal[index];
                if (value != '\\' || index + 1 >= literal.Length) { normalized.Append(value); continue; }
                char next = literal[index + 1];
                int continuationLength = next == '\n' ? 2 : next == '\r' && index + 2 < literal.Length && literal[index + 2] == '\n' ? 3 : 0;
                if (continuationLength == 0)
                {
                    normalized.Append(value).Append(next); index++; continue;
                }
                index += continuationLength;
                for (; index < literal.Length && index < limits.MaximumInputBytes && literal[index] is '\t' or '\n' or '\r' or ' '; index++) Step();
                index--;
            }
            return normalized.ToString();
        }
    }
}

public sealed record P3MacroConcatResult(bool IsSuccessful, string Diagnostic, string Message, string Output,
    TextSpan? ArgumentSpan, IReadOnlyList<TextSpan> LiteralSpans, int MatcherSteps)
{
    public bool FormalAcceptanceAllowed { get; }
    public int ProductionMacroExpansionsExecuted { get; }
}
