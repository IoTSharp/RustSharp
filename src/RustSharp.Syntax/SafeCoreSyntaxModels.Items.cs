namespace RustSharp.Syntax;

/// <summary>A function parameter.</summary>
public sealed record SafeCoreParameterSyntax(
    SafeCorePatternSyntax Pattern,
    SafeCoreTypeSyntax Type,
    TextSpan Span)
{
    /// <summary>The explicit receiver form, when this parameter is a method receiver.</summary>
    public SafeCoreReceiverSyntax? Receiver { get; init; }

    /// <summary>Outer attributes attached to the parameter.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = [];
}

/// <summary>A method's by-value, borrowed, or explicitly typed self receiver.</summary>
public sealed record SafeCoreReceiverSyntax(
    bool IsByReference,
    bool IsMutable,
    string? Lifetime,
    SafeCoreTypeSyntax? ExplicitType,
    TextSpan Span);

/// <summary>The declaration category of a generic parameter.</summary>
public enum SafeCoreGenericParameterKind { Type, Lifetime, Const }

/// <summary>A generic type parameter and its path bounds.</summary>
public sealed record SafeCoreGenericParameterSyntax(
    string Name,
    IReadOnlyList<SafeCoreTypeSyntax> Bounds,
    TextSpan Span)
{
    /// <summary>The parameter category; lifetime parameters never masquerade as type nodes.</summary>
    public SafeCoreGenericParameterKind Kind { get; init; }

    /// <summary>Structured trait and lifetime bounds, including bound modifiers and binders.</summary>
    public IReadOnlyList<SafeCoreTypeBoundSyntax> Constraints { get; init; } = [];

    /// <summary>The required type of a const parameter.</summary>
    public SafeCoreTypeSyntax? ConstType { get; init; }

    /// <summary>An optional type-parameter default.</summary>
    public SafeCoreTypeSyntax? DefaultType { get; init; }

    /// <summary>An optional const-parameter default expression.</summary>
    public SafeCoreExpressionSyntax? DefaultValue { get; init; }

    /// <summary>Attributes attached to this declaration.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = [];
}

/// <summary>A where clause and its structured predicates.</summary>
public sealed record SafeCoreWhereClauseSyntax(IReadOnlyList<SafeCoreWherePredicateSyntax> Predicates, TextSpan Span);

/// <summary>A lifetime or type predicate in a where clause.</summary>
public abstract record SafeCoreWherePredicateSyntax(TextSpan Span);

/// <summary>A lifetime outlives predicate.</summary>
public sealed record SafeCoreLifetimeWherePredicateSyntax(
    string Lifetime, IReadOnlyList<SafeCoreLifetimeBoundSyntax> Bounds, TextSpan Span)
    : SafeCoreWherePredicateSyntax(Span);

/// <summary>A type's trait and lifetime bounds, with an optional higher-ranked lifetime binder.</summary>
public sealed record SafeCoreTypeWherePredicateSyntax(
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    SafeCoreTypeSyntax Type,
    IReadOnlyList<SafeCoreTypeBoundSyntax> Bounds,
    TextSpan Span) : SafeCoreWherePredicateSyntax(Span);

/// <summary>A safe-core function item.</summary>
public sealed record SafeCoreFunctionSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreParameterSyntax> Parameters,
    SafeCoreTypeSyntax? ReturnType,
    SafeCoreBlockSyntax Body,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Function, Attributes, IsPublic, Span)
{
    /// <summary>Optional constraints following the function signature.</summary>
    public SafeCoreWhereClauseSyntax? WhereClause { get; init; }

    /// <summary>Whether the function has a const qualifier.</summary>
    public bool IsConst { get; init; }
}

/// <summary>A named struct field.</summary>
public sealed record SafeCoreFieldSyntax(
    string? Name,
    SafeCoreTypeSyntax Type,
    bool IsPublic,
    TextSpan Span)
{
    /// <summary>The field's full visibility syntax.</summary>
    public SafeCoreVisibilitySyntax? Visibility { get; init; }

    /// <summary>Attributes attached to the field.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = [];
}

/// <summary>A safe-core struct item.</summary>
public sealed record SafeCoreStructSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreFieldSyntax> Fields,
    bool IsTupleStruct,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Struct, Attributes, IsPublic, Span)
{
    /// <summary>Distinguishes <c>struct Name;</c> from an empty braced or tuple struct.</summary>
    public bool IsUnitStruct { get; init; }

    /// <summary>Optional generic constraints on this struct.</summary>
    public SafeCoreWhereClauseSyntax? WhereClause { get; init; }
}

