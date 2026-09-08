namespace RustSharp.Syntax;

/// <summary>Classifies safe-core types.</summary>
public enum SafeCoreTypeKind
{
    Path,
    Reference,
    Tuple,
    Array,
    Slice,
    Unit,
    Never,
    Function,
    Inferred,
    Bounded,
    QualifiedPath,
}

/// <summary>Base type for type syntax.</summary>
public abstract record SafeCoreTypeSyntax(SafeCoreTypeKind Kind, TextSpan Span);

/// <summary>A path type, optionally with generic arguments on each segment.</summary>
public sealed record SafeCorePathTypeSyntax(
    IReadOnlyList<SafeCorePathSegmentSyntax> Segments,
    TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Path, Span)
{
    /// <summary>Whether this path begins with an absolute path separator.</summary>
    public bool IsAbsolute { get; init; }
}

/// <summary>One path segment and its generic arguments.</summary>
public sealed record SafeCorePathSegmentSyntax(
    string Name,
    IReadOnlyList<SafeCoreTypeSyntax> GenericArguments,
    TextSpan Span)
{
    /// <summary>All generic arguments; GenericArguments retains only positional type arguments.</summary>
    public IReadOnlyList<SafeCoreGenericArgumentSyntax> Arguments { get; init; } = [];

    /// <summary>Whether an angle-bracketed argument list is present, including an empty list.</summary>
    public bool HasGenericArguments { get; init; }

    /// <summary>Whether this segment uses parenthesized function-trait arguments.</summary>
    public bool HasFunctionArguments { get; init; }

    /// <summary>Input types of a function-trait path segment.</summary>
    public IReadOnlyList<SafeCoreTypeSyntax> FunctionParameters { get; init; } = [];

    /// <summary>Output type of a function-trait path segment.</summary>
    public SafeCoreTypeSyntax? FunctionReturnType { get; init; }
}

/// <summary>Classifies a generic argument without treating lifetimes as types.</summary>
public enum SafeCoreGenericArgumentKind { Type, Lifetime, Const, AssociatedType, AssociatedConstraint }

/// <summary>Base node for structured generic arguments.</summary>
public abstract record SafeCoreGenericArgumentSyntax(SafeCoreGenericArgumentKind Kind, TextSpan Span);

/// <summary>A positional type argument (a single identifier remains syntactically ambiguous with a const argument).</summary>
public sealed record SafeCoreTypeArgumentSyntax(SafeCoreTypeSyntax Type, TextSpan Span)
    : SafeCoreGenericArgumentSyntax(SafeCoreGenericArgumentKind.Type, Span);

/// <summary>A lifetime argument with its exact spelling.</summary>
public sealed record SafeCoreLifetimeArgumentSyntax(string Lifetime, TextSpan Span)
    : SafeCoreGenericArgumentSyntax(SafeCoreGenericArgumentKind.Lifetime, Span);

/// <summary>A literal or braced const argument.</summary>
public sealed record SafeCoreConstArgumentSyntax(SafeCoreExpressionSyntax Value, TextSpan Span)
    : SafeCoreGenericArgumentSyntax(SafeCoreGenericArgumentKind.Const, Span);

/// <summary>An associated type equality constraint, such as Item = T.</summary>
public sealed record SafeCoreAssociatedTypeArgumentSyntax(string Name, SafeCoreTypeSyntax Type, TextSpan Span)
    : SafeCoreGenericArgumentSyntax(SafeCoreGenericArgumentKind.AssociatedType, Span)
{
    /// <summary>Generic arguments of the associated type being constrained.</summary>
    public IReadOnlyList<SafeCoreGenericArgumentSyntax> Arguments { get; init; } = [];
}

/// <summary>An associated type bound, such as Item: Copy.</summary>
public sealed record SafeCoreAssociatedConstraintArgumentSyntax(
    string Name, IReadOnlyList<SafeCoreTypeBoundSyntax> Bounds, TextSpan Span)
    : SafeCoreGenericArgumentSyntax(SafeCoreGenericArgumentKind.AssociatedConstraint, Span)
{
    /// <summary>Generic arguments of the associated type being constrained.</summary>
    public IReadOnlyList<SafeCoreGenericArgumentSyntax> Arguments { get; init; } = [];
}

/// <summary>A trait or lifetime bound.</summary>
public abstract record SafeCoreTypeBoundSyntax(TextSpan Span);

/// <summary>A lifetime bound such as 'a or 'static.</summary>
public sealed record SafeCoreLifetimeBoundSyntax(string Lifetime, TextSpan Span) : SafeCoreTypeBoundSyntax(Span);

/// <summary>A trait path with an optional ? modifier and higher-ranked lifetime binder.</summary>
public sealed record SafeCoreTraitBoundSyntax(
    SafeCoreTypeSyntax Type,
    bool IsOptional,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    TextSpan Span) : SafeCoreTypeBoundSyntax(Span);

/// <summary>A shared or mutable reference type.</summary>
public sealed record SafeCoreReferenceTypeSyntax(
    string? Lifetime,
    bool IsMutable,
    SafeCoreTypeSyntax Inner,
    TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Reference, Span);

/// <summary>A tuple type.</summary>
public sealed record SafeCoreTupleTypeSyntax(
    IReadOnlyList<SafeCoreTypeSyntax> Elements,
    bool HasTrailingComma,
    TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Tuple, Span);

/// <summary>An array type with a bounded length expression.</summary>
public sealed record SafeCoreArrayTypeSyntax(
    SafeCoreTypeSyntax Element,
    SafeCoreExpressionSyntax Length,
    TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Array, Span);

/// <summary>A slice type.</summary>
public sealed record SafeCoreSliceTypeSyntax(
    SafeCoreTypeSyntax Element,
    TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Slice, Span);

/// <summary>The unit type <c>()</c>.</summary>
public sealed record SafeCoreUnitTypeSyntax(TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Unit, Span);

/// <summary>The never type <c>!</c>.</summary>
public sealed record SafeCoreNeverTypeSyntax(TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Never, Span);

/// <summary>A parameter in a bare function type, with an optional parameter name.</summary>
public sealed record SafeCoreFunctionTypeParameterSyntax(string? Name, SafeCoreTypeSyntax Type, TextSpan Span)
{
    /// <summary>Attributes attached to the parameter.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = [];
}

/// <summary>A safe Rust ABI bare function type, optionally with higher-ranked lifetimes.</summary>
public sealed record SafeCoreFunctionTypeSyntax(
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreFunctionTypeParameterSyntax> Parameters,
    SafeCoreTypeSyntax? ReturnType,
    TextSpan Span) : SafeCoreTypeSyntax(SafeCoreTypeKind.Function, Span);

/// <summary>An inferred type represented by an underscore.</summary>
public sealed record SafeCoreInferredTypeSyntax(TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Inferred, Span);

/// <summary>A dyn trait-object type or impl-trait type.</summary>
public sealed record SafeCoreBoundedTypeSyntax(
    bool IsDynamic,
    IReadOnlyList<SafeCoreTypeBoundSyntax> Bounds,
    TextSpan Span) : SafeCoreTypeSyntax(SafeCoreTypeKind.Bounded, Span);

/// <summary>A qualified type path such as &lt;T as Trait&gt;::Item.</summary>
public sealed record SafeCoreQualifiedPathTypeSyntax(
    SafeCoreTypeSyntax SelfType,
    SafeCoreTypeSyntax? Trait,
    IReadOnlyList<SafeCorePathSegmentSyntax> Segments,
    TextSpan Span) : SafeCoreTypeSyntax(SafeCoreTypeKind.QualifiedPath, Span);
