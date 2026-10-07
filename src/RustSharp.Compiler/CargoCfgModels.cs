using System.Collections.Frozen;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

public sealed record CargoCfgOptions
{
    public required string RuntimeIdentifier { get; init; }
    public string? TargetOs { get; init; }
    public string? TargetArch { get; init; }
    public string? TargetName { get; init; }
}

public sealed record CargoCfgEnvironment
{
    private CargoCfgEnvironment(string rid, string os, IReadOnlySet<string> features)
    { RuntimeIdentifier = rid; TargetOs = os; Features = features; }
    public string RuntimeIdentifier { get; }
    public string TargetOs { get; }
    public string TargetArch { get; } = "x86_64";
    public IReadOnlySet<string> Features { get; }
    public static CargoCfgEnvironment Create(string runtimeIdentifier, IEnumerable<string>? featureNames = null)
    {
        string os = runtimeIdentifier switch
        {
            "win-x64" => "windows", "linux-x64" => "linux",
            _ => throw new ArgumentException("cargo-v1 admits only win-x64 and linux-x64.", nameof(runtimeIdentifier)),
        };
        // Input is explicitly bounded before materializing an arbitrary caller enumerable.
        string[] names = (featureNames ?? Array.Empty<string>()).Take(129).ToArray();
        if (names.Length > 128) throw new ArgumentException("Cargo cfg feature environments admit at most 128 feature names.", nameof(featureNames));
        return new(runtimeIdentifier, os, names.ToFrozenSet(StringComparer.Ordinal));
    }
}

public sealed class CargoCfgResolutionResult
{
    internal CargoCfgResolutionResult(CargoFeatureResolutionResult? features, CargoCfgEnvironment? platform,
        IReadOnlyList<CargoTarget> targets, IReadOnlyList<Diagnostic> diagnostics, int operationsConsumed)
    { Features = features; Platform = platform; Targets = targets; Diagnostics = diagnostics; OperationsConsumed = operationsConsumed; }
    public CargoFeatureResolutionResult? Features { get; }
    public CargoCfgEnvironment? Platform { get; }
    public CargoPackage? RootPackage => Features?.RootPackage;
    public IReadOnlyList<CargoTarget> Targets { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public int OperationsConsumed { get; }
    public bool IsSuccessful => Features?.IsSuccessful == true && Platform is not null && Diagnostics.Count == 0;
    public CargoCfgEnvironment EnvironmentFor(string packageIdentity)
    {
        if (!IsSuccessful) throw new InvalidOperationException("Failed Cargo cfg resolution has no usable environment.");
        CargoPackageFeatureActivation activation = Features!.Activations.Single(a => a.Identity == packageIdentity);
        return CargoCfgEnvironment.Create(Platform!.RuntimeIdentifier, activation.Features);
    }
}

public sealed class CargoCfgSourceResult
{
    internal CargoCfgSourceResult(string sourceText, IReadOnlyList<Diagnostic> diagnostics, int operationsConsumed)
    { SourceText = sourceText; Diagnostics = diagnostics; OperationsConsumed = operationsConsumed; }
    public string SourceText { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public int OperationsConsumed { get; }
    public bool IsSuccessful => Diagnostics.Count == 0;
}
