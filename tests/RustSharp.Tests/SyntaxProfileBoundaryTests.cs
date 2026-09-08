using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SyntaxProfileBoundaryTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("syntax extensions cannot silently pass the narrower semantic profile", RejectsUnimplementedSemanticsAsync),
        new("deferred generic bounds validate nested syntax without binding trait names", DeferredGenericBoundsAsync),
        new("deferred generic bound validation obeys semantic work limits", DeferredGenericBoundLimitsAsync),
        new("attribute semantics reject unknown root and item attributes at their spans", AttributeBoundaryAsync),
    ];

    private static Task AttributeBoundaryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreSyntaxResult smoke = SafeCoreSyntax.Parse("#![no_std] fn main() {}", "attribute-smoke.rs", null, deadline.Token);
        AssertEx.True(smoke.IsSuccessful, "A bare no_std marker must parse.");
        AssertEx.Equal(string.Empty, smoke.Root!.Attributes[0].ArgumentsText,
            "The parser represents a bare attribute with empty arguments.");
        AssertEx.True(SafeCoreNameResolution.Resolve(smoke).IsSuccessful, "The existing bare no_std root marker remains inert.");

        (string Source, string Attribute)[] rejected =
        [
            ("#![cfg(any())] fn main() {}", "#![cfg(any())]"),
            ("#[cfg(any())] fn main() {}", "#[cfg(any())]"),
            ("#[tool::unknown] fn main() {}", "#[tool::unknown]"),
            ("#[doc = \"not interpreted\"] fn main() {}", "#[doc = \"not interpreted\"]"),
            ("#[path = \"child.rs\"] mod child {} fn main() {}", "#[path = \"child.rs\"]"),
            ("#![no_std()] fn main() {}", "#![no_std()]"),
            ("#![no_std = true] fn main() {}", "#![no_std = true]"),
            ("#[no_std] fn main() {}", "#[no_std]"),
        ];
        foreach ((string source, string attribute) in rejected)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "attribute-boundary.rs", null, deadline.Token);
            AssertEx.True(syntax.IsSuccessful, source + ": " + string.Join("; ", syntax.Diagnostics));
            SafeCoreNameResolutionResult resolution = SafeCoreNameResolution.Resolve(syntax,
                new SafeCoreNameResolutionOptions { CancellationToken = deadline.Token });
            Diagnostic rejection = resolution.Diagnostics.First(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
            AssertEx.Equal(attribute, syntax.GetText(rejection.Span), "Attribute rejection must retain its exact source span.");
            SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax);
            AssertEx.True(!hir.IsSuccessful && hir.Nodes.Count == 0 && hir.Diagnostics.Contains(rejection),
                "Unknown attributes must fail before HIR rather than becoming executable metadata.");
            CompilationResult check = CompilerDriver.Check(source, "attribute-boundary.rs",
                CompilationProfile.SafeCorePrimitives, deadline.Token);
            AssertEx.True(!check.Success && check.Diagnostics.Contains(rejection),
                "Compiler checks must retain the attribute boundary diagnostic.");
        }

        SafeCoreAttributeSyntax documentation = new(false, "doc", "= \"doc\"", new TextSpan(0, 1)) { IsDocumentation = true };
        SafeCoreCompilationUnitSyntax root = smoke.Root with
        {
            Attributes = Array.AsReadOnly(Enumerable.Repeat(documentation, 64).ToArray()),
        };
        SafeCoreNameResolutionResult limited = SafeCoreNameResolution.Collect(root, options:
            new SafeCoreNameResolutionOptions { MaximumOperations = 12, CancellationToken = deadline.Token });
        AssertEx.True(limited.IsTruncated && limited.Diagnostics.Any(diagnostic =>
            diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.LimitReached),
            "Even inert attribute traversal must consume the semantic operation budget.");
        return Task.CompletedTask;
    }

    private static Task RejectsUnimplementedSemanticsAsync()
    {
        string[] sources =
        [
            "mod external;",
            "fn f<T>() where T: Copy {}", "trait Marker {}",
            "fn f<'a>() {}", "struct S<T = i32>;",
            "enum E { A { x: i32 } }", "enum E { A = 1 }", "type F = fn(i32);",
            "fn f() { let value: _ = 1; }", "fn f() { let ref value = 1; }",
            "fn f() { let value = 1 else {}; }", "fn f() { loop {} }", "fn f() { || 1; }",
            "fn f() { 1 as i32; }", "fn f() { fn nested() {} }", "fn f() { ; }",
            "mod m { #![cfg(any())] fn f() {} }", "fn f() { #![scope] let x = 1; }",
            "fn f() { #[local] let x = 1; }", "fn f() -> i32 { #[tail] 1 }",
            "fn f(#[parameter] x: i32) {}", "struct S<#[generic] T>;",
            "struct S { #[field] x: i32 }", "enum E { #[variant] A }",
            "enum E { A(#[field] i32) }", "fn f() { const { 1 }; }",
            "use ::external::Item;", "mod api {} use ::crate::api::*;",
            "struct S; impl S {}", "const _: i32 = 1;",
            "struct S<const N: usize>;", "struct S<T: ?Sized>;", "struct S<T: 'static>;",
            "struct S<T: for<'a> Trait<'a>>;", "struct S<T> where T: Copy;",
            "enum E<T> where T: Copy { A(T) }", "type A<T> where T: Copy = T;",
            "type A = impl Copy;", "type A = dyn Copy;", "type A = ::Missing;",
            "type A = <i32 as Trait>::Item;", "struct S; type A = S<'static>;",
            "struct S; type A = S<3>;", "struct S; type A = S<Item = i32>;",
            "struct S; type A = S<Item: Copy>;", "struct S; type A = S(i32);",
            "fn f() { let (a, ..) = (1, 2); }", "fn f() { let &x = &1; }",
            "fn f() { let [x] = [1]; }", "fn f() { let x @ 1 = 1; }",
            "fn f() { let 1..=3 = 1; }", "fn f() { let S { x } = 1; }",
            "enum E { A } fn f() { let E::A::<i32> = 1; }",
            "const VALUE: i32 = 1; fn f() -> i32 { ::VALUE }",
            "const VALUE: i32 = 1; fn f() { let ::VALUE = 1; }",
            "fn f() { f::<>(); }", "fn f() { f::<i32>(); }",
            "fn f() { 1.field; }", "fn f() { 1.method(); }", "fn f() { 1?; }",
            "fn f() { S { x: 1 }; }", "fn f() { 1..3; }",
            "fn f() { match 1 { 1 | 2 => 3, _ => 4 }; }",
            "fn f() { while true {} }", "fn f() { for x in 0..1 {} }",
            "fn f() { 'scope: {}; }", "fn f() { break; }", "fn f() { continue; }",
            "fn f() { (return 1); }", "fn f() { if let x = 1 {} }",
            "fn f() { <i32 as Trait>::value(); }",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (string source in sources)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "profile-boundary.rs", null, deadline.Token);
            AssertEx.True(syntax.IsSuccessful, source + ": " + string.Join("; ", syntax.Diagnostics));
            SafeCoreNameResolutionResult resolution = SafeCoreNameResolution.Resolve(syntax);
            AssertEx.True(!resolution.IsSuccessful && resolution.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
                "New syntax must have an explicit semantic rejection: " + source);
            Diagnostic rejection = resolution.Diagnostics.First(static diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax);
            AssertEx.True(syntax.GetText(rejection.Span).Length > 0,
                "The semantic rejection must identify the unsupported source syntax: " + source);
            SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax);
            AssertEx.False(hir.IsSuccessful, "HIR must not drop new syntax and succeed: " + source);
            AssertEx.True(hir.Root is null && hir.Nodes.Count == 0,
                "Rejected syntax must not produce a partial HIR arena: " + source);
            AssertEx.True(hir.Diagnostics.Contains(rejection),
                "HIR must preserve the RSN1007 message and source span: " + source);
            CompilationResult check = CompilerDriver.Check(source, "profile-boundary.rs",
                CompilationProfile.SafeCorePrimitives, deadline.Token);
            AssertEx.False(check.Success, "The compiler must reject unimplemented syntax: " + source);
            AssertEx.True(check.Diagnostics.Contains(rejection),
                "Compiler checks must preserve the RSN1007 message and source span: " + source);
        }
        return Task.CompletedTask;
    }

    private static Task DeferredGenericBoundsAsync()
    {
        string[] rejected =
        [
            "struct S<T: Fn(i32) -> i32>;",
            "struct S<T: ::Missing>;",
            "struct S<T: Outer<Inner<'static>>>;",
            "struct S<T: Outer<Inner<3>>>;",
            "struct S<T: Outer<Inner<Item = i32>>>;",
            "struct S<T: Outer<Inner<Item: Copy>>>;",
            "struct S<T: Outer<Inner<fn(i32)>>>;",
            "struct S<T: Outer<Inner<::Missing>>>;",
            "struct S<T: Outer<Inner<<i32 as Missing>::Item>>>;",
            "struct S<T: Outer<Inner<dyn Missing>>>;",
            "struct S<T: Outer<Inner<_>>>;",
            "struct S<T: Outer<[i32; const { 1 }]>>;",
            "struct S<T: Outer<[i32; { #[tail] 1 }]>>;",
            "struct S<T: Outer<[i32; ::VALUE]>>;",
            "struct S<T: Outer<[i32; { let ::VALUE = 1; 1 }]>>;",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (string source in rejected)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "deferred-bound.rs", null, deadline.Token);
            AssertEx.True(syntax.IsSuccessful, source + ": " + string.Join("; ", syntax.Diagnostics));
            SafeCoreNameResolutionResult resolution = SafeCoreNameResolution.Resolve(syntax);
            AssertEx.True(!resolution.IsSuccessful && resolution.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
                "Nested bound syntax must receive RSN1007: " + source);
            AssertEx.False(resolution.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnresolvedName),
                "Deferred bound validation must not start binding trait names: " + source);
            AssertEx.False(SafeCoreHirLowering.Lower(syntax).IsSuccessful, "HIR must not silently discard a nested bound extension: " + source);
        }

        foreach (string source in new[]
        {
            "struct S<T: Copy + module::Missing<Other>>;",
            "fn f<T: Outer<Inner<&'static i32>>>() {}",
            "struct S<T: Outer<(Missing, [i32; 3], [Other])>>;",
        })
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "legacy-deferred-bound.rs", null, deadline.Token);
            AssertEx.True(syntax.IsSuccessful, source + ": " + string.Join("; ", syntax.Diagnostics));
            SafeCoreNameResolutionResult resolution = SafeCoreNameResolution.Resolve(syntax);
            AssertEx.True(resolution.IsSuccessful, "Legacy bound names must remain deferred: " + source + ": " + string.Join("; ", resolution.Diagnostics));
            AssertEx.True(SafeCoreHirLowering.Lower(syntax).IsSuccessful, "Legacy bound structure must continue to lower: " + source);
        }
        return Task.CompletedTask;
    }

    private static Task DeferredGenericBoundLimitsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // A minimal bound precedes the depth and operation-budget checks.
        SafeCoreSyntaxResult smoke = SafeCoreSyntax.Parse("struct S<T: Missing>;", "bound-smoke.rs", null, deadline.Token);
        AssertEx.True(SafeCoreNameResolution.Resolve(smoke).IsSuccessful, "A plain deferred bound remains valid.");
        string source = "struct S<T: " + string.Concat(Enumerable.Repeat("Outer<", 12)) + "i32" + new string('>', 12) + ">;";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "bound-limits.rs", null, deadline.Token);
        AssertEx.True(syntax.IsSuccessful, "The nested bound must pass the parser before testing semantic limits.");
        foreach (SafeCoreNameResolutionOptions options in new[]
        {
            new SafeCoreNameResolutionOptions { MaximumNestingDepth = 8 },
            new SafeCoreNameResolutionOptions { MaximumOperations = 40 },
        })
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreNameResolutionResult resolution = SafeCoreNameResolution.Resolve(syntax, options);
            AssertEx.True(resolution.IsTruncated && resolution.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.LimitReached),
                "Deferred structural validation must terminate at its semantic budget.");
        }
        return Task.CompletedTask;
    }
}
