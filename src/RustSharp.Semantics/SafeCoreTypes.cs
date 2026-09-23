using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RustSharp.Semantics;

/// <summary>The structural types supported by the safe-core type checker.</summary>
public enum SafeCoreSemanticTypeKind
{
    Unit, Bool,
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "Char names the Rust primitive type represented by this semantic kind.")]
    Char,
    Str,
    I8, I16, I32, I64, I128, Isize,
    U8, U16, U32, U64, U128, Usize,
    F32, F64, Never,
    Tuple, Array, Slice, Reference, Function, Adt, Inference, Error, Closure,
}

public enum SafeCoreInferenceKind
{
    General,
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "Integer is the Rust inference category for unsuffixed integer literals.")]
    Integer,
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "Float is the Rust inference category for unsuffixed floating-point literals.")]
    Float,
}

/// <summary>
/// Immutable structural type identity. ADTs and function items use canonical declaration names;
/// a function with no name represents a function pointer. Function elements end with the return type.
/// References do not yet encode lifetimes. Construction is bounded to 4,096 children, 128 levels,
/// and 262,144 identity characters, and throws <see cref="SafeCoreTypeInferenceLimitException"/>
/// when those limits are exceeded.
/// </summary>
public sealed class SafeCoreType : IEquatable<SafeCoreType>
{
    private const int MaximumChildren = 4_096;
    private const int MaximumIdentityLength = 262_144;
    private readonly string _identity;
    private readonly string _display;
    private readonly int _depth;

    private SafeCoreType(
        SafeCoreSemanticTypeKind kind,
        IReadOnlyList<SafeCoreType>? elements = null,
        long? length = null,
        bool mutable = false,
        string? name = null,
        int variableId = -1,
        Guid inferenceOwner = default,
        bool hasCaptures = false,
        Stopwatch? constructionClock = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Kind = kind;
        Length = length;
        IsMutable = mutable;
        HasCaptures = hasCaptures;
        Name = name;
        VariableId = variableId;
        InferenceOwner = inferenceOwner;
        var clock = constructionClock ?? Stopwatch.StartNew();
        int count = elements?.Count ?? 0;
        if (count is < 0 or > MaximumChildren || name?.Length > MaximumIdentityLength)
        {
            throw new SafeCoreTypeInferenceLimitException("Structural type size limit reached.");
        }

        var children = new SafeCoreType[count];
        var identity = new StringBuilder();
        identity.Append(CultureInfo.InvariantCulture,
            $"{(int)kind}:{length}:{mutable}:{hasCaptures}:{name?.Length ?? 0}:{name}:{variableId}:{inferenceOwner}");
        if (identity.Length > MaximumIdentityLength)
        {
            throw new SafeCoreTypeInferenceLimitException("Structural type identity length limit reached.");
        }

        _depth = 1;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new SafeCoreTypeInferenceLimitException("Structural type construction timeout reached.");
            }

            SafeCoreType child = elements![index];
            ArgumentNullException.ThrowIfNull(child);
            children[index] = child;
            _depth = Math.Max(_depth, child._depth + 1);
            if (_depth > 128 || identity.Length + child._identity.Length + 12 > MaximumIdentityLength)
            {
                throw new SafeCoreTypeInferenceLimitException("Structural type size or nesting limit reached.");
            }

