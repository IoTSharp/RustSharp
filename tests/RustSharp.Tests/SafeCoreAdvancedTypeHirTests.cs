using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreAdvancedTypeHirTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("advanced type HIR preserves closure parameters captures and return types", ClosureAsync),
        new("advanced type HIR shares or-pattern binding identities across guarded arms", MatchAsync),
        new("advanced type HIR preserves aggregate reference rest and range patterns", PatternsAsync),
        new("advanced type HIR separates let-else bindings from its else block", LetElseAsync),
        new("advanced type HIR binds constructor aliases and constant blocks", AliasesAndConstAsync),
        new("advanced type HIR keeps legacy boundaries and rejects escaped scopes", BoundariesAsync),
        new("advanced type HIR separates braced types and rejects forbidden pattern shadowing", PatternNamespacesAsync),
    ];

    private static Task ClosureAsync()
    {
        SafeCoreHirResult hir = Lower("fn f() { let outer = 1; let op = move |(left, right): (i32, i32)| -> i32 { left + right + outer }; }");
        SafeCoreHirNode closure = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.ClosureExpression);
        AssertEx.True(closure.Modifiers.HasFlag(SafeCoreHirNodeModifiers.MoveCapture), "The capture modifier must survive lowering.");
        AssertEx.Equal(3, closure.ChildIds.Count);
        AssertEx.Equal(SafeCoreHirNodeKind.Parameter, hir.GetNode(closure.ChildIds[0]).Kind);
        AssertEx.Equal(SafeCoreHirNodeKind.PathType, hir.GetNode(closure.ChildIds[1]).Kind);
        AssertEx.Equal(SafeCoreHirNodeKind.BlockExpression, hir.GetNode(closure.ChildIds[2]).Kind);
        SafeCoreHirNode outer = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.NameExpression && n.Name == "outer");
        AssertEx.Equal(SafeCoreSymbolKind.Local, outer.ReferencedSymbol!.Kind);
        AssertEx.True(hir.Nodes.Where(n => n.Kind == SafeCoreHirNodeKind.NameExpression && n.Name is "left" or "right")
            .All(n => n.ReferencedSymbol?.Kind == SafeCoreSymbolKind.Parameter), "Closure parameters must bind inside their own scope.");
        return Task.CompletedTask;
    }

    private static Task MatchAsync()
    {
        SafeCoreHirResult hir = Lower("enum E { A(i32), B(i32) } fn f(e: E) -> i32 { match e { E::A(x) | E::B(x) if x > 0 => x, _ => 0 } }");
        SafeCoreHirNode[] bindings = hir.Nodes.Where(n => n.Kind == SafeCoreHirNodeKind.IdentifierPattern && n.Name == "x").ToArray();
        AssertEx.Equal(2, bindings.Length);
        AssertEx.True(ReferenceEquals(bindings[0].DeclaredSymbol, bindings[1].DeclaredSymbol),
            "Every alternative must reuse the canonical declaration identity.");
        AssertEx.True(hir.Nodes.Where(n => n.Kind == SafeCoreHirNodeKind.NameExpression && n.Name == "x")
            .All(n => Equals(n.ReferencedSymbol, bindings[0].DeclaredSymbol)), "The arm guard and body must resolve that same binding.");
        SafeCoreHirNode guarded = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.MatchArm && n.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasGuard));
        AssertEx.Equal(3, guarded.ChildIds.Count);
        AssertEx.Equal(SafeCoreHirNodeKind.OrPattern, hir.GetNode(guarded.ChildIds[0]).Kind);
        AssertEx.Equal(SafeCoreHirNodeKind.BinaryExpression, hir.GetNode(guarded.ChildIds[1]).Kind);
        return Task.CompletedTask;
    }

    private static Task PatternsAsync()
    {
        SafeCoreHirResult hir = Lower("""
            struct S { value: i32, flag: bool }
            const LOW: i32 = 1;
            fn f(value: S, values: &[i32]) -> i32 {
                let S { value: ref selected, .. } = value;
                let &[first, ref tail @ ..] = values else { return 0; };
                match first { n @ LOW..=3 => n, _ => *selected }
            }
            """);
        AssertEx.True(hir.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.StructPattern && n.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRest)),
            "Struct rest syntax must remain explicit.");
        AssertEx.True(hir.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.IdentifierPattern && n.Name == "selected" &&
            n.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference)), "By-reference bindings must retain their modifier.");
        AssertEx.True(hir.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.ReferencePattern), "Reference patterns must survive.");
        AssertEx.True(hir.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.SlicePattern), "Slice patterns must survive.");
        AssertEx.True(hir.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.RestPattern), "Rest patterns must survive.");
        SafeCoreHirNode range = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.RangePattern);
        AssertEx.True(range.Modifiers.HasFlag(SafeCoreHirNodeModifiers.InclusiveRange | SafeCoreHirNodeModifiers.HasRangeStart | SafeCoreHirNodeModifiers.HasRangeEnd),
            "The inclusive range endpoints must remain explicit.");
        AssertEx.Equal(SafeCoreSymbolKind.Const, hir.GetNode(range.ChildIds[0]).ReferencedSymbol!.Kind);
        return Task.CompletedTask;
    }

    private static Task LetElseAsync()
    {
        SafeCoreHirResult hir = Lower("enum E { Value(i32), Empty } fn f(e: E) -> i32 { let E::Value(x) = e else { return 0; }; x }");
        SafeCoreHirNode binding = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.LetStatement);
        AssertEx.True(binding.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse), "Let-else must be identifiable without inspecting source text.");
        AssertEx.Equal(SafeCoreHirNodeKind.Block, hir.GetNode(binding.ChildIds[^1]).Kind);
        AssertEx.Equal(SafeCoreSymbolKind.Local, hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.NameExpression && n.Name == "x").ReferencedSymbol!.Kind);
        return Task.CompletedTask;
    }

    private static Task AliasesAndConstAsync()
    {
        SafeCoreHirResult hir = Lower("""
            struct S { value: i32 }
            type Alias = S;
            enum E { Unit, Tuple(i32) }
            type Choice = E;
            fn f() { let s = Alias { value: const { let x = 1; x + 1 } }; let e = Choice::Tuple(s.value); }
            """);
        SafeCoreHirNode alias = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.StructExpression);
        AssertEx.Equal(SafeCoreSymbolKind.TypeAlias, alias.ReferencedSymbol!.Kind);
        AssertEx.Equal(SafeCoreSymbolKind.EnumVariant, hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.NameExpression && n.Name == "Choice::Tuple").ReferencedSymbol!.Kind);
        SafeCoreHirNode constant = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.ConstBlockExpression);
        AssertEx.Equal(SafeCoreHirNodeKind.Block, hir.GetNode(constant.ChildIds[0]).Kind);
        SafeCoreHirResult unitPattern = Lower("struct Unit; fn f(value: Unit) { let Unit = value; }");
        AssertEx.True(unitPattern.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.PathPattern && n.Name == "Unit"),
            "An unqualified unit constructor is a path pattern rather than a fresh binding.");
        return Task.CompletedTask;
    }

    private static Task BoundariesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreSyntaxResult syntax = Parse("fn f() { let closure = |x| x; }", deadline.Token);
        AssertEx.True(SafeCoreHirLowering.Lower(syntax).Diagnostics.Any(d => d.Code == "RSN1007"), "The legacy profile remains unchanged.");
        SafeCoreHirResult escaped = SafeCoreHirLowering.Lower(Parse("fn f() { let closure = |x| x; let escaped = x; }", deadline.Token), Options(deadline.Token));
        AssertEx.True(escaped.Diagnostics.Any(d => d.Code == "RSN1003"), "Closure parameters must not escape their lexical scope.");
        SafeCoreHirResult letElse = SafeCoreHirLowering.Lower(Parse("enum E { A(i32), B } fn f(e: E) { let E::A(x) = e else { let bad = x; return; }; }", deadline.Token), Options(deadline.Token));
        AssertEx.True(letElse.Diagnostics.Any(d => d.Code == "RSN1003"), "Successful pattern bindings do not enter the let-else failure block.");
        SafeCoreHirResult tupleAlias = SafeCoreHirLowering.Lower(Parse("struct S(i32); type Alias = S; fn f() { let s = Alias(1); }", deadline.Token), Options(deadline.Token));
        AssertEx.True(tupleAlias.Diagnostics.Any(d => d.Code == "RSN1003"), "A tuple-struct type alias does not introduce a value constructor.");
        return Task.CompletedTask;
    }

    private static Task PatternNamespacesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string[] rejected =
        [
            "const C: i32 = 1; fn f() { let mut C = 2; }",
            "const C: i32 = 1; fn f() { let ref C = 2; }",
            "const C: i32 = 1; fn f(mut C: i32) {}",
            "const C: i32 = 1; fn f(ref C: i32) {}",
            "struct Unit; fn f() { let mut Unit = 1; }",
            "struct Tuple(i32); fn f() { let ref Tuple = 1; }",
        ];
        for (int index = 0; index < rejected.Length; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            SafeCoreHirResult result = SafeCoreHirLowering.Lower(Parse(rejected[index], deadline.Token), Options(deadline.Token));
            AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol),
                $"Constant and constructor shadowing must fail: {rejected[index]}");
        }
        SafeCoreHirResult braced = Lower("struct S { value: i32 } fn f() { let S = 1; let mut T = S; }");
        AssertEx.True(braced.Nodes.Any(n => n.Kind == SafeCoreHirNodeKind.IdentifierPattern && n.Name == "S"),
            "A braced struct does not introduce a value constructor that shadows local bindings.");
        SafeCoreHirResult namespacePair = Lower("struct S { value: i32 } fn S() -> i32 { 1 } fn f() { let value = S(); }");
        AssertEx.Equal(SafeCoreSymbolKind.Function,
            namespacePair.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.NameExpression && n.Name == "S").ReferencedSymbol!.Kind);
        return Task.CompletedTask;
    }

    private static SafeCoreHirResult Lower(string source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(Parse(source, deadline.Token), Options(deadline.Token));
        AssertEx.True(hir.IsSuccessful, string.Join("; ", hir.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return hir;
    }

    private static SafeCoreSyntaxResult Parse(string source, CancellationToken cancellationToken)
    {
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "advanced-type-hir.rs", new() { Timeout = TimeSpan.FromSeconds(5) }, cancellationToken);
        AssertEx.True(syntax.IsSuccessful, string.Join("; ", syntax.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return syntax;
    }

    private static SafeCoreHirLoweringOptions Options(CancellationToken cancellationToken) => new()
    {
        Timeout = TimeSpan.FromSeconds(5), CancellationToken = cancellationToken,
        NameResolution = new() { EnableTypeSystemExtensions = true, CancellationToken = cancellationToken, Timeout = TimeSpan.FromSeconds(5) },
    };
}
