using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>One-level ident item-list substitution into one same-operator reference repetition; no recursive expansion.</summary>
public static class P3MacroRepeatedTemplatePrototype
{
    public static P3MacroRepeatedTemplateResult Substitute(string definition, string invocationBody, P3MacroTemplateLimits? limits = null,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(invocationBody); cancellationToken.ThrowIfCancellationRequested(); limits ??= new();
        if (limits.Input is null || !limits.Input.IsWithinCandidate() || limits.MaximumOutputBytes is <= 0 or > 65_536
            || limits.MaximumOutputTokens <= 0 || limits.MaximumOutputTokens > limits.Input.MaximumExpandedTokens || limits.MaximumOutputDepth is <= 0 or > 32)
            return Failure("RSMAC1090", "Invalid input/output prototype budgets.", 0);
        var emitter = new Emitter(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            P3MacroRepetitionResult match = P3MacroRepetitionPrototype.Match(definition, invocationBody, limits.Input, _ => emitter.Step(), cancellationToken);
            if (!match.IsSuccessful) return Failure(match.Diagnostic, match.Message, emitter.Steps);
            P3MacroRuleParseResult parsed = P3MacroRuleParserPrototype.Parse(definition, limits.Input, _ => emitter.Step(), cancellationToken);
            if (!parsed.IsSuccessful) return Failure(parsed.Diagnostic, parsed.Message, emitter.Steps);
            IReadOnlyList<P3MacroRuleNode> nodes = parsed.Rules[0].Template;
            if (nodes.Count != 1 || nodes[0] is not { Kind: "repetition" } repeat || repeat.Children.Count != 1
                || repeat.Children[0] is not { Kind: "reference" } reference || reference.Text != match.CaptureName || repeat.Detail is not ("" or ","))
                return Failure("RSMAC1001", "Only one whole template repetition with one corresponding reference and optional comma is implemented.", emitter.Steps);
            if (repeat.Text != match.Operator || repeat.Text == "+" && match.ItemCount == 0 || repeat.Text == "?" && match.ItemCount > 1)
                return Failure("RSMAC1004", "Matcher/template operator or item cardinality mismatch.", emitter.Steps);
            string templateText = definition.Substring(repeat.Span.Start, repeat.Span.Length);
            RustLexResult templateLex = emitter.Lex(templateText);
            TextSpan? separatorSpan = null;
            if (repeat.Detail == ",")
            {
                if (templateLex.TokenTrees.Count != 4 || templateLex.TokenTrees[2].Token?.Text != ",")
                    return Failure("RSMAC1002", "Template separator token provenance is invalid.", emitter.Steps);
                TextSpan span = templateLex.TokenTrees[2].Span; separatorSpan = new(repeat.Span.Start + span.Start, span.Length);
            }
            foreach (P3MacroRepetitionItem item in match.Items)
            {
                emitter.Step();
                if (item.Index > 0 && separatorSpan is { } commaSpan) emitter.Add(",", RustTokenKind.Punctuation, commaSpan, null, null);
                RustLexResult lex = emitter.Lex(item.Text);
                if (lex.Tokens.Count != 1 || lex.Tokens[0].Kind != RustTokenKind.Identifier || lex.Tokens[0].Span.Length != item.Text.Length)
                    return Failure("RSMAC1003", "Matched item no longer identifies one lexical identifier.", emitter.Steps);
                emitter.Add(item.Text, lex.Tokens[0].Kind, reference.Span, item.InputSpan, match.CaptureName);
            }
            emitter.Step();
            return new(true, string.Empty, string.Empty, emitter.Text, emitter.Tokens.AsReadOnly(), match.Items, repeat.Span, emitter.Steps);
        }
        catch (OutputFailure exception) { return Failure(exception.Code, exception.Message, emitter.Steps); }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, emitter.Steps); }
    }
    private static P3MacroRepeatedTemplateResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, Array.Empty<P3MacroOutputToken>(), Array.Empty<P3MacroRepetitionItem>(), null, steps);
    private sealed class OutputFailure(string code, string message) : Exception(message) { internal string Code { get; } = code; }
    private sealed class Emitter(P3MacroTemplateLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        private readonly StringBuilder _text = new(); private int _bytes;
        internal List<P3MacroOutputToken> Tokens { get; } = [];
        internal string Text => _text.ToString();
        internal int Steps { get; private set; }
        internal void Step() { cancellationToken.ThrowIfCancellationRequested(); if (Steps >= limits.Input.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.Input.TimeoutMilliseconds) throw new TimeoutException("Repeated template aggregate step/deadline budget exceeded."); Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested(); }
        internal RustLexResult Lex(string source)
        {
            Step(); RustLexResult lex = RustLexer.Lex(source, "repeated-template-token.rs", new RustLexerOptions
            { MaximumSourceLength = limits.Input.MaximumInputBytes, MaximumTokens = limits.Input.MaximumInputTokens,
                MaximumTrivia = limits.Input.MaximumInputBytes, MaximumDiagnostics = 16, MaximumDelimiterDepth = limits.Input.MaximumDepth,
                Timeout = TimeSpan.FromMilliseconds(limits.Input.TimeoutMilliseconds) }, cancellationToken);
            Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static item => item.Code is RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.DelimiterDepthLimit)) throw new OutputFailure("RSMAC1005", "Lexer resource budget exceeded.");
            if (!lex.IsSuccessful) throw new OutputFailure("RSMAC1002", "Malformed captured or template token."); return lex;
        }
        internal void Add(string text, RustTokenKind kind, TextSpan templateSpan, TextSpan? inputSpan, string? captureName)
        {
            Step();
            // The admitted shape can only emit identifiers and commas, so emitted delimiter depth is exactly zero.
            if (kind != RustTokenKind.Identifier && !(kind == RustTokenKind.Punctuation && text == ",")) throw new OutputFailure("RSMAC1001", "Unsupported output token kind.");
            int bytes = Encoding.UTF8.GetByteCount(text) + (Tokens.Count == 0 ? 0 : 1);
            if (Tokens.Count >= limits.MaximumOutputTokens || bytes > limits.MaximumOutputBytes - _bytes) throw new OutputFailure("RSMAC1005", "Output token/UTF8 byte budget exceeded.");
            if (Tokens.Count > 0) _text.Append(' '); int start = _text.Length; _text.Append(text); _bytes += bytes;
            Tokens.Add(new(text, kind, new(start, text.Length), templateSpan, inputSpan, captureName));
        }
    }
}
public sealed record P3MacroRepeatedTemplateResult(bool IsSuccessful, string Diagnostic, string Message, string Output,
    IReadOnlyList<P3MacroOutputToken> Tokens, IReadOnlyList<P3MacroRepetitionItem> Items, TextSpan? TemplateSpan, int MatcherSteps)
{ public bool FormalAcceptanceAllowed { get; } public int ProductionMacroExpansionsExecuted { get; } }
