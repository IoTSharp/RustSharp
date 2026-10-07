namespace RustSharp.Compiler;

/// <summary>Versioned bounded Cargo loading. Load preserves the existing source-linking dialect; LoadV1 selects cargo-v1.</summary>
public static class CargoWorkspace
{
    public const string ManifestDiagnostic = "RSCARGO1001";
    public const string UnsupportedDependencyDiagnostic = "RSCARGO1002";
    public const string DependencyCycleDiagnostic = "RSCARGO1003";
    public const string LimitDiagnostic = "RSCARGO1004";
    public const string UnsupportedManifestDiagnostic = "RSCARGO1005";
    public const string DuplicateIdentityDiagnostic = "RSCARGO1006";
    public const string MissingInputDiagnostic = "RSCARGO1007";

    public static CargoWorkspaceResult Load(string manifestPath, CargoWorkspaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CargoWorkspaceOptions();
        ValidateTimeout(options);
        options = options with
        {
            MaximumPackages = Math.Clamp(options.MaximumPackages, 1, 256),
            MaximumManifestBytes = Math.Clamp(options.MaximumManifestBytes, 1, 4_000_000),
            MaximumOperations = Math.Clamp(options.MaximumOperations, 1, 100_000),
        };
        return new CargoLegacyWorkspaceLoader(Path.GetFullPath(manifestPath), options, cancellationToken).Run();
    }

    /// <summary>Loads the closed cargo-v1 dialect; it never silently falls back to the legacy dialect.</summary>
    public static CargoWorkspaceResult LoadV1(string manifestPath, CargoWorkspaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CargoWorkspaceOptions();
        ValidateTimeout(options);
        options = options with
        {
            MaximumPackages = Math.Clamp(options.MaximumPackages, 1, 64),
            MaximumManifestBytes = Math.Clamp(options.MaximumManifestBytes, 1, 1_000_000),
            MaximumDependenciesPerPackage = Math.Clamp(options.MaximumDependenciesPerPackage, 1, 64),
            MaximumTargets = Math.Clamp(options.MaximumTargets, 1, 32),
            MaximumFeaturesPerPackage = Math.Clamp(options.MaximumFeaturesPerPackage, 1, 128),
            MaximumFeatureEdges = Math.Clamp(options.MaximumFeatureEdges, 1, 1024),
            MaximumCfgDepth = Math.Clamp(options.MaximumCfgDepth, 1, 16),
            MaximumGraphDepth = Math.Clamp(options.MaximumGraphDepth, 1, 32),
            MaximumOperations = Math.Clamp(options.MaximumOperations, 1, 20_000),
        };
        return new CargoWorkspaceLoader(Path.GetFullPath(manifestPath), options, cancellationToken).Run();
    }

    private static void ValidateTimeout(CargoWorkspaceOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(options), "Cargo loading timeout must be positive and at most ten seconds.");
    }
}
