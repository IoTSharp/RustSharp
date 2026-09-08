namespace RustSharp.Syntax;

/// <summary>Classifies safe-core statements.</summary>
public enum SafeCoreStatementKind
{
    Let,
    Return,
    Expression,
    Item,
    Empty,
}

/// <summary>Base type for statements in a safe-core block.</summary>
public abstract record SafeCoreStatementSyntax(SafeCoreStatementKind Kind, TextSpan Span)
{
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = Array.Empty<SafeCoreAttributeSyntax>();
}

/// <summary>A local binding statement.</summary>
public sealed record SafeCoreLetStatementSyntax(
    SafeCorePatternSyntax Pattern,
    SafeCoreTypeSyntax? Type,
    SafeCoreExpressionSyntax? Initializer,
    TextSpan Span)
    : SafeCoreStatementSyntax(SafeCoreStatementKind.Let, Span)
{
    public SafeCoreBlockSyntax? ElseBlock { get; init; }
}

/// <summary>A return statement.</summary>
public sealed record SafeCoreReturnStatementSyntax(
    SafeCoreExpressionSyntax? Value,
    TextSpan Span)
    : SafeCoreStatementSyntax(SafeCoreStatementKind.Return, Span);

/// <summary>An expression statement.</summary>
public sealed record SafeCoreExpressionStatementSyntax(
    SafeCoreExpressionSyntax Expression,
    bool HasSemicolon,
    TextSpan Span)
    : SafeCoreStatementSyntax(SafeCoreStatementKind.Expression, Span);

/// <summary>A brace-delimited block.</summary>
public sealed record SafeCoreBlockSyntax(
    IReadOnlyList<SafeCoreStatementSyntax> Statements,
    SafeCoreExpressionSyntax? TailExpression,
    TextSpan Span)
{
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = Array.Empty<SafeCoreAttributeSyntax>();
}

/// <summary>Classifies safe-core expressions.</summary>
public enum SafeCoreExpressionKind
{
    Name,
    Literal,
    Unary,
    Binary,
    Call,
    Tuple,
    Array,
    Block,
    If,
    Index,
    Print,
    Struct,
    Member,
    Cast,
    Range,
    Match,
    Loop,
    While,
    For,
    Closure,
    Return,
    Break,
    Continue,
    Try,
    Let,
    LabeledBlock,
    ConstBlock,
    QualifiedName,
}

/// <summary>Base type for expressions.</summary>
public abstract record SafeCoreExpressionSyntax(SafeCoreExpressionKind Kind, TextSpan Span)
{
    public IReadOnlyList<SafeCoreAttributeSyntax> Attributes { get; init; } = Array.Empty<SafeCoreAttributeSyntax>();
}

/// <summary>The explicitly supported built-in println! macro expression.</summary>
public sealed record SafeCorePrintExpressionSyntax(
    IReadOnlyList<SafeCoreExpressionSyntax> Arguments,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Print, Span);

/// <summary>A name or path expression.</summary>
public sealed record SafeCoreNameExpressionSyntax(
    string Path,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Name, Span)
{
    public IReadOnlyList<SafeCoreExpressionPathSegmentSyntax> Segments { get; init; } = Array.Empty<SafeCoreExpressionPathSegmentSyntax>();
}

/// <summary>A lexical literal expression.</summary>
public sealed record SafeCoreLiteralExpressionSyntax(
    RustTokenKind LiteralKind,
    string RawText,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Literal, Span);

/// <summary>A prefix unary expression.</summary>
public sealed record SafeCoreUnaryExpressionSyntax(
    string Operator,
    SafeCoreExpressionSyntax Operand,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Unary, Span);

/// <summary>A binary expression.</summary>
public sealed record SafeCoreBinaryExpressionSyntax(
    string Operator,
    SafeCoreExpressionSyntax Left,
    SafeCoreExpressionSyntax Right,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Binary, Span);

/// <summary>A function or constructor call.</summary>
public sealed record SafeCoreCallExpressionSyntax(
    SafeCoreExpressionSyntax Callee,
    IReadOnlyList<SafeCoreExpressionSyntax> Arguments,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Call, Span);

/// <summary>A parenthesized or tuple expression.</summary>
public sealed record SafeCoreTupleExpressionSyntax(
    IReadOnlyList<SafeCoreExpressionSyntax> Elements,
    bool HasTrailingComma,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Tuple, Span);