/// <summary>The syntax shape of an enum variant.</summary>
public enum SafeCoreEnumVariantKind { Unit, Tuple, Struct }

/// <summary>An enum variant and optional tuple payload fields.</summary>
public sealed record SafeCoreEnumVariantSyntax(
    string Name,
    IReadOnlyList<SafeCoreFieldSyntax> Fields,
    TextSpan Span)
{
    /// <summary>Distinguishes unit, tuple, and named-field variants even when their fields are empty.</summary>
    public SafeCoreEnumVariantKind Kind { get; init; }

    /// <summary>An explicit discriminant expression.</summary>
    public SafeCoreExpressionSyntax? Discriminant { get; init; }

    /// <summary>Attributes attached to the variant.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = [];
}

/// <summary>A safe-core enum item.</summary>
public sealed record SafeCoreEnumSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreEnumVariantSyntax> Variants,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Enum, Attributes, IsPublic, Span)
{
    /// <summary>Optional generic constraints on this enum.</summary>
    public SafeCoreWhereClauseSyntax? WhereClause { get; init; }
}

/// <summary>A type alias item.</summary>
public sealed record SafeCoreTypeAliasSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    SafeCoreTypeSyntax Type,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.TypeAlias, Attributes, IsPublic, Span)
{
    /// <summary>Optional generic constraints on this alias.</summary>
    public SafeCoreWhereClauseSyntax? WhereClause { get; init; }
}

/// <summary>A typed constant item.</summary>
public sealed record SafeCoreConstSyntax(
    string Name,
    SafeCoreTypeSyntax Type,
    SafeCoreExpressionSyntax Value,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Const, Attributes, IsPublic, Span);

/// <summary>A safe trait declaration, including supertraits and associated declarations.</summary>
public sealed record SafeCoreTraitSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreTypeBoundSyntax> Bounds,
    SafeCoreWhereClauseSyntax? WhereClause,
    IReadOnlyList<SafeCoreAssociatedItemSyntax> Items,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span) : SafeCoreItemSyntax(SafeCoreItemKind.Trait, Attributes, IsPublic, Span)
{
    /// <summary>Inner attributes at the start of the trait body.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> InnerAttributes { get; init; } = [];
}

/// <summary>An inherent or trait implementation; Trait is null for inherent implementations.</summary>
public sealed record SafeCoreImplSyntax(
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    SafeCoreTypeSyntax? Trait,
    SafeCoreTypeSyntax SelfType,
    SafeCoreWhereClauseSyntax? WhereClause,
    IReadOnlyList<SafeCoreAssociatedItemSyntax> Items,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span) : SafeCoreItemSyntax(SafeCoreItemKind.Implementation, Attributes, false, Span)
{
    /// <summary>Inner attributes at the start of the implementation body.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> InnerAttributes { get; init; } = [];
}

/// <summary>Base node for an associated function, type, or constant.</summary>
public abstract record SafeCoreAssociatedItemSyntax(
    string Name,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    SafeCoreVisibilitySyntax Visibility,
    TextSpan Span);

/// <summary>An associated function declaration or definition with a structured signature.</summary>
public sealed record SafeCoreAssociatedFunctionSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreParameterSyntax> Parameters,
    SafeCoreTypeSyntax? ReturnType,
    SafeCoreWhereClauseSyntax? WhereClause,
    SafeCoreBlockSyntax? Body,
    bool IsConst,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    SafeCoreVisibilitySyntax Visibility,
    TextSpan Span) : SafeCoreAssociatedItemSyntax(Name, Attributes, Visibility, Span);

/// <summary>An associated type declaration, optionally with a default or assigned type.</summary>
public sealed record SafeCoreAssociatedTypeSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreTypeBoundSyntax> Bounds,
    SafeCoreWhereClauseSyntax? WhereClause,
    SafeCoreTypeSyntax? Type,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    SafeCoreVisibilitySyntax Visibility,
    TextSpan Span) : SafeCoreAssociatedItemSyntax(Name, Attributes, Visibility, Span);

/// <summary>An associated constant declaration, optionally with a default or assigned value.</summary>
public sealed record SafeCoreAssociatedConstSyntax(
    string Name,
    SafeCoreTypeSyntax Type,
    SafeCoreExpressionSyntax? Value,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    SafeCoreVisibilitySyntax Visibility,
    TextSpan Span) : SafeCoreAssociatedItemSyntax(Name, Attributes, Visibility, Span);
