using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated token-tree parser for one bounded declarative definition; never matches or expands an invocation.</summary>
public static class P3MacroRuleParserPrototype
{
    public static P3MacroRuleParseResult Parse(string source, P3MacroBuiltinLimits? limits = null,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); cancellationToken.ThrowIfCancellationRequested(); limits ??= new();
        if (!limits.IsWithinCandidate()) return Failure("RSMAC1090", "Invalid candidate resource limits.", 0);
        if (source.Length > limits.MaximumInputBytes || Encoding.UTF8.GetByteCount(source) > limits.MaximumInputBytes)
            return Failure("RSMAC1005", "Definition byte budget exceeded.", 0);
        var parser = new Parser(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            RustLexResult lex = RustLexer.Lex(source, "macro-rule-prototype.rs", new RustLexerOptions
            {
                MaximumSourceLength = limits.MaximumInputBytes, MaximumTokens = limits.MaximumInputTokens,
                MaximumTrivia = limits.MaximumInputBytes, MaximumDiagnostics = 16, MaximumDelimiterDepth = limits.MaximumDepth,
                Timeout = TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds),
            }, cancellationToken);
            parser.Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static item => item.Code is RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Lexer resource budget exceeded.", parser.Steps);
            if (!lex.IsSuccessful) return Failure("RSMAC1002", "Malformed lexical token or delimiter tree.", parser.Steps);
            IReadOnlyList<RustTokenTree> roots = lex.TokenTrees;
            if (roots.Count != 4 || roots[0].Token?.Text != "macro_rules" || roots[1].Token?.Text != "!"
                || roots[2].Token is not { Kind: RustTokenKind.Identifier } name
                || roots[3] is not RustDelimitedTokenTree { Delimiter: RustDelimiterKind.Brace } body)
                return Failure("RSMAC1003", "Expected one macro_rules! name { rules } definition.", parser.Steps);
            var rules = new List<P3MacroParsedRule>();
            IReadOnlyList<RustTokenTree> trees = body.Children;
            for (int index = 0; index < trees.Count && index < limits.MaximumInputTokens;)
            {
                parser.Step();
                if (index + 2 >= trees.Count || trees[index] is not RustDelimitedTokenTree { Delimiter: RustDelimiterKind.Parenthesis } matcher
                    || trees[index + 1].Token?.Text != "=>" || trees[index + 2] is not RustDelimitedTokenTree { Delimiter: RustDelimiterKind.Brace } template)
                    throw new ParseFailure("RSMAC1003", "Each prototype rule requires (matcher) => { template }.");
                var captures = new Dictionary<string, P3MacroParsedCapture>(StringComparer.Ordinal);
                ReadOnlyCollection<P3MacroRuleNode> pattern = parser.Nodes(matcher.Children, false, 0, captures);
                ReadOnlyCollection<P3MacroRuleNode> output = parser.Nodes(template.Children, true, 0, captures);
                rules.Add(new(new(matcher.Span.Start, template.Span.End - matcher.Span.Start), pattern, output, captures.Values.ToArray()));
                index += 3;
                if (index < trees.Count)
                {
                    if (trees[index].Token?.Text != ";") throw new ParseFailure("RSMAC1003", "Semicolon required between rules.");
                    index++;
                }
            }
            parser.Step();
            if (rules.Count == 0) throw new ParseFailure("RSMAC1003", "Empty rule inventory is outside this prototype.");
            return new(true, string.Empty, string.Empty, name.Text, name.Span, rules.AsReadOnly(), parser.Steps);
        }
        catch (ParseFailure exception) { return Failure(exception.Code, exception.Message, parser.Steps); }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, parser.Steps); }
    }
    private static P3MacroRuleParseResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, null, Array.Empty<P3MacroParsedRule>(), steps);
    private sealed class ParseFailure(string code, string message) : Exception(message) { internal string Code { get; } = code; }
    private sealed class Parser(P3MacroBuiltinLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        internal int Steps { get; private set; }
        internal void Step() { cancellationToken.ThrowIfCancellationRequested(); if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds) throw new TimeoutException("Rule parser step/deadline budget exceeded."); Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested(); }
        internal ReadOnlyCollection<P3MacroRuleNode> Nodes(IReadOnlyList<RustTokenTree> trees, bool template, int repetitionDepth,
            Dictionary<string, P3MacroParsedCapture> captures)
        {
            Step();
            if (repetitionDepth > limits.MaximumDepth) throw new ParseFailure("RSMAC1005", "Repetition depth budget exceeded.");
            var nodes = new List<P3MacroRuleNode>();
            for (int index = 0; index < trees.Count && index < limits.MaximumInputTokens; index++)
            {
                Step(); RustTokenTree tree = trees[index];
                if (tree.Token?.Text == "$")
                {
                    if (index + 1 >= trees.Count) throw new ParseFailure("RSMAC1004", "Missing metavariable or repetition after dollar.");
                    if (trees[index + 1] is RustDelimitedTokenTree { Delimiter: RustDelimiterKind.Parenthesis } repeated)
                    {
                        int operatorIndex = index + 2; string separator = string.Empty;
                        if (operatorIndex < trees.Count && trees[operatorIndex].Token?.Text is "," or ";") { separator = trees[operatorIndex].Token!.Text; operatorIndex++; }
                        string? op = operatorIndex < trees.Count ? trees[operatorIndex].Token?.Text : null;
                        if (op is not ("*" or "+" or "?") || op == "?" && separator.Length != 0)
                            throw new ParseFailure("RSMAC1004", "Invalid repetition operator or optional separator.");
                        if (repeated.Children.Count == 0) throw new ParseFailure("RSMAC1004", "Empty repetition body rejects.");
                        ReadOnlyCollection<P3MacroRuleNode> children = Nodes(repeated.Children, template, repetitionDepth + 1, captures);
                        nodes.Add(new("repetition", new(tree.Span.Start, trees[operatorIndex].Span.End - tree.Span.Start), op, separator, children));
                        index = operatorIndex; continue;
                    }
                    if (trees[index + 1].Token is not { Kind: RustTokenKind.Identifier } metavariable)
                        throw new ParseFailure("RSMAC1004", "Metavariable name must be an identifier.");
                    if (template)
                    {
                        if (!captures.TryGetValue(metavariable.Text, out P3MacroParsedCapture? capture) || capture.RepetitionDepth != repetitionDepth)
                            throw new ParseFailure("RSMAC1004", "Unknown capture or transcriber repetition depth mismatch.");
                        nodes.Add(new("reference", new(tree.Span.Start, metavariable.Span.End - tree.Span.Start), metavariable.Text, string.Empty, Array.Empty<P3MacroRuleNode>()));
                        index++; continue;
                    }
                    if (index + 3 >= trees.Count || trees[index + 2].Token?.Text != ":" || trees[index + 3].Token is not { Kind: RustTokenKind.Identifier } fragment)
                        throw new ParseFailure("RSMAC1004", "Capture requires a named fragment specifier.");
                    if (fragment.Text is not ("ident" or "literal" or "tt")) throw new ParseFailure("RSMAC1001", "Unsupported frozen fragment: " + fragment.Text);
                    var captureSpan = new TextSpan(tree.Span.Start, fragment.Span.End - tree.Span.Start);
                    if (!captures.TryAdd(metavariable.Text, new(metavariable.Text, fragment.Text, repetitionDepth, captureSpan)))
                        throw new ParseFailure("RSMAC1006", "Duplicate metavariable binding.");
                    nodes.Add(new("capture", captureSpan, metavariable.Text, fragment.Text, Array.Empty<P3MacroRuleNode>()));
                    index += 3; continue;
                }
                if (tree is RustDelimitedTokenTree group)
                    nodes.Add(new("group", group.Span, group.Delimiter.ToString(), string.Empty, Nodes(group.Children, template, repetitionDepth, captures)));
                else nodes.Add(new("token", tree.Span, tree.Token!.Text, string.Empty, Array.Empty<P3MacroRuleNode>()));
            }
            return nodes.AsReadOnly();
        }
    }
}
public sealed record P3MacroRuleNode(string Kind, TextSpan Span, string Text, string Detail, IReadOnlyList<P3MacroRuleNode> Children);
public sealed record P3MacroParsedCapture(string Name, string Fragment, int RepetitionDepth, TextSpan Span);
public sealed record P3MacroParsedRule(TextSpan Span, IReadOnlyList<P3MacroRuleNode> Matcher, IReadOnlyList<P3MacroRuleNode> Template, IReadOnlyList<P3MacroParsedCapture> Captures);
public sealed record P3MacroRuleParseResult(bool IsSuccessful, string Diagnostic, string Message, string Name, TextSpan? NameSpan, IReadOnlyList<P3MacroParsedRule> Rules, int MatcherSteps)
{ public bool FormalAcceptanceAllowed { get; } public int ProductionMacroExpansionsExecuted { get; } }
