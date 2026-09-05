using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Bounds for one experimental safe-core HIR lowering operation.</summary>
public sealed record SafeCoreHirLoweringOptions
{
    public int MaximumNodes { get; init; } = 100_000;
    public int MaximumNestingDepth { get; init; } = 128;
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumDiagnostics { get; init; } = 128;
    public int MaximumDiagnosticMessageLength { get; init; } = 512;
    public SafeCoreNameResolutionOptions NameResolution { get; init; } = new();
}

/// <summary>Stable diagnostics emitted by the experimental HIR lowering pass.</summary>
public static class SafeCoreHirDiagnosticCodes
{
    public const string InvalidInput = "RSH0001";
    public const string LimitReached = "RSH0002";
    public const string MissingDeclaration = "RSH1001";
    public const string MissingReference = "RSH1002";
    public const string UnsupportedNode = "RSH1003";
}

/// <summary>Semantic roles represented by nodes in the flat safe-core HIR arena.</summary>
public enum SafeCoreHirNodeKind
{
    CompilationUnit,
    Attribute,
    Module,
    Import,
    Function,
    Struct,
    Enum,
    EnumVariant,
    TypeAlias,
    Const,
    GenericParameter,
    Parameter,
    Field,
    Block,
    LetStatement,
    ReturnStatement,
    ExpressionStatement,
    IdentifierPattern,
    WildcardPattern,
    LiteralPattern,
    TuplePattern,
    PathPattern,
    NameExpression,
    LiteralExpression,
    UnaryExpression,
    BinaryExpression,
    CallExpression,
    TupleExpression,
    ArrayExpression,
    BlockExpression,
    IfExpression,
    IndexExpression,
    PathType,
    PathSegment,
    ReferenceType,
    TupleType,
    ArrayType,
    SliceType,
    UnitType,
    NeverType,
    PrintExpression,
}

/// <summary>Compact properties whose meaning is determined by a HIR node kind.</summary>
[Flags]
public enum SafeCoreHirNodeModifiers
{
    None = 0,
    Public = 1 << 0,
    Mutable = 1 << 1,
    InnerAttribute = 1 << 2,
    HasSemicolon = 1 << 3,
    HasTrailingComma = 1 << 4,
    TupleStruct = 1 << 5,
    MutableReference = 1 << 6,
    RepeatedArray = 1 << 7,
}

/// <summary>
/// One node in a deterministic flat HIR arena. Child IDs index the owning
/// result's <see cref="SafeCoreHirResult.Nodes"/> collection.
/// </summary>
public sealed class SafeCoreHirNode
{
    internal SafeCoreHirNode(
        int id,
        SafeCoreHirNodeKind kind,
        TextSpan span,
        string? name,
        string? value,
        SafeCoreHirNodeModifiers flags,
        SafeCoreSymbol? declaredSymbol,
        SafeCoreSymbol? referencedSymbol,
        IReadOnlyList<int> childIds)
    {
        Id = id;
        Kind = kind;
        Span = span;
        Name = name;
        Value = value;
        Modifiers = flags;
        DeclaredSymbol = declaredSymbol;
        ReferencedSymbol = referencedSymbol;
        ChildIds = Array.AsReadOnly(childIds.ToArray());
    }

    public int Id { get; }
    public SafeCoreHirNodeKind Kind { get; }
    public TextSpan Span { get; }
    public string? Name { get; }
    public string? Value { get; }
    public SafeCoreHirNodeModifiers Modifiers { get; }
    public SafeCoreSymbol? DeclaredSymbol { get; }
    public SafeCoreSymbol? ReferencedSymbol { get; }
    public IReadOnlyList<int> ChildIds { get; }
}

/// <summary>Result of lowering one successfully parsed safe-core document.</summary>
public sealed class SafeCoreHirResult
{
    internal SafeCoreHirResult(
        string sourcePath,
        SafeCoreHirNode? root,
        IReadOnlyList<SafeCoreHirNode> nodes,
        SafeCoreNameResolutionResult? nameResolution,
        IReadOnlyList<Diagnostic> diagnostics,
        bool isTruncated)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(diagnostics);
        SourcePath = sourcePath;
        Root = root;
        Nodes = Array.AsReadOnly(nodes.ToArray());
        NameResolution = nameResolution;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        IsTruncated = isTruncated;
    }

    public string SourcePath { get; }
    public SafeCoreHirNode? Root { get; }
    public IReadOnlyList<SafeCoreHirNode> Nodes { get; }
    public SafeCoreNameResolutionResult? NameResolution { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsTruncated { get; }
    public bool IsSuccessful => !IsTruncated && Root is not null && Diagnostics.Count == 0;

    public SafeCoreHirNode GetNode(int id)
    {
        if ((uint)id >= (uint)Nodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        return Nodes[id];
    }
}
