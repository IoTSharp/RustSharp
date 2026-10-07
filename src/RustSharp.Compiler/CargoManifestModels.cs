using System.Collections.ObjectModel;
using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

public sealed record CargoWorkspaceOptions
{
    public int MaximumPackages { get; init; } = 64;
    public int MaximumManifestBytes { get; init; } = 1_000_000;
    public int MaximumDependenciesPerPackage { get; init; } = 64;
    public int MaximumTargets { get; init; } = 32;
    public int MaximumFeaturesPerPackage { get; init; } = 128;
    public int MaximumFeatureEdges { get; init; } = 1024;
    public int MaximumCfgDepth { get; init; } = 16;
    public int MaximumGraphDepth { get; init; } = 32;
    public int MaximumOperations { get; init; } = 20_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

public enum CargoTargetKind { Library, Binary }

public sealed record CargoTarget(CargoTargetKind Kind, string Name, string SourcePath,
    IReadOnlyList<string> RequiredFeatures, TextSpan DeclarationSpan)
{
    public TextSpan PathSpan { get; init; }
}

public sealed record CargoWorkspaceManifest(IReadOnlyList<string> Members, IReadOnlyList<string> Excludes,
    string Resolver, TextSpan DeclarationSpan);

public sealed record CargoDependency(string Name, string? Path)
{
    public string? PackageName { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TextSpan> FeatureSpans { get; init; } = Array.Empty<TextSpan>();
    public bool Optional { get; init; }
    public bool DefaultFeatures { get; init; } = true;
    public string? CfgCondition { get; init; }
    public TextSpan CfgConditionSpan { get; init; }
    public TextSpan DeclarationSpan { get; init; }
    public TextSpan PathSpan { get; init; }
    public string? ResolvedManifestPath { get; init; }
}

public sealed record CargoPackage(string ManifestPath, string Name, string Version, string Edition,
    string SourcePath, IReadOnlyList<CargoDependency> Dependencies)
{
    public string? LibrarySourcePath { get; init; }
    public IReadOnlyList<CargoTarget> Targets { get; init; } = Array.Empty<CargoTarget>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Features { get; init; } =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
    public IReadOnlyDictionary<string, TextSpan> FeatureSpans { get; init; } =
        new ReadOnlyDictionary<string, TextSpan>(new Dictionary<string, TextSpan>(StringComparer.Ordinal));
    public IReadOnlyDictionary<string, IReadOnlyList<TextSpan>> FeatureMemberSpans { get; init; } =
        new ReadOnlyDictionary<string, IReadOnlyList<TextSpan>>(new Dictionary<string, IReadOnlyList<TextSpan>>(StringComparer.Ordinal));
    public TextSpan DeclarationSpan { get; init; }
    public CargoWorkspaceManifest? Workspace { get; init; }
}

public sealed class CargoWorkspaceResult
{
    internal CargoWorkspaceResult(string rootManifestPath, CargoPackage? rootPackage, bool isVirtualWorkspace,
        IReadOnlyList<CargoPackage> packages, IReadOnlyList<string> workspaceMemberPaths,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        RootManifestPath = rootManifestPath;
        RootPackageOrNull = rootPackage;
        IsVirtualWorkspace = isVirtualWorkspace;
        Packages = packages;
        WorkspaceMemberPaths = workspaceMemberPaths;
        Diagnostics = diagnostics;
    }

    public string RootManifestPath { get; }
    public CargoPackage? RootPackageOrNull { get; }
    public bool IsVirtualWorkspace { get; }
    public IReadOnlyList<CargoPackage> Packages { get; }
    public IReadOnlyList<string> WorkspaceMemberPaths { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsSuccessful => Diagnostics.Count == 0 && (RootPackageOrNull is not null || IsVirtualWorkspace);
    public CargoPackage RootPackage => RootPackageOrNull ??
        throw new InvalidOperationException("A virtual Cargo workspace has no root package; select a package before compilation.");
}

internal sealed class CargoLoadException(string code, string message, string sourcePath, TextSpan span) : Exception(message)
{
    internal string Code { get; } = code;
    internal string SourcePath { get; } = sourcePath;
    internal TextSpan Span { get; } = span;
}

internal sealed class CargoLoadBudget(CargoWorkspaceOptions options, CancellationToken cancellationToken)
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private int _operations;
    internal int OperationsConsumed => _operations;

    internal void Step(string path, TextSpan span)
    {
        Check(path, span);
        if (++_operations > options.MaximumOperations)
            throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo loading exceeded its operation limit.", path, span);
    }

    internal void Check(string path, TextSpan span)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(_startedAt) >= options.Timeout)
            throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo loading exceeded its time budget.", path, span);
    }
}

internal sealed record CargoTomlValue(object Value, TextSpan Span);
internal sealed record CargoTomlEntry(string Key, TextSpan KeySpan, CargoTomlValue Value);
internal sealed class CargoTomlTable(IReadOnlyList<string> path, TextSpan span, bool array)
{
    internal IReadOnlyList<string> Path { get; } = path;
    internal IReadOnlyList<TextSpan> PathSpans { get; init; } = Array.Empty<TextSpan>();
    internal TextSpan Span { get; } = span;
    internal bool IsArray { get; } = array;
    internal Dictionary<string, CargoTomlEntry> Entries { get; } = new(StringComparer.Ordinal);
}
internal sealed record CargoParsedManifest(string Path, IReadOnlyList<CargoTomlTable> Tables);
