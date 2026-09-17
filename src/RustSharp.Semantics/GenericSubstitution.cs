using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RustSharp.Semantics;

/// <summary>Limits shared by every phase of one generic analysis operation.</summary>
public sealed record GenericAnalysisLimits
{
    public int MaximumDepth { get; init; } = 64;
    public int MaximumWork { get; init; } = 100_000;
    public int MaximumItems { get; init; } = 4096;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
}

public enum GenericAnalysisStatus
{
    Complete,
    NoMatch,
    InvalidInput,
    MissingImplementation,
    OverlappingImplementations,
    CyclicObligation,
    LimitExceeded,
}

public sealed record GenericSubstitutionResult(
    GenericAnalysisStatus Status,
    RustType? Type,
    ImmutableDictionary<string, RustType> Bindings,
    string? Diagnostic)
{
    public bool IsSuccess => Status == GenericAnalysisStatus.Complete;
}

/// <summary>Opt-in, bounded operations on the P0 structural generic type model.</summary>
public static class GenericSubstitution
{
    /// <summary>Simultaneously substitutes parameters; replacements are not recursively substituted.</summary>
    public static GenericSubstitutionResult Apply(
        RustType type,
        ImmutableDictionary<string, RustType> bindings,
        GenericAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(bindings);
        var budget = new GenericBudget(limits, cancellationToken);
        try
        {
            budget.Count(bindings.Count);
            var ordinalBindings = ImmutableDictionary.CreateBuilder<string, RustType>(StringComparer.Ordinal);
            foreach ((string name, RustType value) in bindings)
            {
                budget.Name(name);
                GenericTypes.Key(value, budget);
                if (!ordinalBindings.TryAdd(name, value))
                {
                    throw new GenericFailure(GenericAnalysisStatus.InvalidInput,
                        "Substitution parameters must be unique under ordinal name comparison.");
                }
            }

            ImmutableDictionary<string, RustType> normalizedBindings = ordinalBindings.ToImmutable();
            RustType substituted = GenericTypes.Substitute(type, normalizedBindings, budget, 0);
            // A replacement can increase total nesting beyond either input's depth.
            GenericTypes.Key(substituted, budget);
            return new(GenericAnalysisStatus.Complete, substituted, normalizedBindings, null);
        }
        catch (GenericFailure failure)
        {
            return new(failure.Status, null, ImmutableDictionary<string, RustType>.Empty, failure.Message);
        }
    }

    /// <summary>Matches a template to a closed type, preserving repeated-parameter equality.</summary>
    public static GenericSubstitutionResult Match(
        RustType template,
        RustType closedType,
        GenericAnalysisLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(closedType);
        var budget = new GenericBudget(limits, cancellationToken);
        try
        {
            GenericTypes.Key(template, budget);
            GenericTypes.Key(closedType, budget, requireClosed: true);
            var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
            if (!GenericTypes.Match(template, closedType, bindings, budget, 0))
            {
                return new(GenericAnalysisStatus.NoMatch, null, ImmutableDictionary<string, RustType>.Empty,
                    "The closed type does not match every occurrence of the template parameters.");
            }

            return new(GenericAnalysisStatus.Complete, closedType, bindings.ToImmutableDictionary(StringComparer.Ordinal), null);
        }
        catch (GenericFailure failure)
        {
            return new(failure.Status, null, ImmutableDictionary<string, RustType>.Empty, failure.Message);
        }
    }
}

internal sealed class GenericFailure(GenericAnalysisStatus status, string message) : Exception(message)
{
    public GenericAnalysisStatus Status { get; } = status;
}

