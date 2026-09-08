namespace RustSharp.Syntax;

/// <summary>Bounds applied to one safe-core syntax parse.</summary>
public sealed record SafeCoreSyntaxOptions
{
    /// <summary>Wall-clock budget including lexing; must be positive and at most one minute.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

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
    TextSpan Span)
{
    /// <summary>Whether this attribute was desugared from a documentation comment.</summary>
    public bool IsDocumentation { get; init; }

    /// <summary>The documentation content, with its comment delimiters removed.</summary>
    public string? DocumentationText { get; init; }
}

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
    Trait,
    Implementation,
}

/// <summary>The written visibility restriction, before name resolution.</summary>
public enum SafeCoreVisibilityKind
{
    Private,
    Public,
    Crate,
    Self,
    Super,
    Restricted,
}

/// <summary>A visibility modifier with its exact span and optional restriction path.</summary>
public sealed record SafeCoreVisibilitySyntax(SafeCoreVisibilityKind Kind, string? Path, TextSpan Span);

/// <summary>Base type for a module-level item.</summary>
public abstract record SafeCoreItemSyntax(
    SafeCoreItemKind Kind,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    bool IsPublic,
    TextSpan Span)
{
    /// <summary>Retains restrictions separately from unrestricted public visibility.</summary>
    public SafeCoreVisibilitySyntax Visibility { get; init; } = new(
        IsPublic ? SafeCoreVisibilityKind.Public : SafeCoreVisibilityKind.Private, null, new TextSpan(Span.Start, 0));
}

/// <summary>A nested <c>mod name { ... }</c> item.</summary>
public sealed record SafeCoreModuleSyntax(
    string Name,
    IReadOnlyList<SafeCoreItemSyntax> Items,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Module, Attributes, IsPublic, Span)
{
    /// <summary>True for a semicolon declaration whose body must be loaded separately.</summary>
    public bool IsExternal { get; init; }

    /// <summary>Attributes in the inline module's preamble.</summary>
    public IReadOnlyList<SafeCoreAttributeSyntax> InnerAttributes { get; init; } = Array.Empty<SafeCoreAttributeSyntax>();
}

/// <summary>A path import item.</summary>
public sealed record SafeCoreUseSyntax(
    string Path,
    string? Alias,
    bool IsPublic,
    IReadOnlyList<SafeCoreAttributeSyntax> Attributes,
    TextSpan Span)
    : SafeCoreItemSyntax(SafeCoreItemKind.Use, Attributes, IsPublic, Span)
{
    /// <summary>The complete import tree; Path and Alias retain the simple-import API.</summary>
    public SafeCoreUseTreeSyntax? Tree { get; init; }
}

/// <summary>Distinguishes a single import, a glob and a nested import group.</summary>
public enum SafeCoreUseTreeKind
{
    Path,
    Glob,
    Group,
}

/// <summary>A recursive use tree, preserving aliases, absolute roots and empty groups.</summary>
public sealed record SafeCoreUseTreeSyntax(
    SafeCoreUseTreeKind Kind,
    bool IsAbsolute,
    IReadOnlyList<string> Prefix,
    string? Alias,
    IReadOnlyList<SafeCoreUseTreeSyntax> Children,
    TextSpan Span);
