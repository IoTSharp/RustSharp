using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Single-rule, whole-matcher, one-level ident repetition; no backtracking or template evaluation.</summary>
public static class P3MacroRepetitionPrototype
{
    public static P3MacroRepetitionResult Match(string definition, string invocationBody, P3MacroBuiltinLimits? limits = null,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(invocationBody);
        cancellationToken.ThrowIfCancellationRequested(); limits ??= new();
        if (!limits.IsWithinCandidate()) return Failure("RSMAC1090", "Invalid frozen resource limits.", 0);
        if (definition.Length > limits.MaximumInputBytes || invocationBody.Length > limits.MaximumInputBytes
            || Encoding.UTF8.GetByteCount(definition) + Encoding.UTF8.GetByteCount(invocationBody) > limits.MaximumInputBytes)
            return Failure("RSMAC1005", "Aggregate definition/body byte budget exceeded.", 0);
        var state = new State(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            RustLexerOptions options = new() { MaximumSourceLength = limits.MaximumInputBytes, MaximumTokens = limits.MaximumInputTokens,
                MaximumTrivia = limits.MaximumInputBytes, MaximumDiagnostics = 16, MaximumDelimiterDepth = limits.MaximumDepth,
                Timeout = TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds) };
            RustLexResult definitionLex = RustLexer.Lex(definition, "repeat-definition.rs", options, cancellationToken);
            state.Step();
            RustLexResult input = RustLexer.Lex(invocationBody, "repeat-invocation-body.rs", options, cancellationToken);
            state.Step();
            if (definitionLex.IsTruncated || input.IsTruncated || definitionLex.Tokens.Count + input.Tokens.Count > limits.MaximumInputTokens
                || definitionLex.Diagnostics.Concat(input.Diagnostics).Any(static item => item.Code is RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Aggregate lexical token/depth budget exceeded.", state.Steps);
            if (!definitionLex.IsSuccessful || !input.IsSuccessful) return Failure("RSMAC1002", "Malformed definition or invocation token tree.", state.Steps);
            P3MacroRuleParseResult parsed = P3MacroRuleParserPrototype.Parse(definition, limits, _ => state.Step(), cancellationToken);
            if (!parsed.IsSuccessful) return Failure(parsed.Diagnostic, parsed.Message, state.Steps);
            if (parsed.Rules.Count != 1 || parsed.Rules[0].Matcher.Count != 1)
                return Failure("RSMAC1001", "Only one rule with an entire single repetition matcher is supported.", state.Steps);
            P3MacroRuleNode repeat = parsed.Rules[0].Matcher[0];
            if (repeat.Kind != "repetition" || repeat.Children.Count != 1 || repeat.Children[0] is not { Kind: "capture", Detail: "ident" } capture
                || repeat.Text is not ("*" or "+" or "?") || repeat.Detail is not ("" or ",") || repeat.Text == "?" && repeat.Detail.Length != 0)
                return Failure("RSMAC1001", "Nested/multiple/non-ident captures, semicolon separation and other matcher shapes remain unsupported.", state.Steps);
            var items = new List<P3MacroRepetitionItem>();
            bool commaSeparated = repeat.Detail == ",", expectIdentifier = true;
            IReadOnlyList<RustTokenTree> trees = input.TokenTrees;
            for (int index = 0; index < trees.Count && index < limits.MaximumInputTokens; index++)
            {
                state.Step(); RustTokenTree tree = trees[index];
                if (expectIdentifier)
                {
                    string text = invocationBody.Substring(tree.Span.Start, tree.Span.Length);
                    P3MacroMatchResult fragment = P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, text, new()
                    {
                        MaximumInputBytes = limits.MaximumInputBytes, MaximumInputTokens = limits.MaximumInputTokens,
                        MaximumDepth = limits.MaximumDepth, MaximumMatcherSteps = limits.MaximumMatcherSteps,
                        TimeoutMilliseconds = limits.TimeoutMilliseconds,
                    }, _ => state.Step(), cancellationToken);
                    if (!fragment.IsSuccessful) return Failure(fragment.Diagnostic == "RSMAC1005" ? "RSMAC1005" : "RSMAC1004", "Repetition item failed the frozen ident fragment.", state.Steps);
                    items.Add(new(items.Count, text, tree.Span, capture.Span));
                    if (repeat.Text == "?" && items.Count > 1) return Failure("RSMAC1004", "Optional repetition accepts at most one item.", state.Steps);
                    expectIdentifier = !commaSeparated;
                }
                else
                {
                    if (tree.Token is not { Kind: RustTokenKind.Punctuation, Text: "," })
                        return Failure("RSMAC1004", "Comma required between repetition items.", state.Steps);
                    expectIdentifier = true;
                }
            }
            if (commaSeparated && trees.Count > 0 && expectIdentifier) return Failure("RSMAC1004", "Trailing separator is outside the frozen pattern.", state.Steps);
            if (repeat.Text == "+" && items.Count == 0) return Failure("RSMAC1004", "Plus repetition requires a nonempty item list.", state.Steps);
            state.Step();
            return new(true, string.Empty, string.Empty, parsed.Name, capture.Text, repeat.Text, repeat.Detail,
                parsed.Rules[0].Span, repeat.Span, items.AsReadOnly(), state.Steps);
        }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, state.Steps); }
    }
    private static P3MacroRepetitionResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, string.Empty, string.Empty, string.Empty, null, null, Array.Empty<P3MacroRepetitionItem>(), steps);
    private sealed class State(P3MacroBuiltinLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        internal int Steps { get; private set; }
        internal void Step() { cancellationToken.ThrowIfCancellationRequested(); if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds) throw new TimeoutException("Repetition aggregate step/deadline budget exceeded."); Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested(); }
    }
}
public sealed record P3MacroRepetitionItem(int Index, string Text, TextSpan InputSpan, TextSpan DefinitionCaptureSpan);
public sealed record P3MacroRepetitionResult(bool IsSuccessful, string Diagnostic, string Message, string MacroName,
    string CaptureName, string Operator, string Separator, TextSpan? RuleSpan, TextSpan? RepetitionSpan,
    IReadOnlyList<P3MacroRepetitionItem> Items, int MatcherSteps)
{
    public int ItemCount => Items.Count;
    public bool FormalAcceptanceAllowed { get; }
    public int ProductionMacroExpansionsExecuted { get; }
}
