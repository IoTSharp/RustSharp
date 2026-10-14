using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroConcatPrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 concat prototype accepts zero strings", () => OutputAsync("concat!()", "")),
        new("P3 concat prototype accepts one string", () => OutputAsync("concat!(\"one\")", "one")),
        new("P3 concat prototype concatenates many and trailing comma", () => OutputAsync("concat!(\"a\",\"b\",)", "ab")),
        new("P3 concat prototype ignores comments around commas", () => OutputAsync("concat!(\"a\" /*x*/, /*y*/ \"b\")", "ab")),
        new("P3 concat prototype decodes simple Rust escapes", () => OutputAsync("concat!(\"\\n\\r\\t\\0\\\\\\\"\\'\")", "\n\r\t\0\\\"'")),
        new("P3 concat prototype decodes ASCII hex escapes", () => OutputAsync("concat!(\"\\x00\\x41\\x7f\")", "\0A\u007f")),
        new("P3 concat prototype decodes Unicode scalar escapes", () => OutputAsync("concat!(\"\\u{1F_600}\\u{4e2d}\")", "😀中")),
        new("P3 concat prototype preserves literal UTF8 characters", () => OutputAsync("concat!(\"中😀\",\"文\")", "中😀文")),
        new("P3 concat prototype does not decode an escaped backslash twice", () => OutputAsync("concat!(\"\\\\x41\")", "\\x41")),
        new("P3 concat prototype normalizes unescaped CRLF", () => OutputAsync("concat!(\"a\r\nb\")", "a\nb")),
        new("P3 concat prototype removes ASCII string continuation whitespace", () => OutputAsync("concat!(\"a\\\n \t\r\nb\")", "ab")),
        new("P3 concat prototype preserves nonASCII continuation whitespace", () => OutputAsync("concat!(\"a\\\n \u00a0b\")", "a\u00a0b")),
        new("P3 concat prototype removes CRLF continuation", () => OutputAsync("concat!(\"a\\\r\n b\")", "ab")),
        new("P3 concat prototype retains original literal and argument spans", SpansAsync),
        new("P3 concat prototype rejects numeric literal", () => RejectAsync("concat!(1)", "RSMAC1003")),
        new("P3 concat prototype rejects character literal", () => RejectAsync("concat!('x')", "RSMAC1003")),
        new("P3 concat prototype rejects raw string literal", () => RejectAsync("concat!(r\"raw\")", "RSMAC1003")),
        new("P3 concat prototype rejects byte string literal", () => RejectAsync("concat!(b\"bytes\")", "RSMAC1003")),
        new("P3 concat prototype rejects suffixed cooked string", () => RejectAsync("concat!(\"x\"tag)", "RSMAC1003")),
        new("P3 concat prototype rejects missing comma", () => RejectAsync("concat!(\"a\" \"b\")", "RSMAC1003")),
        new("P3 concat prototype rejects repeated comma", () => RejectAsync("concat!(\"a\",,)", "RSMAC1003")),
        new("P3 concat prototype rejects comma without literal", () => RejectAsync("concat!(,)", "RSMAC1003")),
        new("P3 concat prototype rejects malformed escape", () => RejectAsync("concat!(\"\\q\")", "RSMAC1002")),
        new("P3 concat prototype rejects nonASCII hex escape", () => RejectAsync("concat!(\"\\x80\")", "RSMAC1002")),
        new("P3 concat prototype rejects surrogate Unicode escape", () => RejectAsync("concat!(\"\\u{d800}\")", "RSMAC1002")),
        new("P3 concat prototype rejects Unicode scalar overflow", () => RejectAsync("concat!(\"\\u{110000}\")", "RSMAC1002")),
        new("P3 concat prototype rejects malformed delimiter", () => RejectAsync("concat!(\"x\"]", "RSMAC1002")),
        new("P3 concat prototype rejects nested macro arguments", () => RejectAsync("concat!(stringify!(x))", "RSMAC1003")),
        new("P3 concat prototype enforces byte exact and over boundary", ByteLimitAsync),
        new("P3 concat prototype enforces token exact and over boundary", TokenLimitAsync),
        new("P3 concat prototype enforces step exact and over boundary", StepLimitAsync),
        new("P3 concat prototype propagates pre entry cancellation", CancellationAsync),
        new("P3 concat prototype propagates mid decoding cancellation", MidCancellationAsync),
    ];
    private static Task OutputAsync(string input, string expected) { Success(P3MacroConcatPrototype.Concat(input), expected); return Task.CompletedTask; }
    private static Task RejectAsync(string input, string diagnostic) { Rejected(P3MacroConcatPrototype.Concat(input), diagnostic); return Task.CompletedTask; }
    private static Task SpansAsync()
    {
        const string input = "concat!(\"a\", \"b\",)";
        P3MacroConcatResult result = P3MacroConcatPrototype.Concat(input);
        Success(result, "ab");
        AssertEx.Equal(8, result.ArgumentSpan!.Value.Start);
        AssertEx.Equal(2, result.LiteralSpans.Count);
        AssertEx.Equal("\"a\"", input.Substring(result.LiteralSpans[0].Start, result.LiteralSpans[0].Length));
        AssertEx.Equal("\"b\"", input.Substring(result.LiteralSpans[1].Start, result.LiteralSpans[1].Length));
        return Task.CompletedTask;
    }
    private static Task ByteLimitAsync()
    {
        const string input = "concat!(\"x\")";
        Success(P3MacroConcatPrototype.Concat(input, new() { MaximumInputBytes = input.Length }), "x");
        Rejected(P3MacroConcatPrototype.Concat(input, new() { MaximumInputBytes = input.Length - 1 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task TokenLimitAsync()
    {
        Success(P3MacroConcatPrototype.Concat("concat!(\"x\")", new() { MaximumInputTokens = 5 }), "x");
        Rejected(P3MacroConcatPrototype.Concat("concat!(\"x\")", new() { MaximumInputTokens = 4 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task StepLimitAsync()
    {
        P3MacroConcatResult first = P3MacroConcatPrototype.Concat("concat!(\"a\")");
        Success(first, "a");
        Success(P3MacroConcatPrototype.Concat("concat!(\"a\")", new() { MaximumMatcherSteps = first.MatcherSteps }), "a");
        Rejected(P3MacroConcatPrototype.Concat("concat!(\"a\")", new() { MaximumMatcherSteps = first.MatcherSteps - 1 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task CancellationAsync()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P3MacroConcatPrototype.Concat("concat!(\"a\")", cancellationToken: canceled.Token));
        return Task.CompletedTask;
    }
    private static Task MidCancellationAsync()
    {
        using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int observed = 0;
        AssertEx.Throws<OperationCanceledException>(() => P3MacroConcatPrototype.Concat("concat!(\"alphabet\")",
            progress: steps => { observed = steps; if (steps == 5) canceled.Cancel(); }, cancellationToken: canceled.Token));
        AssertEx.Equal(5, observed, "Cancellation interrupted normalization before decoder output could publish.");
        return Task.CompletedTask;
    }
    private static void Success(P3MacroConcatResult result, string expected)
    {
        AssertEx.True(result.IsSuccessful, result.Message); AssertEx.Equal(expected, result.Output);
        AssertEx.True(result.ArgumentSpan is not null, "Successful concat retains original argument span.");
        AssertEx.False(result.FormalAcceptanceAllowed, "Prototype cannot accept P3."); AssertEx.Equal(0, result.ProductionMacroExpansionsExecuted);
    }
    private static void Rejected(P3MacroConcatResult result, string diagnostic)
    {
        AssertEx.False(result.IsSuccessful, "Malformed or excluded input must reject."); AssertEx.Equal(diagnostic, result.Diagnostic);
        AssertEx.Equal(string.Empty, result.Output); AssertEx.Equal(0, result.LiteralSpans.Count);
        AssertEx.True(result.ArgumentSpan is null, "Rejected input exposes no partial spans or output.");
    }
}
