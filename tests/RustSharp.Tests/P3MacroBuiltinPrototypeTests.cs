using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroBuiltinPrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 builtin prototype stringify outputs frozen candidate token spacing", () => OutputAsync("stringify!(a + 1)", "a + 1")),
        new("P3 builtin prototype stringify accepts empty token tree", () => OutputAsync("stringify!()", "")),
        new("P3 builtin prototype stringify preserves punctuation token spelling", () => OutputAsync("stringify!(a::b=>c)", "a :: b => c")),
        new("P3 builtin prototype stringify preserves nested delimiters", () => OutputAsync("stringify!([a (b)])", "[ a ( b ) ]")),
        new("P3 builtin prototype stringify keeps literal spelling and escapes", () => OutputAsync("stringify!(\"a\\n\" r#\"raw\"#)", "\"a\\n\" r#\"raw\"#")),
        new("P3 builtin prototype stringify ignores comment and whitespace trivia", () => OutputAsync("stringify!(a/* note */+\n b)", "a + b")),
        new("P3 builtin prototype stringify does not expand nested invocations", () => OutputAsync("stringify!(concat!(\"a\",\"b\"))", "concat ! ( \"a\" , \"b\" )")),
        new("P3 builtin prototype stringify returns exact argument and token spans", SpansAsync),
        new("P3 builtin prototype stringify rejects mismatched delimiter", () => RejectAsync("stringify!([)", "RSMAC1002")),
        new("P3 builtin prototype stringify rejects unclosed invocation", () => RejectAsync("stringify!(a", "RSMAC1002")),
        new("P3 builtin prototype stringify rejects another builtin", () => RejectAsync("concat!(\"a\")", "RSMAC1003")),
        new("P3 builtin prototype stringify rejects extra trailing invocation", () => RejectAsync("stringify!(a) stringify!(b)", "RSMAC1003")),
        new("P3 builtin prototype stringify rejects missing bang", () => RejectAsync("stringify(a)", "RSMAC1003")),
        new("P3 builtin prototype stringify enforces input byte boundary", ByteLimitAsync),
        new("P3 builtin prototype stringify enforces lexical token boundary", TokenLimitAsync),
        new("P3 builtin prototype stringify enforces output token boundary", OutputLimitAsync),
        new("P3 builtin prototype stringify enforces matcher step boundary", StepLimitAsync),
        new("P3 builtin prototype stringify enforces delimiter depth boundary", DepthLimitAsync),
        new("P3 builtin prototype stringify propagates pre entry cancellation", CancellationAsync),
        new("P3 builtin prototype stringify propagates cancellation before output publication", MidCancellationAsync),
        new("P3 builtin prototype stringify rejects inflated candidate budgets", InvalidLimitsAsync),
    ];
    private static Task OutputAsync(string input, string expected)
    {
        Success(P3MacroBuiltinPrototype.Stringify(input), expected);
        return Task.CompletedTask;
    }
    private static Task RejectAsync(string input, string diagnostic)
    {
        Rejected(P3MacroBuiltinPrototype.Stringify(input), diagnostic);
        return Task.CompletedTask;
    }
    private static Task SpansAsync()
    {
        const string input = "  stringify!(alpha+1)";
        P3MacroBuiltinResult result = P3MacroBuiltinPrototype.Stringify(input);
        Success(result, "alpha + 1");
        AssertEx.Equal(13, result.ArgumentSpan!.Value.Start);
        AssertEx.Equal("alpha+1", input.Substring(result.ArgumentSpan.Value.Start, result.ArgumentSpan.Value.Length));
        AssertEx.Equal(3, result.TokenSpans.Count);
        AssertEx.Equal("alpha", input.Substring(result.TokenSpans[0].Start, result.TokenSpans[0].Length));
        AssertEx.Equal("+", input.Substring(result.TokenSpans[1].Start, result.TokenSpans[1].Length));
        return Task.CompletedTask;
    }
    private static Task ByteLimitAsync()
    {
        const string input = "stringify!(x)";
        Success(P3MacroBuiltinPrototype.Stringify(input, new() { MaximumInputBytes = input.Length }), "x");
        Rejected(P3MacroBuiltinPrototype.Stringify(input, new() { MaximumInputBytes = input.Length - 1 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task TokenLimitAsync()
    {
        Success(P3MacroBuiltinPrototype.Stringify("stringify!(x)", new() { MaximumInputTokens = 5 }), "x");
        Rejected(P3MacroBuiltinPrototype.Stringify("stringify!(x)", new() { MaximumInputTokens = 4 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task OutputLimitAsync()
    {
        Success(P3MacroBuiltinPrototype.Stringify("stringify!(a b)", new() { MaximumExpandedTokens = 2 }), "a b");
        Rejected(P3MacroBuiltinPrototype.Stringify("stringify!(a b)", new() { MaximumExpandedTokens = 1 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task StepLimitAsync()
    {
        P3MacroBuiltinResult first = P3MacroBuiltinPrototype.Stringify("stringify!(a)");
        Success(first, "a");
        Success(P3MacroBuiltinPrototype.Stringify("stringify!(a)", new() { MaximumMatcherSteps = first.MatcherSteps }), "a");
        Rejected(P3MacroBuiltinPrototype.Stringify("stringify!(a)", new() { MaximumMatcherSteps = first.MatcherSteps - 1 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task DepthLimitAsync()
    {
        Success(P3MacroBuiltinPrototype.Stringify("stringify!([a])", new() { MaximumDepth = 2 }), "[ a ]");
        Rejected(P3MacroBuiltinPrototype.Stringify("stringify!([(a)])", new() { MaximumDepth = 2 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task CancellationAsync()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P3MacroBuiltinPrototype.Stringify("stringify!(a)", cancellationToken: canceled.Token));
        return Task.CompletedTask;
    }
    private static Task MidCancellationAsync()
    {
        using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int observed = 0;
        AssertEx.Throws<OperationCanceledException>(() => P3MacroBuiltinPrototype.Stringify("stringify!(alpha beta)",
            progress: steps => { observed = steps; if (steps == 6) canceled.Cancel(); }, cancellationToken: canceled.Token));
        AssertEx.Equal(6, observed, "Cancellation happened during token emission, before any result was published.");
        return Task.CompletedTask;
    }
    private static Task InvalidLimitsAsync()
    {
        Rejected(P3MacroBuiltinPrototype.Stringify("stringify!(a)", new() { MaximumExpandedTokens = 16_385 }), "RSMAC1090");
        return Task.CompletedTask;
    }
    private static void Success(P3MacroBuiltinResult result, string expected)
    {
        AssertEx.True(result.IsSuccessful, result.Message);
        AssertEx.Equal(expected, result.Output);
        AssertEx.True(result.ArgumentSpan is not null, "Successful output retains the original argument span.");
        AssertEx.False(result.FormalAcceptanceAllowed, "Prototype output cannot accept P3.");
        AssertEx.Equal(0, result.ProductionMacroExpansionsExecuted);
    }
    private static void Rejected(P3MacroBuiltinResult result, string diagnostic)
    {
        AssertEx.False(result.IsSuccessful, "Malformed, wrong-form or over-budget input rejects.");
        AssertEx.Equal(diagnostic, result.Diagnostic);
        AssertEx.Equal(string.Empty, result.Output, "Rejected output must not publish a partial string.");
        AssertEx.Equal(0, result.TokenSpans.Count);
        AssertEx.True(result.ArgumentSpan is null, "Rejected input has no usable partial source mapping.");
    }
}
