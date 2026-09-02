using System.Collections.Immutable;

namespace RustSharp.Semantics;

public enum RustTypeKind
{
    Unit,
    Bool,
    I32,
    Text,
    Parameter,
    Named,
}

public sealed class RustType : IEquatable<RustType>
{
    private RustType(RustTypeKind kind, string name, ImmutableArray<RustType> arguments)
    {
        Kind = kind;
        Name = name;
        Arguments = arguments;
    }

    public RustTypeKind Kind { get; }
    public string Name { get; }
    public ImmutableArray<RustType> Arguments { get; }

    public static RustType Unit { get; } = new(RustTypeKind.Unit, "()", []);
    public static RustType Bool { get; } = new(RustTypeKind.Bool, "bool", []);
    public static RustType I32 { get; } = new(RustTypeKind.I32, "i32", []);
    public static RustType Text { get; } = new(RustTypeKind.Text, "str", []);

    public static RustType Parameter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(RustTypeKind.Parameter, name, []);
    }

    public static RustType Named(string name, params RustType[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length > TraitSolver.MaximumTypeArguments)
        {
            throw new ArgumentException(
                $"A type may have at most {TraitSolver.MaximumTypeArguments} arguments.",
                nameof(arguments));
        }

        return new(
            RustTypeKind.Named,
            name,
            ImmutableArray.Create(arguments));
    }

    public override string ToString() => Arguments.IsEmpty
        ? Name
        : $"{Name}<{string.Join(", ", Arguments)}>";

    public bool Equals(RustType? other) =>
        other is not null &&
        Kind == other.Kind &&
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        Arguments.SequenceEqual(other.Arguments);

    public override bool Equals(object? obj) => Equals(obj as RustType);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Name, StringComparer.Ordinal);
        foreach (RustType argument in Arguments)
        {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }
}

public sealed record GenericTypeDefinition(string Name, ImmutableArray<string> Parameters)
{
    public GenericTypeDefinition(string name, IEnumerable<string> parameters)
        : this(
            ValidateName(name),
            CopyParameters(parameters))
    {
    }

    public RustType Instantiate(IReadOnlyList<RustType> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != Parameters.Length)
        {
            throw new ArgumentException(
                $"Type '{Name}' expects {Parameters.Length} arguments, received {arguments.Count}.",
                nameof(arguments));
        }

        return RustType.Named(Name, [.. arguments]);
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }

    private static ImmutableArray<string> CopyParameters(IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var result = ImmutableArray.CreateBuilder<string>();
        using IEnumerator<string> enumerator = parameters.GetEnumerator();
        for (var index = 0; index < TraitSolver.MaximumTypeArguments; index++)
        {
            if (!enumerator.MoveNext())
            {
                return result.ToImmutable();
            }

            string parameter = enumerator.Current;
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
            if (!result.AddUnique(parameter))
            {
                throw new ArgumentException($"Duplicate generic parameter '{parameter}'.", nameof(parameters));
            }
        }

        throw new ArgumentException(
            $"A generic definition may have at most {TraitSolver.MaximumTypeArguments} parameters.",
            nameof(parameters));
    }
}

public sealed record MonomorphizedType(
    GenericTypeDefinition Definition,
    ImmutableArray<RustType> Arguments,
    RustType ClosedType)
{
    public static MonomorphizedType Create(GenericTypeDefinition definition, IReadOnlyList<RustType> arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arguments);
        var copied = ImmutableArray.Create(arguments.ToArray());
        return new(definition, copied, definition.Instantiate(copied));
    }
}

public sealed record TraitDefinition
{
    public TraitDefinition(string name)
    {
        Name = Validate(name);
    }

    public string Name { get; }

    private static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}

public sealed record TraitImplementation(
    TraitDefinition Trait,
    RustType Target,
    ImmutableArray<RustType> TypeArguments,
    string Provider)
{
    public TraitImplementation(TraitDefinition trait, RustType target, string provider)
        : this(trait, target, [], ValidateProvider(provider))
    {
    }

    private static string ValidateProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return provider;
    }
}

public enum TraitResolutionStatus
{
    Resolved,
    Missing,
    Ambiguous,
    LimitExceeded,
}

