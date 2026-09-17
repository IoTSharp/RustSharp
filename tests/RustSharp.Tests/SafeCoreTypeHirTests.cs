using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreTypeHirTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("safe-core type HIR extensions remain opt-in", PreservesLegacyBoundaryAsync),
        new("safe-core type HIR preserves function and aggregate shapes", PreservesExtendedShapesAsync),
        new("safe-core type HIR collects nested aggregate and loop scopes", PreservesNestedScopesAsync),
        new("safe-core type HIR rejects unbound signature names and unsupported labels", RejectsUnsupportedAsync),
        new("safe-core type HIR extensions observe node and operation budgets", PreservesBudgetsAsync),
    ];

    private static readonly SafeCoreHirLoweringOptions Extended = new()
    {
        NameResolution = new() { EnableTypeSystemExtensions = true },
    };

    private static Task PreservesLegacyBoundaryAsync()
    {
        SafeCoreSyntaxResult syntax = Parse("fn use_fn(f: fn(i32) -> i32) -> i32 { f(1) }");
        SafeCoreHirResult legacy = SafeCoreHirLowering.Lower(syntax);
        AssertEx.True(legacy.Diagnostics.Any(d => d.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
            "The existing semantic profile must keep its RSN1007 boundary.");
        AssertEx.True(SafeCoreHirLowering.Lower(syntax, Extended).IsSuccessful,
            "Bare function types must lower with the type-system extension enabled.");
        return Task.CompletedTask;
    }

    private static Task PreservesExtendedShapesAsync()
    {
        const string source = """
            struct Pair { first: i32, second: i32 }
            enum Choice { Unit, Tuple(), Named { value: i32 }, Empty {} }
            fn apply(f: fn(i32) -> i32, effect: fn()) -> i32 {
                let pair: _ = Pair { first: 1, second: 2 };
                let updated = Pair { first: 3, ..pair };
                let choice = Choice::Named { value: updated.first as i32 };
                f(updated.second)
            }
            """;
        SafeCoreHirResult hir = Lower(source);
        SafeCoreHirNode[] functions = hir.Nodes.Where(n => n.Kind == SafeCoreHirNodeKind.FunctionType).ToArray();
        AssertEx.Equal(2, functions.Length);
        AssertEx.Equal(2, functions[0].ChildIds.Count);
        AssertEx.Equal(1, functions[1].ChildIds.Count);
        AssertEx.Equal(SafeCoreHirNodeKind.UnitType, hir.GetNode(functions[1].ChildIds[0]).Kind);
        AssertEx.True(hir.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.InferredType), "Inference placeholders must survive lowering.");
        SafeCoreHirNode updated = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.StructExpression &&
            n.Modifiers.HasFlag(SafeCoreHirNodeModifiers.StructUpdate));
        AssertEx.Equal(2, updated.ChildIds.Count);
        AssertEx.Equal(SafeCoreHirNodeKind.StructExpressionField, hir.GetNode(updated.ChildIds[0]).Kind);
        AssertEx.Equal(SafeCoreHirNodeKind.NameExpression, hir.GetNode(updated.ChildIds[1]).Kind);
        SafeCoreHirNode named = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.StructExpression && n.Name == "Choice::Named");
        AssertEx.Equal(SafeCoreSymbolKind.EnumVariant, named.ReferencedSymbol!.Kind);
        SafeCoreHirNode declaredField = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.Field && n.Name == "value");
        AssertEx.Equal("crate::Choice::Named::value", declaredField.DeclaredSymbol!.QualifiedName);
        AssertEx.True(hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.EnumVariant && n.Name == "Unit")
            .Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct), "Unit variant shape must survive lowering.");
        AssertEx.True(hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.EnumVariant && n.Name == "Tuple")
            .Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct), "Empty tuple variants must remain constructors.");
        AssertEx.Equal(SafeCoreHirNodeModifiers.None,
            hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.EnumVariant && n.Name == "Empty").Modifiers);
        SafeCoreHirNode cast = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.CastExpression);
        AssertEx.Equal(SafeCoreHirNodeKind.MemberExpression, hir.GetNode(cast.ChildIds[0]).Kind);
        AssertEx.Equal(SafeCoreHirNodeKind.PathType, hir.GetNode(cast.ChildIds[1]).Kind);
        AssertEx.True(hir.Nodes.Where(n => n.Kind == SafeCoreHirNodeKind.LiteralExpression).All(n => n.Name is not null),
            "The extended HIR must retain token kinds for literal typing.");
        return Task.CompletedTask;
    }

    private static Task PreservesNestedScopesAsync()
    {
        SafeCoreHirResult hir = Lower("""
            struct Value { value: i32 }
            fn choose(flag: bool) -> i32 {
                let value = Value { value: { let inner = 1; inner } };
                while flag { let inner = 2; continue; }
                loop { let inner = value.value; break inner; }
            }
            """);
        SafeCoreHirNode[] uses = hir.Nodes.Where(n => n.Kind == SafeCoreHirNodeKind.NameExpression && n.Name == "inner").ToArray();
        AssertEx.Equal(2, uses.Length);
        AssertEx.False(uses[0].ReferencedSymbol!.Equals(uses[1].ReferencedSymbol),
            "Bindings inside aggregate values and loops must use distinct lexical scopes.");
        AssertEx.True(hir.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.ContinueExpression), "Continue expressions must be retained.");
        SafeCoreHirNode loop = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.WhileExpression);
        AssertEx.Equal(SafeCoreHirNodeKind.NameExpression, hir.GetNode(loop.ChildIds[0]).Kind);
        AssertEx.Equal(SafeCoreHirNodeKind.Block, hir.GetNode(loop.ChildIds[1]).Kind);
        return Task.CompletedTask;
    }

    private static Task RejectsUnsupportedAsync()
    {
        SafeCoreHirResult unbound = SafeCoreHirLowering.Lower(Parse("fn apply(f: fn(Missing)) {}"), Extended);
        AssertEx.True(unbound.Diagnostics.Any(d => d.Code == SafeCoreNameResolutionDiagnosticCodes.UnresolvedName),
            "Bare function signature types must resolve their names.");
        SafeCoreHirResult labeled = SafeCoreHirLowering.Lower(Parse("fn run() { 'outer: loop { break 'outer; } }"), Extended);
        AssertEx.True(labeled.Diagnostics.Any(d => d.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
            "Labels remain outside the bounded type-system profile.");
        SafeCoreHirResult higherRanked = SafeCoreHirLowering.Lower(Parse("fn use_fn(f: for<'a> fn(&'a i32)) {}"), Extended);
        AssertEx.True(higherRanked.Diagnostics.Any(d => d.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
            "Higher-ranked function types require the later generic profile.");
        return Task.CompletedTask;
    }

    private static Task PreservesBudgetsAsync()
    {
        SafeCoreSyntaxResult syntax = Parse("fn apply(f: fn(i32) -> i32) -> i32 { f(1) }");
        SafeCoreHirResult nodes = SafeCoreHirLowering.Lower(syntax, Extended with { MaximumNodes = 2 });
        AssertEx.True(nodes.IsTruncated && nodes.Nodes.Count <= 2, "Extended nodes obey the HIR arena limit.");
        SafeCoreNameResolutionResult resolution = SafeCoreNameResolution.Resolve(syntax,
            Extended.NameResolution with { MaximumOperations = 2 });
        AssertEx.True(resolution.IsTruncated, "Extended resolution observes the operation budget.");
        return Task.CompletedTask;
    }

    private static SafeCoreHirResult Lower(string source)
    {
        SafeCoreHirResult result = SafeCoreHirLowering.Lower(Parse(source), Extended);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result;
    }

    private static SafeCoreSyntaxResult Parse(string source)
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "type-hir.rs");
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result;
    }
}
