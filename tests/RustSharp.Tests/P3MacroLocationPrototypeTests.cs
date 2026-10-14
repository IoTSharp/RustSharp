using System.Text;
using RustSharp.Conformance;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P3MacroLocationPrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 location prototype line at document start is one", () => NumberAsync("", "line!()", 1)),
        new("P3 location prototype line counts LF", () => NumberAsync("one\ntwo\n", "line!()", 3)),
        new("P3 location prototype line counts CRLF once", () => NumberAsync("one\r\ntwo\r\n", "line!()", 3)),
        new("P3 location prototype column at document start is one", () => NumberAsync("", "column!()", 1)),
        new("P3 location prototype column counts ASCII prefix", () => NumberAsync("  ", "column!()", 3)),
        new("P3 location prototype column counts emoji as one scalar", () => NumberAsync("😀 ", "column!()", 3)),
        new("P3 location prototype column counts combining mark as a scalar", () => NumberAsync("a\u0301 ", "column!()", 4)),
        new("P3 location prototype column resets after CRLF", () => NumberAsync("😀\r\n \t", "column!()", 3)),
        new("P3 location prototype file normalizes slash without file access", FileAsync),
        new("P3 location prototype returns exact original callsite span", SpanAsync),
        new("P3 location prototype rejects line arguments", () => RejectAsync("line!(1)", "src/main.rs", "RSMAC1003")),
        new("P3 location prototype rejects column arguments", () => RejectAsync("column!(x)", "src/main.rs", "RSMAC1003")),
        new("P3 location prototype rejects file arguments", () => RejectAsync("file!(x)", "src/main.rs", "RSMAC1003")),
        new("P3 location prototype rejects Windows absolute path", () => RejectAsync("file!()", "C:\\secret.rs", "RSMAC1003")),
        new("P3 location prototype rejects Unix absolute path", () => RejectAsync("file!()", "/secret.rs", "RSMAC1003")),
        new("P3 location prototype rejects UNC path", () => RejectAsync("file!()", "\\\\server\\share.rs", "RSMAC1003")),
        new("P3 location prototype rejects traversal path", () => RejectAsync("file!()", "src/../secret.rs", "RSMAC1003")),
        new("P3 location prototype rejects backslash traversal", () => RejectAsync("file!()", "src\\..\\secret.rs", "RSMAC1003")),
        new("P3 location prototype rejects empty path", () => RejectAsync("file!()", "", "RSMAC1003")),
        new("P3 location prototype rejects empty source and span", EmptyAsync),
        new("P3 location prototype rejects negative and overflowing spans", BadSpanAsync),
        new("P3 location prototype rejects malformed Unicode source", UnicodeAsync),
        new("P3 location prototype rejects span inside surrogate pair", SurrogateSpanAsync),
        new("P3 location prototype rejects malformed delimiters", () => RejectAsync("line!(]", "src/main.rs", "RSMAC1002")),
        new("P3 location prototype enforces original source byte boundary", ByteLimitAsync),
        new("P3 location prototype enforces token boundary", TokenLimitAsync),
        new("P3 location prototype enforces step boundary", StepLimitAsync),
        new("P3 location prototype propagates pre entry cancellation", CancellationAsync),
        new("P3 location prototype propagates prefix scan cancellation", MidCancellationAsync),
    ];
    private static SafeCoreSourceLocation Input(string source, string path, TextSpan span) => new(new(path, source, Encoding.UTF8.GetBytes(source)), span);
    private static Task NumberAsync(string prefix, string invocation, uint value)
    {
        P3MacroLocationResult result = P3MacroLocationPrototype.Evaluate(Input(prefix + invocation, "src/main.rs", new(prefix.Length, invocation.Length)));
        Success(result); AssertEx.Equal(value, result.NumericValue!.Value); AssertEx.True(result.FileValue is null, "Numeric builtin has no file output.");
        return Task.CompletedTask;
    }
    private static Task FileAsync()
    {
        P3MacroLocationResult result = P3MacroLocationPrototype.Evaluate(Input("file!()", "src\\ghost\\never-created.rs", new(0, 7)));
        Success(result); AssertEx.Equal("src/ghost/never-created.rs", result.FileValue!); AssertEx.True(result.NumericValue is null, "File builtin has no numeric output.");
        return Task.CompletedTask;
    }
    private static Task SpanAsync()
    {
        P3MacroLocationResult result = P3MacroLocationPrototype.Evaluate(Input("x\nline!()", "src/main.rs", new(2, 7)));
        Success(result); AssertEx.Equal(new TextSpan(2, 7), result.CallSite!.Span); AssertEx.Equal("src/main.rs", result.CallSite.Document.Path);
        return Task.CompletedTask;
    }
    private static Task RejectAsync(string input, string path, string code)
    { Rejected(P3MacroLocationPrototype.Evaluate(Input(input, path, new(0, input.Length))), code); return Task.CompletedTask; }
    private static Task EmptyAsync() { Rejected(P3MacroLocationPrototype.Evaluate(Input("", "src/main.rs", new(0, 0))), "RSMAC1003"); return Task.CompletedTask; }
    private static Task BadSpanAsync()
    {
        Rejected(P3MacroLocationPrototype.Evaluate(Input("line!()", "src/main.rs", new(-1, 7))), "RSMAC1003");
        Rejected(P3MacroLocationPrototype.Evaluate(Input("line!()", "src/main.rs", new(int.MaxValue, int.MaxValue))), "RSMAC1003");
        return Task.CompletedTask;
    }
    private static Task UnicodeAsync()
    {
        Rejected(P3MacroLocationPrototype.Evaluate(Input("\ud800line!()", "src/main.rs", new(1, 7))), "RSMAC1003");
        Rejected(P3MacroLocationPrototype.Evaluate(Input("\udc00line!()", "src/main.rs", new(1, 7))), "RSMAC1003");
        return Task.CompletedTask;
    }
    private static Task SurrogateSpanAsync()
    { Rejected(P3MacroLocationPrototype.Evaluate(Input("😀line!()", "src/main.rs", new(1, 8))), "RSMAC1003"); return Task.CompletedTask; }
    private static Task ByteLimitAsync()
    {
        SafeCoreSourceLocation input = Input("line!()", "a.rs", new(0, 7));
        Success(P3MacroLocationPrototype.Evaluate(input, new() { MaximumInputBytes = 7 }));
        Rejected(P3MacroLocationPrototype.Evaluate(input, new() { MaximumInputBytes = 6 }), "RSMAC1005"); return Task.CompletedTask;
    }
    private static Task TokenLimitAsync()
    {
        SafeCoreSourceLocation input = Input("line!()", "a.rs", new(0, 7));
        Success(P3MacroLocationPrototype.Evaluate(input, new() { MaximumInputTokens = 4 }));
        Rejected(P3MacroLocationPrototype.Evaluate(input, new() { MaximumInputTokens = 3 }), "RSMAC1005"); return Task.CompletedTask;
    }
    private static Task StepLimitAsync()
    {
        SafeCoreSourceLocation input = Input("line!()", "a.rs", new(0, 7));
        P3MacroLocationResult first = P3MacroLocationPrototype.Evaluate(input); Success(first);
        Success(P3MacroLocationPrototype.Evaluate(input, new() { MaximumMatcherSteps = first.MatcherSteps }));
        Rejected(P3MacroLocationPrototype.Evaluate(input, new() { MaximumMatcherSteps = first.MatcherSteps - 1 }), "RSMAC1005"); return Task.CompletedTask;
    }
    private static Task CancellationAsync()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P3MacroLocationPrototype.Evaluate(Input("line!()", "a.rs", new(0, 7)), cancellationToken: canceled.Token)); return Task.CompletedTask;
    }
    private static Task MidCancellationAsync()
    {
        using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(5)); int observed = 0;
        AssertEx.Throws<OperationCanceledException>(() => P3MacroLocationPrototype.Evaluate(Input("prefix\nline!()", "a.rs", new(7, 7)),
            progress: steps => { observed = steps; if (steps == 8) canceled.Cancel(); }, cancellationToken: canceled.Token));
        AssertEx.Equal(8, observed, "Cancellation interrupts source scanning before output publication."); return Task.CompletedTask;
    }
    private static void Success(P3MacroLocationResult result)
    { AssertEx.True(result.IsSuccessful, result.Message); AssertEx.True(result.CallSite is not null, "Original callsite retained."); AssertEx.False(result.FormalAcceptanceAllowed, "Prototype cannot accept P3."); AssertEx.Equal(0, result.ProductionMacroExpansionsExecuted); }
    private static void Rejected(P3MacroLocationResult result, string code)
    { AssertEx.False(result.IsSuccessful, "Invalid source map or invocation rejects."); AssertEx.Equal(code, result.Diagnostic); AssertEx.True(result.CallSite is null && result.NumericValue is null && result.FileValue is null, "No partial output or source location is exposed."); }
}