public sealed record TraitResolutionResult(
    TraitResolutionStatus Status,
    TraitImplementation? Implementation,
    ImmutableArray<string> Candidates,
    string? Diagnostic)
{
    public bool IsSuccess => Status == TraitResolutionStatus.Resolved;
}

public sealed record TraitSolverLimits
{
    public TraitSolverLimits() : this(32, 1024) { }

    public TraitSolverLimits(int maximumDepth, int maximumWork)
    {
        if (maximumDepth is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (maximumWork is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumWork));
        MaximumDepth = maximumDepth;
        MaximumWork = maximumWork;
    }

    public int MaximumDepth { get; }
    public int MaximumWork { get; }
}

public sealed class TraitSolver
{
    public const int MaximumTypeArguments = 16;
    public const int MaximumImplementations = 4096;

    private readonly List<TraitImplementation> implementations = [];
    private readonly TraitSolverLimits limits;

    public TraitSolver(TraitSolverLimits? limits = null)
    {
        this.limits = limits ?? new TraitSolverLimits();
    }

    public void AddImplementation(TraitImplementation implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        if (implementations.Count >= MaximumImplementations)
        {
            throw new InvalidOperationException($"A trait solver supports at most {MaximumImplementations} implementations.");
        }

        if (implementation.TypeArguments.Length > MaximumTypeArguments)
        {
            throw new ArgumentException("Trait implementation has too many type arguments.", nameof(implementation));
        }

        implementations.Add(implementation);
    }

    public TraitResolutionResult Resolve(TraitDefinition trait, RustType target)
    {
        ArgumentNullException.ThrowIfNull(trait);
        ArgumentNullException.ThrowIfNull(target);
        var candidates = ImmutableArray.CreateBuilder<string>();
        var work = 0;
        var limitExceeded = false;
        var best = new List<TraitImplementation>();
        foreach (TraitImplementation implementation in implementations)
        {
            if (++work > limits.MaximumWork)
            {
                return new(TraitResolutionStatus.LimitExceeded, null, candidates.ToImmutable(), "Trait resolution work limit exceeded.");
            }

            if (!string.Equals(implementation.Trait.Name, trait.Name, StringComparison.Ordinal) ||
                !Matches(implementation.Target, target, depth: 0, ref work))
            {
                if (limitExceeded)
                {
                    return new(TraitResolutionStatus.LimitExceeded, null, candidates.ToImmutable(), "Trait resolution depth/work limit exceeded.");
                }

                continue;
            }

            best.Add(implementation);
            candidates.Add($"{implementation.Trait.Name} for {implementation.Target} ({implementation.Provider})");
        }

        return best.Count switch
        {
            0 => new(TraitResolutionStatus.Missing, null, candidates.ToImmutable(), $"No implementation of '{trait.Name}' for '{target}'."),
            1 => new(TraitResolutionStatus.Resolved, best[0], candidates.ToImmutable(), null),
            _ => new(TraitResolutionStatus.Ambiguous, null, candidates.ToImmutable(), $"Multiple implementations of '{trait.Name}' match '{target}'."),
        };

        bool Matches(RustType pattern, RustType actual, int depth, ref int units)
        {
            if (depth > limits.MaximumDepth || ++units > limits.MaximumWork)
            {
                limitExceeded = true;
                return false;
            }

            if (pattern.Kind == RustTypeKind.Parameter)
            {
                return true;
            }

            if (pattern.Kind != actual.Kind || !string.Equals(pattern.Name, actual.Name, StringComparison.Ordinal) || pattern.Arguments.Length != actual.Arguments.Length)
            {
                return false;
            }

            for (var index = 0; index < pattern.Arguments.Length; index++)
            {
                if (!Matches(pattern.Arguments[index], actual.Arguments[index], depth + 1, ref units))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

internal static class ImmutableArrayExtensions
{
    public static bool AddUnique(this ImmutableArray<string>.Builder builder, string value)
    {
        if (builder.Contains(value, StringComparer.Ordinal))
        {
            return false;
        }

        builder.Add(value);
        return true;
    }
}