/// <summary>An array literal expression.</summary>
public sealed record SafeCoreArrayExpressionSyntax(
    IReadOnlyList<SafeCoreExpressionSyntax> Elements,
    SafeCoreExpressionSyntax? RepeatCount,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Array, Span);

/// <summary>A block expression.</summary>
public sealed record SafeCoreBlockExpressionSyntax(
    SafeCoreBlockSyntax Block,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Block, Span);

/// <summary>An <c>if</c>/<c>else</c> expression.</summary>
public sealed record SafeCoreIfExpressionSyntax(
    SafeCoreExpressionSyntax Condition,
    SafeCoreBlockSyntax Then,
    SafeCoreExpressionSyntax? Else,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.If, Span);

/// <summary>An indexed expression such as <c>values[0]</c>.</summary>
public sealed record SafeCoreIndexExpressionSyntax(
    SafeCoreExpressionSyntax Target,
    SafeCoreExpressionSyntax Index,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Index, Span);

/// <summary>Classifies safe-core patterns.</summary>
public enum SafeCorePatternKind
{
    Identifier,
    Wildcard,
    Literal,
    Tuple,
    Path,
    Reference,
    Slice,
    Rest,
    At,
    Or,
    Range,
    Struct,
}

/// <summary>Base type for patterns.</summary>
public abstract record SafeCorePatternSyntax(SafeCorePatternKind Kind, TextSpan Span);

/// <summary>An identifier binding pattern.</summary>
public sealed record SafeCoreIdentifierPatternSyntax(
    string Name,
    bool IsMutable,
    TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Identifier, Span)
{
    public bool IsByReference { get; init; }
}

/// <summary>The wildcard <c>_</c> pattern.</summary>
public sealed record SafeCoreWildcardPatternSyntax(TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Wildcard, Span);

/// <summary>A literal pattern.</summary>
public sealed record SafeCoreLiteralPatternSyntax(
    RustTokenKind LiteralKind,
    string RawText,
    TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Literal, Span);

/// <summary>A tuple pattern.</summary>
public sealed record SafeCoreTuplePatternSyntax(
    IReadOnlyList<SafeCorePatternSyntax> Elements,
    bool HasTrailingComma,
    TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Tuple, Span);

/// <summary>A path pattern such as <c>Some(value)</c>.</summary>
public sealed record SafeCorePathPatternSyntax(
    string Path,
    IReadOnlyList<SafeCorePatternSyntax> Arguments,
    TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Path, Span)
{
    public bool HasArguments { get; init; }
    public IReadOnlyList<SafeCoreExpressionPathSegmentSyntax> Segments { get; init; } = Array.Empty<SafeCoreExpressionPathSegmentSyntax>();
    public SafeCoreQualifiedNameExpressionSyntax? Qualifier { get; init; }
}

public sealed record SafeCoreQualifiedNameExpressionSyntax(
    SafeCoreTypeSyntax SelfType, SafeCoreTypeSyntax? TraitType, SafeCoreNameExpressionSyntax Path, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.QualifiedName, Span);

public sealed record SafeCoreItemStatementSyntax(SafeCoreItemSyntax Item, TextSpan Span)
    : SafeCoreStatementSyntax(SafeCoreStatementKind.Item, Span);

public sealed record SafeCoreEmptyStatementSyntax(TextSpan Span)
    : SafeCoreStatementSyntax(SafeCoreStatementKind.Empty, Span);

public sealed record SafeCoreExpressionPathSegmentSyntax(
    string Name, IReadOnlyList<SafeCoreGenericArgumentSyntax> GenericArguments, TextSpan Span)
{
    public bool HasGenericArguments { get; init; }
}

public sealed record SafeCoreStructExpressionFieldSyntax(
    string Name, SafeCoreExpressionSyntax Value, bool IsShorthand,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes, TextSpan Span);

public sealed record SafeCoreStructExpressionSyntax(
    SafeCoreNameExpressionSyntax Path, IReadOnlyList<SafeCoreStructExpressionFieldSyntax> Fields,
    SafeCoreExpressionSyntax? Base, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Struct, Span)
{
    public SafeCoreQualifiedNameExpressionSyntax? Qualifier { get; init; }
}

public sealed record SafeCoreMemberExpressionSyntax(
    SafeCoreExpressionSyntax Target, string Member,
    IReadOnlyList<SafeCoreGenericArgumentSyntax> GenericArguments, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Member, Span)
{
    public bool HasGenericArguments { get; init; }
}

