using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SyntaxModuleExpansionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("syntax preserves recursive import trees and aliases", ImportsAsync),
        new("syntax distinguishes restricted visibility and external modules", VisibilityAsync),
        new("syntax desugars documentation and retains inner attributes", DocumentationAsync),
        new("syntax rejects malformed import visibility and documentation placement", MalformedAsync),
        new("syntax bounds recursive import trees and documentation work", LimitsAsync),
    ];

    private static SafeCoreSyntaxResult Pass(string source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "module-expansion.rs", null, deadline.Token);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        AssertEx.Equal(source, result.LexResult.ToSourceText());
        return result;
    }

    private static Task ImportsAsync()
    {
        const string source = "use ::crate_name::{self as whole, nested::{Thing as Alias, *}, Other as _}; use {}; use {a, b};";
        SafeCoreSyntaxResult result = Pass(source);
        var tree = ((SafeCoreUseSyntax)result.Root!.Items[0]).Tree!;
        AssertEx.True(tree.IsAbsolute && tree.Kind == SafeCoreUseTreeKind.Group, "The absolute import group must be retained.");
        AssertEx.Equal("crate_name", tree.Prefix.Single());
        AssertEx.Equal("whole", tree.Children[0].Alias!);
        AssertEx.Equal("Alias", tree.Children[1].Children[0].Alias!);
        AssertEx.Equal(SafeCoreUseTreeKind.Glob, tree.Children[1].Children[1].Kind);
        AssertEx.Equal("_", tree.Children[2].Alias!);
        AssertEx.Equal("nested::{Thing as Alias, *}", result.GetText(tree.Children[1].Span));
        AssertEx.Equal(SafeCoreUseTreeKind.Group, ((SafeCoreUseSyntax)result.Root.Items[1]).Tree!.Kind);
        AssertEx.Equal(0, ((SafeCoreUseSyntax)result.Root.Items[1]).Tree!.Children.Count);
        return Task.CompletedTask;
    }

    private static Task VisibilityAsync()
    {
        SafeCoreSyntaxResult result = Pass("pub(in crate::inner) mod external; pub mod inline {} pub(self) use a; pub(super) struct S;");
        var external = (SafeCoreModuleSyntax)result.Root!.Items[0];
        AssertEx.True(external.IsExternal && !external.IsPublic, "An external restricted module is not an inline public module.");
        AssertEx.Equal(SafeCoreVisibilityKind.Restricted, external.Visibility.Kind);
        AssertEx.Equal("crate::inner", external.Visibility.Path!);
        AssertEx.Equal("pub(in crate::inner)", result.GetText(external.Visibility.Span));
        var inline = (SafeCoreModuleSyntax)result.Root.Items[1];
        AssertEx.True(!inline.IsExternal && inline.IsPublic, "Unrestricted public visibility remains supported.");
        AssertEx.Equal(SafeCoreVisibilityKind.Self, result.Root.Items[2].Visibility.Kind);
        AssertEx.Equal(SafeCoreVisibilityKind.Super, result.Root.Items[3].Visibility.Kind);
        return Task.CompletedTask;
    }

    private static Task DocumentationAsync()
    {
        const string source = "//! crate docs\n/// module docs\nmod m { /*! inner */ #[allow(dead_code)] /// function docs\nfn f() { #![allow(unused)] } }";
        SafeCoreSyntaxResult result = Pass(source);
        AssertEx.Equal(" crate docs", result.Root!.Attributes.Single().DocumentationText!);
        var module = (SafeCoreModuleSyntax)result.Root.Items.Single();
        AssertEx.True(module.Attributes.Single().IsDocumentation, "Outer docs must annotate the module.");
        AssertEx.Equal(" inner ", module.InnerAttributes.Single().DocumentationText!);
        var function = (SafeCoreFunctionSyntax)module.Items.Single();
        AssertEx.Equal(2, function.Attributes.Count);
        AssertEx.Equal(" function docs", function.Attributes[1].DocumentationText!);
        AssertEx.Equal("/// function docs", result.GetText(function.Attributes[1].Span));
        AssertEx.True(function.Body.Attributes.Single().IsInner, "The function body keeps its inner attribute.");
        AssertEx.Equal("#![allow(unused)]", result.GetText(function.Body.Attributes.Single().Span));
        return Task.CompletedTask;
    }

    private static Task MalformedAsync()
    {
        string[] sources =
        [
            "use a{b};", "use a::* as b;", "use a::{,b};", "use a::{b c};", "use a::{b::};",
            "pub(foo) fn f() {}", "pub(crate::a) fn f() {}", "pub(in other) fn f() {}",
            "pub(in crate::) fn f() {}", "/// dangling", "mod m { /// dangling\n }",
            "fn f() { 1 + /// misplaced\n2; }", "#[a] //! late inner\nfn f() {}",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (string source in sources)
        {
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "malformed-module.rs", null, deadline.Token);
            AssertEx.True(!result.IsSuccessful && !result.IsTruncated && result.Root is null && result.Diagnostics.Count > 0,
                "Malformed syntax must fail with a diagnostic: " + source);
        }
        return Task.CompletedTask;
    }

    private static Task LimitsAsync()
    {
        string source = "use " + string.Concat(Enumerable.Repeat("a::{", 40)) + "b" + new string('}', 40) + ";";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreSyntaxResult depth = SafeCoreSyntax.Parse(source, "deep-use.rs",
            new SafeCoreSyntaxOptions { MaximumNestingDepth = 16 }, deadline.Token);
        AssertEx.True(depth.IsTruncated && depth.Root is null, "Nested import trees obey syntax depth bounds.");
        SafeCoreSyntaxResult work = SafeCoreSyntax.Parse("/// " + new string('x', 2048) + "\nfn f() {}", "docs-budget.rs",
            new SafeCoreSyntaxOptions { MaximumOperations = 128 }, deadline.Token);
        AssertEx.True(work.IsTruncated && work.Root is null, "Documentation desugaring obeys parser work bounds.");
        return Task.CompletedTask;
    }
}
