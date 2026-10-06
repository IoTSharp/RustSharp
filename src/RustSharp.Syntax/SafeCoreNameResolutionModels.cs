using System.Collections.Immutable;

namespace RustSharp.Syntax;

/// <summary>
/// A bounded exported function supplied by an independently compiled Rust#
/// assembly. The signature uses the CLR-LIR spelling (for example
/// <c>I32,I32-&gt;I32</c>) so the syntax layer stays independent of the backend
/// type model; profile-specific semantic passes validate the supported subset.
/// </summary>
public sealed record SafeCoreExternalFunction(
    string SourceQualifiedName,
    string ClrName,
    string Signature,
    string AssemblyName,
    string AssemblyPath,
    bool IsPublic = true)
{
    /// <summary>CLR namespace containing generated Rust# program types.</summary>
    public string ClrNamespace { get; init; } = "RustSharp.Generated";

    /// <summary>CLR type containing the exported static method.</summary>
    public string ClrTypeName { get; init; } = "Program";

    /// <summary>Short source name used under the dependency module.</summary>
    public string SourceName
    {
        get
        {
            int separator = SourceQualifiedName.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? SourceQualifiedName : SourceQualifiedName[(separator + 2)..];
        }
    }

    /// <summary>
    /// Optional producer-declared ownership and panic terms. The compiler must
    /// validate these terms before emitting an imported MemberRef; they are
    /// never inferred from the CLR signature. These fields stay in the syntax
    /// assembly to avoid a dependency cycle on the CLR emitter.
    /// </summary>
    public string? CallPanicStrategy { get; init; }
    public ImmutableArray<string> CallParameterContracts { get; init; } = [];
    public string? CallReturnContract { get; init; }
    public string? SourceSchema { get; init; }
    public ImmutableArray<string> SourceParameterTypes { get; init; } = [];
    public ImmutableArray<bool> SourceParameterStaticLifetimes { get; init; } = [];
    public string? SourceReturnType { get; init; }
    public ImmutableArray<string> ReturnOrigins { get; init; } = [];
    public ImmutableArray<SafeCoreExternalReturnedVariant> SourceReturnVariants { get; init; } = [];
    public string? NominalScope { get; init; }
    public ImmutableArray<SafeCoreExternalValueType> SourceValueTypes { get; init; } = [];
    public ImmutableArray<SafeCoreExternalStructuralType> StructuralTypes { get; init; } = [];
}

/// <summary>An anonymous source aggregate paired with its actual producer CLR layout.</summary>
public sealed record SafeCoreExternalStructuralType(string SourceType, string AssemblyName,
    string ClrName, string NominalScope)
{
    /// <summary>Verified original producer identity; SourceName is its canonical anonymous shape.</summary>
    public SafeCoreExternalOwner? Owner { get; init; }
}

/// <summary>A producer-owned nominal layout, distinct from the consumer's own types.</summary>
public sealed record SafeCoreExternalValueType(string Name, string ClrName,
    ImmutableArray<SafeCoreExternalField> Fields, bool IsCopy, string AssemblyName)
{
    public SafeCoreExternalTypeKind Kind { get; init; } = SafeCoreExternalTypeKind.NamedStruct;
    public ImmutableArray<SafeCoreExternalVariant> Variants { get; init; } = [];
    /// <summary>Verified original owner scope for a re-exported nominal layout.</summary>
    public string? NominalScope { get; init; }
    /// <summary>Original owner's source name; Name remains the exporting package's source spelling.</summary>
    public string? NominalSourceName { get; init; }
    public SafeCoreExternalOwner? Owner { get; init; }
    public string? DropClrName { get; init; }
    public SafeCoreExternalFunction? DropFunction { get; init; }
}

/// <summary>Verified producer identity for a re-exported source nominal.</summary>
public sealed record SafeCoreExternalOwner(string AssemblyName, string SourceName, string ClrName,
    Guid ModuleVersionId, string SourceSha256, string AssemblySha256);

public sealed record SafeCoreExternalField(string Name, string Type, bool IsPublic)
{
    public bool RequiresStaticLifetime { get; init; }
}

public enum SafeCoreExternalTypeKind { NamedStruct, TupleStruct, UnitStruct, Enum }