public sealed record SafeCoreCastExpressionSyntax(
    SafeCoreExpressionSyntax Expression, SafeCoreTypeSyntax Type, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Cast, Span);

public sealed record SafeCoreRangeExpressionSyntax(
    SafeCoreExpressionSyntax? Start, SafeCoreExpressionSyntax? End, bool IsInclusive, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Range, Span);

public sealed record SafeCoreMatchArmSyntax(
    SafeCorePatternSyntax Pattern, SafeCoreExpressionSyntax? Guard,
    SafeCoreExpressionSyntax Body, IReadOnlyList<SafeCoreAttributeSyntax> Attributes, TextSpan Span);

public sealed record SafeCoreMatchExpressionSyntax(
    SafeCoreExpressionSyntax Scrutinee, IReadOnlyList<SafeCoreMatchArmSyntax> Arms, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Match, Span);

public sealed record SafeCoreLoopExpressionSyntax(string? Label, SafeCoreBlockSyntax Body, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Loop, Span);

public sealed record SafeCoreWhileExpressionSyntax(
    string? Label, SafeCoreExpressionSyntax Condition, SafeCoreBlockSyntax Body, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.While, Span);

public sealed record SafeCoreForExpressionSyntax(
    string? Label, SafeCorePatternSyntax Pattern, SafeCoreExpressionSyntax Iterator,
    SafeCoreBlockSyntax Body, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.For, Span);

public sealed record SafeCoreClosureParameterSyntax(
    SafeCorePatternSyntax Pattern, SafeCoreTypeSyntax? Type,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes, TextSpan Span);

public sealed record SafeCoreClosureExpressionSyntax(
    bool IsMove, IReadOnlyList<SafeCoreClosureParameterSyntax> Parameters,
    SafeCoreTypeSyntax? ReturnType, SafeCoreExpressionSyntax Body, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Closure, Span);

public sealed record SafeCoreReturnExpressionSyntax(SafeCoreExpressionSyntax? Value, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Return, Span);

public sealed record SafeCoreBreakExpressionSyntax(string? Label, SafeCoreExpressionSyntax? Value, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Break, Span);

public sealed record SafeCoreContinueExpressionSyntax(string? Label, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Continue, Span);

public sealed record SafeCoreTryExpressionSyntax(SafeCoreExpressionSyntax Operand, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Try, Span);

public sealed record SafeCoreLetExpressionSyntax(
    SafeCorePatternSyntax Pattern, SafeCoreExpressionSyntax Value, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Let, Span);

public sealed record SafeCoreLabeledBlockExpressionSyntax(string Label, SafeCoreBlockSyntax Block, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.LabeledBlock, Span);

public sealed record SafeCoreConstBlockExpressionSyntax(SafeCoreBlockSyntax Block, TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.ConstBlock, Span);

public sealed record SafeCoreReferencePatternSyntax(bool IsMutable, SafeCorePatternSyntax Pattern, TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Reference, Span);

public sealed record SafeCoreSlicePatternSyntax(IReadOnlyList<SafeCorePatternSyntax> Elements, TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Slice, Span);

public sealed record SafeCoreRestPatternSyntax(TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Rest, Span);

public sealed record SafeCoreAtPatternSyntax(
    SafeCoreIdentifierPatternSyntax Binding, SafeCorePatternSyntax Pattern, TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.At, Span);

public sealed record SafeCoreOrPatternSyntax(IReadOnlyList<SafeCorePatternSyntax> Alternatives, TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Or, Span);

public sealed record SafeCoreRangePatternSyntax(
    SafeCorePatternSyntax? Start, SafeCorePatternSyntax? End, bool IsInclusive, TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Range, Span);

public sealed record SafeCoreStructPatternFieldSyntax(
    string Name, SafeCorePatternSyntax Pattern, bool IsShorthand,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes, TextSpan Span);

public sealed record SafeCoreStructPatternSyntax(
    string Path, IReadOnlyList<SafeCoreStructPatternFieldSyntax> Fields, bool HasRest, TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Struct, Span)
{
    public IReadOnlyList<SafeCoreExpressionPathSegmentSyntax> Segments { get; init; } = Array.Empty<SafeCoreExpressionPathSegmentSyntax>();
    public SafeCoreQualifiedNameExpressionSyntax? Qualifier { get; init; }
}