internal sealed class GenericBudget
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly CancellationToken cancellationToken;
    private int work;

    public GenericBudget(GenericAnalysisLimits? limits, CancellationToken cancellationToken)
    {
        Limits = limits ?? new GenericAnalysisLimits();
        if (Limits.MaximumDepth is < 1 or > 128 || Limits.MaximumWork is < 1 or > 1_000_000 ||
            Limits.MaximumItems is < 1 or > 4096 || Limits.Timeout <= TimeSpan.Zero || Limits.Timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Generic analysis requires finite positive depth, work, item and time limits.");
        }

        this.cancellationToken = cancellationToken;
    }

    public GenericAnalysisLimits Limits { get; }

    public void Step(int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++work > Limits.MaximumWork || depth > Limits.MaximumDepth || Stopwatch.GetElapsedTime(started) >= Limits.Timeout)
        {
            throw new GenericFailure(GenericAnalysisStatus.LimitExceeded, "Generic analysis exceeded its depth, work or elapsed-time budget.");
        }
    }

    public void Count(int count)
    {
        Step();
        if (count < 0)
        {
            throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "Generic collections must be initialized.");
        }

        if (count > Limits.MaximumItems)
        {
            throw new GenericFailure(GenericAnalysisStatus.LimitExceeded, "Generic analysis exceeded its item budget.");
        }
    }

    public void Name(string? name)
    {
        Step();
        if (name is null || name.Length is < 1 or > 1024 || string.IsNullOrWhiteSpace(name))
        {
            throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "Generic identities must contain 1 to 1024 nonblank characters.");
        }
    }

    public HashSet<string> Parameters(ImmutableArray<string> parameters)
    {
        Count(parameters.IsDefault ? -1 : parameters.Length);
        if (parameters.Length > TraitSolver.MaximumTypeArguments)
        {
            throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "A generic declaration supports at most 16 type parameters.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string parameter in parameters)
        {
            Name(parameter);
            if (!names.Add(parameter))
            {
                throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "Generic parameter names must be distinct.");
            }
        }

        return names;
    }
}

internal static class GenericTypes
{
    public static string Key(RustType type, GenericBudget budget, bool requireClosed = false, HashSet<string>? parameters = null)
    {
        var builder = new StringBuilder();
        Append(type, 0);
        if (builder.Length > 65_536)
        {
            throw new GenericFailure(GenericAnalysisStatus.LimitExceeded, "A canonical type identity exceeds 65536 characters.");
        }

        return builder.ToString();

        void Append(RustType current, int depth)
        {
            budget.Step(depth);
            if (current is null)
            {
                throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "A type argument must not be null.");
            }

            budget.Name(current.Name);
            if (builder.Length + current.Name.Length + 32 > 65_536)
            {
                throw new GenericFailure(GenericAnalysisStatus.LimitExceeded, "A canonical type identity exceeds 65536 characters.");
            }
            if (current.Kind == RustTypeKind.Parameter && (requireClosed || parameters is not null && !parameters.Contains(current.Name)))
            {
                throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "A type contains an unbound or undeclared parameter.");
            }

            builder.Append((int)current.Kind).Append(':').Append(current.Name.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(current.Name).Append('[');
            budget.Count(current.Arguments.Length);
            foreach (RustType argument in current.Arguments)
            {
                Append(argument, depth + 1);
            }

            builder.Append(']');
        }
    }

    public static RustType Substitute(RustType type, IReadOnlyDictionary<string, RustType> bindings, GenericBudget budget, int depth)
    {
        budget.Step(depth);
        if (type is null)
        {
            throw new GenericFailure(GenericAnalysisStatus.InvalidInput, "A type argument must not be null.");
        }

        budget.Name(type.Name);
        if (type.Kind == RustTypeKind.Parameter)
        {
            return bindings.TryGetValue(type.Name, out RustType? replacement) ? replacement : type;
        }

        budget.Count(type.Arguments.Length);
        if (type.Arguments.IsEmpty) return type;
        var arguments = new RustType[type.Arguments.Length];
        for (int index = 0; index < arguments.Length; index++)
        {
            arguments[index] = Substitute(type.Arguments[index], bindings, budget, depth + 1);
        }

        return RustType.Named(type.Name, arguments);
    }

    public static bool Match(RustType template, RustType actual, Dictionary<string, RustType> bindings, GenericBudget budget, int depth)
    {
        budget.Step(depth);
        if (template.Kind == RustTypeKind.Parameter)
        {
            if (bindings.TryGetValue(template.Name, out RustType? previous))
            {
                return Equal(previous, actual, budget, depth + 1);
            }

            budget.Count(bindings.Count + 1);
            bindings.Add(template.Name, actual);
            return true;
        }

        if (template.Kind != actual.Kind || !string.Equals(template.Name, actual.Name, StringComparison.Ordinal) ||
            template.Arguments.Length != actual.Arguments.Length) return false;
        for (int index = 0; index < template.Arguments.Length; index++)
        {
            if (!Match(template.Arguments[index], actual.Arguments[index], bindings, budget, depth + 1)) return false;
        }

        return true;
    }

    private static bool Equal(RustType left, RustType right, GenericBudget budget, int depth)
    {
        budget.Step(depth);
        if (left.Kind != right.Kind || !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Arguments.Length != right.Arguments.Length) return false;
        for (int index = 0; index < left.Arguments.Length; index++)
        {
            if (!Equal(left.Arguments[index], right.Arguments[index], budget, depth + 1)) return false;
        }

        return true;
    }
}
