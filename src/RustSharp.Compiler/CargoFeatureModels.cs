using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>A feature request with the original option or source location.</summary>
public sealed record CargoFeatureRequest(string Value)
{
    public string? SourcePath { get; init; }
    public TextSpan Span { get; init; }
}

public sealed record CargoFeatureOptions
{
    public bool NoDefaultFeatures { get; init; }
    public string? RootPackageIdentity { get; init; }
    public IReadOnlyList<CargoFeatureRequest> Requests { get; init; } = Array.Empty<CargoFeatureRequest>();
}

public sealed record CargoPackageFeatureActivation(CargoPackage Package, IReadOnlyList<string> Features)
{
    public string Identity => Package.Name + "@" + Package.Version;
}

public sealed record CargoActivatedDependency(string SourceIdentity, string Alias, string TargetIdentity);

/// <summary>Only successful resolution exposes a workspace or activation graph.</summary>
public sealed class CargoFeatureResolutionResult
{
    internal CargoFeatureResolutionResult(CargoWorkspaceResult? workspace, CargoPackage? rootPackage,
        IReadOnlyList<CargoPackageFeatureActivation> activations,
        IReadOnlyList<CargoActivatedDependency> dependencies, IReadOnlyList<Diagnostic> diagnostics,
        int operationsConsumed)
    {
        Workspace = workspace;
        RootPackage = rootPackage;
        Activations = activations;
        Dependencies = dependencies;
        Diagnostics = diagnostics;
        OperationsConsumed = operationsConsumed;
    }

    public CargoWorkspaceResult? Workspace { get; }
    public CargoPackage? RootPackage { get; }
    public IReadOnlyList<CargoPackageFeatureActivation> Activations { get; }
    public IReadOnlyList<CargoActivatedDependency> Dependencies { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public int OperationsConsumed { get; }
    public bool IsSuccessful => Workspace is not null && RootPackage is not null && Diagnostics.Count == 0;
}
