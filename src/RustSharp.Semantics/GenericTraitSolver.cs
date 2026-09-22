using System.Collections.Immutable;

namespace RustSharp.Semantics;

public sealed record GenericTraitObligation(string Trait, RustType Target);

public sealed record GenericTraitImplementation(
    string Id,
    string Trait,
    RustType Target,
    ImmutableArray<string> Parameters,
    ImmutableArray<GenericTraitObligation> Bounds);

public sealed record GenericTraitResolutionResult(
    GenericAnalysisStatus Status,
    string? ImplementationId,
    ImmutableArray<string> SelectedImplementations,
    string? Diagnostic)
{
    public bool IsSuccess => Status == GenericAnalysisStatus.Complete;
}

/// <summary>
/// A conservative, closed-world trait subset. Overlapping heads are rejected before
/// solving; bounds never imply specialization or permit otherwise overlapping impls.
/// </summary>
public sealed class GenericTraitSolver(ImmutableArray<GenericTraitImplementation> implementations)
{
    public const string ProfileId = "bounded-traits-v1";

    public GenericTraitResolutionResult CheckCoherence(
        GenericAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var budget = new GenericBudget(limits, cancellationToken);
        try
        {
            CreateSession(budget);
            return new(GenericAnalysisStatus.Complete, null, [], null);
        }
        catch (GenericFailure failure)
        {
            return Failure(failure);
        }
    }

    public GenericTraitResolutionResult Resolve(
        GenericTraitObligation obligation,
        GenericAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        var budget = new GenericBudget(limits, cancellationToken);
        try
        {
            GenericTraitSession session = CreateSession(budget);
            string implementation = session.Resolve(obligation, 0);
            return new(GenericAnalysisStatus.Complete, implementation, session.Selected, null);
        }
        catch (GenericFailure failure)
        {
            return Failure(failure);
        }
    }

    internal GenericTraitSession CreateSession(GenericBudget budget) => new(implementations, budget);

    private static GenericTraitResolutionResult Failure(GenericFailure failure) =>
        new(failure.Status, null, [], failure.Message);
}

internal sealed class GenericTraitSession
{
    private readonly GenericBudget budget;
    private readonly ImmutableArray<GenericTraitImplementation> implementations;
    private readonly Dictionary<string, string> resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    private readonly SortedSet<string> selected = new(StringComparer.Ordinal);

    public GenericTraitSession(ImmutableArray<GenericTraitImplementation> implementations, GenericBudget budget)
    {
        this.budget = budget;
        budget.Count(implementations.IsDefault ? -1 : implementations.Length);
        var sorted = new SortedDictionary<string, GenericTraitImplementation>(StringComparer.Ordinal);
        foreach (GenericTraitImplementation implementation in implementations)
        {
            budget.Step();
            if (implementation is null)
            {
                throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "A trait implementation must not be null.");
            }

            budget.Name(implementation.Id);
            budget.Name(implementation.Trait);
            HashSet<string> parameters = budget.Parameters(implementation.Parameters);
            GenericTypes.Key(implementation.Target, budget, parameters: parameters);
            budget.Count(implementation.Bounds.IsDefault ? -1 : implementation.Bounds.Length);
            foreach (GenericTraitObligation bound in implementation.Bounds)
            {
                ValidateObligation(bound, parameters);
            }

            // Every implementation parameter must be constrained by its head.
            var headParameters = new HashSet<string>(StringComparer.Ordinal);
            CollectParameters(implementation.Target, headParameters, 0);
            if (!parameters.SetEquals(headParameters))
            {
                throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "Implementation parameters must all occur in the target type.");
            }

