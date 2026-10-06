using System.Diagnostics;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P1LabelResolutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 loop labels preserve nested break and continue bindings in HIR", NestedLabelsAsync),
        new("P1 loop labels permit lexical shadowing", ShadowedLabelsAsync),
        new("P1 loop labels reject missing and cross-boundary labels stably", MissingLabelsAsync),
        new("P1 loop labels isolate const blocks and keep nested const items outside the profile", ConstBoundariesAsync),
    ];

    private static Task NestedLabelsAsync()
    {
        const string source = """
            fn main() {
                'outer: loop {
                    'inner: while false { continue 'inner; }
                    break 'outer;
                }
            }
            """;
        SafeCoreHirResult result = Lower(source);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal("'outer", result.Nodes.Single(node => node.Kind == SafeCoreHirNodeKind.LoopExpression).Name!);
        AssertEx.Equal("'inner", result.Nodes.Single(node => node.Kind == SafeCoreHirNodeKind.WhileExpression).Name!);
        AssertEx.Equal("'outer", result.Nodes.Single(node => node.Kind == SafeCoreHirNodeKind.BreakExpression).Name!);
        AssertEx.Equal("'inner", result.Nodes.Single(node => node.Kind == SafeCoreHirNodeKind.ContinueExpression).Name!);
        return Task.CompletedTask;
    }

    private static Task ShadowedLabelsAsync()
    {
        const string source = "fn main() { 'scope: loop { 'scope: loop { break 'scope; } break 'scope; } }";
        SafeCoreHirResult result = Lower(source);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(2, result.Nodes.Count(node => node.Kind == SafeCoreHirNodeKind.LoopExpression && node.Name == "'scope"));
        AssertEx.Equal(2, result.Nodes.Count(node => node.Kind == SafeCoreHirNodeKind.BreakExpression && node.Name == "'scope"));
        return Task.CompletedTask;
    }

    private static Task MissingLabelsAsync()
    {
        string[] sources =
        [
            "fn main() { loop { break 'missing; } }",
            "fn main() { loop { continue 'missing; } }",
            "fn main() { 'expired: loop { break; } loop { break 'expired; } }",
            "fn main() { 'outer: loop { let escape = || { break 'outer; }; break 'outer; } }",
            "fn first() { 'local: loop { break; } } fn main() { loop { break 'local; } }",
            "fn main() { 'outer: loop { let value = const { break 'outer 1; }; break 'outer; } }",
            "fn main() { 'outer: loop { let value = const { continue 'outer; }; break 'outer; } }",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        foreach (string source in sources)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(10), "The seven-case label corpus exceeded its wall-clock bound.");
            SafeCoreHirResult first = Lower(source, deadline.Token);
            SafeCoreHirResult second = Lower(source, deadline.Token);
            AssertEx.False(first.IsSuccessful || second.IsSuccessful, "An unknown label may not reach typed MIR.");
            AssertEx.Equal("p1-labels.rs", first.SourcePath);
            AssertEx.True(first.Diagnostics.Count is > 0 and <= 8 && first.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnresolvedName &&
                diagnostic.Message.Contains("loop label", StringComparison.Ordinal)),
                "Label scope failures need an explicit finite unresolved-label diagnostic.");
            AssertEx.True(first.Diagnostics.All(diagnostic => diagnostic.Span.Start >= 0 && diagnostic.Span.End <= source.Length),
                "Rejected labels must retain the source expression extent.");
            AssertEx.Equal(string.Join('\n', first.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)),
                string.Join('\n', second.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
            AssertEx.True(first.Diagnostics.Select(diagnostic => (diagnostic.Span, diagnostic.SourcePath))
                .SequenceEqual(second.Diagnostics.Select(diagnostic => (diagnostic.Span, diagnostic.SourcePath))),
                "Repeated label resolution must retain the exact same diagnostic span and source path.");
        }
        return Task.CompletedTask;
    }

    private static Task ConstBoundariesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string block = "fn main() { 'outer: loop { let value = const { 'outer: loop { break 'outer 1; } }; break 'outer; } }";
        SafeCoreHirResult ownLoop = Lower(block, deadline.Token);
        AssertEx.True(ownLoop.IsSuccessful, string.Join(Environment.NewLine, ownLoop.Diagnostics));
        AssertEx.Equal(2, ownLoop.Nodes.Count(node => node.Kind == SafeCoreHirNodeKind.LoopExpression && node.Name == "'outer"));
        const string item = "fn main() { 'outer: loop { const VALUE: i32 = { break 'outer 1; }; break 'outer; } }";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(item, "p1-labels.rs", new()
            { Timeout = TimeSpan.FromSeconds(5) }, deadline.Token);
        AssertEx.True(syntax.IsSuccessful, "The syntax parser accepts a nested const item before the profile boundary check.");
        SafeCoreHirResult nestedItem = Lower(item, deadline.Token);
        AssertEx.False(nestedItem.IsSuccessful, "A nested const item cannot capture the enclosing loop label through an unimplemented item scope.");
        AssertEx.True(nestedItem.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
            "Nested items must keep their existing explicit profile diagnostic.");
        return Task.CompletedTask;
    }

    private static SafeCoreHirResult Lower(string source, CancellationToken cancellationToken = default) =>
        SafeCoreHirLowering.Lower(SafeCoreSyntax.Parse(source, "p1-labels.rs", new()
            { Timeout = TimeSpan.FromSeconds(5) }, cancellationToken), new()
        {
            Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 65_536, CancellationToken = cancellationToken,
            NameResolution = new()
            {
                EnableTypeSystemExtensions = true, EnableLoopLabels = true, Timeout = TimeSpan.FromSeconds(5),
                MaximumOperations = 65_536, CancellationToken = cancellationToken,
            },
        });
}
