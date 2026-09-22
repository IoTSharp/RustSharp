using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreGenericHirBindingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("safe-core generic binding closes inferred source and HIR instances", InferredIdentityAsync),
        new("safe-core generic binding follows canonical imported callees", ImportedIdentityAsync),
        new("safe-core generic binding propagates caller substitutions through nested calls", NestedPropagationAsync),
        new("safe-core generic binding is deterministic across repeated runs", DeterministicAsync),
        new("safe-core generic binding rejects missing inference without a partial plan", MissingInferenceAsync),
        new("safe-core generic binding reports the turbofish profile boundary", ExplicitArgumentBoundaryAsync),
        new("safe-core generic binding observes its operation budget", BudgetAsync),
    ];

    private static Task InferredIdentityAsync()
    {
        const string source = "fn identity<T>(value: T) -> T { value } fn main() { identity(42); }";
        SafeCoreGenericHirBindingResult result = Bind(source);

        AssertSuccessful(result);
        AssertEx.True(result.Hir.Nodes.Any(static node => node.Kind == SafeCoreHirNodeKind.Function &&
            node.Name == "identity"), "The result must retain the source HIR declaration.");
        SafeCoreHirNode call = result.Hir.Nodes.Single(static node => node.Kind == SafeCoreHirNodeKind.CallExpression);
        AssertEx.True(call.ChildIds.Count > 0, "The HIR call must retain its callee child.");
        SafeCoreHirNode callee = result.Hir.GetNode(call.ChildIds[0]);
        AssertEx.Equal(SafeCoreSymbolKind.Function, callee.ReferencedSymbol!.Kind,
            "The call must be linked through the bound HIR symbol, not a text-only lookup.");
        AssertEx.Equal("crate::identity", callee.ReferencedSymbol.QualifiedName);

        GenericMonomorphizedFunction identity = result.Plan.Instances.Single(instance =>
            instance.Instance.FunctionId == "crate::identity");
        AssertEx.True(identity.Instance.Arguments.SequenceEqual([RustType.I32]),
            "The inferred identity instance must use i32.");
        AssertEx.Equal(RustType.I32, identity.ReturnType);
        SafeCoreGenericHirInstanceBinding binding = result.Bindings.Single(value =>
            value.FunctionId == "crate::identity");
        AssertEx.Equal("crate::identity", result.Hir.GetNode(binding.HirNodeId).DeclaredSymbol!.QualifiedName);
        AssertEx.Equal(call.Id, result.Bindings.Single(value => value.FunctionId == "crate::main").CallNodeIds.Single());
        return Task.CompletedTask;
    }

    private static Task NestedPropagationAsync()
    {
        const string source = "fn identity<T>(value: T) -> T { value } " +
                              "fn wrapper<U>(value: U) -> U { identity(value) } " +
                              "fn main() { wrapper(true); }";
        SafeCoreGenericHirBindingResult result = Bind(source);

        AssertSuccessful(result);
        AssertEx.True(result.Plan.Instances.Any(instance =>
            instance.Instance.FunctionId == "crate::wrapper" &&
            instance.Instance.Arguments.SequenceEqual([RustType.Bool])),
            "The wrapper call must close its caller parameter to bool.");
        AssertEx.True(result.Plan.Instances.Any(instance =>
            instance.Instance.FunctionId == "crate::identity" &&
            instance.Instance.Arguments.SequenceEqual([RustType.Bool])),
            "The nested identity call must receive the substituted bool argument.");
        SafeCoreGenericHirInstanceBinding wrapper = result.Bindings.Single(value =>
            value.FunctionId == "crate::wrapper" && value.Arguments.SequenceEqual([RustType.Bool]));
        AssertEx.Equal(1, wrapper.CallNodeIds.Length, "The wrapper declaration must retain its call site.");
        return Task.CompletedTask;
    }

    private static Task ImportedIdentityAsync()
    {
        const string source = "fn identity<T>(value: T) -> T { value } " +
                              "use crate::identity as apply; fn main() { apply(42); }";
        SafeCoreGenericHirBindingResult result = Bind(source);

        AssertSuccessful(result);
        AssertEx.True(result.Plan.Instances.Any(instance =>
            instance.Instance.FunctionId == "crate::identity" &&
            instance.Instance.Arguments.SequenceEqual([RustType.I32])),
            "An imported alias must still specialize its canonical function declaration.");
        SafeCoreHirNode call = result.Hir.Nodes.Single(static node => node.Kind == SafeCoreHirNodeKind.CallExpression);
        SafeCoreSymbol imported = result.Hir.GetNode(call.ChildIds[0]).ReferencedSymbol!;
        AssertEx.Equal("crate::identity", imported.ResolvedImportTargetQualifiedName!);
        return Task.CompletedTask;
    }

    private static Task DeterministicAsync()
    {
        const string source = "fn identity<T>(value: T) -> T { value } fn main() { identity(42); }";
        SafeCoreGenericHirBindingResult first = Bind(source);
        SafeCoreGenericHirBindingResult second = Bind(source);
        AssertSuccessful(first);
        AssertSuccessful(second);
        AssertEx.Equal(Describe(first), Describe(second),
            "Source/HIR generic binding must be stable across repeated runs.");
        return Task.CompletedTask;
    }

    private static Task MissingInferenceAsync()
    {
        const string source = "fn make<T>() -> T {} fn main() { make(); }";
        SafeCoreGenericHirBindingResult result = Bind(source);

        AssertEx.False(result.IsSuccessful, "A call with no type evidence must fail closed.");
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, result.Plan.Status);
        AssertEx.Equal(0, result.Plan.Instances.Length,
            "Inference failure must not expose a partial monomorphization plan.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == SafeCoreGenericHirBinding.MissingEvidence),
            "Missing inference evidence must have a stable bridge diagnostic.");
        return Task.CompletedTask;
    }

    private static Task ExplicitArgumentBoundaryAsync()
    {
        const string source = "fn identity<T>(value: T) -> T { value } fn main() { identity::<i32>(42); }";
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "generic-turbofish.rs");
        AssertEx.True(syntax.IsSuccessful, FormatDiagnostics(syntax.Diagnostics));
        SafeCoreGenericHirBindingResult result = SafeCoreGenericHirBinding.Bind(syntax);

        AssertEx.False(result.IsSuccessful, "The current HIR profile must reject explicit turbofish calls.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
            "The unsupported explicit-generic boundary must remain visible to callers.");
        AssertEx.Equal(0, result.Plan.Instances.Length, "Rejected HIR must never yield a partial plan.");
        return Task.CompletedTask;
    }

    private static Task BudgetAsync()
    {
        const string source = "fn identity<T>(value: T) -> T { value } fn main() { identity(42); }";
        SafeCoreGenericHirBindingResult result = Bind(source, new SafeCoreGenericHirBindingOptions
        {
            MaximumOperations = 4,
            MaximumDiagnostics = 8,
            Timeout = TimeSpan.FromSeconds(1),
        });

        AssertEx.True(result.IsTruncated || result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == SafeCoreGenericHirBinding.LimitReached),
            "The bridge must report a bounded operation failure.");
        AssertEx.Equal(0, result.Plan.Instances.Length, "A truncated analysis must not expose a plan.");
        return Task.CompletedTask;
    }

    private static SafeCoreGenericHirBindingResult Bind(
        string source,
        SafeCoreGenericHirBindingOptions? options = null)
    {
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "generic-hir-binding.rs");
        AssertEx.True(syntax.IsSuccessful, FormatDiagnostics(syntax.Diagnostics));
        return SafeCoreGenericHirBinding.Bind(syntax, options ?? new SafeCoreGenericHirBindingOptions
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOperations = 20_000,
        });
    }

    private static void AssertSuccessful(SafeCoreGenericHirBindingResult result) =>
        AssertEx.True(result.IsSuccessful,
            string.Join("; ", result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")) +
            $" plan={result.Plan.Status} ({result.Plan.Diagnostic})" +
            $" hir={string.Join("; ", result.Hir.Diagnostics.Select(static diagnostic => diagnostic.Code))}");

    private static string Describe(SafeCoreGenericHirBindingResult result)
    {
        string plans = string.Join(
            ";",
            result.Plan.Instances.Select(static instance =>
                $"{instance.Instance.FunctionId}<{string.Join(',', instance.Instance.Arguments)}>" +
                $":{instance.ReturnType}:" +
                string.Join(',', instance.Calls.Select(static call =>
                    $"{call.FunctionId}<{string.Join(',', call.Arguments)}>") )));
        string bindings = string.Join(
            ";",
            result.Bindings.Select(static binding =>
                $"{binding.FunctionId}:{binding.HirNodeId}:{string.Join(',', binding.Arguments)}:" +
                string.Join(',', binding.CallNodeIds)));
        return plans + "|" + bindings;
    }

    private static string FormatDiagnostics(IReadOnlyList<Diagnostic> diagnostics) => string.Join(
        "; ",
        diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message} [{diagnostic.Span.Start},{diagnostic.Span.Length}]"));
}
