using System.Collections.Immutable;
using System.Globalization;

namespace RustSharp.Semantics;

public sealed record GenericFunctionInstance(string FunctionId, ImmutableArray<RustType> Arguments);

/// <summary>A structural call/signature template, supplied by a future typed HIR integration.</summary>
public sealed record GenericFunctionDefinition(
    string Id,
    ImmutableArray<string> Parameters,
    ImmutableArray<RustType> ParameterTypes,
    RustType ReturnType,
    ImmutableArray<GenericFunctionInstance> Calls,
    ImmutableArray<GenericTraitObligation> Bounds);

public sealed record GenericMonomorphizedFunction(
    GenericFunctionInstance Instance,
    ImmutableArray<RustType> ParameterTypes,
    RustType ReturnType,
    ImmutableArray<GenericFunctionInstance> Calls);

public sealed record GenericMonomorphizationResult(
    GenericAnalysisStatus Status,
    ImmutableArray<GenericMonomorphizedFunction> Instances,
    ImmutableArray<string> SelectedImplementations,
    string? Diagnostic)
{
    public bool IsSuccess => Status == GenericAnalysisStatus.Complete;
}

/// <summary>Computes a deterministic finite set of closed reachable signatures and calls, without emitting bodies.</summary>
public static class GenericMonomorphization
{
    public const string ProfileId = "generic-plan-v1";

    public static GenericMonomorphizationResult Plan(
        ImmutableArray<GenericFunctionDefinition> definitions,
        ImmutableArray<GenericFunctionInstance> roots,
        GenericTraitSolver? traitSolver = null,
        GenericAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var budget = new GenericBudget(limits, cancellationToken);
        try
        {
            var planner = new Planner(definitions, traitSolver ?? new GenericTraitSolver([]), budget);
            return planner.Plan(roots);
        }
        catch (GenericFailure failure)
        {
            // A partial reachability graph must never be mistaken for a complete AOT plan.
            return new(failure.Status, [], [], failure.Message);
        }
    }

    private sealed class Planner
    {
        private readonly GenericBudget budget;
        private readonly GenericTraitSession traits;
        private readonly Dictionary<string, GenericFunctionDefinition> definitions = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, GenericFunctionInstance> pending = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, GenericMonomorphizedFunction> instances = new(StringComparer.Ordinal);

        public Planner(ImmutableArray<GenericFunctionDefinition> definitions, GenericTraitSolver solver, GenericBudget budget)
        {
            this.budget = budget;
            budget.Count(definitions.IsDefault ? -1 : definitions.Length);
            foreach (GenericFunctionDefinition definition in definitions)
            {
                budget.Step();
                if (definition is null) throw Invalid("A function definition must not be null.");
                budget.Name(definition.Id);
                HashSet<string> parameters = budget.Parameters(definition.Parameters);
                ValidateTypes(definition.ParameterTypes, parameters);
                GenericTypes.Key(definition.ReturnType, budget, parameters: parameters);
                budget.Count(definition.Calls.IsDefault ? -1 : definition.Calls.Length);
                foreach (GenericFunctionInstance call in definition.Calls)
                {
                    if (call is null) throw Invalid("A call template must not be null.");
                    budget.Name(call.FunctionId);
                    ValidateTypes(call.Arguments, parameters);
                }

                budget.Count(definition.Bounds.IsDefault ? -1 : definition.Bounds.Length);
                foreach (GenericTraitObligation bound in definition.Bounds)
                {
                    budget.Step();
                    if (bound is null) throw Invalid("A function bound must not be null.");
                    budget.Name(bound.Trait);
                    GenericTypes.Key(bound.Target, budget, parameters: parameters);
                }

                if (!this.definitions.TryAdd(definition.Id, definition)) throw Invalid("Function identifiers must be unique.");
            }

            foreach (GenericFunctionDefinition definition in definitions)
            {
                budget.Step();
                foreach (GenericFunctionInstance call in definition.Calls)
                {
                    ValidateCall(call);
                }
            }

            traits = solver.CreateSession(budget);
        }

