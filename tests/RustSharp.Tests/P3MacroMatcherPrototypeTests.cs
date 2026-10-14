using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroMatcherPrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 matcher prototype ident accepts ASCII with exact capture span", IdentifierAsync),
        new("P3 matcher prototype ident rejects keyword", () => RejectAsync(P3MacroPattern.Identifier, "fn", "RSMAC1003")),
        new("P3 matcher prototype ident rejects raw identifier", () => RejectAsync(P3MacroPattern.Identifier, "r#name", "RSMAC1003")),
        new("P3 matcher prototype ident rejects Unicode identifier", () => RejectAsync(P3MacroPattern.Identifier, "π", "RSMAC1003")),
        new("P3 matcher prototype ident rejects lone underscore", () => RejectAsync(P3MacroPattern.Identifier, "_", "RSMAC1003")),
        new("P3 matcher prototype ident rejects two token capture", () => RejectAsync(P3MacroPattern.Identifier, "a b", "RSMAC1003")),
        new("P3 matcher prototype tt captures recursively balanced groups", TokenTreeAsync),
        new("P3 matcher prototype tt captures punctuation leaf", () => MatchAsync(P3MacroPattern.TokenTree, "+", "+")),
        new("P3 matcher prototype tt rejects mismatched delimiter", () => RejectAsync(P3MacroPattern.TokenTree, "[a)", "RSMAC1002")),
        new("P3 matcher prototype tt rejects unterminated group", () => RejectAsync(P3MacroPattern.TokenTree, "{a", "RSMAC1002")),
        new("P3 matcher prototype comma star accepts zero", () => MatchAsync(P3MacroPattern.CommaStarIdentifiers, "")),
        new("P3 matcher prototype comma star accepts one", () => MatchAsync(P3MacroPattern.CommaStarIdentifiers, "a", "a")),
        new("P3 matcher prototype comma star accepts many with trivia", () => MatchAsync(P3MacroPattern.CommaStarIdentifiers, "a, /* separator */ b,c", "a", "b", "c")),
        new("P3 matcher prototype comma star rejects trailing comma", () => RejectAsync(P3MacroPattern.CommaStarIdentifiers, "a,", "RSMAC1004")),
        new("P3 matcher prototype comma star rejects wrong separator", () => RejectAsync(P3MacroPattern.CommaStarIdentifiers, "a;b", "RSMAC1004")),
        new("P3 matcher prototype comma plus accepts one and many", PlusAsync),
        new("P3 matcher prototype comma plus rejects zero", () => RejectAsync(P3MacroPattern.CommaPlusIdentifiers, "", "RSMAC1004")),
        new("P3 matcher prototype optional accepts zero and one", OptionalAsync),
        new("P3 matcher prototype optional rejects many", () => RejectAsync(P3MacroPattern.OptionalIdentifier, "a b", "RSMAC1004")),
        new("P3 matcher prototype rejects input byte over budget", ByteLimitAsync),
        new("P3 matcher prototype rejects input token over budget", TokenLimitAsync),
        new("P3 matcher prototype enforces matcher step exact boundary", StepLimitAsync),
        new("P3 matcher prototype rejects delimiter depth over budget", DepthLimitAsync),
        new("P3 matcher prototype propagates pre entry cancellation", CancellationAsync),
        new("P3 matcher prototype propagates cancellation during matching", MidMatchCancellationAsync),
        new("P3 matcher prototype rejects expanded candidate limits", InvalidLimitsAsync),
    ];

    private static Task IdentifierAsync()
    {
        P3MacroMatchResult result = P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "  _name42 ");
        Success(result, "_name42");
        AssertEx.Equal(2, result.Captures[0].Span.Start);
        AssertEx.Equal(7, result.Captures[0].Span.Length);
        return Task.CompletedTask;
    }
    private static Task TokenTreeAsync()
    {
        const string source = "[a (b {c})]";
        P3MacroMatchResult result = P3MacroMatcherPrototype.Match(P3MacroPattern.TokenTree, source);
        Success(result, source);
        AssertEx.True(result.Captures[0].IsDelimited, "Balanced lexer group is retained as one token-tree capture.");
        return Task.CompletedTask;
    }
    private static Task PlusAsync()
    {
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.CommaPlusIdentifiers, "a"), "a");
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.CommaPlusIdentifiers, "a,b"), "a", "b");
        return Task.CompletedTask;
    }
    private static Task OptionalAsync()
    {
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.OptionalIdentifier, ""));
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.OptionalIdentifier, "a"), "a");
        return Task.CompletedTask;
    }
    private static Task ByteLimitAsync()
    {
        var limits = new P3MacroMatchLimits { MaximumInputBytes = 2 };
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "ab", limits), "ab");
        Rejected(P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "abc", limits), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task TokenLimitAsync()
    {
        var limits = new P3MacroMatchLimits { MaximumInputTokens = 3 };
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.CommaStarIdentifiers, "a,b", limits), "a", "b");
        Rejected(P3MacroMatcherPrototype.Match(P3MacroPattern.CommaStarIdentifiers, "a,b,c", limits), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task StepLimitAsync()
    {
        P3MacroMatchResult first = P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "a");
        Success(first, "a");
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "a", new() { MaximumMatcherSteps = first.MatcherSteps }), "a");
        Rejected(P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "a", new() { MaximumMatcherSteps = first.MatcherSteps - 1 }), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task DepthLimitAsync()
    {
        var limits = new P3MacroMatchLimits { MaximumDepth = 2 };
        Success(P3MacroMatcherPrototype.Match(P3MacroPattern.TokenTree, "[(a)]", limits), "[(a)]");
        Rejected(P3MacroMatcherPrototype.Match(P3MacroPattern.TokenTree, "[({a})]", limits), "RSMAC1005");
        return Task.CompletedTask;
    }
    private static Task CancellationAsync()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "a", cancellationToken: canceled.Token));
        return Task.CompletedTask;
    }
    private static Task MidMatchCancellationAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int observed = 0;
        AssertEx.Throws<OperationCanceledException>(() => P3MacroMatcherPrototype.Match(P3MacroPattern.CommaStarIdentifiers, "alpha,beta,gamma",
            cancellationToken: deadline.Token, progress: steps => { observed = steps; if (steps == 4) deadline.Cancel(); }));
        AssertEx.Equal(4, observed, "Matching was entered and canceled before a result could publish partial captures.");
        return Task.CompletedTask;
    }
    private static Task InvalidLimitsAsync()
    {
        Rejected(P3MacroMatcherPrototype.Match(P3MacroPattern.Identifier, "a", new() { MaximumInputBytes = 65_537 }), "RSMAC1090");
        Rejected(P3MacroMatcherPrototype.Match((P3MacroPattern)999, "a"), "RSMAC1001");
        return Task.CompletedTask;
    }
    private static Task MatchAsync(P3MacroPattern pattern, string source, params string[] captures)
    {
        Success(P3MacroMatcherPrototype.Match(pattern, source), captures);
        return Task.CompletedTask;
    }
    private static Task RejectAsync(P3MacroPattern pattern, string source, string diagnostic)
    {
        Rejected(P3MacroMatcherPrototype.Match(pattern, source), diagnostic);
        return Task.CompletedTask;
    }
    private static void Success(P3MacroMatchResult result, params string[] captures)
    {
        AssertEx.True(result.IsSuccessful, result.Message);
        AssertEx.Equal(captures.Length, result.Captures.Count);
        AssertEx.Equal(string.Join('|', captures), string.Join('|', result.Captures.Select(static capture => capture.Text)));
        AssertEx.False(result.FormalAcceptanceAllowed, "Prototype matches cannot accept a P3 leaf.");
        AssertEx.Equal(0, result.MacroExpansionsExecuted);
    }
    private static void Rejected(P3MacroMatchResult result, string diagnostic)
    {
        AssertEx.False(result.IsSuccessful, "Invalid or over-budget input must reject.");
        AssertEx.Equal(diagnostic, result.Diagnostic);
        AssertEx.Equal(0, result.Captures.Count, "Rejected input exposes no partial captures.");
    }
}
