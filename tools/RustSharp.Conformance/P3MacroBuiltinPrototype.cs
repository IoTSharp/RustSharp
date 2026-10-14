using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated stringify invocation prototype for the frozen candidate spacing rule; no compiler expansion.</summary>
public static class P3MacroBuiltinPrototype
{
    public static P3MacroBuiltinResult Stringify(string invocation, P3MacroBuiltinLimits? limits = null,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new();
        if (!limits.IsWithinCandidate()) return Failure("RSMAC1090", "Prototype budgets exceed the frozen candidate or are nonpositive.", 0);
        if (invocation.Length > limits.MaximumInputBytes || Encoding.UTF8.GetByteCount(invocation) > limits.MaximumInputBytes)
            return Failure("RSMAC1005", "Input byte budget exceeded.", 0);
        var state = new State(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            RustLexResult lex = RustLexer.Lex(invocation, "builtin-prototype.rs", new RustLexerOptions
            {
                MaximumSourceLength = limits.MaximumInputBytes,
                MaximumTokens = limits.MaximumInputTokens,
                MaximumTrivia = limits.MaximumInputBytes,
                MaximumDelimiterDepth = limits.MaximumDepth,
                MaximumDiagnostics = 16,
                Timeout = TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds),
            }, cancellationToken);
            state.Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static item => item.Code is RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Lexer resource budget exceeded.", state.Steps);
            if (!lex.IsSuccessful) return Failure("RSMAC1002", "Malformed lexical input or delimiter tree.", state.Steps);
            IReadOnlyList<RustTokenTree> trees = lex.TokenTrees;
            if (trees.Count != 3 || trees[0].Token is not { Kind: RustTokenKind.Identifier, Text: "stringify" }
                || trees[1].Token is not { Kind: RustTokenKind.Punctuation, Text: "!" }
                || trees[2] is not RustDelimitedTokenTree { Delimiter: RustDelimiterKind.Parenthesis, IsClosed: true } group)
                return Failure("RSMAC1003", "Expected exactly one stringify!(balanced token trees) invocation.", state.Steps);
            int start = group.OpenToken.Span.End;
            int end = group.CloseToken!.Span.Start;
            var output = new StringBuilder();
            var spans = new List<TextSpan>();
            for (int index = 0; index < lex.Tokens.Count && index < limits.MaximumInputTokens; index++)
            {
                state.Step();
                RustToken token = lex.Tokens[index];
                if (token.Span.Start < start || token.Span.End > end) continue;
                if (spans.Count >= limits.MaximumExpandedTokens)
                    return Failure("RSMAC1005", "Expanded token budget exceeded.", state.Steps);
                if (spans.Count > 0) output.Append(' ');
                output.Append(token.Text);
                spans.Add(token.Span);
            }
            state.Step();
            return new(true, string.Empty, string.Empty, output.ToString(), new(start, end - start), spans.AsReadOnly(), state.Steps);
        }
        catch (TimeoutException exception)
        {
            return Failure("RSMAC1005", exception.Message, state.Steps);
        }
    }

    private static P3MacroBuiltinResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, null, Array.Empty<TextSpan>(), steps);

    private sealed class State(P3MacroBuiltinLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        internal int Steps { get; private set; }
        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds)
                throw new TimeoutException("Builtin prototype step/deadline budget exceeded.");
            Steps++;
            progress?.Invoke(Steps);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

public sealed record P3MacroBuiltinLimits
{
    public int MaximumInputBytes { get; init; } = 65_536;
    public int MaximumInputTokens { get; init; } = 4_096;
    public int MaximumDepth { get; init; } = 32;
    public int MaximumMatcherSteps { get; init; } = 100_000;
    public int MaximumExpandedTokens { get; init; } = 16_384;
    public int TimeoutMilliseconds { get; init; } = 2_000;
    internal bool IsWithinCandidate() => MaximumInputBytes is > 0 and <= 65_536 && MaximumInputTokens is > 0 and <= 4_096
        && MaximumDepth is > 0 and <= 32 && MaximumMatcherSteps is > 0 and <= 100_000
        && MaximumExpandedTokens is > 0 and <= 16_384 && TimeoutMilliseconds is > 0 and <= 2_000;
}

public sealed record P3MacroBuiltinResult(bool IsSuccessful, string Diagnostic, string Message, string Output,
    TextSpan? ArgumentSpan, IReadOnlyList<TextSpan> TokenSpans, int MatcherSteps)
{
    public bool FormalAcceptanceAllowed { get; }
    public int ProductionMacroExpansionsExecuted { get; }
}
