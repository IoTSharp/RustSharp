using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated one-pass substitution of a genuinely matched flat rule; never recursively expands or compiles output.</summary>
public static class P3MacroTemplatePrototype
{
    public static P3MacroTemplateResult Substitute(string definition, string invocationBody, P3MacroTemplateLimits? limits = null,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(invocationBody);
        cancellationToken.ThrowIfCancellationRequested(); limits ??= new();
        if (limits.Input is null || !limits.Input.IsWithinCandidate() || limits.MaximumOutputBytes is <= 0 or > 65_536
            || limits.MaximumOutputTokens <= 0 || limits.MaximumOutputTokens > limits.Input.MaximumExpandedTokens
            || limits.MaximumOutputDepth is <= 0 or > 32)
            return Failure("RSMAC1090", "Invalid input/output prototype resource limits.", 0);
        var emitter = new Emitter(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            P3MacroRuleMatchResult match = P3MacroRuleMatchPrototype.Match(definition, invocationBody, limits.Input, _ => emitter.Step(), cancellationToken);
            if (!match.IsSuccessful) return Failure(match.Diagnostic, match.Message, emitter.Steps);
            P3MacroRuleParseResult parsed = P3MacroRuleParserPrototype.Parse(definition, limits.Input, _ => emitter.Step(), cancellationToken);
            if (!parsed.IsSuccessful) return Failure(parsed.Diagnostic, parsed.Message, emitter.Steps);
            emitter.Emit(parsed.Rules[match.SelectedRule!.Value].Template, match.Captures, 0);
            emitter.Step();
            if (emitter.Depth != 0) return Failure("RSMAC1002", "Substitution produced unmatched delimiters.", emitter.Steps);
            return new(true, string.Empty, string.Empty, emitter.Text, emitter.Tokens.AsReadOnly(), match.Captures, match.SelectedRule, emitter.Steps);
        }
        catch (SubstitutionFailure exception) { return Failure(exception.Code, exception.Message, emitter.Steps); }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, emitter.Steps); }
    }
    private static P3MacroTemplateResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, Array.Empty<P3MacroOutputToken>(), Array.Empty<P3MacroRuleMatchCapture>(), null, steps);
    private sealed class SubstitutionFailure(string code, string message) : Exception(message) { internal string Code { get; } = code; }
    private sealed class Emitter(P3MacroTemplateLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        private readonly StringBuilder _text = new();
        private int _bytes;
        internal List<P3MacroOutputToken> Tokens { get; } = [];
        internal string Text => _text.ToString();
        internal int Steps { get; private set; }
        internal int Depth { get; private set; }
        internal void Step() { cancellationToken.ThrowIfCancellationRequested(); if (Steps >= limits.Input.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.Input.TimeoutMilliseconds) throw new TimeoutException("Template aggregate step/deadline budget exceeded."); Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested(); }
        private RustLexResult Lex(string source)
        {
            Step();
            RustLexResult lex = RustLexer.Lex(source, "template-token-prototype.rs", new RustLexerOptions
            {
                MaximumSourceLength = limits.Input.MaximumInputBytes, MaximumTokens = limits.Input.MaximumInputTokens,
                MaximumTrivia = limits.Input.MaximumInputBytes, MaximumDiagnostics = 16, MaximumDelimiterDepth = limits.Input.MaximumDepth,
                Timeout = TimeSpan.FromMilliseconds(limits.Input.TimeoutMilliseconds),
            }, cancellationToken);
            Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static item => item.Code is RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.DelimiterDepthLimit)) throw new SubstitutionFailure("RSMAC1005", "Captured token lexer resource budget exceeded.");
            if (!lex.IsSuccessful) throw new SubstitutionFailure("RSMAC1002", "Captured or template token failed lexical validation.");
            return lex;
        }
        internal void Emit(IReadOnlyList<P3MacroRuleNode> nodes, IReadOnlyList<P3MacroRuleMatchCapture> captures, int level)
        {
            Step();
            if (level > limits.MaximumOutputDepth) throw new SubstitutionFailure("RSMAC1005", "Output group recursion budget exceeded.");
            foreach (P3MacroRuleNode node in nodes)
            {
                Step();
                if (node.Kind == "reference")
                {
                    P3MacroRuleMatchCapture? capture = null;
                    foreach (P3MacroRuleMatchCapture item in captures) { Step(); if (item.Name == node.Text) { capture = item; break; } }
                    if (capture is null) throw new SubstitutionFailure("RSMAC1004", "Template reference has no matched capture.");
                    RustLexResult lex = Lex(capture.Text);
                    foreach (RustToken token in lex.Tokens)
                    {
                        Step();
                        Add(token.Text, token.Kind, node.Span, new(capture.InputSpan.Start + token.Span.Start, token.Span.Length), capture.Name);
                    }
                }
                else if (node.Kind == "token")
                {
                    RustLexResult lex = Lex(node.Text);
                    if (lex.Tokens.Count != 1) throw new SubstitutionFailure("RSMAC1002", "Ordinary template leaf must remain one lexical token.");
                    Add(node.Text, lex.Tokens[0].Kind, node.Span, null, null);
                }
                else if (node.Kind == "group")
                {
                    (string open, string close) = node.Text switch
                    {
                        "Parenthesis" => ("(", ")"), "Bracket" => ("[", "]"), "Brace" => ("{", "}"),
                        _ => throw new SubstitutionFailure("RSMAC1001", "Unsupported template delimiter kind."),
                    };
                    Add(open, RustTokenKind.OpenDelimiter, new(node.Span.Start, 1), null, null);
                    Emit(node.Children, captures, level + 1);
                    Add(close, RustTokenKind.CloseDelimiter, new(node.Span.End - 1, 1), null, null);
                }
                else throw new SubstitutionFailure("RSMAC1001", "Template repetition and unsupported node kinds are not implemented.");
            }
        }
        private void Add(string text, RustTokenKind kind, TextSpan templateSpan, TextSpan? inputSpan, string? captureName)
        {
            Step();
            int bytes = Encoding.UTF8.GetByteCount(text) + (Tokens.Count == 0 ? 0 : 1);
            if (Tokens.Count >= limits.MaximumOutputTokens || bytes > limits.MaximumOutputBytes - _bytes)
                throw new SubstitutionFailure("RSMAC1005", "Output token/UTF8 byte budget exceeded.");
            if (kind == RustTokenKind.OpenDelimiter && ++Depth > limits.MaximumOutputDepth)
                throw new SubstitutionFailure("RSMAC1005", "Output delimiter depth budget exceeded.");
            if (kind == RustTokenKind.CloseDelimiter && --Depth < 0) throw new SubstitutionFailure("RSMAC1002", "Output delimiter underflow.");
            if (Tokens.Count > 0) _text.Append(' ');
            int start = _text.Length; _text.Append(text); _bytes += bytes;
            Tokens.Add(new(text, kind, new(start, text.Length), templateSpan, inputSpan, captureName));
        }
    }
}
public sealed record P3MacroTemplateLimits
{
    public P3MacroBuiltinLimits Input { get; init; } = new();
    public int MaximumOutputBytes { get; init; } = 65_536;
    public int MaximumOutputTokens { get; init; } = 16_384;
    public int MaximumOutputDepth { get; init; } = 32;
}
public sealed record P3MacroOutputToken(string Text, RustTokenKind Kind, TextSpan OutputSpan, TextSpan TemplateSpan, TextSpan? InputSpan, string? CaptureName);
public sealed record P3MacroTemplateResult(bool IsSuccessful, string Diagnostic, string Message, string Output,
    IReadOnlyList<P3MacroOutputToken> Tokens, IReadOnlyList<P3MacroRuleMatchCapture> Captures, int? SelectedRule, int MatcherSteps)
{ public bool FormalAcceptanceAllowed { get; } public int ProductionMacroExpansionsExecuted { get; } }
