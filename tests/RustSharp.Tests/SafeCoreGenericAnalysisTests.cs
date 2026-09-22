using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreGenericAnalysisTests
{
    private const string SourcePath = "generic-analysis.rs";
    private const string Identity = "fn identity<T>(value: T) -> T { value }";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("source generics retain bound HIR and open body type evidence", HirEvidenceAsync),
        new("source generics infer nested calls from arguments and return contexts", ContextInferenceAsync),
        new("source generics reject invalid bodies even without reachable instances", UnreachableBodiesAsync),
        new("source generics resolve forward and qualified trait declarations", TraitNamesAsync),
        new("source generics prove bounds through every generic wrapper", BoundPropagationAsync),
        new("source generic traits reject missing overlapping and cyclic evidence", TraitFailuresAsync),
        new("source generics preserve nominal generic heads and nested substitutions", NominalHeadsAsync),
        new("source generics diagnose inconsistent repeated parameters at the call", RepeatedParametersAsync),
        new("source generics reject defaults lifetime const and associated declarations", UnsupportedDeclarationsAsync),
        new("source generics reject incorrect type argument and value argument arity", CallArityAsync),
        new("source generics resolve imports raw identifiers and visibility", NameResolutionAsync),
        new("source generic specialization preserves deterministic closed node evidence", SpecializationEvidenceAsync),
        new("source generic entry points and field operations obey explicit boundaries", BodyBoundariesAsync),
        new("source generic assignments reject type arguments on local and parameter targets", AssignmentTargetsAsync),
        new("source generic analysis bounds work depth items time and growing instances", LimitsAsync),
        new("source generic analysis validates limits preserves cancellation and exposes no partial result", InvalidOptionsAsync),
    ];

    private static Task HirEvidenceAsync()
    {
        SafeCoreGenericAnalysisProgram program = Accept(Identity + " fn main() { identity::<i32>(1); }");
        AssertEx.True(program.Hir.IsSuccessful, "Generic evidence must refer to a successfully name-bound HIR.");
        SafeCoreGenericFunctionDefinition identity = program.Functions.Single(static value => value.Id == "crate::identity");
        AssertEx.Equal(SafeCoreHirNodeKind.Function, identity.Declaration.Kind);
        AssertEx.Equal(SafeCoreHirNodeKind.Block, identity.Body.Kind);
        AssertEx.True(identity.Declaration.DeclaredSymbol is not null, "A generic function must retain its bound symbol.");
        string parameter = identity.PlanDefinition.Parameters.Single();
        int valueNode = identity.Types.Keys.Single(id => program.Hir.GetNode(id).Kind == SafeCoreHirNodeKind.NameExpression);
        AssertEx.Equal(RustType.Parameter(parameter), identity.Types[valueNode]);
        AssertEx.Equal(RustType.Parameter(parameter), identity.PlanDefinition.ReturnType);
        AssertEx.Equal(2, program.Plan.Instances.Length);
        SafeCoreGenericSpecialization closed = program.Specializations.Single(static value => value.Instance.FunctionId == "crate::identity");
        AssertEx.Equal(identity.Declaration.Id, closed.Declaration.Id);
        AssertEx.Equal(RustType.I32, closed.Bindings[parameter]);
        AssertEx.Equal(RustType.I32, closed.Types[valueNode]);
        AssertEx.Equal(RustType.Parameter(parameter), identity.Types[valueNode], "Specialization must retain the open definition evidence.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreGenericAnalysisResult checkedHir = SafeCoreGenericAnalysis.Check(program.Hir, cancellationToken: deadline.Token);
        AssertEx.True(checkedHir.IsSuccessful, string.Join("; ", checkedHir.Diagnostics));
        AssertEx.Equal(Describe(program), Describe(checkedHir.Program!), "The retained HIR must reproduce the same generic evidence directly.");
        return Task.CompletedTask;
    }

    private static Task ContextInferenceAsync()
    {
        string[] sources =
        [
            Identity + " fn main() { identity(identity(1)); identity(identity(true)); }",
            Identity + " fn forward<T>(value: T) -> T { identity(value) } fn main() { forward::<bool>(true); }",
            "fn make<T>() -> T { make::<T>() } fn main() { let value: i32 = make(); }",
            "fn make<T>() -> T { make::<T>() } fn answer() -> i32 { make() } fn main() { answer(); }",
        ];
        foreach (string source in sources)
        {
            SafeCoreGenericAnalysisProgram program = Accept(source);
            AssertEx.True(program.Plan.Instances.Length >= 2, "Nested or contextual calls must enter the closed reachability plan.");
            AssertEx.True(program.Specializations.All(static value => value.Instance.Arguments.All(type => !HasParameter(type))),
                "Inferred root calls must close every generic argument.");
        }
        return Task.CompletedTask;
    }

    private static Task UnreachableBodiesAsync()
    {
        string[] sources =
        [
            "fn bad<T>(value: T) -> i32 { value } fn main() {}",
            "fn bad<T>(value: T) -> T { true } fn main() {}",
            "fn bad<T>(value: T) -> T { let invalid: i32 = true; value } fn main() {}",
            "fn bad<T>(value: T) -> T { true } fn main() { bad::<bool>(true); }",
        ];
        foreach (string source in sources) Reject(source, SafeCoreGenericDiagnosticCodes.BodyMismatch);
        SafeCoreGenericAnalysisProgram library = Accept(Identity);
        AssertEx.Equal(0, library.Plan.Instances.Length, "A checked generic library without roots must have an empty closed plan.");
        AssertEx.Equal(1, library.Functions.Length);
        AssertEx.True(library.Functions[0].Types.Count > 0, "An unreachable generic body must still have checked type evidence.");
        return Task.CompletedTask;
    }

    private static Task TraitNamesAsync()
    {
        string[] sources =
        [
            "impl Print for i32 {} fn show<T: Print>(value: T) -> T { value } trait Print {} fn main() { show(1); }",
            "mod traits { pub trait Print {} } impl traits::Print for i32 {} fn show<T: traits::Print>(value: T) -> T { value } fn main() { show(1); }",
            "mod traits { pub trait Print {} } use traits::Print as Display; impl Display for bool {} fn show<T: Display>(value: T) -> T { value } fn main() { show(true); }",
        ];
        foreach (string source in sources)
        {
            SafeCoreGenericAnalysisProgram program = Accept(source);
            AssertEx.Equal(1, program.Plan.SelectedImplementations.Length);
        }
        return Task.CompletedTask;
    }

    private static Task BoundPropagationAsync()
    {
        const string prefix = "trait Print {} impl Print for i32 {} fn show<T: Print>(value: T) -> T { value } ";
        string[] accepted =
        [
            prefix + "fn wrapper<T: Print>(value: T) -> T { show(value) } fn main() { wrapper(1); }",
            prefix + "fn wrapper<T>(value: T) -> T where T: Print { show(value) } fn main() { wrapper(1); }",
        ];
        foreach (string source in accepted) Accept(source);
        string[] rejected =
        [
            prefix + "fn wrapper<T>(value: T) -> T { show(value) } fn main() {}",
            prefix + "fn wrapper<T>(value: T) -> T { show(value) } fn main() { wrapper(1); }",
        ];
        foreach (string source in rejected) Reject(source, SafeCoreGenericDiagnosticCodes.Trait);
        return Task.CompletedTask;
    }

    private static Task TraitFailuresAsync()
    {
        string[] sources =
        [
            "trait Print {} fn show<T: Print>(value: T) -> T { value } fn main() { show(1); }",
            "struct Box<T> { value: T } trait Print {} impl<T> Print for Box<T> {} impl Print for Box<i32> {} fn main() {}",
            "trait Print {} impl<T: Print> Print for T {} fn show<T: Print>(value: T) -> T { value } fn main() { show(1); }",
        ];
        foreach (string source in sources) Reject(source, SafeCoreGenericDiagnosticCodes.Trait);
        return Task.CompletedTask;
    }

    private static Task NominalHeadsAsync()
    {
        const string source = "struct Box<T> { value: T } trait Print {} impl<T> Print for Box<T> {} " +
            "fn show<T: Print>(value: T) -> T { value } " +
            "fn wrapper(value: Box<i32>) -> Box<i32> { show(value) } " +
            "fn nested<T>(value: Box<Box<T>>) -> Box<Box<T>> { value } fn main() {}";
        SafeCoreGenericAnalysisProgram program = Accept(source);
        SafeCoreGenericFunctionDefinition wrapper = program.Functions.Single(static value => value.Id == "crate::wrapper");
        RustType nominal = wrapper.PlanDefinition.ParameterTypes[0];
        AssertEx.Equal(RustTypeKind.Named, nominal.Kind);
        AssertEx.True(nominal.Name.EndsWith("::Box", StringComparison.Ordinal), "Nominal types must retain resolved module identity.");
        AssertEx.Equal(RustType.I32, nominal.Arguments.Single());
        GenericFunctionInstance call = wrapper.Calls.Values.Single();
        AssertEx.Equal("crate::show", call.FunctionId);
        AssertEx.Equal(nominal, call.Arguments.Single());
        SafeCoreGenericFunctionDefinition nested = program.Functions.Single(static value => value.Id == "crate::nested");
        AssertEx.Equal(RustType.Parameter(nested.PlanDefinition.Parameters.Single()), nested.PlanDefinition.ReturnType.Arguments[0].Arguments[0]);
        return Task.CompletedTask;
    }

    private static Task RepeatedParametersAsync()
    {
        const string prefix = "fn same<T>(left: T, right: T) -> T { left } fn main() { ";
        string[] calls = ["same(1, true)", "same::<i32>(1, true)"];
        foreach (string call in calls)
        {
            string source = prefix + call + "; }";
            SafeCoreGenericAnalysisResult result = Reject(source, SafeCoreGenericDiagnosticCodes.InvalidCall);
            Diagnostic diagnostic = result.Diagnostics.First(static value => value.Code == SafeCoreGenericDiagnosticCodes.InvalidCall);
            int start = source.IndexOf(call, StringComparison.Ordinal);
            AssertEx.True(diagnostic.Span.Start < start + call.Length && diagnostic.Span.End > start,
                "The inconsistent argument diagnostic must point into the failing call.");
        }
        Accept(prefix + "same(1, 2); same(true, false); }");
        return Task.CompletedTask;
    }

    private static Task UnsupportedDeclarationsAsync()
    {
        string[] sources =
        [
            "struct Box<T = i32> { value: T } fn main() {}",
            "fn value<'a>(value: i32) -> i32 { value } fn main() {}",
            "fn value<const N: usize>(value: i32) -> i32 { value } fn main() {}",
            "trait Value { type Item; } fn main() {}",
            "trait Value { fn get() -> i32; } fn main() {}",
            "trait Value<T> {} fn main() {}",
        ];
        foreach (string source in sources) RejectUnsupported(source);
        return Task.CompletedTask;
    }

    private static Task CallArityAsync()
    {
        string[] sources =
        [
            "fn value(value: i32) -> i32 { value } fn main() { value::<i32>(1); }",
            Identity + " fn main() { identity::<i32, bool>(1); }",
            Identity + " fn main() { identity::<i32>(); }",
            "fn first<T, U>(left: T, right: U) -> T { left } fn main() { first::<i32>(1, true); }",
        ];
        foreach (string source in sources) Reject(source, SafeCoreGenericDiagnosticCodes.InvalidCall);
        return Task.CompletedTask;
    }

    private static Task NameResolutionAsync()
    {
        const string source = "mod tools { pub fn r#type<T>(r#match: T) -> T { r#match } } " +
            "use tools::r#type as identity; fn main() { identity::<i32>(1); }";
        SafeCoreGenericAnalysisProgram program = Accept(source);
        AssertEx.True(program.Specializations.Any(static value => value.Instance.FunctionId == "crate::tools::type"),
            "Imported raw identifiers must keep one canonical function identity.");
        string[] rejected =
        [
            "mod tools { fn identity<T>(value: T) -> T { value } } fn main() { tools::identity(1); }",
            "fn main() { missing::<i32>(1); }",
            "mod traits { trait Print {} } fn show<T: traits::Print>(value: T) -> T { value } fn main() {}",
        ];
        foreach (string invalid in rejected) Reject(invalid);
        return Task.CompletedTask;
    }

    private static Task SpecializationEvidenceAsync()
    {
        const string source = Identity + " fn forward<T>(value: T) -> T { identity::<T>(value) } " +
            "fn main() { forward::<i32>(1); forward::<bool>(true); forward::<i32>(2); }";
        SafeCoreGenericAnalysisProgram first = Accept(source);
        SafeCoreGenericAnalysisProgram second = Accept(source);
        AssertEx.Equal(5, first.Specializations.Length);
        AssertEx.Equal(Describe(first), Describe(second), "Identical source must preserve deterministic instance and node evidence.");
        SafeCoreGenericFunctionDefinition identity = first.Functions.Single(static value => value.Id == "crate::identity");
        int node = identity.Types.Keys.Single(id => first.Hir.GetNode(id).Kind == SafeCoreHirNodeKind.NameExpression);
        SafeCoreGenericSpecialization integer = first.Specializations.Single(static value =>
            value.Instance.FunctionId == "crate::identity" && value.Instance.Arguments.SequenceEqual([RustType.I32]));
        SafeCoreGenericSpecialization boolean = first.Specializations.Single(static value =>
            value.Instance.FunctionId == "crate::identity" && value.Instance.Arguments.SequenceEqual([RustType.Bool]));
        AssertEx.Equal(RustType.I32, integer.Types[node]);
        AssertEx.Equal(RustType.Bool, boolean.Types[node]);
        AssertEx.Equal(RustType.Parameter(identity.PlanDefinition.Parameters.Single()), identity.Types[node]);
        foreach (SafeCoreGenericSpecialization specialization in first.Specializations)
        {
            AssertEx.True(specialization.Types.Values.All(static value => !HasParameter(value)),
                "Closed specializations must not expose open node types.");
            AssertEx.True(specialization.Calls.Values.All(static value => value.Arguments.All(type => !HasParameter(type))),
                "Closed specializations must substitute each call's arguments.");
            foreach (int id in specialization.Types.Keys)
                AssertEx.Equal(id, first.Hir.GetNode(id).Id, "Specialization evidence must refer to retained HIR node identities.");
        }
        return Task.CompletedTask;
    }

    private static Task BodyBoundariesAsync()
    {
        Reject("fn main<T>() {}");
        string[] supported =
        [
            "struct Box<T> { value: T } fn build<T>(value: T) -> Box<T> { Box { value } } fn main() {}",
            "struct Box<T> { value: T } fn get<T>(value: Box<T>) -> T { value.value } fn main() {}",
        ];
        foreach (string source in supported) Accept(source);
        Reject("struct Box<T> { value: T } fn main() { let mut b=Box{value:1}; b.value=2; }");
        return Task.CompletedTask;
    }

    private static Task AssignmentTargetsAsync()
    {
        string[] rejected =
        [
            "fn main() { let mut value = 1; value::<bool> = 2; }",
            "fn main() { let mut value = 1; value::<> = 2; }",
            "fn replace<T>(mut value: T, next: T) -> T { value::<bool> = next; value } fn main() {}",
        ];
        foreach (string source in rejected) RejectUnsupported(source);
        Accept("fn replace<T>(mut value: T, next: T) -> T { value = next; value } " +
            "fn main() { let mut value = 1; value = replace(value, 2); }");
        return Task.CompletedTask;
    }

    private static Task LimitsAsync()
    {
        const string source = Identity + " fn main() { identity::<i32>(1); identity::<bool>(true); }";
        GenericAnalysisLimits[] limits =
        [
            new() { MaximumWork = 1 },
            new() { MaximumDepth = 1 },
            new() { MaximumItems = 1 },
            new() { Timeout = TimeSpan.FromTicks(1) },
        ];
        foreach (GenericAnalysisLimits limit in limits)
            Reject(source, SafeCoreGenericDiagnosticCodes.LimitReached, new() { Limits = limit });
        Reject("struct Box<T> { value: T } fn grow<T>() { grow::<Box<T>>(); } fn main() { grow::<i32>(); }",
            SafeCoreGenericDiagnosticCodes.LimitReached, new() { Limits = new() { MaximumItems = 64 } });
        return Task.CompletedTask;
    }

    private static Task InvalidOptionsAsync()
    {
        SafeCoreSyntaxResult syntax = Parse(Identity + " fn main() {}");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreGenericAnalysis.Check(syntax, cancellationToken: cancelled.Token));
        AssertEx.Throws<ArgumentNullException>(() => SafeCoreGenericAnalysis.Check((SafeCoreSyntaxResult)null!));
        GenericAnalysisLimits[] limits =
        [
            new() { MaximumDepth = 0 }, new() { MaximumDepth = 129 },
            new() { MaximumWork = 0 }, new() { MaximumWork = 1_000_001 },
            new() { MaximumItems = 0 }, new() { MaximumItems = 4097 },
            new() { Timeout = TimeSpan.Zero }, new() { Timeout = TimeSpan.FromMinutes(2) },
        ];
        foreach (GenericAnalysisLimits limit in limits)
            AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreGenericAnalysis.Check(syntax, new() { Limits = limit }));
        AssertEx.Throws<ArgumentException>(() => SafeCoreGenericAnalysis.Check(syntax, new() { Limits = null! }));
        SafeCoreSyntaxResult invalid = SafeCoreSyntax.Parse("fn unfinished(", SourcePath);
        AssertEx.False(invalid.IsSuccessful, "The fixture must have syntax errors.");
        SafeCoreGenericAnalysisResult rejected = SafeCoreGenericAnalysis.Check(invalid);
        AssertEx.True(rejected.Program is null && !rejected.IsSuccessful && rejected.Diagnostics.Count > 0,
            "Invalid syntax must not expose a partial generic program.");
        return Task.CompletedTask;
    }

    private static SafeCoreGenericAnalysisProgram Accept(string source)
    {
        SafeCoreGenericAnalysisResult result = Check(source);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(static value => value.Code + ": " + value.Message)));
        return AssertEx.NotNull(result.Program, "A successful analysis must publish complete evidence.");
    }

    private static SafeCoreGenericAnalysisResult Reject(string source, string? code = null,
        SafeCoreGenericAnalysisOptions? options = null)
    {
        SafeCoreGenericAnalysisResult result = Check(source, options);
        AssertEx.False(result.IsSuccessful, "Invalid source must fail generic analysis: " + source);
        AssertEx.True(result.Program is null, "A failed analysis must not expose partial body or specialization evidence.");
        AssertEx.True(result.Diagnostics.Count > 0, "A failed analysis must explain its failure.");
        if (code is not null)
            AssertEx.True(result.Diagnostics.Any(value => value.Code == code),
                $"Expected {code}; found {string.Join("; ", result.Diagnostics.Select(static value => value.Code + ": " + value.Message))}");
        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            AssertEx.Equal(SourcePath, diagnostic.SourcePath!);
            AssertEx.True(diagnostic.Span.Start >= 0 && diagnostic.Span.Length > 0 && diagnostic.Span.End <= source.Length,
                "Source diagnostics must retain a nonempty span in their originating source.");
        }
        return result;
    }

    private static SafeCoreGenericAnalysisResult Check(string source, SafeCoreGenericAnalysisOptions? options = null)
    {
        SafeCoreSyntaxResult syntax = Parse(source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return SafeCoreGenericAnalysis.Check(syntax, options, deadline.Token);
    }

    private static void RejectUnsupported(string source)
    {
        SafeCoreGenericAnalysisResult result = Reject(source);
        AssertEx.True(result.Diagnostics.Any(static value => value.Code is
            SafeCoreGenericDiagnosticCodes.Unsupported or SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax or
            SafeCoreHirDiagnosticCodes.UnsupportedNode), "Unsupported forms must fail at an explicit profile boundary.");
    }

    private static SafeCoreSyntaxResult Parse(string source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, SourcePath, null, deadline.Token);
        AssertEx.True(syntax.IsSuccessful, "Fixture syntax must parse: " + string.Join("; ", syntax.Diagnostics));
        return syntax;
    }

    private static bool HasParameter(RustType type) =>
        type.Kind == RustTypeKind.Parameter || type.Arguments.Any(HasParameter);

    private static string Describe(SafeCoreGenericAnalysisProgram program) => string.Join("\n", program.Specializations.Select(value =>
        value.Instance.FunctionId + "<" + string.Join(',', value.Instance.Arguments) + ">:" + value.PlannedSignature.ReturnType + ":" +
        string.Join(';', value.Types.OrderBy(static pair => pair.Key).Select(static pair => pair.Key + "=" + pair.Value)) + ":" +
        string.Join(';', value.Calls.OrderBy(static pair => pair.Key).Select(static pair =>
            pair.Key + "=" + pair.Value.FunctionId + "<" + string.Join(',', pair.Value.Arguments) + ">"))));
}
