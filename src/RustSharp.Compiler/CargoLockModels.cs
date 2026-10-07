using RustSharp.Syntax;

namespace RustSharp.Compiler;

public enum CargoLockMode { Plan, Locked, Update }

public sealed record CargoLockOptions
{
    public CargoLockMode Mode { get; init; } = CargoLockMode.Plan;
}

public sealed record CargoLockPackage(string Name, string Version, IReadOnlyList<string> Dependencies)
{
    public string Identity => Name + "@" + Version;
}

/// <summary>Validated portable bytes and dependency-first package order; failed results expose no plan.</summary>
public sealed class CargoLockPlan
{
    internal CargoLockPlan(CargoCfgResolutionResult selection, IReadOnlyList<CargoLockPackage> packages,
        IReadOnlyList<CargoPackage> dependencyOrder, string lockText, string resolutionText, string fingerprint)
    { Selection = selection; Packages = packages; DependencyOrder = dependencyOrder; LockText = lockText; ResolutionText = resolutionText; ActivationFingerprint = fingerprint; }
    public CargoCfgResolutionResult Selection { get; }
    public IReadOnlyList<CargoLockPackage> Packages { get; }
    public IReadOnlyList<CargoPackage> DependencyOrder { get; }
    public string LockText { get; }
    public string ResolutionText { get; }
    public string ActivationFingerprint { get; }
}

public sealed class CargoLockResolutionResult
{
    internal CargoLockResolutionResult(CargoLockPlan? plan, IReadOnlyList<Diagnostic> diagnostics, int operationsConsumed,
        bool filesWritten = false, bool cleanupComplete = true)
    { Plan = plan; Diagnostics = diagnostics; OperationsConsumed = operationsConsumed; FilesWritten = filesWritten; CleanupComplete = cleanupComplete; }
    public CargoLockPlan? Plan { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public int OperationsConsumed { get; }
    public bool FilesWritten { get; }
    public bool CleanupComplete { get; }
    public bool IsSuccessful => Plan is not null && Diagnostics.Count == 0 && CleanupComplete;
}