            identity.Append(CultureInfo.InvariantCulture, $":{child._identity.Length}:{child._identity}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (clock.Elapsed > TimeSpan.FromSeconds(10))
        {
            throw new SafeCoreTypeInferenceLimitException("Structural type construction timeout reached.");
        }

        Elements = System.Array.AsReadOnly(children);
        ParameterTypes = kind is SafeCoreSemanticTypeKind.Function or SafeCoreSemanticTypeKind.Closure
            ? System.Array.AsReadOnly(children[..^1])
            : System.Array.Empty<SafeCoreType>();
        _identity = identity.ToString();
        _display = kind switch
        {
            SafeCoreSemanticTypeKind.Unit => "()",
            SafeCoreSemanticTypeKind.Never => "!",
            SafeCoreSemanticTypeKind.Tuple => $"({string.Join(", ", Elements)}{(count == 1 ? "," : "")})",
            SafeCoreSemanticTypeKind.Array => $"[{Elements[0]}; {length?.ToString(CultureInfo.InvariantCulture)}]",
            SafeCoreSemanticTypeKind.Slice => $"[{Elements[0]}]",
            SafeCoreSemanticTypeKind.Reference => $"&{(mutable ? "mut " : "")}{Elements[0]}",
            SafeCoreSemanticTypeKind.Function => $"fn({string.Join(", ", ParameterTypes)}) -> {ReturnType}{(name is null ? "" : $" {{{name}}}")}",
            SafeCoreSemanticTypeKind.Closure => $"closure {name}({string.Join(", ", ParameterTypes)}) -> {ReturnType}",
            SafeCoreSemanticTypeKind.Adt => name!,
            SafeCoreSemanticTypeKind.Inference => $"?{variableId.ToString(CultureInfo.InvariantCulture)}",
            SafeCoreSemanticTypeKind.Error => "<error>",
            _ => kind.ToString().ToLowerInvariant(),
        };
    }

    public SafeCoreSemanticTypeKind Kind { get; }
    public IReadOnlyList<SafeCoreType> Elements { get; }
    public long? Length { get; }
    public bool IsMutable { get; }
    public bool HasCaptures { get; }
    public string? Name { get; }
    public int VariableId { get; }
    public SafeCoreType ElementType => Elements[0];
    public IReadOnlyList<SafeCoreType> ParameterTypes { get; }
    public SafeCoreType ReturnType => Elements[^1];
    internal Guid InferenceOwner { get; }

    public bool IsInteger => Kind is >= SafeCoreSemanticTypeKind.I8 and <= SafeCoreSemanticTypeKind.Usize;
    public bool IsFloat => Kind is SafeCoreSemanticTypeKind.F32 or SafeCoreSemanticTypeKind.F64;

    public static SafeCoreType Primitive(SafeCoreSemanticTypeKind kind, CancellationToken cancellationToken = default)
    {
        if (kind is < SafeCoreSemanticTypeKind.Unit or > SafeCoreSemanticTypeKind.Never
            && kind != SafeCoreSemanticTypeKind.Error)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "A primitive type kind is required.");
        }

        return new(kind, cancellationToken: cancellationToken);
    }

    public static SafeCoreType Tuple(IReadOnlyList<SafeCoreType> elements, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elements);
        cancellationToken.ThrowIfCancellationRequested();
        return elements.Count == 0 ? Primitive(SafeCoreSemanticTypeKind.Unit, cancellationToken)
            : new(SafeCoreSemanticTypeKind.Tuple, elements, cancellationToken: cancellationToken);
    }

    public static SafeCoreType Array(SafeCoreType element, long length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new(SafeCoreSemanticTypeKind.Array, [element], length, cancellationToken: cancellationToken);
    }

    public static SafeCoreType Slice(SafeCoreType element, CancellationToken cancellationToken = default) =>
        new(SafeCoreSemanticTypeKind.Slice, [element], cancellationToken: cancellationToken);
    public static SafeCoreType Reference(SafeCoreType element, bool mutable, CancellationToken cancellationToken = default) =>
        new(SafeCoreSemanticTypeKind.Reference, [element], mutable: mutable, cancellationToken: cancellationToken);

    public static SafeCoreType Function(IReadOnlyList<SafeCoreType> parameters, SafeCoreType returnType, string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
        }

        int count = parameters.Count;
        if (count is < 0 or >= MaximumChildren)
        {
            throw new SafeCoreTypeInferenceLimitException("Function parameter count limit reached.");
        }

        var elements = new SafeCoreType[count + 1];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new SafeCoreTypeInferenceLimitException("Function type construction timeout reached.");
            }

            elements[index] = parameters[index];
        }

        elements[count] = returnType;
        return new(SafeCoreSemanticTypeKind.Function, elements, name: name,
            cancellationToken: cancellationToken, constructionClock: clock);
    }

    public static SafeCoreType Adt(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new(SafeCoreSemanticTypeKind.Adt, name: name, cancellationToken: cancellationToken);
    }

    public static SafeCoreType Closure(IReadOnlyList<SafeCoreType> parameters, SafeCoreType returnType,
        string name, bool hasCaptures, bool isMutable = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        SafeCoreType signature = Function(parameters, returnType, cancellationToken: cancellationToken);
        return new(SafeCoreSemanticTypeKind.Closure, signature.Elements, mutable: isMutable, name: name,
            hasCaptures: hasCaptures, cancellationToken: cancellationToken);
    }

    internal static SafeCoreType Inference(int id, Guid owner, CancellationToken cancellationToken) =>
        new(SafeCoreSemanticTypeKind.Inference, variableId: id, inferenceOwner: owner, cancellationToken: cancellationToken);

    public bool Equals(SafeCoreType? other) => other is not null && string.Equals(_identity, other._identity, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is SafeCoreType type && Equals(type);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_identity);
    public override string ToString() => _display;
    public static bool operator ==(SafeCoreType? left, SafeCoreType? right) => Equals(left, right);
    public static bool operator !=(SafeCoreType? left, SafeCoreType? right) => !Equals(left, right);
}

