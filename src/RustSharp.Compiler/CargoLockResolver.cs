using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>Portable graph locking plus exact feature/cfg resolution metadata for cargo-v1.</summary>
public static class CargoLockResolver
{
    public const string IncompatibleLockDiagnostic = "RSCARGO1010";
    public const string ResolutionFileName = "Cargo.lock.rustsharp.json";

    public static CargoLockResolutionResult ResolveV1(string manifestPath, CargoCfgOptions cfg,
        CargoFeatureOptions? features = null, CargoLockOptions? options = null,
        CargoWorkspaceOptions? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath); ArgumentNullException.ThrowIfNull(cfg);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options));
        CargoWorkspaceOptions normalized = CargoWorkspace.NormalizeV1Options(limits ?? new());
        var budget = new CargoLoadBudget(normalized, cancellationToken);
        string path = Path.GetFullPath(manifestPath);
        try
        {
            CargoCfgResolutionResult selection = CargoCfgResolver.ResolveV1Core(path, cfg, features, normalized, budget, cancellationToken);
            if (!selection.IsSuccessful) return new(null, selection.Diagnostics, budget.OperationsConsumed);
            CargoFeatureResolutionResult activation = selection.Features!;
            CargoWorkspaceResult workspace = activation.Workspace!;
            var byPath = workspace.Packages.ToDictionary(static p => p.ManifestPath,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var packages = new List<CargoLockPackage>();
            foreach (CargoPackage package in workspace.Packages)
            {
                budget.Step(package.ManifestPath, package.DeclarationSpan);
                var edges = new SortedSet<string>(StringComparer.Ordinal);
                foreach (CargoDependency dependency in package.Dependencies)
                {
                    budget.Step(package.ManifestPath, dependency.DeclarationSpan);
                    CargoPackage target = byPath[dependency.ResolvedManifestPath!];
                    edges.Add(target.Name + " " + target.Version);
                }
                packages.Add(new(package.Name, package.Version, Array.AsReadOnly(edges.ToArray())));
            }
            CargoLockPackage[] sorted = packages.OrderBy(static p => p.Identity, StringComparer.Ordinal).ToArray();
            IReadOnlyList<CargoPackage> order = DependencyFirst(workspace, byPath, normalized, budget);
            string lockText = CargoLockFormat.Serialize(sorted, path, budget);
            string activationText = ActivationMetadata(selection, path, budget);
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activationText)));
            string metadata = "{\"schemaVersion\":1,\"activationFingerprint\":\"" + fingerprint + "\",\"resolution\":" + activationText + "}\n";
            if (Encoding.UTF8.GetByteCount(lockText) > normalized.MaximumLockBytes || Encoding.UTF8.GetByteCount(metadata) > normalized.MaximumLockBytes)
                throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo lock output exceeded its byte limit.", path, default);
            var plan = new CargoLockPlan(selection, Array.AsReadOnly(sorted), order, lockText, metadata, fingerprint);
            string directory = Path.GetDirectoryName(path)!;
            string lockPath = Path.Combine(directory, "Cargo.lock"); string resolutionPath = Path.Combine(directory, ResolutionFileName);
            if (options.Mode == CargoLockMode.Locked)
            {
                byte[] existing = CargoLockFiles.Read(lockPath, normalized, budget);
                IReadOnlyList<CargoLockPackage> parsed = CargoLockFormat.Parse(CargoLockFiles.Decode(existing, lockPath), lockPath, normalized, budget);
                string actualCanonical = CargoLockFormat.Serialize(parsed, lockPath, budget);
                if (actualCanonical != lockText) throw new CargoLoadException(IncompatibleLockDiagnostic, "Cargo.lock does not match the exact discovered package graph.", lockPath, default);
                byte[] existingResolution = CargoLockFiles.Read(resolutionPath, normalized, budget);
                if (!existingResolution.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(metadata)))
                    throw new CargoLoadException(IncompatibleLockDiagnostic, "Cargo resolution metadata is missing, stale or incompatible with profile/RID/root/features/cfg/targets.", resolutionPath, default);
            }
            else if (options.Mode == CargoLockMode.Update)
                CargoLockFiles.ReplacePair(lockPath, lockText, resolutionPath, metadata, normalized, budget);
            if (options.Mode != CargoLockMode.Update) budget.Check(path, default);
            return new(plan, Array.Empty<Diagnostic>(), budget.OperationsConsumed, options.Mode == CargoLockMode.Update);
        }
        catch (CargoLoadException exception)
        { return new(null, Array.AsReadOnly(new[] { new Diagnostic(exception.Code, exception.Message, exception.Span) { SourcePath = exception.SourcePath } }), budget.OperationsConsumed); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return new(null, Array.AsReadOnly(new[] { new Diagnostic(IncompatibleLockDiagnostic, "Cannot use Cargo lock inputs: " + exception.Message, default) { SourcePath = path } }), budget.OperationsConsumed, cleanupComplete: exception is not CargoLockCleanupException); }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<CargoPackage> DependencyFirst(CargoWorkspaceResult workspace,
        Dictionary<string, CargoPackage> byPath, CargoWorkspaceOptions limits, CargoLoadBudget budget)
    {
        var pending = new Dictionary<CargoPackage, HashSet<CargoPackage>>();
        var consumers = workspace.Packages.ToDictionary(static p => p, static _ => new HashSet<CargoPackage>());
        foreach (CargoPackage package in workspace.Packages)
        {
            budget.Step(package.ManifestPath, package.DeclarationSpan);
            var targets = new HashSet<CargoPackage>();
            foreach (CargoDependency dependency in package.Dependencies)
            { budget.Step(package.ManifestPath, dependency.DeclarationSpan); targets.Add(byPath[dependency.ResolvedManifestPath!]); }
            pending.Add(package, targets);
            foreach (CargoPackage target in targets) consumers[target].Add(package);
        }
        var ready = new SortedDictionary<string, CargoPackage>(StringComparer.Ordinal);
        foreach ((CargoPackage package, HashSet<CargoPackage> targets) in pending)
            if (targets.Count == 0) ready.Add(package.Name + "@" + package.Version, package);
        var result = new List<CargoPackage>();
        for (int index = 0; ready.Count > 0 && index < limits.MaximumPackages; index++)
        {
            CargoPackage package = ready.First().Value; ready.Remove(package.Name + "@" + package.Version);
            budget.Step(package.ManifestPath, package.DeclarationSpan); result.Add(package);
            foreach (CargoPackage consumer in consumers[package])
            {
                budget.Step(consumer.ManifestPath, consumer.DeclarationSpan);
                pending[consumer].Remove(package);
                if (pending[consumer].Count == 0) ready.Add(consumer.Name + "@" + consumer.Version, consumer);
            }
        }
        if (result.Count != workspace.Packages.Count) throw new CargoLoadException(CargoWorkspace.DependencyCycleDiagnostic, "Cargo dependency order requires an acyclic graph.", workspace.RootManifestPath, default);
        return result.AsReadOnly();
    }

    private static string ActivationMetadata(CargoCfgResolutionResult selection, string path, CargoLoadBudget budget)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("profile", "cargo-v1"); writer.WriteString("runtimeIdentifier", selection.Platform!.RuntimeIdentifier);
            writer.WriteString("targetOs", selection.Platform.TargetOs); writer.WriteString("targetArch", selection.Platform.TargetArch);
            writer.WriteString("rootPackage", selection.RootPackage!.Name + "@" + selection.RootPackage.Version);
            writer.WriteStartArray("activations");
            foreach (CargoPackageFeatureActivation activation in selection.Features!.Activations.OrderBy(static a => a.Identity, StringComparer.Ordinal))
            {
                budget.Step(path, default); writer.WriteStartObject(); writer.WriteString("package", activation.Identity); writer.WriteStartArray("features");
                foreach (string feature in activation.Features.Order(StringComparer.Ordinal)) { budget.Step(path, default); writer.WriteStringValue(feature); }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("dependencies");
            foreach (CargoActivatedDependency edge in selection.Features.Dependencies.OrderBy(static e => e.SourceIdentity, StringComparer.Ordinal).ThenBy(static e => e.Alias, StringComparer.Ordinal).ThenBy(static e => e.TargetIdentity, StringComparer.Ordinal))
            { budget.Step(path, default); writer.WriteStartObject(); writer.WriteString("owner", edge.SourceIdentity); writer.WriteString("alias", edge.Alias); writer.WriteString("target", edge.TargetIdentity); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteStartArray("targets");
            foreach (CargoTarget target in selection.Targets.OrderBy(static t => t.Kind).ThenBy(static t => t.Name, StringComparer.Ordinal))
            { budget.Step(path, target.DeclarationSpan); writer.WriteStartObject(); writer.WriteString("kind", target.Kind == CargoTargetKind.Library ? "library" : "binary"); writer.WriteString("name", target.Name); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        budget.Check(path, default);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