        public GenericMonomorphizationResult Plan(ImmutableArray<GenericFunctionInstance> roots)
        {
            budget.Count(roots.IsDefault ? -1 : roots.Length);
            foreach (GenericFunctionInstance root in roots) Enqueue(root);
            // Each iteration consumes one unique instance; the count and shared time/work
            // budget bound type-growing recursion while same-instance recursion deduplicates.
            for (int iteration = 0; iteration < budget.Limits.MaximumItems && pending.Count > 0; iteration++)
            {
                budget.Step();
                KeyValuePair<string, GenericFunctionInstance> entry = pending.First();
                pending.Remove(entry.Key);
                GenericFunctionInstance instance = entry.Value;
                GenericFunctionDefinition definition = definitions[instance.FunctionId];
                var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                for (int index = 0; index < definition.Parameters.Length; index++)
                {
                    budget.Step();
                    bindings.Add(definition.Parameters[index], instance.Arguments[index]);
                }

                ImmutableArray<RustType> parameterTypes = SubstituteTypes(definition.ParameterTypes, bindings);
                RustType returnType = GenericTypes.Substitute(definition.ReturnType, bindings, budget, 0);
                GenericTypes.Key(returnType, budget, requireClosed: true);
                foreach (GenericTraitObligation bound in definition.Bounds)
                {
                    RustType target = GenericTypes.Substitute(bound.Target, bindings, budget, 0);
                    traits.Resolve(new(bound.Trait, target), 0);
                }

                var calls = new SortedDictionary<string, GenericFunctionInstance>(StringComparer.Ordinal);
                foreach (GenericFunctionInstance template in definition.Calls)
                {
                    budget.Step();
                    var call = new GenericFunctionInstance(template.FunctionId, SubstituteTypes(template.Arguments, bindings));
                    calls.TryAdd(InstanceKey(call), call);
                }

                instances.Add(entry.Key, new(instance, parameterTypes, returnType, [.. calls.Values]));
                foreach (GenericFunctionInstance call in calls.Values) Enqueue(call);
            }

            if (pending.Count > 0) throw new GenericFailure(GenericAnalysisStatus.LimitExceeded, "Monomorphization exceeded its closed-instance budget.");
            return new(GenericAnalysisStatus.Complete, [.. instances.Values], traits.Selected, null);
        }

        private void Enqueue(GenericFunctionInstance instance)
        {
            string key = InstanceKey(instance);
            if (instances.ContainsKey(key) || pending.ContainsKey(key)) return;
            budget.Count(instances.Count + pending.Count + 1);
            pending.Add(key, instance);
        }

        private string InstanceKey(GenericFunctionInstance instance)
        {
            budget.Step();
            ValidateCall(instance);
            string key = instance.FunctionId.Length.ToString(CultureInfo.InvariantCulture) + ":" + instance.FunctionId + "[";
            foreach (RustType argument in instance.Arguments)
            {
                key += GenericTypes.Key(argument, budget, requireClosed: true);
            }

            return key + "]";
        }

        private void ValidateCall(GenericFunctionInstance instance)
        {
            budget.Step();
            if (instance is null) throw Invalid("A function instance must not be null.");
            budget.Name(instance.FunctionId);
            budget.Count(instance.Arguments.IsDefault ? -1 : instance.Arguments.Length);
            if (!definitions.TryGetValue(instance.FunctionId, out GenericFunctionDefinition? definition))
            {
                throw Invalid("A call references an undefined function.");
            }

            if (definition.Parameters.Length != instance.Arguments.Length) throw Invalid("A function instance has incorrect generic arity.");
        }

        private void ValidateTypes(ImmutableArray<RustType> types, HashSet<string> parameters)
        {
            budget.Count(types.IsDefault ? -1 : types.Length);
            foreach (RustType type in types) GenericTypes.Key(type, budget, parameters: parameters);
        }

        private ImmutableArray<RustType> SubstituteTypes(ImmutableArray<RustType> types, Dictionary<string, RustType> bindings)
        {
            var result = ImmutableArray.CreateBuilder<RustType>(types.Length);
            foreach (RustType type in types)
            {
                RustType closed = GenericTypes.Substitute(type, bindings, budget, 0);
                GenericTypes.Key(closed, budget, requireClosed: true);
                result.Add(closed);
            }

            return result.ToImmutable();
        }

        private static GenericFailure Invalid(string message) => new(GenericAnalysisStatus.InvalidInput, message);
    }
}