            if (!sorted.TryAdd(implementation.Id, implementation))
            {
                throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "Implementation identifiers must be unique.");
            }
        }

        this.implementations = [.. sorted.Values];
        for (int left = 0; left < this.implementations.Length; left++)
        {
            for (int right = left + 1; right < this.implementations.Length; right++)
            {
                budget.Step();
                GenericTraitImplementation a = this.implementations[left];
                GenericTraitImplementation b = this.implementations[right];
                if (string.Equals(a.Trait, b.Trait, StringComparison.Ordinal) && Overlap(a.Target, b.Target))
                {
                    throw new GenericFailure(GenericAnalysisStatus.OverlappingImplementations,
                        $"Trait '{a.Trait}' implementations '{a.Id}' and '{b.Id}' have overlapping heads; specialization is outside {GenericTraitSolver.ProfileId}.");
                }
            }
        }
    }

    public ImmutableArray<string> Selected => [.. selected];

    // Source bodies are checked with rigid parameters. Only implementation-head
    // parameters can match a goal; caller parameters never become inference variables.
    internal void Prove(GenericTraitObligation obligation,
        ImmutableArray<GenericTraitObligation> assumptions)
    {
        var assumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (GenericTraitObligation assumption in assumptions)
        {
            budget.Step();
            assumed.Add(GoalKey(assumption));
        }
        var proving = new HashSet<string>(StringComparer.Ordinal);
        ProveCore(obligation, 0);

        string GoalKey(GenericTraitObligation goal) => goal.Trait.Length + ":" + goal.Trait +
            GenericTypes.Key(goal.Target, budget);

        void ProveCore(GenericTraitObligation goal, int depth)
        {
            budget.Step(depth);
            string key = GoalKey(goal);
            if (assumed.Contains(key)) return;
            budget.Count(proving.Count + 1);
            if (!proving.Add(key))
                throw new GenericFailure(GenericAnalysisStatus.CyclicObligation,
                    "A generic body obligation requires a finite proof.");
            try
            {
                foreach (GenericTraitImplementation implementation in implementations)
                {
                    budget.Step(depth);
                    if (!string.Equals(goal.Trait, implementation.Trait, StringComparison.Ordinal)) continue;
                    var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                    if (!GenericTypes.Match(implementation.Target, goal.Target, bindings, budget, depth)) continue;
                    foreach (GenericTraitObligation bound in implementation.Bounds)
                        ProveCore(new(bound.Trait, GenericTypes.Substitute(bound.Target, bindings, budget, depth + 1)), depth + 1);
                    return;
                }
                throw new GenericFailure(GenericAnalysisStatus.MissingImplementation,
                    $"The generic body cannot prove trait '{goal.Trait}'; add the required bound.");
            }
            finally { proving.Remove(key); }
        }
    }

    public string Resolve(GenericTraitObligation obligation, int depth)
    {
        budget.Step(depth);
        ValidateObligation(obligation, null);
        string key = obligation.Trait.Length + ":" + obligation.Trait + GenericTypes.Key(obligation.Target, budget, requireClosed: true);
        if (resolved.TryGetValue(key, out string? cached)) return cached;
        budget.Count(active.Count + 1);
        if (!active.Add(key))
        {
            throw new GenericFailure(GenericAnalysisStatus.CyclicObligation,
                $"A recursive '{obligation.Trait}' obligation has no finite proof; coinductive solving is outside {GenericTraitSolver.ProfileId}.");
        }

        try
        {
            foreach (GenericTraitImplementation implementation in implementations)
            {
                budget.Step(depth);
                if (!string.Equals(implementation.Trait, obligation.Trait, StringComparison.Ordinal)) continue;
                var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                if (!GenericTypes.Match(implementation.Target, obligation.Target, bindings, budget, depth)) continue;
                foreach (GenericTraitObligation bound in implementation.Bounds)
                {
                    RustType target = GenericTypes.Substitute(bound.Target, bindings, budget, depth + 1);
                    Resolve(new(bound.Trait, target), depth + 1);
                }

                budget.Count(resolved.Count + 1);
                resolved.Add(key, implementation.Id);
                selected.Add(implementation.Id);
                return implementation.Id;
            }

            throw new GenericFailure(GenericAnalysisStatus.MissingImplementation,
                $"No implementation satisfies the closed '{obligation.Trait}' obligation.");
        }
        finally
        {
            active.Remove(key);
        }
    }

    private void ValidateObligation(GenericTraitObligation obligation, HashSet<string>? parameters)
    {
        budget.Step();
        if (obligation is null)
        {
            throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "A trait obligation must not be null.");
        }

        budget.Name(obligation.Trait);
        GenericTypes.Key(obligation.Target, budget, parameters: parameters);
    }

    private void CollectParameters(RustType type, HashSet<string> parameters, int depth)
    {
        budget.Step(depth);
        if (type.Kind == RustTypeKind.Parameter) parameters.Add(type.Name);
        foreach (RustType argument in type.Arguments) CollectParameters(argument, parameters, depth + 1);
    }

    private readonly record struct Term(RustType Type, int Side);

    private bool Overlap(RustType left, RustType right)
    {
        var substitutions = new Dictionary<(int Side, string Name), Term>();
        return Unify(new(left, 0), new(right, 1), 0);

        Term Walk(Term term, int depth)
        {
            budget.Step(depth);
            return term.Type.Kind == RustTypeKind.Parameter && substitutions.TryGetValue((term.Side, term.Type.Name), out Term replacement)
                ? Walk(replacement, depth + 1) : term;
        }

        bool Occurs(Term variable, Term target, int depth)
        {
            budget.Step(depth);
            target = Walk(target, depth);
            if (target.Type.Kind == RustTypeKind.Parameter)
            {
                return variable.Side == target.Side && string.Equals(variable.Type.Name, target.Type.Name, StringComparison.Ordinal);
            }

            foreach (RustType argument in target.Type.Arguments)
            {
                if (Occurs(variable, new(argument, target.Side), depth + 1)) return true;
            }

            return false;
        }

        bool Unify(Term a, Term b, int depth)
        {
            budget.Step(depth);
            a = Walk(a, depth);
            b = Walk(b, depth);
            if (a.Type.Kind == RustTypeKind.Parameter)
            {
                if (b.Type.Kind == RustTypeKind.Parameter && a.Side == b.Side && string.Equals(a.Type.Name, b.Type.Name, StringComparison.Ordinal)) return true;
                if (Occurs(a, b, depth + 1)) return false;
                budget.Count(substitutions.Count + 1);
                substitutions.Add((a.Side, a.Type.Name), b);
                return true;
            }

            if (b.Type.Kind == RustTypeKind.Parameter) return Unify(b, a, depth + 1);
            if (a.Type.Kind != b.Type.Kind || !string.Equals(a.Type.Name, b.Type.Name, StringComparison.Ordinal) ||
                a.Type.Arguments.Length != b.Type.Arguments.Length) return false;
            for (int index = 0; index < a.Type.Arguments.Length; index++)
            {
                if (!Unify(new(a.Type.Arguments[index], a.Side), new(b.Type.Arguments[index], b.Side), depth + 1)) return false;
            }

            return true;
        }
    }
}