/// <summary>Budgets apply across the lifetime of an inference context, including resolution.</summary>
public sealed record SafeCoreTypeInferenceOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public CancellationToken CancellationToken { get; init; }
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumNestingDepth { get; init; } = 128;
    public int MaximumVariables { get; init; } = 100_000;
}

/// <summary>A resource budget was exhausted; the caller can convert this into a diagnostic.</summary>
public sealed class SafeCoreTypeInferenceLimitException(string message) : Exception(message);

/// <summary>
/// Bounded, monomorphic structural inference. Failed constraints roll back their substitutions.
/// Cancellation throws <see cref="OperationCanceledException"/>; exhausted budgets throw
/// <see cref="SafeCoreTypeInferenceLimitException"/>. Inference variables belong to one context.
/// </summary>
public sealed class SafeCoreTypeInference
{
    private sealed record Variable(SafeCoreInferenceKind Kind, SafeCoreType? Binding = null);

    private readonly SafeCoreTypeInferenceOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly List<Variable> _variables = [];
    private readonly List<(int Id, Variable Previous)> _trail = [];
    private int _operations;

    /// <summary>
    /// Bounded work consumed by this inference context.  The property is
    /// internal so composite validators can account for nested inference
    /// without exposing another mutable budget surface to callers.
    /// </summary>
    internal int OperationsUsed => _operations;

    public SafeCoreTypeInference(SafeCoreTypeInferenceOptions? options = null)
    {
        _options = options ?? new();
        if (_options.Timeout <= TimeSpan.Zero || _options.MaximumOperations <= 0
            || _options.MaximumNestingDepth is <= 0 or > 128 || _options.MaximumVariables <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Positive inference budgets and a depth at most 128 are required.");
        }
    }

    public SafeCoreType Fresh(SafeCoreInferenceKind kind = SafeCoreInferenceKind.General)
    {
        Step(0);
        if (kind is < SafeCoreInferenceKind.General or > SafeCoreInferenceKind.Float)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (_variables.Count >= _options.MaximumVariables)
        {
            throw new SafeCoreTypeInferenceLimitException("Inference variable count limit reached.");
        }

        SafeCoreType type = SafeCoreType.Inference(_variables.Count, _owner, _options.CancellationToken);
        _variables.Add(new(kind));
        return type;
    }

    public bool Unify(SafeCoreType left, SafeCoreType right) => Constrain(left, right, coercion: false);
    public bool Coerce(SafeCoreType source, SafeCoreType target) => Constrain(source, target, coercion: true);

    /// <summary>Substitutes known types, optionally defaulting unconstrained integer/float variables to i32/f64.</summary>
    public SafeCoreType Resolve(SafeCoreType type, bool defaultNumerics = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ResolveCore(type, defaultNumerics, 0);
    }

