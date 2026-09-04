using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreHirTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("safe-core HIR binds declarations and references deterministically", BindsSymbolsAsync),
        new("safe-core HIR binds NFC-equivalent identifiers", BindsUnicodeIdentifiersAsync),
        new("safe-core HIR preserves expression type and pattern shapes", PreservesShapesAsync),
        new("safe-core HIR rejects invalid input and obeys limits", RejectsInvalidAndBoundedAsync),
    ];

    private static Task BindsSymbolsAsync()
    {
        const string source =
            "#![no_std]\n" +
            "pub mod model {\n" +
            "    pub struct Pair<T> { pub first: T, second: i32 }\n" +
            "    pub enum Choice<T> { One(T), None, }\n" +
            "    pub const LIMIT: usize = 4;\n" +
            "    pub fn identity<T>(value: T) -> T {\n" +
            "        let r#value: T = value; return value;\n" +
            "    }\n" +
            "}\n" +
            "use crate::model::identity as apply;\n" +
            "fn main(input: i32) -> i32 { apply(input) }\n";

        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "hir-bindings.rs");
        AssertEx.True(syntax.IsSuccessful, FormatDiagnostics(syntax.Diagnostics));

        SafeCoreHirResult first = SafeCoreHirLowering.Lower(syntax);
        SafeCoreHirResult second = SafeCoreHirLowering.Lower(syntax);
        AssertEx.True(first.IsSuccessful, FormatDiagnostics(first.Diagnostics));
        AssertEx.True(second.IsSuccessful, FormatDiagnostics(second.Diagnostics));
        AssertEx.Equal(Snapshot(first), Snapshot(second), "HIR lowering must be deterministic.");
        AssertEx.Equal(0, first.Root!.Id, "The root must receive the first stable arena ID.");

        for (var index = 0; index < first.Nodes.Count; index++)
        {
            SafeCoreHirNode node = first.Nodes[index];
            AssertEx.Equal(index, node.Id, "Arena IDs must be contiguous and indexable.");
            AssertEx.True(
                node.ChildIds.All(childId => childId > node.Id && childId < first.Nodes.Count),
                "Preorder child IDs must point forward inside the same arena.");
        }

        SafeCoreHirNode module = first.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.Module && node.Name == "model");
        AssertEx.Equal("crate::model", module.DeclaredSymbol!.QualifiedName);
        AssertEx.True(
            module.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Public),
            "Public visibility must be retained.");

        SafeCoreHirNode importedCall = first.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.NameExpression && node.Name == "apply");
        AssertEx.Equal(SafeCoreSymbolKind.Import, importedCall.ReferencedSymbol!.Kind);
        AssertEx.Equal("crate::apply", importedCall.ReferencedSymbol.QualifiedName);

        SafeCoreHirNode[] valueUses = first.Nodes
            .Where(node => node.Kind == SafeCoreHirNodeKind.NameExpression && node.Name == "value")
            .ToArray();
        AssertEx.Equal(2, valueUses.Length);
        AssertEx.Equal(SafeCoreSymbolKind.Parameter, valueUses[0].ReferencedSymbol!.Kind);
        AssertEx.Equal(SafeCoreSymbolKind.Local, valueUses[1].ReferencedSymbol!.Kind);

        SafeCoreHirNode rawBinding = first.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.IdentifierPattern && node.Name == "r#value");
        AssertEx.Equal(
            "value",
            rawBinding.DeclaredSymbol!.Name,
            "HIR must retain source spelling while symbols use canonical identifier names.");
        return Task.CompletedTask;
    }

    private static Task PreservesShapesAsync()
    {
        const string source =
            "fn compute(value: &mut [i32; 2], flag: bool) -> i32 {\n" +
            "    let (left, right): (i32, i32) = (1, 2);\n" +
            "    let repeated: [i32; 2] = [left; 2];\n" +
            "    let mut result: i32 = value[0] + right * 3;\n" +
            "    if flag { result } else { return repeated[0]; }\n" +
            "}\n";

        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "hir-shapes.rs");
        AssertEx.True(syntax.IsSuccessful, FormatDiagnostics(syntax.Diagnostics));
        SafeCoreHirResult result = SafeCoreHirLowering.Lower(syntax);
        AssertEx.True(result.IsSuccessful, FormatDiagnostics(result.Diagnostics));

        AssertKind(result, SafeCoreHirNodeKind.ReferenceType);
        AssertKind(result, SafeCoreHirNodeKind.ArrayType);
        AssertKind(result, SafeCoreHirNodeKind.TupleType);
        AssertKind(result, SafeCoreHirNodeKind.TuplePattern);
        AssertKind(result, SafeCoreHirNodeKind.TupleExpression);
        AssertKind(result, SafeCoreHirNodeKind.BinaryExpression);
        AssertKind(result, SafeCoreHirNodeKind.IndexExpression);
        AssertKind(result, SafeCoreHirNodeKind.IfExpression);
        AssertKind(result, SafeCoreHirNodeKind.ReturnStatement);

        SafeCoreHirNode reference = result.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.ReferenceType);
        AssertEx.True(
            reference.Modifiers.HasFlag(SafeCoreHirNodeModifiers.MutableReference),
            "Mutable references must retain their modifier.");
        SafeCoreHirNode repeated = result.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.ArrayExpression);
        AssertEx.True(
            repeated.Modifiers.HasFlag(SafeCoreHirNodeModifiers.RepeatedArray),
            "Repeated arrays must remain distinguishable from list arrays.");
        SafeCoreHirNode mutableBinding = result.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.IdentifierPattern && node.Name == "result");
        AssertEx.True(
            mutableBinding.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable),
            "Mutable bindings must retain their modifier.");
        return Task.CompletedTask;
    }

    private static Task BindsUnicodeIdentifiersAsync()
    {
        const string decomposedName = "e\u0301";
        const string composedName = "\u00e9";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(
            $"fn {decomposedName}() {{}} fn caller() {{ {composedName}(); }}",
            "hir-unicode.rs");
        AssertEx.True(syntax.IsSuccessful, FormatDiagnostics(syntax.Diagnostics));

        SafeCoreHirResult result = SafeCoreHirLowering.Lower(syntax);
        AssertEx.True(result.IsSuccessful, FormatDiagnostics(result.Diagnostics));
        SafeCoreHirNode declaration = result.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.Function && node.Name == decomposedName);
        SafeCoreHirNode reference = result.Nodes.Single(node =>
            node.Kind == SafeCoreHirNodeKind.NameExpression && node.Name == composedName);
        SafeCoreSymbol declaredSymbol = AssertEx.NotNull(
            declaration.DeclaredSymbol,
            "The decomposed declaration must bind to its canonical symbol.");
        SafeCoreSymbol referencedSymbol = AssertEx.NotNull(
            reference.ReferencedSymbol,
            "The composed reference must bind to the canonical declaration.");
        AssertEx.Equal(composedName, declaredSymbol.Name);
        AssertEx.Equal(declaredSymbol, referencedSymbol);
        return Task.CompletedTask;
    }

    private static Task RejectsInvalidAndBoundedAsync()
    {
        SafeCoreSyntaxResult malformed = SafeCoreSyntax.Parse(
            "fn broken(value: i32 { value }",
            "hir-malformed.rs");
        SafeCoreHirResult malformedHir = SafeCoreHirLowering.Lower(malformed);
        AssertEx.False(malformedHir.IsSuccessful, "Malformed syntax must not produce successful HIR.");
        AssertEx.True(malformedHir.Root is null, "Malformed syntax must not expose a HIR root.");
        AssertEx.Equal(
            string.Join(",", malformed.Diagnostics.Select(static diagnostic => diagnostic.Code)),
            string.Join(",", malformedHir.Diagnostics.Select(static diagnostic => diagnostic.Code)),
            "Syntax diagnostics must survive the rejected lowering unchanged.");

        SafeCoreSyntaxResult unresolved = SafeCoreSyntax.Parse(
            "fn main() { missing(); }",
            "hir-unresolved.rs");
        AssertEx.True(unresolved.IsSuccessful, FormatDiagnostics(unresolved.Diagnostics));
        SafeCoreHirResult unresolvedHir = SafeCoreHirLowering.Lower(unresolved);
        AssertEx.False(unresolvedHir.IsSuccessful, "Unresolved names must not produce successful HIR.");
        AssertEx.True(unresolvedHir.Root is null, "Failed name resolution must not expose a HIR root.");
        AssertEx.True(
            unresolvedHir.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnresolvedName),
            "Name-resolution diagnostics must survive the rejected lowering.");

        SafeCoreSyntaxResult valid = SafeCoreSyntax.Parse(
            "fn main(value: i32) -> i32 { let value: i32 = value; value }",
            "hir-bounded.rs");
        AssertEx.True(valid.IsSuccessful, FormatDiagnostics(valid.Diagnostics));
        SafeCoreHirResult nodeLimited = SafeCoreHirLowering.Lower(
            valid,
            new SafeCoreHirLoweringOptions
            {
                MaximumNodes = 4,
                MaximumNestingDepth = 32,
                MaximumOperations = 256,
                MaximumDiagnostics = 8,
            });
        AssertLimited(nodeLimited, 4);

        SafeCoreHirResult depthLimited = SafeCoreHirLowering.Lower(
            valid,
            new SafeCoreHirLoweringOptions
            {
                MaximumNodes = 64,
                MaximumNestingDepth = 1,
                MaximumOperations = 256,
                MaximumDiagnostics = 8,
            });
        AssertLimited(depthLimited, 64);

        SafeCoreHirResult operationLimited = SafeCoreHirLowering.Lower(
            valid,
            new SafeCoreHirLoweringOptions
            {
                MaximumNodes = 64,
                MaximumNestingDepth = 32,
                MaximumOperations = 1,
                MaximumDiagnostics = 8,
            });
        AssertLimited(operationLimited, 64);

        SafeCoreSyntaxResult unresolvedMany = SafeCoreSyntax.Parse(
            "fn main() { missing(); also_missing(); }",
            "hir-diagnostic-limits.rs");
        AssertEx.True(unresolvedMany.IsSuccessful, FormatDiagnostics(unresolvedMany.Diagnostics));
        SafeCoreHirResult diagnosticLimited = SafeCoreHirLowering.Lower(
            unresolvedMany,
            new SafeCoreHirLoweringOptions
            {
                MaximumDiagnostics = 1,
                MaximumDiagnosticMessageLength = 12,
            });
        AssertEx.True(diagnosticLimited.IsTruncated, "Truncated dependency diagnostics must be explicit.");
        AssertEx.Equal(1, diagnosticLimited.Diagnostics.Count);
        AssertEx.Equal(SafeCoreHirDiagnosticCodes.LimitReached, diagnosticLimited.Diagnostics[0].Code);
        AssertEx.True(
            diagnosticLimited.Diagnostics[0].Message.Length <= 12,
            "HIR diagnostic messages must honor the configured length bound.");
        return Task.CompletedTask;
    }

    private static void AssertKind(SafeCoreHirResult result, SafeCoreHirNodeKind kind) =>
        AssertEx.True(
            result.Nodes.Any(node => node.Kind == kind),
            $"HIR must contain a {kind} node.");

    private static void AssertLimited(SafeCoreHirResult result, int maximumNodes)
    {
        AssertEx.True(result.IsTruncated, "A lowering limit must mark the result as truncated.");
        AssertEx.True(result.Nodes.Count <= maximumNodes, "HIR nodes must remain within the configured bound.");
        foreach (SafeCoreHirNode node in result.Nodes)
        {
            AssertEx.True(
                node.ChildIds.All(childId => childId > node.Id && childId < result.Nodes.Count),
                "Truncated HIR child IDs must remain forward-pointing and inside the arena.");
        }

        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreHirDiagnosticCodes.LimitReached),
            "A lowering limit must emit RSH0002.");
    }

    private static string Snapshot(SafeCoreHirResult result) => string.Join(
        "\n",
        result.Nodes.Select(node => string.Join(
            "|",
            node.Id,
            node.Kind,
            node.Span.Start,
            node.Span.Length,
            node.Name,
            node.Value,
            node.Modifiers,
            node.DeclaredSymbol?.QualifiedName,
            node.ReferencedSymbol?.QualifiedName,
            string.Join(",", node.ChildIds))));

    private static string FormatDiagnostics(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join(
            "; ",
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.Message} [{diagnostic.Span.Start},{diagnostic.Span.Length}]"));
}
