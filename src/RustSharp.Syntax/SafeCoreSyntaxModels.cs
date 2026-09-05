namespace RustSharp.Syntax;

/// <summary>Bounds applied to one safe-core syntax parse.</summary>
public sealed record SafeCoreSyntaxOptions
{
    /// <summary>Maximum UTF-16 source characters inspected by the parse.</summary>
    public int MaximumSourceLength { get; init; } = 1_000_000;

    /// <summary>Maximum non-trivia tokens retained by the parse.</summary>
    public int MaximumTokens { get; init; } = 250_000;

    /// <summary>Maximum AST nodes produced by the parse.</summary>
    public int MaximumNodes { get; init; } = 100_000;

    /// <summary>Maximum parser diagnostics retained.</summary>
    public int MaximumDiagnostics { get; init; } = 128;

    /// <summary>Maximum recursive syntax nesting accepted.</summary>
    public int MaximumNestingDepth { get; init; } = 128;

    /// <summary>Maximum parser operations, independent of source size.</summary>
    public int MaximumOperations { get; init; } = 1_000_000;
}

/// <summary>Stable diagnostics emitted by the safe-core parser.</summary>
public static class SafeCoreSyntaxDiagnosticCodes
{
    /// <summary>An expected token or construct was missing.</summary>
    public const string ExpectedToken = "RSP1001";

    /// <summary>An extra token was found where the grammar had ended.</summary>
    public const string UnexpectedToken = "RSP1002";

    /// <summary>The token is outside the declared safe-core profile.</summary>
    public const string UnsupportedSyntax = "RSP1003";

    /// <summary>A parser delimiter or construct was not terminated.</summary>
    public const string UnterminatedConstruct = "RSP1004";

    /// <summary>A literal suffix is invalid when interpreted as an expression or pattern.</summary>
    public const string InvalidLiteralSuffix = "RSP1005";

    /// <summary>The parser reached one of its configured work limits.</summary>
    public const string LimitReached = "RSP0002";

    /// <summary>The lexer result was truncated before parsing could finish.</summary>
    public const string LexicalTruncation = "RSP0003";
}

/// <summary>Result of parsing one bounded safe-core source document.</summary>
public sealed class SafeCoreSyntaxResult
{
    internal SafeCoreSyntaxResult(
        string source,
        string sourcePath,
        SafeCoreCompilationUnitSyntax? root,
        IReadOnlyList<Diagnostic> diagnostics,
        RustLexResult lexResult,
        bool isTruncated)
    {
        Source = source;
        SourcePath = sourcePath;
        Root = root;
        Diagnostics = diagnostics;
        LexResult = lexResult;
        IsTruncated = isTruncated;
    }

    /// <summary>The exact source text supplied to the parser.</summary>
    public string Source { get; }

    /// <summary>The source path supplied by the caller.</summary>
    public string SourcePath { get; }

    /// <summary>The parsed root when the document has no diagnostics.</summary>
    public SafeCoreCompilationUnitSyntax? Root { get; }

    /// <summary>Stable lexical and syntactic diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>The lossless lexical result used by this parse.</summary>
    public RustLexResult LexResult { get; }

    /// <summary>Whether a source, lexer, or parser limit prevented completion.</summary>
    public bool IsTruncated { get; }

    /// <summary>Whether parsing completed without a diagnostic.</summary>
    public bool IsSuccessful => !IsTruncated && Root is not null && Diagnostics.Count == 0;

    /// <summary>Returns exact source text covered by a span.</summary>
    public string GetText(TextSpan span)
    {
        if (span.Start < 0 || span.Length < 0 || span.Start > Source.Length - span.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(span));
        }

        return Source.Substring(span.Start, span.Length);
    }
}

/// <summary>Root node for a safe-core source document.</summary>
public sealed record SafeCoreCompilationUnitSyntax(
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    IReadOnlyList<SafeCoreItemSyntax> Items,
    TextSpan Span);

/// <summary>One outer or inner Rust attribute.</summary>
public sealed record SafeCoreAttributeSyntax(
    bool IsInner,
    string Path,
    string ArgumentsText,
    TextSpan Span);

/// <summary>Classifies safe-core item nodes.</summary>
public enum SafeCoreItemKind
{
    Module,
    Use,
    Function,
    Struct,
    Enum,
    TypeAlias,
    Const,
}

/// <summary>Base type for a module-level item.</summary>
public abstract record SafeCoreItemSyntax(
    SafeCoreItemKind Kind,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    bool IsPublic,
    TextSpan Span);

/// <summary>A nested <c>mod name { ... }</c> item.</summary>
public sealed record SafeCoreModuleSyntax(
    string Name,
    IReadOnlyList<SafeCoreItemSyntax> Items,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Module, Attributes, IsPublic, Span);

