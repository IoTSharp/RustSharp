using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated matching of invocation bodies from macros-v1.candidate; no expansion or compiler integration.</summary>
public static class P3MacroMatcherPrototype
{
    public static P3MacroMatchResult Match(P3MacroPattern pattern, string input,
        P3MacroMatchLimits? limits = null, Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new();
        if (!limits.IsWithinCandidate()) return Failure("RSMAC1090", "Prototype limits must be positive and no larger than the frozen candidate.", 0);
        if (!Enum.IsDefined(pattern)) return Failure("RSMAC1001", "Unsupported prototype pattern.", 0);
        if (input.Length > limits.MaximumInputBytes || Encoding.UTF8.GetByteCount(input) > limits.MaximumInputBytes)
            return Failure("RSMAC1005", "Input byte budget exceeded.", 0);
        var clock = Stopwatch.StartNew();
        var state = new MatchState(limits, clock, progress, cancellationToken);
        try
        {
            RustLexResult lex = RustLexer.Lex(input, "macro-prototype.rs", new RustLexerOptions
            {
                MaximumSourceLength = limits.MaximumInputBytes,
                MaximumTokens = limits.MaximumInputTokens,
                MaximumTrivia = limits.MaximumInputBytes,
                MaximumDiagnostics = 16,
                MaximumDelimiterDepth = limits.MaximumDepth,
                Timeout = TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds),
            }, cancellationToken);
            state.Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static diagnostic => diagnostic.Code is RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Lexer resource budget exceeded.", state.Steps);
            if (!lex.IsSuccessful) return Failure("RSMAC1002", "Malformed lexical token or delimiter tree.", state.Steps);
            if (lex.Tokens.Count > limits.MaximumInputTokens) return Failure("RSMAC1005", "Token budget exceeded.", state.Steps);
            IReadOnlyList<RustTokenTree> trees = lex.TokenTrees;
            var captures = new List<P3MacroCapture>();
            bool single = pattern is P3MacroPattern.Identifier or P3MacroPattern.TokenTree;
            if (single)
            {
                if (trees.Count != 1) return Failure("RSMAC1003", "Exactly one fragment is required.", state.Steps);
                if (pattern == P3MacroPattern.Identifier && !state.IsIdentifier(trees[0]))
                    return Failure("RSMAC1003", "Expected one non-keyword ASCII identifier.", state.Steps);
                captures.Add(Capture(input, trees[0]));
            }
            else
            {
                bool commaSeparated = pattern is P3MacroPattern.CommaStarIdentifiers or P3MacroPattern.CommaPlusIdentifiers;
                bool expectIdentifier = true;
                for (int index = 0; index < trees.Count && index < limits.MaximumInputTokens; index++)
                {
                    state.Step();
                    RustTokenTree tree = trees[index];
                    if (expectIdentifier)
                    {
                        if (!state.IsIdentifier(tree)) return Failure("RSMAC1004", "Repetition requires an identifier at this position.", state.Steps);
                        captures.Add(Capture(input, tree));
                        expectIdentifier = !commaSeparated;
                    }
                    else
                    {
                        if (tree.Token is not { Kind: RustTokenKind.Punctuation, Text: "," })
                            return Failure("RSMAC1004", "Only the declared comma separator is accepted.", state.Steps);
                        expectIdentifier = true;
                    }
                    if (pattern == P3MacroPattern.OptionalIdentifier && captures.Count > 1)
                        return Failure("RSMAC1004", "Optional repetition admits at most one capture.", state.Steps);
                }
                if (commaSeparated && trees.Count > 0 && expectIdentifier)
                    return Failure("RSMAC1004", "Trailing comma is outside this frozen pattern.", state.Steps);
                if (pattern == P3MacroPattern.CommaPlusIdentifiers && captures.Count == 0)
                    return Failure("RSMAC1004", "Plus repetition requires at least one capture.", state.Steps);
            }
            state.Step();
            return new(true, string.Empty, string.Empty, captures.AsReadOnly(), state.Steps);
        }
        catch (TimeoutException exception)
        {
            return Failure("RSMAC1005", exception.Message, state.Steps);
        }
    }

    private static P3MacroCapture Capture(string input, RustTokenTree tree) =>
        new(input.Substring(tree.Span.Start, tree.Span.Length), tree.Span, tree.IsDelimited);

    private static P3MacroMatchResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, Array.Empty<P3MacroCapture>(), steps);

    private sealed class MatchState(P3MacroMatchLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken token)
    {
        internal int Steps { get; private set; }
        internal void Step()
        {
            token.ThrowIfCancellationRequested();
            if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds)
                throw new TimeoutException("Prototype matcher step/deadline budget exceeded.");
            Steps++;
            progress?.Invoke(Steps);
            token.ThrowIfCancellationRequested();
        }
        internal bool IsIdentifier(RustTokenTree tree)
        {
            Step();
            if (tree.Token is not { Kind: RustTokenKind.Identifier } tokenValue || tokenValue.Text == "_") return false;
            string text = tokenValue.Text;
            for (int index = 0; index < text.Length && index < limits.MaximumInputBytes; index++)
            {
                Step();
                char value = text[index];
                if (!(char.IsAsciiLetter(value) || value == '_' || (index > 0 && char.IsAsciiDigit(value)))) return false;
            }
            return text.Length > 0;
        }
    }
}

public enum P3MacroPattern { Identifier, TokenTree, CommaStarIdentifiers, CommaPlusIdentifiers, OptionalIdentifier }

public sealed record P3MacroMatchLimits
{
    public int MaximumInputBytes { get; init; } = 65_536;
    public int MaximumInputTokens { get; init; } = 4_096;
    public int MaximumDepth { get; init; } = 32;
    public int MaximumMatcherSteps { get; init; } = 100_000;
    public int TimeoutMilliseconds { get; init; } = 2_000;
    internal bool IsWithinCandidate() => MaximumInputBytes is > 0 and <= 65_536 && MaximumInputTokens is > 0 and <= 4_096
        && MaximumDepth is > 0 and <= 32 && MaximumMatcherSteps is > 0 and <= 100_000 && TimeoutMilliseconds is > 0 and <= 2_000;
}

public sealed record P3MacroCapture(string Text, TextSpan Span, bool IsDelimited);
public sealed record P3MacroMatchResult(bool IsSuccessful, string Diagnostic, string Message,
    IReadOnlyList<P3MacroCapture> Captures, int MatcherSteps)
{
    public bool FormalAcceptanceAllowed { get; }
    public int MacroExpansionsExecuted { get; }
}
