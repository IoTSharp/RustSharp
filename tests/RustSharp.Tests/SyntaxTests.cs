using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SyntaxTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("syntax parses fn main and println", ParsesHelloWorldAsync),
        new("syntax decodes strings and nested comments", ParsesTriviaAndEscapesAsync),
        new("syntax rejects a missing semicolon", RejectsMissingSemicolonAsync),
        new("syntax rejects trailing input", RejectsTrailingInputAsync),
    ];

    private static Task ParsesHelloWorldAsync()
    {
        const string source = "fn main() { println!(\"Hello from Rust#\"); }";
        var tree = SyntaxTree.Parse(source, "hello.rs");

        AssertEx.Equal(0, tree.Diagnostics.Count);
        var root = AssertEx.NotNull(tree.Root, "A valid program must produce a syntax root.");
        AssertEx.Equal(1, root.Statements.Count);
        AssertEx.Equal("Hello from Rust#", root.Statements[0].Value);
        return Task.CompletedTask;
    }

    private static Task ParsesTriviaAndEscapesAsync()
    {
        const string source = "/* outer /* nested */ */ fn main() { // line\n println!(\"A\\n\\u{1F980}\"); }";
        var tree = SyntaxTree.Parse(source, "escapes.rs");

        AssertEx.Equal(0, tree.Diagnostics.Count);
        var root = AssertEx.NotNull(tree.Root, "A valid program must produce a syntax root.");
        AssertEx.Equal("A\n\U0001F980", root.Statements[0].Value);
        return Task.CompletedTask;
    }

    private static Task RejectsMissingSemicolonAsync()
    {
        const string source = "fn main() { println!(\"missing\") }";
        var tree = SyntaxTree.Parse(source, "invalid.rs");

        AssertEx.True(tree.Diagnostics.Count > 0, "Invalid RustSharp source must have diagnostics.");
        AssertEx.True(tree.Root is null, "A syntax tree with errors must not expose a compilable root.");
        AssertEx.Equal("RSC1001", tree.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static Task RejectsTrailingInputAsync()
    {
        const string source = "fn main() {} trailing";
        var tree = SyntaxTree.Parse(source, "invalid.rs");

        AssertEx.Equal(1, tree.Diagnostics.Count);
        AssertEx.Equal("RSC1002", tree.Diagnostics[0].Code);
        return Task.CompletedTask;
    }
}
