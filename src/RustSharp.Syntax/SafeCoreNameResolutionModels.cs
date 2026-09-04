namespace RustSharp.Syntax;

/// <summary>
/// Bounds for the experimental safe-core name-resolution prototype. These
/// limits are independent from the parser limits so callers can safely run
/// resolution on an already-built syntax tree.
/// </summary>
public sealed record SafeCoreNameResolutionOptions
{
    public int MaximumSymbols { get; init; } = 100_000;
    public int MaximumScopes { get; init; } = 50_000;
    public int MaximumPathSegments { get; init; } = 128;
    public int MaximumNameLength { get; init; } = 1_024;
    public int MaximumPathLength { get; init; } = 4_096;
    public int MaximumDiagnosticMessageLength { get; init; } = 512;
    public int MaximumDiagnostics { get; init; } = 128;
    public int MaximumNestingDepth { get; init; } = 128;
    public int MaximumOperations { get; init; } = 1_000_000;
}

/// <summary>Stable diagnostics emitted by the P1-03 prototype.</summary>
public static class SafeCoreNameResolutionDiagnosticCodes
{
    public const string InvalidSyntax = "RSN0001";
    public const string LimitReached = "RSN0002";
    public const string InvalidPath = "RSN1001";
    public const string DuplicateSymbol = "RSN1002";
    public const string UnresolvedName = "RSN1003";
    public const string AmbiguousName = "RSN1004";
    public const string PrivateName = "RSN1005";
    public const string ImportCycle = "RSN1006";
}

/// <summary>The namespace in which a safe-core symbol can be referenced.</summary>
public enum SafeCoreSymbolNamespace
{
    Type,
    Value,
    Both,
}

/// <summary>Declaration categories exposed by the prototype symbol table.</summary>
public enum SafeCoreSymbolKind
{
    Module,
    Import,
    Function,
    Struct,
    Enum,
    TypeAlias,
    Const,
    GenericParameter,
    Parameter,
    Local,
    Field,
    EnumVariant,
}

/// <summary>Outcome of resolving one path occurrence.</summary>
public enum SafeCoreNameResolutionStatus
{
    Resolved,
    Unresolved,
    Ambiguous,
    Private,
    Invalid,
    LimitExceeded,
}

/// <summary>A declaration collected from a safe-core syntax tree.</summary>
public sealed record SafeCoreSymbol(
    string Name,
    string QualifiedName,
    SafeCoreSymbolKind Kind,
    SafeCoreSymbolNamespace Namespace,
    bool IsPublic,
    bool IsImport,
    string? TargetPath,
    TextSpan Span,
    string ScopePath);

/// <summary>A lexical/module scope and its directly declared symbols.</summary>
public sealed record SafeCoreScope(
    string Path,
    string? ParentPath,
    string ModulePath,
    IReadOnlyList<SafeCoreSymbol> Symbols);

/// <summary>One recorded path resolution, including successful references.</summary>
public sealed record SafeCorePathResolution(
    string Path,
    string ScopePath,
    SafeCoreNameResolutionStatus Status,
    SafeCoreSymbol? Symbol,
    IReadOnlyList<SafeCoreSymbol> Candidates,
    TextSpan Span)
{
    public bool IsSuccess => Status == SafeCoreNameResolutionStatus.Resolved;
}

/// <summary>
/// Result of the bounded P1-03 name-resolution prototype. This API is
/// evidence/prototyping only and is not used by the production compiler path.
/// </summary>
public sealed class SafeCoreNameResolutionResult
{
    internal SafeCoreNameResolutionResult(
        string sourcePath,
        SafeCoreScope? rootScope,
        IReadOnlyList<SafeCoreScope> scopes,
        IReadOnlyList<SafeCoreSymbol> symbols,
        IReadOnlyList<SafeCorePathResolution> resolutions,
        IReadOnlyList<Diagnostic> diagnostics,
        bool isTruncated)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(resolutions);
        ArgumentNullException.ThrowIfNull(diagnostics);
        SourcePath = sourcePath;
        RootScope = rootScope;
        Scopes = Array.AsReadOnly(scopes.ToArray());
        Symbols = Array.AsReadOnly(symbols.ToArray());
        Resolutions = Array.AsReadOnly(resolutions.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        IsTruncated = isTruncated;
    }

    public string SourcePath { get; }
    public SafeCoreScope? RootScope { get; }
    public IReadOnlyList<SafeCoreScope> Scopes { get; }
    public IReadOnlyList<SafeCoreSymbol> Symbols { get; }
    public IReadOnlyList<SafeCorePathResolution> Resolutions { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsTruncated { get; }
    public bool IsSuccessful => !IsTruncated && RootScope is not null && Diagnostics.Count == 0;

    /// <summary>Finds an exact-scope resolution recorded during the pass.</summary>
    public SafeCorePathResolution? FindResolution(string path, string? scopePath = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (SafeCorePathResolution resolution in Resolutions)
        {
            if (string.Equals(resolution.Path, path, StringComparison.Ordinal) &&
                (scopePath is null || string.Equals(resolution.ScopePath, scopePath, StringComparison.Ordinal)))
            {
                return resolution;
            }
        }

        return null;
    }
}
