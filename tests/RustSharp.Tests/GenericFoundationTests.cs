using System.Collections.Immutable;
using System.Diagnostics;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class GenericFoundationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generic substitution preserves nested structure and simultaneous replacements", SubstitutionAsync),
        new("generic substitution uses ordinal parameter identities", OrdinalBindingsAsync),
        new("generic matching preserves repeated parameter equality without partial bindings", MatchingAsync),
        new("generic substitution enforces composed result depth", SubstitutionDepthAsync),
        new("generic traits resolve nested bounds and report missing evidence", TraitBoundsAsync),
        new("generic traits preserve repeated parameters in implementation heads", RepeatedHeadAsync),
        new("generic coherence rejects exact and generic overlap deterministically", CoherenceAsync),
        new("generic coherence alpha renames implementations and checks infinite types", AlphaRenameAsync),
        new("generic trait cycles require a finite proof", TraitCyclesAsync),
        new("generic trait growth shares the recursive operation budget", TraitGrowthAsync),
        new("generic plan closes signatures and deduplicates recursive reachability", ReachabilityAsync),
        new("generic plan ordering is independent of roots definitions and calls", StablePlanAsync),
        new("generic plan validates reachable trait obligations", PlanBoundsAsync),
        new("generic plan bounds growing instance recursion without partial success", GrowingPlanAsync),
        new("generic plan canonical identities cannot collide through display text", CanonicalIdentityAsync),
        new("generic APIs diagnose malformed open and undeclared input", InvalidInputAsync),
        new("generic analysis consistently enforces work time item and cancellation bounds", LimitsAsync),
    ];

    private static RustType T => RustType.Parameter("T");
    private static RustType U => RustType.Parameter("U");
    private static RustType Box(RustType type) => RustType.Named("Box", type);
    private static RustType Pair(RustType left, RustType right) => RustType.Named("Pair", left, right);

    private static Task SubstitutionAsync()
    {
        var bindings = ImmutableDictionary<string, RustType>.Empty.Add("T", RustType.Bool).Add("U", RustType.I32);
        GenericSubstitutionResult result = GenericSubstitution.Apply(Pair(Box(T), U), bindings);
        AssertEx.True(result.IsSuccess, result.Diagnostic ?? "Substitution should succeed.");
        AssertEx.Equal(Pair(Box(RustType.Bool), RustType.I32), result.Type!);
        AssertEx.Equal(Pair(U, RustType.I32), GenericSubstitution.Apply(Pair(T, U),
            ImmutableDictionary<string, RustType>.Empty.Add("T", U).Add("U", RustType.I32)).Type!);
        AssertEx.Equal(T, GenericSubstitution.Apply(T, ImmutableDictionary<string, RustType>.Empty).Type!);
        return Task.CompletedTask;
    }

    private static Task OrdinalBindingsAsync()
    {
        var bindings = ImmutableDictionary.Create<string, RustType>(StringComparer.OrdinalIgnoreCase).Add("T", RustType.I32);
        GenericSubstitutionResult result = GenericSubstitution.Apply(RustType.Parameter("t"), bindings);
        AssertEx.True(result.IsSuccess, "A foreign comparer must not change Rust identifiers.");
        AssertEx.Equal(RustType.Parameter("t"), result.Type!);
        AssertEx.False(result.Bindings.ContainsKey("t"), "Returned bindings must also use ordinal comparison.");
        var duplicates = ImmutableDictionary.Create<string, RustType>(ReferenceEqualityComparer.Instance)
            .Add(new string('T', 1), RustType.I32)
            .Add(new string('T', 1), RustType.Bool);
        GenericSubstitutionResult duplicateResult = GenericSubstitution.Apply(T, duplicates);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, duplicateResult.Status);
        AssertEx.Equal(0, duplicateResult.Bindings.Count);
        AssertEx.True(duplicateResult.Type is null, "Ordinal duplicate input must return a diagnostic without partial substitution.");
        return Task.CompletedTask;
    }

    private static Task MatchingAsync()
    {
        GenericSubstitutionResult yes = GenericSubstitution.Match(Pair(T, Box(T)), Pair(RustType.I32, Box(RustType.I32)));
        AssertEx.True(yes.IsSuccess, "Repeated parameters with equal actual types must match.");
        AssertEx.Equal(RustType.I32, yes.Bindings["T"]);
        GenericSubstitutionResult no = GenericSubstitution.Match(Pair(T, T), Pair(RustType.I32, RustType.Bool));
        AssertEx.Equal(GenericAnalysisStatus.NoMatch, no.Status);
        AssertEx.Equal(0, no.Bindings.Count);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericSubstitution.Match(T, U).Status);
        return Task.CompletedTask;
    }

    private static Task SubstitutionDepthAsync()
    {
        GenericSubstitutionResult result = GenericSubstitution.Apply(Box(Box(T)),
            ImmutableDictionary<string, RustType>.Empty.Add("T", Box(RustType.I32)), new() { MaximumDepth = 2 });
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, result.Status);
        AssertEx.True(result.Type is null, "A rejected composed type must not escape as a usable result.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        RustType longIdentity = RustType.Named(new string('L', 352));
        string wrapperName = new('N', 1010);
        for (int depth = 0; depth < 64; depth++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Canonical-identity fixture construction exceeded its deadline.");
            longIdentity = RustType.Named(wrapperName, longIdentity);
        }

        // All node prefixes fit the per-node reserve, but pending closing brackets
        // take the final identity beyond 65536 characters.
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded,
            GenericSubstitution.Match(longIdentity, longIdentity, cancellationToken: cancellation.Token).Status);
        return Task.CompletedTask;
    }

    private static GenericTraitSolver PrintableSolver() => new([
        new("print_bool", "Print", RustType.Bool, [], []),
        new("print_box", "Print", Box(T), ["T"], [new("Print", T)]),
    ]);

    private static Task TraitBoundsAsync()
    {
        GenericTraitSolver solver = PrintableSolver();
        GenericTraitResolutionResult result = solver.Resolve(new("Print", Box(Box(RustType.Bool))));
        AssertEx.True(result.IsSuccess, result.Diagnostic ?? "Nested bounds must resolve.");
        AssertEx.Equal("print_box", result.ImplementationId!);
        AssertEx.Equal("print_bool,print_box", string.Join(',', result.SelectedImplementations));
        GenericTraitResolutionResult missing = solver.Resolve(new("Print", Box(RustType.I32)));
        AssertEx.Equal(GenericAnalysisStatus.MissingImplementation, missing.Status);
        AssertEx.Equal(0, missing.SelectedImplementations.Length);
        return Task.CompletedTask;
    }

    private static Task RepeatedHeadAsync()
    {
        var solver = new GenericTraitSolver([new("same", "Same", Pair(T, T), ["T"], [])]);
        AssertEx.True(solver.Resolve(new("Same", Pair(RustType.Bool, RustType.Bool))).IsSuccess, "Equal repeated arguments should resolve.");
        AssertEx.Equal(GenericAnalysisStatus.MissingImplementation, solver.Resolve(new("Same", Pair(RustType.Bool, RustType.I32))).Status);
        return Task.CompletedTask;
    }

    private static Task CoherenceAsync()
    {
        GenericTraitImplementation exact = new("a_exact", "Print", RustType.Bool, [], []);
        GenericTraitImplementation blanket = new("z_blanket", "Print", T, ["T"], [new("Other", T)]);
        var first = new GenericTraitSolver([exact, blanket]);
        var second = new GenericTraitSolver([blanket, exact]);
        AssertEx.Equal(GenericAnalysisStatus.OverlappingImplementations, first.CheckCoherence().Status);
        AssertEx.Equal(first.CheckCoherence().Diagnostic!, second.CheckCoherence().Diagnostic!);
        AssertEx.Equal(GenericAnalysisStatus.OverlappingImplementations, first.Resolve(new("Print", RustType.Bool)).Status);
        return Task.CompletedTask;
    }

    private static Task AlphaRenameAsync()
    {
        var overlaps = new GenericTraitSolver([
            new("left", "Trait", Pair(T, RustType.I32), ["T"], []),
            new("right", "Trait", Pair(RustType.Bool, T), ["T"], []),
        ]);
        AssertEx.Equal(GenericAnalysisStatus.OverlappingImplementations, overlaps.CheckCoherence().Status);
        var disjoint = new GenericTraitSolver([
            new("same", "Trait", Pair(T, T), ["T"], []),
            new("different", "Trait", Pair(RustType.I32, RustType.Bool), [], []),
        ]);
        AssertEx.True(disjoint.CheckCoherence().IsSuccess, "Repeated variables rule out the mismatched concrete pair.");
        var occurs = new GenericTraitSolver([
            new("same", "Trait", Pair(T, T), ["T"], []),
            new("grows", "Trait", Pair(U, Box(U)), ["U"], []),
        ]);
        AssertEx.True(occurs.CheckCoherence().IsSuccess, "Only an infinite type could overlap these two heads.");
        return Task.CompletedTask;
    }

    private static Task TraitCyclesAsync()
    {
        var solver = new GenericTraitSolver([
            new("a", "A", T, ["T"], [new("B", T)]),
            new("b", "B", T, ["T"], [new("A", T)]),
        ]);
        GenericTraitResolutionResult result = solver.Resolve(new("A", RustType.I32));
        AssertEx.Equal(GenericAnalysisStatus.CyclicObligation, result.Status);
        AssertEx.Equal(0, result.SelectedImplementations.Length);
        return Task.CompletedTask;
    }

    private static Task TraitGrowthAsync()
    {
        var solver = new GenericTraitSolver([new("grow", "Grow", T, ["T"], [new("Grow", Box(T))])]);
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded,
            solver.Resolve(new("Grow", RustType.Bool), new() { MaximumDepth = 8 }).Status);
        return Task.CompletedTask;
    }

    private static GenericFunctionDefinition Function(string name, ImmutableArray<GenericFunctionInstance> calls,
        ImmutableArray<GenericTraitObligation> bounds = default) =>
        new(name, ["T"], [Box(T)], T, calls, bounds.IsDefault ? [] : bounds);

    private static Task ReachabilityAsync()
    {
        GenericFunctionDefinition entry = Function("entry", [new("worker", [T]), new("worker", [T])]);
        GenericFunctionDefinition worker = Function("worker", [new("worker", [T])]);
        GenericFunctionDefinition dead = Function("dead", []);
        GenericMonomorphizationResult result = GenericMonomorphization.Plan([entry, worker, dead], [new("entry", [RustType.Bool])]);
        AssertEx.True(result.IsSuccess, result.Diagnostic ?? "Closed recursive instances must be finite.");
        AssertEx.Equal(2, result.Instances.Length);
        GenericMonomorphizedFunction closedEntry = result.Instances.Single(value => value.Instance.FunctionId == "entry");
        AssertEx.Equal(Box(RustType.Bool), closedEntry.ParameterTypes[0]);
        AssertEx.Equal(RustType.Bool, closedEntry.ReturnType);
        AssertEx.Equal(1, closedEntry.Calls.Length);
        AssertEx.Equal(RustType.Bool, closedEntry.Calls[0].Arguments[0]);
        return Task.CompletedTask;
    }

    private static Task StablePlanAsync()
    {
        GenericFunctionDefinition a = Function("a", [new("b", [T]), new("b", [RustType.Bool])]);
        GenericFunctionDefinition b = Function("b", []);
        GenericMonomorphizationResult first = GenericMonomorphization.Plan([a, b], [new("a", [RustType.I32]), new("a", [RustType.Bool])]);
        GenericMonomorphizationResult second = GenericMonomorphization.Plan([b, a with { Calls = [new("b", [RustType.Bool]), new("b", [T])] }],
            [new("a", [RustType.Bool]), new("a", [RustType.I32]), new("a", [RustType.Bool])]);
        AssertEx.True(first.IsSuccess && second.IsSuccess, "Reordered inputs must both yield plans.");
        AssertEx.Equal(4, first.Instances.Length);
        AssertEx.Equal(Describe(first), Describe(second));
        return Task.CompletedTask;
    }

    private static string Describe(GenericMonomorphizationResult result) => string.Join(';', result.Instances.Select(value =>
        value.Instance.FunctionId + "<" + string.Join(',', value.Instance.Arguments) + ">:" + value.ReturnType + ":" +
        string.Join(',', value.Calls.Select(call => call.FunctionId + "<" + string.Join(',', call.Arguments) + ">"))));

    private static Task PlanBoundsAsync()
    {
        GenericFunctionDefinition function = Function("display", [], [new("Print", T)]);
        AssertEx.True(GenericMonomorphization.Plan([function], [new("display", [Box(RustType.Bool)])], PrintableSolver()).IsSuccess,
            "A closed instance must check substituted bounds.");
        GenericMonomorphizationResult failure = GenericMonomorphization.Plan([function], [new("display", [Box(RustType.I32)])], PrintableSolver());
        AssertEx.Equal(GenericAnalysisStatus.MissingImplementation, failure.Status);
        AssertEx.Equal(0, failure.Instances.Length);
        return Task.CompletedTask;
    }

    private static Task GrowingPlanAsync()
    {
        GenericFunctionDefinition function = Function("grow", [new("grow", [Box(T)])]);
        GenericMonomorphizationResult result = GenericMonomorphization.Plan([function], [new("grow", [RustType.I32])],
            limits: new() { MaximumItems = 4 });
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, result.Status);
        AssertEx.Equal(0, result.Instances.Length);
        return Task.CompletedTask;
    }

    private static Task CanonicalIdentityAsync()
    {
        GenericFunctionDefinition function = Function("f", []);
        GenericMonomorphizationResult result = GenericMonomorphization.Plan([function],
            [new("f", [RustType.Named("X<Y>")]), new("f", [RustType.Named("X", RustType.Named("Y"))])]);
        AssertEx.True(result.IsSuccess, "Structural keys must not use ambiguous display text.");
        AssertEx.Equal(2, result.Instances.Length);
        return Task.CompletedTask;
    }

    private static Task InvalidInputAsync()
    {
        GenericFunctionDefinition valid = Function("f", []);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericMonomorphization.Plan([valid], [new("f", [T])]).Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericMonomorphization.Plan([valid], [new("f", [])]).Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericMonomorphization.Plan([valid], [new("missing", [RustType.I32])]).Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericMonomorphization.Plan([valid with { ReturnType = U }], []).Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericMonomorphization.Plan([valid with { Calls = default }], []).Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericMonomorphization.Plan([valid with { ReturnType = null! }], []).Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericMonomorphization.Plan(default, []).Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, new GenericTraitSolver(default).CheckCoherence().Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, new GenericTraitSolver([null!]).CheckCoherence().Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, new GenericTraitSolver([new("bad", "Trait", T, ["T", "U"], [])]).CheckCoherence().Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, new GenericTraitSolver([new("bad", "Trait", T, ["T"], [new("Trait", U)])]).CheckCoherence().Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, new GenericTraitSolver([new("bad", "Trait", T, ["T", "T"], [])]).CheckCoherence().Status);
        AssertEx.Equal(GenericAnalysisStatus.InvalidInput, GenericSubstitution.Match(RustType.Named("Null", [null!]), RustType.I32).Status);
        return Task.CompletedTask;
    }

    private static Task LimitsAsync()
    {
        var work = new GenericAnalysisLimits { MaximumWork = 1 };
        var time = new GenericAnalysisLimits { Timeout = TimeSpan.FromTicks(1) };
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, GenericSubstitution.Match(T, RustType.I32, work).Status);
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, GenericSubstitution.Match(T, RustType.I32, time).Status);
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, PrintableSolver().CheckCoherence(work).Status);
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, PrintableSolver().Resolve(new("Print", RustType.Bool), time).Status);
        GenericFunctionDefinition f = Function("f", []);
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, GenericMonomorphization.Plan([f], [new("f", [RustType.I32])], limits: work).Status);
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, GenericMonomorphization.Plan([f], [new("f", [RustType.I32])], limits: time).Status);
        AssertEx.Equal(GenericAnalysisStatus.LimitExceeded, GenericMonomorphization.Plan([f, f with { Id = "g" }], [], limits: new() { MaximumItems = 1 }).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => GenericSubstitution.Match(T, RustType.I32, cancellationToken: cancellation.Token));
        AssertEx.Throws<OperationCanceledException>(() => PrintableSolver().Resolve(new("Print", RustType.Bool), cancellationToken: cancellation.Token));
        AssertEx.Throws<OperationCanceledException>(() => GenericMonomorphization.Plan([f], [], cancellationToken: cancellation.Token));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => GenericSubstitution.Match(T, RustType.I32, new() { MaximumDepth = 0 }));
        return Task.CompletedTask;
    }
}