    private bool Constrain(SafeCoreType left, SafeCoreType right, bool coercion)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var success = false;
        _trail.Clear();
        try
        {
            success = coercion ? CoerceCore(left, right, 0) : UnifyCore(left, right, 0);
            return success;
        }
        finally
        {
            // The trail contains at most MaximumOperations entries. Rollback must finish even
            // after timeout/cancellation, so it deliberately performs no further budget checks.
            if (!success)
            {
                for (int index = _trail.Count - 1; index >= 0; index--)
                {
                    (int id, Variable previous) = _trail[index];
                    _variables[id] = previous;
                }
            }

            _trail.Clear();
        }
    }

    private bool UnifyCore(SafeCoreType left, SafeCoreType right, int depth)
    {
        Step(depth);
        left = Follow(left, depth);
        right = Follow(right, depth);
        if (left == right || left.Kind == SafeCoreSemanticTypeKind.Error || right.Kind == SafeCoreSemanticTypeKind.Error)
        {
            return true;
        }

        if (left.Kind == SafeCoreSemanticTypeKind.Inference)
        {
            return Bind(left, right, depth + 1);
        }

        if (right.Kind == SafeCoreSemanticTypeKind.Inference)
        {
            return Bind(right, left, depth + 1);
        }

        if (left.Kind != right.Kind || left.Length != right.Length || left.IsMutable != right.IsMutable || left.HasCaptures != right.HasCaptures
            || !string.Equals(left.Name, right.Name, StringComparison.Ordinal) || left.Elements.Count != right.Elements.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Elements.Count; index++)
        {
            if (!UnifyCore(left.Elements[index], right.Elements[index], depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    private bool CoerceCore(SafeCoreType source, SafeCoreType target, int depth)
    {
        Step(depth);
        source = Follow(source, depth);
        target = Follow(target, depth);
        if (source.Kind == SafeCoreSemanticTypeKind.Never)
        {
            return true;
        }

        if (source.Kind == SafeCoreSemanticTypeKind.Reference && target.Kind == SafeCoreSemanticTypeKind.Reference
            && (!target.IsMutable || source.IsMutable))
        {
            SafeCoreType from = Follow(source.ElementType, depth + 1);
            SafeCoreType to = Follow(target.ElementType, depth + 1);
            for (var index = 0; index <= _options.MaximumNestingDepth; index++)
            {
                Step(depth + index);
                int checkpoint = _trail.Count;
                if (from.Kind == SafeCoreSemanticTypeKind.Array && to.Kind == SafeCoreSemanticTypeKind.Slice
                    ? UnifyCore(from.ElementType, to.ElementType, depth + 1)
                    : UnifyCore(from, to, depth + 1)) return true;
                Rollback(checkpoint);
                if (from.Kind != SafeCoreSemanticTypeKind.Reference || target.IsMutable && !from.IsMutable) return false;
                from = Follow(from.ElementType, depth + 1);
            }
            return false;
        }

        if ((source.Kind == SafeCoreSemanticTypeKind.Function || source.Kind == SafeCoreSemanticTypeKind.Closure && !source.HasCaptures) && target.Kind == SafeCoreSemanticTypeKind.Function
            && target.Name is null && source.Elements.Count == target.Elements.Count)
        {
            for (var index = 0; index < source.Elements.Count; index++)
            {
                if (!UnifyCore(source.Elements[index], target.Elements[index], depth + 1))
                {
                    return false;
                }
            }

            return true;
        }

        return UnifyCore(source, target, depth);
    }

    private bool Bind(SafeCoreType variable, SafeCoreType type, int depth)
    {
        Step(depth);
        Variable entry = GetVariable(variable);
        if (type.Kind == SafeCoreSemanticTypeKind.Inference)
        {
            Variable other = GetVariable(type);
            if (entry.Kind != SafeCoreInferenceKind.General && other.Kind == SafeCoreInferenceKind.General)
            {
                Set(type.VariableId, variable);
                return true;
            }

            if (entry.Kind != SafeCoreInferenceKind.General && other.Kind != entry.Kind)
            {
                return false;
            }
        }
        else if (entry.Kind == SafeCoreInferenceKind.Integer && !type.IsInteger
            || entry.Kind == SafeCoreInferenceKind.Float && !type.IsFloat)
        {
            return false;
        }

        if (Occurs(variable, type, depth + 1))
        {
            return false;
        }

        Set(variable.VariableId, type);
        return true;
    }

    private bool Occurs(SafeCoreType variable, SafeCoreType type, int depth)
    {
        Step(depth);
        type = Follow(type, depth);
        if (type == variable)
        {
            return true;
        }

        for (var index = 0; index < type.Elements.Count; index++)
        {
            if (Occurs(variable, type.Elements[index], depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private SafeCoreType ResolveCore(SafeCoreType type, bool defaultNumerics, int depth)
    {
        Step(depth);
        type = Follow(type, depth);
        if (type.Kind == SafeCoreSemanticTypeKind.Inference)
        {
            Variable variable = GetVariable(type);
            if (defaultNumerics && variable.Kind != SafeCoreInferenceKind.General)
            {
                SafeCoreType result = SafeCoreType.Primitive(variable.Kind == SafeCoreInferenceKind.Integer
                    ? SafeCoreSemanticTypeKind.I32 : SafeCoreSemanticTypeKind.F64, _options.CancellationToken);
                _variables[type.VariableId] = variable with { Binding = result };
                return result;
            }

            return type;
        }

        if (type.Elements.Count == 0)
        {
            return type;
        }

        var elements = new SafeCoreType[type.Elements.Count];
        var changed = false;
        for (var index = 0; index < elements.Length; index++)
        {
            elements[index] = ResolveCore(type.Elements[index], defaultNumerics, depth + 1);
            changed |= elements[index] != type.Elements[index];
        }

        if (!changed)
        {
            return type;
        }

        return type.Kind switch
        {
            SafeCoreSemanticTypeKind.Tuple => SafeCoreType.Tuple(elements, _options.CancellationToken),
            SafeCoreSemanticTypeKind.Array => SafeCoreType.Array(elements[0], type.Length!.Value, _options.CancellationToken),
            SafeCoreSemanticTypeKind.Slice => SafeCoreType.Slice(elements[0], _options.CancellationToken),
            SafeCoreSemanticTypeKind.Reference => SafeCoreType.Reference(elements[0], type.IsMutable, _options.CancellationToken),
            SafeCoreSemanticTypeKind.Function => SafeCoreType.Function(elements[..^1], elements[^1], type.Name, _options.CancellationToken),
            SafeCoreSemanticTypeKind.Closure => SafeCoreType.Closure(elements[..^1], elements[^1], type.Name!, type.HasCaptures, type.IsMutable, _options.CancellationToken),
            _ => type,
        };
    }

    private SafeCoreType Follow(SafeCoreType type, int depth)
    {
        for (var index = 0; index <= _variables.Count; index++)
        {
            Step(depth);
            if (type.Kind != SafeCoreSemanticTypeKind.Inference || GetVariable(type).Binding is not { } binding)
            {
                return type;
            }

            type = binding;
        }

        throw new SafeCoreTypeInferenceLimitException("Inference substitution chain limit reached.");
    }

    private Variable GetVariable(SafeCoreType type)
    {
        if (type.InferenceOwner != _owner || type.VariableId < 0 || type.VariableId >= _variables.Count)
        {
            throw new ArgumentException("Inference variables cannot cross inference contexts.", nameof(type));
        }

        return _variables[type.VariableId];
    }

    private void Set(int id, SafeCoreType type)
    {
        _trail.Add((id, _variables[id]));
        _variables[id] = _variables[id] with { Binding = type };
    }

    private void Rollback(int checkpoint)
    {
        // Mandatory rollback remains bounded by MaximumOperations even after cancellation.
        for (int index = _trail.Count - 1; index >= checkpoint; index--)
        {
            (int id, Variable previous) = _trail[index];
            _variables[id] = previous;
        }
        _trail.RemoveRange(checkpoint, _trail.Count - checkpoint);
    }

    private void Step(int depth)
    {
        _options.CancellationToken.ThrowIfCancellationRequested();
        if (_operations >= _options.MaximumOperations || depth > _options.MaximumNestingDepth || _clock.Elapsed > _options.Timeout)
        {
            throw new SafeCoreTypeInferenceLimitException("Inference operation, nesting, or time limit reached.");
        }

        _operations++;
    }
}