/// <summary>A path import item.</summary>
public sealed record SafeCoreUseSyntax(
    string Path,
    string? Alias,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Use, Attributes, IsPublic, Span);

/// <summary>A function parameter.</summary>
public sealed record SafeCoreParameterSyntax(
    SafeCorePatternSyntax Pattern,
    SafeCoreTypeSyntax Type,
    TextSpan Span);

/// <summary>A generic type or const parameter and its bounds.</summary>
public sealed record SafeCoreGenericParameterSyntax(
    string Name,
    IReadOnlyList<SafeCoreTypeSyntax> Bounds,
    TextSpan Span);

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
    : SafeCoreItemSyntax(SafeCoreItemKind.Function, Attributes, IsPublic, Span);

/// <summary>A named struct field.</summary>
public sealed record SafeCoreFieldSyntax(
    string? Name,
    SafeCoreTypeSyntax Type,
    bool IsPublic,
    TextSpan Span);

/// <summary>A safe-core struct item.</summary>
public sealed record SafeCoreStructSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreFieldSyntax> Fields,
    bool IsTupleStruct,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Struct, Attributes, IsPublic, Span);

/// <summary>An enum variant and optional tuple payload fields.</summary>
public sealed record SafeCoreEnumVariantSyntax(
    string Name,
    IReadOnlyList<SafeCoreFieldSyntax> Fields,
    TextSpan Span);

/// <summary>A safe-core enum item.</summary>
public sealed record SafeCoreEnumSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    IReadOnlyList<SafeCoreEnumVariantSyntax> Variants,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Enum, Attributes, IsPublic, Span);

/// <summary>A type alias item.</summary>
public sealed record SafeCoreTypeAliasSyntax(
    string Name,
    IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
    SafeCoreTypeSyntax Type,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.TypeAlias, Attributes, IsPublic, Span);

/// <summary>A typed constant item.</summary>
public sealed record SafeCoreConstSyntax(
    string Name,
    SafeCoreTypeSyntax Type,
    SafeCoreExpressionSyntax Value,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Const, Attributes, IsPublic, Span);

/// <summary>Classifies safe-core statements.</summary>
public enum SafeCoreStatementKind
{
    Let,
    Return,
    Expression,
}

/// <summary>Base type for statements in a safe-core block.</summary>
public abstract record SafeCoreStatementSyntax(SafeCoreStatementKind Kind, TextSpan Span);

/// <summary>A local binding statement.</summary>
public sealed record SafeCoreLetStatementSyntax(
    SafeCorePatternSyntax Pattern,
    SafeCoreTypeSyntax? Type,
    SafeCoreExpressionSyntax? Initializer,
    TextSpan Span)
    : SafeCoreStatementSyntax(SafeCoreStatementKind.Let, Span);

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
    TextSpan Span);

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
}

/// <summary>Base type for expressions.</summary>
public abstract record SafeCoreExpressionSyntax(SafeCoreExpressionKind Kind, TextSpan Span);

/// <summary>The explicitly supported built-in println! macro expression.</summary>
public sealed record SafeCorePrintExpressionSyntax(
    IReadOnlyList<SafeCoreExpressionSyntax> Arguments,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Print, Span);

/// <summary>A name or path expression.</summary>
public sealed record SafeCoreNameExpressionSyntax(
    string Path,
    TextSpan Span)
    : SafeCoreExpressionSyntax(SafeCoreExpressionKind.Name, Span);

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
}

/// <summary>Base type for patterns.</summary>
public abstract record SafeCorePatternSyntax(SafeCorePatternKind Kind, TextSpan Span);

/// <summary>An identifier binding pattern.</summary>
public sealed record SafeCoreIdentifierPatternSyntax(
    string Name,
    bool IsMutable,
    TextSpan Span)
    : SafeCorePatternSyntax(SafeCorePatternKind.Identifier, Span);

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
    : SafeCorePatternSyntax(SafeCorePatternKind.Path, Span);

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
}

/// <summary>Base type for type syntax.</summary>
public abstract record SafeCoreTypeSyntax(SafeCoreTypeKind Kind, TextSpan Span);

/// <summary>A path type, optionally with generic arguments on each segment.</summary>
public sealed record SafeCorePathTypeSyntax(
    IReadOnlyList<SafeCorePathSegmentSyntax> Segments,
    TextSpan Span)
    : SafeCoreTypeSyntax(SafeCoreTypeKind.Path, Span);

/// <summary>One path segment and its generic arguments.</summary>
public sealed record SafeCorePathSegmentSyntax(
    string Name,
    IReadOnlyList<SafeCoreTypeSyntax> GenericArguments,
    TextSpan Span);

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