/// <summary>A checked producer enum variant and its payload's physical field offset.</summary>
public sealed record SafeCoreExternalVariant(string Name, int Discriminant, int FieldOffset,
    ImmutableArray<SafeCoreExternalField> Fields)
{
    public SafeCoreExternalTypeKind Kind { get; init; } = SafeCoreExternalTypeKind.UnitStruct;
    public string SourceName
    {
        get
        {
            int separator = Name.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? Name : Name[(separator + 2)..];
        }
    }
}

public sealed record SafeCoreExternalEnumVariant(SafeCoreExternalType EnumType, SafeCoreExternalVariant Variant);

public sealed record SafeCoreExternalReturnedVariant(ImmutableArray<string> ValuePath, string VariantName);

/// <summary>An exported source type whose nominal identity and layout belong to its producer.</summary>
public sealed record SafeCoreExternalType(string SourceQualifiedName, SafeCoreExternalValueType Layout,
    string AssemblyPath, string NominalScope, bool IsPublic = true)
{
    public SafeCoreExternalTypeKind Kind { get; init; } = SafeCoreExternalTypeKind.NamedStruct;
    public string SourceName
    {
        get
        {
            int separator = SourceQualifiedName.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? SourceQualifiedName : SourceQualifiedName[(separator + 2)..];
        }
    }
}

/// <summary>A source-linked Cargo crate and its direct extern-prelude dependencies.</summary>
public sealed record SafeCoreCrate(string ScopePath, string Identity, ImmutableDictionary<string, string> Dependencies)
{
    /// <summary>Exports supplied by an independently compiled dependency.</summary>
    public ImmutableArray<SafeCoreExternalFunction> Exports { get; init; } = [];
    public ImmutableArray<SafeCoreExternalType> TypeExports { get; init; } = [];
    public ImmutableArray<SafeCoreExternalStructuralType> StructuralTypes { get; init; } = [];
}

/// <summary>
/// Bounds for the experimental safe-core name-resolution prototype. These
/// limits are independent from the parser limits so callers can safely run
/// resolution on an already-built syntax tree.
/// </summary>
public sealed record SafeCoreNameResolutionOptions
{
    /// <summary>Enables the bounded P1-04 type-checking syntax without changing the legacy profile.</summary>
    public bool EnableTypeSystemExtensions { get; init; }
    /// <summary>Enables type generics, marker traits and positive bounds without changing legacy profiles.</summary>
    public bool EnableGenericExtensions { get; init; }
    /// <summary>Enables the versioned MIR profile's built-in Drop implementation syntax.</summary>
    public bool EnableDropImplementations { get; init; }
    /// <summary>Enables lexically scoped loop labels without changing legacy type profiles.</summary>
    public bool EnableLoopLabels { get; init; }
    public ImmutableArray<SafeCoreCrate> Crates { get; init; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public CancellationToken CancellationToken { get; init; }
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
    /// <summary>Parsed syntax exceeds the current name-resolution and HIR profile.</summary>
    public const string UnsupportedSyntax = "RSN1007";
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
    Trait,
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
    string ScopePath)
{
    /// <summary>The canonical declaration behind a resolved import, after following aliases.</summary>
    public string? ResolvedImportTargetQualifiedName { get; init; }

    /// <summary>Module subtree allowed to access this declaration; null means unrestricted public visibility.</summary>
    public string? VisibilityScopePath { get; init; } = IsPublic ? null : ScopePath;

    /// <summary>An underscore import validates its target without introducing a lookup name.</summary>
    public bool IsAnonymousImport { get; init; }

    /// <summary>Non-null when this symbol is backed by an independently compiled crate.</summary>
    public SafeCoreExternalFunction? ExternalFunction { get; init; }
    public SafeCoreExternalType? ExternalType { get; init; }
    public SafeCoreExternalEnumVariant? ExternalEnumVariant { get; init; }
}

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
/// Result of bounded P1-03 name resolution, consumed by HIR lowering and
/// the opt-in primitive compilation profile.
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
    public ImmutableArray<SafeCoreCrate> Crates { get; internal init; } = [];
    public SafeCoreScope? RootScope { get; }
    public IReadOnlyList<SafeCoreScope> Scopes { get; }
    public IReadOnlyList<SafeCoreSymbol> Symbols { get; }
    public IReadOnlyList<SafeCorePathResolution> Resolutions { get; }
    /// <summary>Canonical binding identity for each pattern occurrence, including or-pattern alternatives.</summary>
    public IReadOnlyDictionary<TextSpan, SafeCoreSymbol> PatternBindings { get; internal init; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<TextSpan, SafeCoreSymbol>(new Dictionary<TextSpan, SafeCoreSymbol>());
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
