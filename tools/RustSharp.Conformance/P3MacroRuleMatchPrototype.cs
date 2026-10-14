using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated ordered matching of parsed flat rules; does not substitute, expand or compile templates.</summary>
public static class P3MacroRuleMatchPrototype
{
    public static P3MacroRuleMatchResult Match(string definition, string invocationBody, P3MacroBuiltinLimits? limits = null,
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
            RustLexResult definitionLex = RustLexer.Lex(definition, "rule-definition.rs", options, cancellationToken);
            state.Step();
            RustLexResult bodyLex = RustLexer.Lex(invocationBody, "rule-invocation-body.rs", options, cancellationToken);
            state.Step();
            if (definitionLex.IsTruncated || bodyLex.IsTruncated || definitionLex.Tokens.Count + bodyLex.Tokens.Count > limits.MaximumInputTokens
                || definitionLex.Diagnostics.Concat(bodyLex.Diagnostics).Any(static issue => issue.Code is RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Aggregate lexical token/depth budget exceeded.", state.Steps);
            if (!definitionLex.IsSuccessful || !bodyLex.IsSuccessful) return Failure("RSMAC1002", "Malformed definition or invocation token tree.", state.Steps);
            P3MacroRuleParseResult parsed = P3MacroRuleParserPrototype.Parse(definition, limits, _ => state.Step(), cancellationToken);
            if (!parsed.IsSuccessful) return Failure(parsed.Diagnostic, parsed.Message, state.Steps);
            foreach (P3MacroParsedRule rule in parsed.Rules)
            {
                state.Step();
                foreach (P3MacroRuleNode node in rule.Matcher)
                {
                    state.Step();
                    if (node.Kind is not ("capture" or "token"))
                        return Failure("RSMAC1001", "Repetition and matcher group nodes remain outside this flat matcher prototype.", state.Steps);
                }
            }
            IReadOnlyList<RustTokenTree> input = bodyLex.TokenTrees;
            for (int arm = 0; arm < parsed.Rules.Count && arm < limits.MaximumInputTokens; arm++)
            {
                state.Step(); P3MacroParsedRule rule = parsed.Rules[arm];
                var captures = new List<P3MacroRuleMatchCapture>();
                bool matched = rule.Matcher.Count == input.Count;
                if (!matched) continue;
                for (int index = 0; index < rule.Matcher.Count && index < limits.MaximumInputTokens; index++)
                {
                    state.Step(); P3MacroRuleNode node = rule.Matcher[index]; RustTokenTree tree = input[index];
                    if (node.Kind == "token") { if (tree.Token?.Text != node.Text) { matched = false; break; } continue; }
                    string text = invocationBody.Substring(tree.Span.Start, tree.Span.Length);
                    if (node.Detail == "literal") matched = state.IsLiteral(tree);
                    else
                    {
                        P3MacroPattern pattern = node.Detail == "ident" ? P3MacroPattern.Identifier : P3MacroPattern.TokenTree;
                        P3MacroMatchResult fragment = P3MacroMatcherPrototype.Match(pattern, text, new()
                        {
                            MaximumInputBytes = limits.MaximumInputBytes, MaximumInputTokens = limits.MaximumInputTokens,
                            MaximumDepth = limits.MaximumDepth, MaximumMatcherSteps = limits.MaximumMatcherSteps,
                            TimeoutMilliseconds = limits.TimeoutMilliseconds,
                        }, _ => state.Step(), cancellationToken);
                        if (!fragment.IsSuccessful && fragment.Diagnostic == "RSMAC1005") return Failure(fragment.Diagnostic, fragment.Message, state.Steps);
                        matched = fragment.IsSuccessful;
                    }
                    if (!matched) break;
                    captures.Add(new(node.Text, node.Detail, node.Span, tree.Span, text, tree.IsDelimited));
                }
                if (matched)
                {
                    state.Step();
                    return new(true, string.Empty, string.Empty, parsed.Name, arm, rule.Span, captures.AsReadOnly(), state.Steps);
                }
            }
            return Failure("RSMAC1003", "No flat rule consumes the complete invocation body.", state.Steps);
        }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, state.Steps); }
    }
    private static P3MacroRuleMatchResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, null, null, Array.Empty<P3MacroRuleMatchCapture>(), steps);
    private sealed class State(P3MacroBuiltinLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        internal int Steps { get; private set; }
        internal void Step() { cancellationToken.ThrowIfCancellationRequested(); if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds) throw new TimeoutException("Rule matcher aggregate step/deadline budget exceeded."); Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested(); }
        internal bool IsLiteral(RustTokenTree tree)
        {
            Step();
            if (tree.Token is not { LiteralSuffix: null } token) return false;
            if (token.Kind == RustTokenKind.StringLiteral) return true;
            if (token.Kind != RustTokenKind.IntegerLiteral || token.Text.Length == 0 || !char.IsAsciiDigit(token.Text[0])) return false;
            for (int index = 0; index < token.Text.Length && index < limits.MaximumInputBytes; index++)
            { Step(); if (!char.IsAsciiDigit(token.Text[index]) && token.Text[index] != '_') return false; }
            return true;
        }
    }
}
public sealed record P3MacroRuleMatchCapture(string Name, string Fragment, TextSpan DefinitionSpan, TextSpan InputSpan, string Text, bool IsDelimited);
public sealed record P3MacroRuleMatchResult(bool IsSuccessful, string Diagnostic, string Message, string MacroName,
    int? SelectedRule, TextSpan? RuleSpan, IReadOnlyList<P3MacroRuleMatchCapture> Captures, int MatcherSteps)
{ public bool FormalAcceptanceAllowed { get; } public int ProductionMacroExpansionsExecuted { get; } }
