using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated original-source line/column/file evaluation; no filesystem or compiler integration.</summary>
public static class P3MacroLocationPrototype
{
    public static P3MacroLocationResult Evaluate(SafeCoreSourceLocation location, P3MacroBuiltinLimits? limits = null,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new();
        if (!limits.IsWithinCandidate()) return Failure("RSMAC1090", "Invalid candidate resource limits.", 0);
        if (location?.Document is not { Text: not null, Path: not null } document)
            return Failure("RSMAC1003", "An original source document and span are required.", 0);
        string source = document.Text;
        TextSpan span = location.Span;
        if (span.Start < 0 || span.Length <= 0 || span.Start > source.Length - span.Length)
            return Failure("RSMAC1003", "Invalid original invocation span.", 0);
        if (source.Length > limits.MaximumInputBytes || document.Path.Length > limits.MaximumInputBytes
            || Encoding.UTF8.GetByteCount(source) > limits.MaximumInputBytes || Encoding.UTF8.GetByteCount(document.Path) > limits.MaximumInputBytes)
            return Failure("RSMAC1005", "Original source/path byte budget exceeded.", 0);
        var state = new State(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            string path = document.Path.Replace('\\', '/');
            if (path.Length == 0 || path.StartsWith('/') || path.Contains(':'))
                return Failure("RSMAC1003", "Source path must be workspace-relative.", state.Steps);
            int segmentStart = 0;
            for (int index = 0; index <= path.Length && index <= limits.MaximumInputBytes; index++)
            {
                state.Step();
                if (index < path.Length && path[index] != '/')
                {
                    if (char.IsControl(path[index])) return Failure("RSMAC1003", "Source path contains a control character.", state.Steps);
                    if (char.IsHighSurrogate(path[index]))
                    {
                        if (index + 1 >= path.Length || !char.IsLowSurrogate(path[index + 1]))
                            return Failure("RSMAC1003", "Source path contains an invalid Unicode scalar.", state.Steps);
                        index++;
                    }
                    else if (char.IsLowSurrogate(path[index])) return Failure("RSMAC1003", "Source path contains an unmatched low surrogate.", state.Steps);
                    continue;
                }
                ReadOnlySpan<char> segment = path.AsSpan(segmentStart, index - segmentStart);
                if (segment.IsEmpty || segment.SequenceEqual(".".AsSpan()) || segment.SequenceEqual("..".AsSpan()))
                    return Failure("RSMAC1003", "Empty, current-directory or traversal source-path component.", state.Steps);
                segmentStart = index + 1;
            }
            uint line = 1, column = 1, callLine = 0, callColumn = 0;
            for (int index = 0; index < source.Length && index < limits.MaximumInputBytes; index++)
            {
                state.Step();
                if (index == span.Start) { callLine = line; callColumn = column; }
                char value = source[index];
                if (char.IsHighSurrogate(value))
                {
                    if (index + 1 >= source.Length || !char.IsLowSurrogate(source[index + 1]))
                        return Failure("RSMAC1003", "Original source contains an invalid Unicode scalar.", state.Steps);
                    index++;
                }
                else if (char.IsLowSurrogate(value)) return Failure("RSMAC1003", "Original source contains an unmatched low surrogate.", state.Steps);
                if (value == '\r' && index + 1 < source.Length && source[index + 1] == '\n') { index++; line++; column = 1; }
                else if (value == '\n') { line++; column = 1; }
                else column++;
            }
            if (callLine == 0) return Failure("RSMAC1003", "Invocation span starts inside a Unicode scalar or newline pair.", state.Steps);
            string invocation = source.Substring(span.Start, span.Length);
            RustLexResult lex = RustLexer.Lex(invocation, path, new RustLexerOptions
            {
                MaximumSourceLength = limits.MaximumInputBytes, MaximumTokens = limits.MaximumInputTokens,
                MaximumTrivia = limits.MaximumInputBytes, MaximumDiagnostics = 16,
                MaximumDelimiterDepth = limits.MaximumDepth, Timeout = TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds),
            }, cancellationToken);
            state.Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static item => item.Code is RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Lexer resource budget exceeded.", state.Steps);
            if (!lex.IsSuccessful) return Failure("RSMAC1002", "Malformed lexical input or delimiter group.", state.Steps);
            IReadOnlyList<RustTokenTree> trees = lex.TokenTrees;
            if (trees.Count != 3 || trees[0].Token is not { Kind: RustTokenKind.Identifier, Span.Start: 0 } name
                || trees[1].Token is not { Kind: RustTokenKind.Punctuation, Text: "!" }
                || trees[2] is not RustDelimitedTokenTree { Delimiter: RustDelimiterKind.Parenthesis, IsClosed: true } group
                || group.Span.End != invocation.Length || group.Children.Count != 0)
                return Failure("RSMAC1003", "Expected one exact zero-argument location builtin invocation.", state.Steps);
            if (name.Text is not ("line" or "column" or "file")) return Failure("RSMAC1001", "Unsupported location builtin.", state.Steps);
            state.Step();
            var original = new SafeCoreSourceLocation(new(path, source, document.Bytes), span);
            return new(true, string.Empty, string.Empty, name.Text, name.Text == "line" ? callLine : name.Text == "column" ? callColumn : null,
                name.Text == "file" ? path : null, original, state.Steps);
        }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, state.Steps); }
    }
    private static P3MacroLocationResult Failure(string diagnostic, string message, int steps) =>
        new(false, diagnostic, message, string.Empty, null, null, null, steps);
    private sealed class State(P3MacroBuiltinLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        internal int Steps { get; private set; }
        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds)
                throw new TimeoutException("Location prototype step/deadline budget exceeded.");
            Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
public sealed record P3MacroLocationResult(bool IsSuccessful, string Diagnostic, string Message, string Builtin,
    uint? NumericValue, string? FileValue, SafeCoreSourceLocation? CallSite, int MatcherSteps)
{
    public bool FormalAcceptanceAllowed { get; }
    public int ProductionMacroExpansionsExecuted { get; }
}
