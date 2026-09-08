using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SyntaxItemExpansionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("syntax retains typed lifetime const generic defaults and where predicates", GenericsAsync),
        new("syntax distinguishes complete generic arguments and associated constraints", GenericArgumentsAsync),
        new("syntax represents bare function and higher-ranked trait types", FunctionTypesAsync),
        new("syntax retains trait implementation associated items and self receivers", AssociatedItemsAsync),
        new("syntax retains enum shapes attributes and discriminant expressions", EnumShapesAsync),
        new("syntax rejects missing generic item and associated declaration children", MalformedItemsAsync),
        new("syntax bounds deeply nested generic binders and function types", ItemLimitsAsync),
    ];

    private static Task GenericsAsync()
    {
        const string source = "struct Buffer<'a: 'b, 'b, T: ?Sized + 'a = u8, const N: usize = { 2 + 2 }> where T: Copy, 'a: 'b { value: &'a T, data: [u8; N] }";
        SafeCoreSyntaxResult result = Pass(source);
        var item = (SafeCoreStructSyntax)result.Root!.Items.Single();
        AssertEx.Equal(4, item.GenericParameters.Count);
        AssertEx.Equal(SafeCoreGenericParameterKind.Lifetime, item.GenericParameters[0].Kind);
        AssertEx.Equal(0, item.GenericParameters[0].Bounds.Count);
        AssertEx.Equal("'b", ((SafeCoreLifetimeBoundSyntax)item.GenericParameters[0].Constraints.Single()).Lifetime);
        AssertEx.True(item.GenericParameters[2].Constraints[0] is SafeCoreTraitBoundSyntax { IsOptional: true }, "The ?Sized modifier must survive parsing.");
        AssertEx.True(item.GenericParameters[2].DefaultType is SafeCorePathTypeSyntax, "A type default must have type structure.");
        AssertEx.Equal(SafeCoreGenericParameterKind.Const, item.GenericParameters[3].Kind);
        AssertEx.True(item.GenericParameters[3].ConstType is SafeCorePathTypeSyntax, "A const parameter must retain its required type.");
        AssertEx.True(item.GenericParameters[3].DefaultValue is SafeCoreBlockExpressionSyntax { Block.TailExpression: SafeCoreBinaryExpressionSyntax { Operator: "+" } }, "A braced default must preserve its expression AST.");
        AssertEx.Equal(2, item.WhereClause!.Predicates.Count);
        AssertEx.True(item.WhereClause.Predicates[1] is SafeCoreLifetimeWherePredicateSyntax { Lifetime: "'a" }, "Lifetime predicates must not become type predicates.");
        AssertEx.Equal("where T: Copy, 'a: 'b", result.GetText(item.WhereClause.Span));
        Pass("struct Pair<T>(T) where T: Copy; struct Unit<T> where T: Copy; type Project<T> where T: Trait = <T as Trait>::Item;");
        return Task.CompletedTask;
    }

    private static Task GenericArgumentsAsync()
    {
        const string source = "type A<T> = Outer<'static, T, 3, { 1 + 2 }, -1, Item<'static>=u8, Item: Copy>;";
        var alias = (SafeCoreTypeAliasSyntax)Pass(source).Root!.Items.Single();
        SafeCorePathSegmentSyntax segment = ((SafeCorePathTypeSyntax)alias.Type).Segments.Single();
        AssertEx.Equal(7, segment.Arguments.Count);
        AssertEx.Equal(1, segment.GenericArguments.Count);
        AssertEx.True(segment.Arguments[0] is SafeCoreLifetimeArgumentSyntax { Lifetime: "'static" }, "A lifetime argument must have its own node.");
        AssertEx.True(segment.Arguments[2] is SafeCoreConstArgumentSyntax { Value: SafeCoreLiteralExpressionSyntax }, "Literal const arguments need expression nodes.");
        AssertEx.True(segment.Arguments[3] is SafeCoreConstArgumentSyntax { Value: SafeCoreBlockExpressionSyntax }, "Braced const arguments need block nodes.");
        AssertEx.True(segment.Arguments[4] is SafeCoreConstArgumentSyntax { Value: SafeCoreUnaryExpressionSyntax { Operator: "-" } }, "Negative const literals must retain unary structure.");
        AssertEx.True(segment.Arguments[5] is SafeCoreAssociatedTypeArgumentSyntax { Name: "Item", Arguments.Count: 1 }, "Generic associated equality must retain the constrained arguments.");
        AssertEx.True(segment.Arguments[6] is SafeCoreAssociatedConstraintArgumentSyntax { Name: "Item", Bounds.Count: 1 }, "Associated bounds must remain structured.");
        const string joint = "type A = Outer<<T as Trait>::Item>; fn f() { call::<<T as Trait>::Item>(); value.method::<<T as Trait>::Item>(); }";
        SafeCoreSyntaxResult split = Pass(joint);
        var outer = (SafeCorePathTypeSyntax)((SafeCoreTypeAliasSyntax)split.Root!.Items[0]).Type;
        var qualified = (SafeCoreQualifiedPathTypeSyntax)outer.Segments.Single().GenericArguments.Single();
        AssertEx.Equal("<T as Trait>::Item", split.GetText(qualified.Span));
        AssertEx.Equal(joint, split.LexResult.ToSourceText());
        AssertEx.True(split.LexResult.Tokens.Any(static token => token.Text == "<<"), "Splitting generic openers must not alter lexical shift tokens.");
        return Task.CompletedTask;
    }

    private static Task FunctionTypesAsync()
    {
        const string source = "type Callback = for<'a> fn(#[cfg(enabled)] value: &'a str, _: i32) -> bool; type Closure = dyn for<'b> Fn(&'b str) -> usize + 'static; type Opaque = impl Copy + 'static;";
        SafeCoreCompilationUnitSyntax root = Pass(source).Root!;
        var function = (SafeCoreFunctionTypeSyntax)((SafeCoreTypeAliasSyntax)root.Items[0]).Type;
        AssertEx.Equal(SafeCoreGenericParameterKind.Lifetime, function.GenericParameters.Single().Kind);
        AssertEx.Equal("value", function.Parameters[0].Name!);
        AssertEx.Equal("cfg", function.Parameters[0].Attributes.Single().Path);
        AssertEx.True(function.Parameters[0].Type is SafeCoreReferenceTypeSyntax { Lifetime: "'a" }, "Function parameter lifetimes must survive.");
        var dynamic = (SafeCoreBoundedTypeSyntax)((SafeCoreTypeAliasSyntax)root.Items[1]).Type;
        var bound = (SafeCoreTraitBoundSyntax)dynamic.Bounds[0];
        AssertEx.Equal("'b", bound.GenericParameters.Single().Name);
        AssertEx.True(((SafeCorePathTypeSyntax)bound.Type).Segments[0] is { HasFunctionArguments: true, FunctionParameters.Count: 1, FunctionReturnType: not null }, "Fn path parameters and return type must survive.");
        AssertEx.True(((SafeCoreTypeAliasSyntax)root.Items[2]).Type is SafeCoreBoundedTypeSyntax { IsDynamic: false }, "Opaque and dynamic bounded types must remain distinct.");
        Pass("fn project<T>() where for<'a> &'a T: Into<u8>, <T as Trait>::Item: Copy {}");
        Pass("type P = &(dyn Copy + Send); type F = dyn Fn::() -> u8 + Send; type E = Vec<>;");
        var outputBound = (SafeCoreBoundedTypeSyntax)((SafeCoreTypeAliasSyntax)Pass("type F = dyn Fn() -> dyn Base + Send;").Root!.Items.Single()).Type;
        AssertEx.Equal(2, outputBound.Bounds.Count);
        AssertEx.True(((SafeCorePathTypeSyntax)((SafeCoreTraitBoundSyntax)outputBound.Bounds[0]).Type).Segments[0].FunctionReturnType is SafeCoreBoundedTypeSyntax { Bounds.Count: 1 }, "TypeNoBounds function-trait output must leave the following '+' for the outer bound list.");
        return Task.CompletedTask;
    }

    private static Task AssociatedItemsAsync()
    {
        const string source = "trait Stream<'a, const N: usize>: Sized where Self: 'a { type Item<'b>: Copy where Self: 'b; const SIZE: usize; fn next(&'a mut self) -> Self::Item<'a>; fn take(mut self) {} } impl<'a, const N: usize> Stream<'a, N> for Buffer<'a, N> where Self: Sized { type Item<'b> = u8 where Self: 'b; const SIZE: usize = N; fn next(&'a mut self) -> Self::Item<'a> { 0 } fn take(mut self) {} } impl Buffer<'static, 4> { pub(crate) fn typed(self: Box<Self>) {} }";
        SafeCoreCompilationUnitSyntax root = Pass(source).Root!;
        var trait = (SafeCoreTraitSyntax)root.Items[0];
        AssertEx.Equal(4, trait.Items.Count);
        AssertEx.True(trait.Items[0] is SafeCoreAssociatedTypeSyntax { Type: null, GenericParameters.Count: 1, Bounds.Count: 1, WhereClause: not null }, "A generic associated declaration must retain its signature.");
        AssertEx.True(trait.Items[1] is SafeCoreAssociatedConstSyntax { Value: null }, "A trait constant declaration may omit its value.");
        var declaration = (SafeCoreAssociatedFunctionSyntax)trait.Items[2];
        AssertEx.True(declaration.Body is null, "Trait method declarations must not acquire a synthetic body.");
        AssertEx.True(declaration.Parameters[0].Receiver is { IsByReference: true, IsMutable: true, Lifetime: "'a" }, "Borrowed receivers need structured syntax.");
        var implementation = (SafeCoreImplSyntax)root.Items[1];
        AssertEx.True(implementation.Trait is SafeCorePathTypeSyntax, "Trait and inherent implementations must remain distinct.");
        AssertEx.True(implementation.Items[0] is SafeCoreAssociatedTypeSyntax { Type: SafeCorePathTypeSyntax, WhereClause: not null }, "Implementation associated types must retain assignments and trailing where clauses.");
        var typed = (SafeCoreAssociatedFunctionSyntax)((SafeCoreImplSyntax)root.Items[2]).Items.Single();
        AssertEx.Equal(SafeCoreVisibilityKind.Crate, typed.Visibility.Kind);
        AssertEx.True(typed.Parameters[0].Receiver is { ExplicitType: SafeCorePathTypeSyntax }, "Typed self must retain its explicit type.");
        return Task.CompletedTask;
    }

    private static Task EnumShapesAsync()
    {
        const string source = "enum E { #[marker] Empty {}, Tuple(), Named { #[field] key: i32 }, Unit = -1, Next = 2 + 3, }";
        SafeCoreSyntaxResult result = Pass(source);
        var item = (SafeCoreEnumSyntax)result.Root!.Items.Single();
        AssertEx.Equal(SafeCoreEnumVariantKind.Struct, item.Variants[0].Kind);
        AssertEx.Equal(SafeCoreEnumVariantKind.Tuple, item.Variants[1].Kind);
        AssertEx.Equal("marker", item.Variants[0].Attributes.Single().Path);
        AssertEx.Equal("field", item.Variants[2].Fields.Single().Attributes.Single().Path);
        AssertEx.True(item.Variants[3].Discriminant is SafeCoreUnaryExpressionSyntax { Operator: "-" }, "Discriminants must retain expression nodes.");
        AssertEx.True(item.Variants[4].Discriminant is SafeCoreBinaryExpressionSyntax { Operator: "+" }, "Discriminant operators must not be discarded.");
        AssertEx.Equal("Next = 2 + 3", result.GetText(item.Variants[4].Span));
        return Task.CompletedTask;
    }

    private static Task MalformedItemsAsync()
    {
        string[] sources =
        [
            "struct S<T = >;", "struct S<const N: usize = >;", "struct S<const N: >;",
            "struct S<'a = 'b>;", "struct S<T> where T Copy {}", "fn f() where : Copy {}",
            "fn f() -> {}", "fn f();", "fn f(self: Self) {}", "type F = fn(i32) ->;",
            "type A = V<'a, Item = >;", "type A = V<1 + 2>;", "enum E { V =, }",
            "enum E { V { key: } }", "impl T { fn f(); }", "impl T { type A; }",
            "impl T { const N: usize; }", "trait T { type A =; }", "trait T { const N: usize =; }",
            "trait T { fn f(x: i32, self); }", "trait T { fn f(&self: T); }", "type F = for<T> fn(T);",
            "trait T { unsafe fn f(); }", "impl T { async fn f() {} }", "type F = extern fn();",
            "type A =", "type A = ::", "type A = Foo::", "fn f() ->", "const N:",
            "type A = Foo<>::<u8>;", "type A = Foo<>(u8);", "type A = Foo<u8>(u8);",
            "type A = &dyn Copy + Send;", "type A = fn() -> dyn Copy + Send;",
        ];
        var clock = Stopwatch.StartNew();
        foreach (string source in sources)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30)) { throw new TimeoutException("Item malformed corpus deadline exceeded."); }
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "item-malformed.rs", new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromSeconds(1) });
            AssertEx.True(!result.IsSuccessful && !result.IsTruncated && result.Diagnostics.Count != 0, $"Malformed item unexpectedly accepted: {source}");
        }
        return Task.CompletedTask;
    }

    private static Task ItemLimitsAsync()
    {
        string source = "type F = " + string.Concat(Enumerable.Repeat("fn(", 48)) + "i32" + new string(')', 48) + ";";
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "nested-function.rs", new SafeCoreSyntaxOptions
        {
            MaximumNestingDepth = 12,
            MaximumOperations = 256,
            Timeout = TimeSpan.FromSeconds(1),
        });
        AssertEx.True(result.IsTruncated && result.Root is null, "Recursive function types must honor the shared syntax limit.");
        string binderSource = "struct S<T: " + string.Concat(Enumerable.Repeat("for<T: ", 48)) + "Copy" + new string('>', 48) + " Bound>;";
        SafeCoreSyntaxResult binder = SafeCoreSyntax.Parse(binderSource, "nested-binders.rs", new SafeCoreSyntaxOptions
        {
            MaximumNestingDepth = 12,
            MaximumOperations = 256,
            Timeout = TimeSpan.FromSeconds(1),
        });
        AssertEx.True(binder.IsTruncated && binder.Root is null, "Higher-ranked generic recursion without lexical delimiters must honor syntax depth limits.");
        return Task.CompletedTask;
    }

    private static SafeCoreSyntaxResult Pass(string source)
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "item-expansion.rs", new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromSeconds(2) });
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return result;
    }
}
