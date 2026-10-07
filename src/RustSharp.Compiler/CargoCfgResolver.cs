using RustSharp.Syntax;
using Catalog = RustSharp.Compiler.CargoFeatureResolver.Catalog;
using Reference = RustSharp.Compiler.CargoFeatureResolver.Reference;

namespace RustSharp.Compiler;

/// <summary>Owner-first package finalization selects cfg from each package's final unified features.</summary>
public static class CargoCfgResolver
{
    public const string InvalidCfgDiagnostic = "RSCARGO1009";
    public static CargoCfgResolutionResult ResolveV1(string manifestPath, CargoCfgOptions cfg,
        CargoFeatureOptions? features = null, CargoWorkspaceOptions? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath); ArgumentNullException.ThrowIfNull(cfg);
        cancellationToken.ThrowIfCancellationRequested();
        CargoWorkspaceOptions normalized = CargoWorkspace.NormalizeV1Options(limits ?? new());
        var budget = new CargoLoadBudget(normalized, cancellationToken);
        return ResolveV1Core(Path.GetFullPath(manifestPath), cfg, features, normalized, budget, cancellationToken);
    }
    internal static CargoCfgResolutionResult ResolveV1Core(string path, CargoCfgOptions cfg,
        CargoFeatureOptions? features, CargoWorkspaceOptions normalized, CargoLoadBudget budget, CancellationToken cancellationToken)
    {
        try
        {
            budget.Step(path, default);
            CargoCfgEnvironment platform;
            try { platform = CargoCfgEnvironment.Create(cfg.RuntimeIdentifier); }
            catch (ArgumentException exception) { throw new CargoLoadException(InvalidCfgDiagnostic, exception.Message, path, default); }
            if (cfg.TargetOs is not null && cfg.TargetOs != platform.TargetOs || cfg.TargetArch is not null && cfg.TargetArch != platform.TargetArch)
                throw new CargoLoadException(InvalidCfgDiagnostic, "Requested RID, target OS and architecture are incompatible.", path, default);
            CargoWorkspaceResult workspace = CargoWorkspace.LoadV1Core(path, normalized, budget, cancellationToken);
            if (!workspace.IsSuccessful) return Failure(workspace.Diagnostics, budget);
            return new Resolver(workspace, cfg, features ?? new(), normalized, budget, platform).Run();
        }
        catch (CargoLoadException exception)
        { return Failure(Array.AsReadOnly(new[] { new Diagnostic(exception.Code, exception.Message, exception.Span) { SourcePath = exception.SourcePath } }), budget); }
    }
    private static CargoCfgResolutionResult Failure(IReadOnlyList<Diagnostic> diagnostics, CargoLoadBudget budget) =>
        new(null, null, Array.Empty<CargoTarget>(), diagnostics, budget.OperationsConsumed);

    private sealed record Pending(string Feature, string Path, TextSpan Span);
    private sealed record Intent(string Alias, string? Feature, string Path, TextSpan Span);

    private sealed class Resolver(CargoWorkspaceResult workspace, CargoCfgOptions cfg, CargoFeatureOptions features,
        CargoWorkspaceOptions limits, CargoLoadBudget budget, CargoCfgEnvironment platform)
    {
        private readonly CargoFeatureResolver.Resolver _features = new(workspace, features, limits, budget, allowConditional: true);
        private readonly Dictionary<Catalog, List<Pending>> _incoming = [];
        private readonly Dictionary<CargoDependency, CargoCfgPredicate> _conditions = [];
        private readonly List<CargoActivatedDependency> _edges = [];
        internal CargoCfgResolutionResult Run()
        {
            Catalog root = _features.PrepareV1();
            IReadOnlyDictionary<string, Catalog> catalogs = _features.Catalogs;
            foreach (Catalog catalog in catalogs.Values)
            {
                _incoming.Add(catalog, []);
                foreach (CargoDependency dependency in catalog.Package.Dependencies)
                {
                    budget.Step(catalog.Package.ManifestPath, dependency.DeclarationSpan);
                    if (dependency.CfgCondition is not null)
                        _conditions.Add(dependency, new CargoCfgPredicateParser(dependency.CfgCondition, catalog.Package.ManifestPath,
                            dependency.CfgConditionSpan, limits, budget).Parse());
                }
            }
            root.Active = true;
            if (!features.NoDefaultFeatures && root.Features.ContainsKey("default")) AddIncoming(root, "default", root.Package.ManifestPath, root.Package.DeclarationSpan);
            if (features.Requests is null || features.Requests.Count > limits.MaximumFeatureEdges)
                Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature request count exceeded its bound.", workspace.RootManifestPath, default);
            var rootIntents = new List<Intent>();
            foreach (CargoFeatureRequest request in features.Requests!)
            {
                budget.Step(workspace.RootManifestPath, default);
                if (request is null || request.Value is null) Fail(CargoFeatureResolver.FeatureDiagnostic, "Feature requests require a name.", workspace.RootManifestPath, default);
                Reference reference = _features.ParseReference(root, request!.Value, request.SourcePath ?? workspace.RootManifestPath, request.Span);
                AcceptReference(root, reference, rootIntents);
            }
            foreach (Catalog catalog in OwnerFirst(catalogs))
            {
                budget.Step(catalog.Package.ManifestPath, catalog.Package.DeclarationSpan);
                if (!catalog.Active) continue;
                var intents = ReferenceEquals(catalog, root) ? rootIntents : new List<Intent>();
                List<Pending> pending = _incoming[catalog];
                for (int index = 0; index < pending.Count && index < limits.MaximumOperations; index++)
                {
                    Pending request = pending[index]; budget.Step(request.Path, request.Span);
                    if (!catalog.ActiveFeatures.Add(request.Feature)) continue;
                    foreach (Reference reference in catalog.Features[request.Feature])
                    { budget.Step(reference.Path, reference.Span); AcceptReference(catalog, reference, intents); }
                }
                CargoCfgEnvironment environment = CargoCfgEnvironment.Create(platform.RuntimeIdentifier, catalog.ActiveFeatures.Order(StringComparer.Ordinal));
                foreach (CargoDependency dependency in catalog.Package.Dependencies)
                {
                    budget.Step(catalog.Package.ManifestPath, dependency.DeclarationSpan);
                    if (_conditions.TryGetValue(dependency, out CargoCfgPredicate? predicate) && !predicate.Evaluate(environment, budget, catalog.Package.ManifestPath, dependency.CfgConditionSpan)) continue;
                    Intent[] requests = intents.Where(intent => intent.Alias == dependency.Name).ToArray();
                    if (dependency.Optional && requests.Length == 0) continue;
                    Catalog target = catalogs[dependency.ResolvedManifestPath!]; target.Active = true;
                    _edges.Add(new(catalog.Identity, dependency.Name, target.Identity));
                    if (_edges.Count > limits.MaximumPackages * limits.MaximumDependenciesPerPackage)
                        Fail(CargoWorkspace.LimitDiagnostic, "Activated cfg dependencies exceeded their item bound.", catalog.Package.ManifestPath, dependency.DeclarationSpan);
                    if (dependency.DefaultFeatures && target.Features.ContainsKey("default")) AddIncoming(target, "default", catalog.Package.ManifestPath, dependency.DeclarationSpan);
                    for (int index = 0; index < dependency.Features.Count && index < limits.MaximumFeatureEdges; index++)
                        AddIncoming(target, dependency.Features[index], catalog.Package.ManifestPath,
                            index < dependency.FeatureSpans.Count ? dependency.FeatureSpans[index] : dependency.DeclarationSpan);
                    foreach (Intent request in requests)
                        if (request.Feature is not null) AddIncoming(target, request.Feature, request.Path, request.Span);
                }
            }
            CargoPackageFeatureActivation[] activated = catalogs.Values.Where(static catalog => catalog.Active)
                .OrderBy(static catalog => catalog.Identity, StringComparer.Ordinal)
                .Select(static catalog => new CargoPackageFeatureActivation(catalog.Package, Array.AsReadOnly(catalog.ActiveFeatures.Order(StringComparer.Ordinal).ToArray()))).ToArray();
            CargoActivatedDependency[] edges = _edges.Distinct().OrderBy(static edge => edge.SourceIdentity, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Alias, StringComparer.Ordinal).ThenBy(static edge => edge.TargetIdentity, StringComparer.Ordinal).ToArray();
            var result = new CargoFeatureResolutionResult(workspace, root.Package, Array.AsReadOnly(activated), Array.AsReadOnly(edges), Array.Empty<Diagnostic>(), budget.OperationsConsumed);
            var selected = new List<CargoTarget>();
            foreach (CargoTarget target in root.Package.Targets)
            {
                budget.Step(root.Package.ManifestPath, target.DeclarationSpan);
                bool admitted = target.RequiredFeatures.All(root.ActiveFeatures.Contains);
                if (cfg.TargetName == target.Name && !admitted) Fail(InvalidCfgDiagnostic, "The explicitly selected binary requires disabled features.", root.Package.ManifestPath, target.DeclarationSpan);
                if (admitted && (cfg.TargetName is null || cfg.TargetName == target.Name)) selected.Add(target);
            }
            if (cfg.TargetName is not null && selected.Count == 0) Fail(InvalidCfgDiagnostic, "The selected Cargo target does not exist.", root.Package.ManifestPath, root.Package.DeclarationSpan);
            budget.Check(workspace.RootManifestPath, default);
            return new(result, platform, selected.AsReadOnly(), Array.Empty<Diagnostic>(), budget.OperationsConsumed);
        }

        private System.Collections.ObjectModel.ReadOnlyCollection<Catalog> OwnerFirst(IReadOnlyDictionary<string, Catalog> catalogs)
        {
            var indegree = catalogs.Values.ToDictionary(static catalog => catalog, static _ => 0);
            foreach (Catalog owner in catalogs.Values)
                foreach (CargoDependency dependency in owner.Package.Dependencies)
                { budget.Step(owner.Package.ManifestPath, dependency.DeclarationSpan); indegree[catalogs[dependency.ResolvedManifestPath!]]++; }
            var ready = new SortedDictionary<string, Catalog>(StringComparer.Ordinal);
            foreach ((Catalog catalog, int count) in indegree) if (count == 0) ready.Add(catalog.Identity, catalog);
            var sorted = new List<Catalog>();
            for (int index = 0; ready.Count > 0 && index < limits.MaximumPackages; index++)
            {
                Catalog owner = ready.First().Value; ready.Remove(owner.Identity); sorted.Add(owner);
                budget.Step(owner.Package.ManifestPath, owner.Package.DeclarationSpan);
                foreach (CargoDependency dependency in owner.Package.Dependencies)
                {
                    budget.Step(owner.Package.ManifestPath, dependency.DeclarationSpan);
                    Catalog target = catalogs[dependency.ResolvedManifestPath!];
                    if (--indegree[target] == 0) ready.Add(target.Identity, target);
                }
            }
            if (sorted.Count != catalogs.Count) Fail(CargoWorkspace.DependencyCycleDiagnostic, "Cargo cfg finalization requires an acyclic package graph.", workspace.RootManifestPath, default);
            return sorted.AsReadOnly();
        }
        private void AcceptReference(Catalog owner, Reference reference, List<Intent> intents)
        {
            if (reference.Dependency is null) AddIncoming(owner, reference.Feature!, reference.Path, reference.Span);
            else
            {
                budget.Step(reference.Path, reference.Span);
                if (intents.Count >= limits.MaximumOperations) Fail(CargoWorkspace.LimitDiagnostic, "Cargo cfg feature intents exceeded their item bound.", reference.Path, reference.Span);
                intents.Add(new(reference.Dependency.Name, reference.Feature, reference.Path, reference.Span));
            }
        }
        private void AddIncoming(Catalog owner, string feature, string path, TextSpan span)
        {
            budget.Step(path, span); List<Pending> requests = _incoming[owner];
            if (requests.Count >= limits.MaximumOperations) Fail(CargoWorkspace.LimitDiagnostic, "Cargo cfg feature work exceeded its item bound.", path, span);
            requests.Add(new(feature, path, span));
        }
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        private static void Fail(string code, string message, string path, TextSpan span) => throw new CargoLoadException(code, message, path, span);
    }
}
