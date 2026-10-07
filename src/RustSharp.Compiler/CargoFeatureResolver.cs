using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>Bounded feature closure for the strict, local-only cargo-v1 manifest dialect.</summary>
public static class CargoFeatureResolver
{
    public const string FeatureDiagnostic = "RSCARGO1008";

    public static CargoFeatureResolutionResult ResolveV1(string manifestPath, CargoFeatureOptions? options = null,
        CargoWorkspaceOptions? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();
        CargoWorkspaceOptions normalized = CargoWorkspace.NormalizeV1Options(limits ?? new());
        string fullPath = Path.GetFullPath(manifestPath);
        var budget = new CargoLoadBudget(normalized, cancellationToken);
        CargoWorkspaceResult workspace = CargoWorkspace.LoadV1Core(fullPath, normalized, budget, cancellationToken);
        if (!workspace.IsSuccessful) return Failure(workspace.Diagnostics, budget);
        try { return new Resolver(workspace, options ?? new(), normalized, budget).Run(); }
        catch (CargoLoadException exception)
        {
            return Failure(Array.AsReadOnly(new[]
            {
                new Diagnostic(exception.Code, exception.Message, exception.Span) { SourcePath = exception.SourcePath },
            }), budget);
        }
    }

    private static CargoFeatureResolutionResult Failure(IReadOnlyList<Diagnostic> diagnostics, CargoLoadBudget budget) =>
        new(null, null, Array.Empty<CargoPackageFeatureActivation>(), Array.Empty<CargoActivatedDependency>(), diagnostics, budget.OperationsConsumed);

    internal sealed class Catalog(CargoPackage package)
    {
        internal CargoPackage Package { get; } = package;
        internal string Identity => Package.Name + "@" + Package.Version;
        internal Dictionary<string, List<Reference>> Features { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, CargoDependency> Aliases { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<CargoDependency>> AliasGroups { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> ActiveFeatures { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> ActiveDependencies { get; } = new(StringComparer.Ordinal);
        internal bool Active { get; set; }
    }

    internal sealed record Reference(Catalog Target, string? Feature, CargoDependency? Dependency, string Path, TextSpan Span);
    private sealed record Work(Catalog Owner, string? Feature, CargoDependency? Dependency, string Path, TextSpan Span);
    private sealed record Frame(Catalog Owner, string Feature, int Next);

    internal sealed class Resolver(CargoWorkspaceResult workspace, CargoFeatureOptions options, CargoWorkspaceOptions limits, CargoLoadBudget budget, bool allowConditional = false)
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly Dictionary<string, Catalog> _catalogs = new(PathComparer);
        internal IReadOnlyDictionary<string, Catalog> Catalogs => _catalogs;
        internal Catalog PrepareV1()
        {
            CreateCatalogs(); PopulateReferences(); ValidateCycles(); return SelectRoot();
        }
        private readonly Queue<Work> _queue = new();
        private int _rawEdges;

        internal CargoFeatureResolutionResult Run()
        {
            Catalog root = PrepareV1();
            Enqueue(new(root, null, null, root.Package.ManifestPath, root.Package.DeclarationSpan));
            if (!options.NoDefaultFeatures && root.Features.ContainsKey("default"))
                Enqueue(new(root, "default", null, root.Package.ManifestPath, SpanFor(root, "default")));
            if (options.Requests is null || options.Requests.Count > limits.MaximumFeatureEdges)
                Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature requests exceed their item limit.", workspace.RootManifestPath, default);
            foreach (CargoFeatureRequest request in options.Requests!)
            {
                string path = request?.SourcePath ?? workspace.RootManifestPath;
                TextSpan span = request?.Span ?? default;
                budget.Step(path, span);
                if (request is null || request.Value is null)
                    Fail(FeatureDiagnostic, "Cargo feature request must contain a feature name.", path, span);
                Reference reference = ParseReference(root, request!.Value, path, span);
                QueueReference(root, reference);
            }
            for (int step = 0; _queue.Count > 0 && step < limits.MaximumOperations; step++)
            {
                Work work = _queue.Dequeue();
                budget.Step(work.Path, work.Span);
                if (work.Dependency is not null) ActivateDependency(work.Owner, work.Dependency, work.Path, work.Span);
                else if (work.Feature is not null) ActivateFeature(work.Owner, work.Feature);
                else ActivatePackage(work.Owner);
            }
            if (_queue.Count != 0) Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature expansion exceeded its work limit.", workspace.RootManifestPath, default);
            budget.Check(workspace.RootManifestPath, default);
            CargoPackageFeatureActivation[] activations = _catalogs.Values.Where(static c => c.Active)
                .OrderBy(static c => c.Identity, StringComparer.Ordinal)
                .Select(static c => new CargoPackageFeatureActivation(c.Package,
                    Array.AsReadOnly(c.ActiveFeatures.Order(StringComparer.Ordinal).ToArray()))).ToArray();
            CargoActivatedDependency[] dependencies = _catalogs.Values.SelectMany(c => c.ActiveDependencies.Select(alias =>
                new CargoActivatedDependency(c.Identity, alias, Target(c.Aliases[alias]).Identity)))
                .OrderBy(static d => d.SourceIdentity, StringComparer.Ordinal).ThenBy(static d => d.Alias, StringComparer.Ordinal)
                .ThenBy(static d => d.TargetIdentity, StringComparer.Ordinal).ToArray();
            budget.Check(workspace.RootManifestPath, default);
            return new(workspace, root.Package, Array.AsReadOnly(activations), Array.AsReadOnly(dependencies), Array.Empty<Diagnostic>(), budget.OperationsConsumed);
        }

        private void CreateCatalogs()
        {
            foreach (CargoPackage package in workspace.Packages)
            {
                budget.Step(package.ManifestPath, package.DeclarationSpan);
                _catalogs.Add(package.ManifestPath, new(package));
            }
            foreach (Catalog catalog in _catalogs.Values)
            {
                CargoPackage package = catalog.Package;
                foreach (CargoDependency dependency in package.Dependencies)
                {
                    budget.Step(package.ManifestPath, dependency.DeclarationSpan);
                    if (dependency.CfgCondition is not null && !allowConditional)
                        Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Conditional Cargo dependencies require cargo-v1 cfg selection before feature resolution.", package.ManifestPath, dependency.DeclarationSpan);
                    catalog.Aliases.TryAdd(dependency.Name, dependency);
                    if (!catalog.AliasGroups.TryGetValue(dependency.Name, out List<CargoDependency>? group))
                        catalog.AliasGroups.Add(dependency.Name, group = []);
                    group.Add(dependency);
                }
                var suppressed = new HashSet<string>(StringComparer.Ordinal);
                foreach ((string name, IReadOnlyList<string> values) in package.Features)
                {
                    budget.Step(package.ManifestPath, SpanFor(catalog, name));
                    catalog.Features.Add(name, []);
                    for (int index = 0; index < values.Count && index < limits.MaximumFeatureEdges; index++)
                    {
                        TextSpan span = MemberSpan(package, name, index);
                        budget.Step(package.ManifestPath, span);
                        CountEdge(package.ManifestPath, span);
                        if (values[index].StartsWith("dep:", StringComparison.Ordinal)) suppressed.Add(values[index][4..]);
                    }
                }
                foreach (CargoDependency dependency in package.Dependencies)
                {
                    budget.Step(package.ManifestPath, dependency.DeclarationSpan);
                    if (dependency.Optional && !suppressed.Contains(dependency.Name) && !catalog.Features.ContainsKey(dependency.Name))
                    {
                        if (catalog.Features.Count >= limits.MaximumFeaturesPerPackage)
                            Fail(CargoWorkspace.LimitDiagnostic, "Implicit Cargo features exceed the package feature count limit.", package.ManifestPath, dependency.DeclarationSpan);
                        CountEdge(package.ManifestPath, dependency.DeclarationSpan);
                        catalog.Features.Add(dependency.Name, [new(catalog, null, dependency, package.ManifestPath, dependency.DeclarationSpan)]);
                    }
                    for (int index = 0; index < dependency.Features.Count && index < limits.MaximumFeatureEdges; index++)
                    {
                        TextSpan span = DependencyMemberSpan(dependency, index);
                        budget.Step(package.ManifestPath, span);
                        CountEdge(package.ManifestPath, span);
                    }
                    if (dependency.Features.Count > limits.MaximumFeatureEdges)
                        Fail(CargoWorkspace.LimitDiagnostic, "Cargo dependency feature requests exceed their edge limit.", package.ManifestPath, dependency.DeclarationSpan);
                }
            }
        }

        private void PopulateReferences()
        {
            foreach (Catalog catalog in _catalogs.Values)
            {
                CargoPackage package = catalog.Package;
                foreach ((string name, IReadOnlyList<string> values) in package.Features)
                {
                    for (int index = 0; index < values.Count && index < limits.MaximumFeatureEdges; index++)
                    {
                        TextSpan span = MemberSpan(package, name, index);
                        budget.Step(package.ManifestPath, span);
                        catalog.Features[name].Add(ParseReference(catalog, values[index], package.ManifestPath, span));
                    }
                }
                foreach (CargoDependency dependency in package.Dependencies)
                {
                    Catalog target = Target(dependency);
                    for (int index = 0; index < dependency.Features.Count && index < limits.MaximumFeatureEdges; index++)
                    {
                        TextSpan span = DependencyMemberSpan(dependency, index);
                        budget.Step(package.ManifestPath, span);
                        string requested = dependency.Features[index];
                        if (!IsName(requested) || !target.Features.ContainsKey(requested))
                            Fail(FeatureDiagnostic, "Unknown dependency feature '" + requested + "'.", package.ManifestPath, span);
                    }
                }
            }
        }

        internal Reference ParseReference(Catalog owner, string value, string path, TextSpan span)
        {
            budget.Step(path, span);
            if (value.Length > limits.MaximumManifestBytes)
                Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature expression exceeds the input byte bound.", path, span);
            if (value.StartsWith("dep:", StringComparison.Ordinal))
            {
                string alias = value[4..];
                if (!IsName(alias) || !owner.AliasGroups.TryGetValue(alias, out List<CargoDependency>? group) || !group.Any(static dependency => dependency.Optional))
                    Fail(FeatureDiagnostic, "dep:name must name an optional local dependency.", path, span);
                return new(owner, null, owner.Aliases[alias], path, span);
            }
            int slash = value.IndexOf('/');
            if (slash >= 0)
            {
                string alias = value[..slash];
                string feature = value[(slash + 1)..];
                if (!IsName(alias) || !IsName(feature) || !owner.Aliases.TryGetValue(alias, out CargoDependency? dependency))
                    Fail(FeatureDiagnostic, "Unsupported or unknown Cargo dependency feature expression '" + value + "'.", path, span);
                CargoDependency found = owner.Aliases[alias];
                Catalog target = Target(found);
                foreach (CargoDependency candidate in owner.AliasGroups[alias])
                {
                    budget.Step(path, span);
                    if (!Target(candidate).Features.ContainsKey(feature)) Fail(FeatureDiagnostic, "Unknown dependency feature '" + feature + "'.", path, span);
                }
                return new(target, feature, found, path, span);
            }
            if (!IsName(value) || !owner.Features.ContainsKey(value))
                Fail(FeatureDiagnostic, "Unsupported or unknown Cargo feature '" + value + "'.", path, span);
            return new(owner, value, null, path, span);
        }

        private void ValidateCycles()
        {
            var colors = new Dictionary<(string Path, string Feature), byte>();
            var stack = new Stack<Frame>();
            foreach (Catalog catalog in _catalogs.Values)
            {
                foreach (string feature in catalog.Features.Keys.Order(StringComparer.Ordinal))
                {
                    budget.Step(catalog.Package.ManifestPath, SpanFor(catalog, feature));
                    var key = (catalog.Package.ManifestPath, feature);
                    if (colors.ContainsKey(key)) continue;
                    colors.Add(key, 1);
                    stack.Push(new(catalog, feature, 0));
                    for (int step = 0; stack.Count > 0 && step < limits.MaximumOperations; step++)
                    {
                        Frame frame = stack.Pop();
                        budget.Step(frame.Owner.Package.ManifestPath, SpanFor(frame.Owner, frame.Feature));
                        List<Reference> references = frame.Owner.Features[frame.Feature];
                        if (frame.Next >= references.Count)
                        { colors[(frame.Owner.Package.ManifestPath, frame.Feature)] = 2; continue; }
                        Reference reference = references[frame.Next];
                        stack.Push(frame with { Next = frame.Next + 1 });
                        if (reference.Feature is null) continue;
                        var targetKey = (reference.Target.Package.ManifestPath, reference.Feature);
                        if (colors.TryGetValue(targetKey, out byte color))
                        {
                            if (color == 1) Fail(FeatureDiagnostic, "Cargo feature cycle detected.", reference.Path, reference.Span);
                            continue;
                        }
                        colors.Add(targetKey, 1);
                        stack.Push(new(reference.Target, reference.Feature, 0));
                    }
                    if (stack.Count != 0) Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature cycle validation exceeded its work limit.", catalog.Package.ManifestPath, SpanFor(catalog, feature));
                }
            }
        }

        private Catalog SelectRoot()
        {
            budget.Step(workspace.RootManifestPath, default);
            if (options.RootPackageIdentity is null)
            {
                if (workspace.RootPackageOrNull is not null) return _catalogs[workspace.RootPackageOrNull.ManifestPath];
                Fail(FeatureDiagnostic, "A virtual Cargo workspace requires an explicit member package identity.", workspace.RootManifestPath, default);
            }
            foreach (Catalog catalog in _catalogs.Values)
            {
                budget.Step(catalog.Package.ManifestPath, catalog.Package.DeclarationSpan);
                bool selectable = PathComparer.Equals(catalog.Package.ManifestPath, workspace.RootPackageOrNull?.ManifestPath) ||
                    workspace.WorkspaceMemberPaths.Contains(catalog.Package.ManifestPath, PathComparer);
                if (selectable && catalog.Identity == options.RootPackageIdentity) return catalog;
            }
            Fail(FeatureDiagnostic, "The selected Cargo root package identity is not a workspace root or member.", workspace.RootManifestPath, default);
            throw new InvalidOperationException();
        }

        private void ActivatePackage(Catalog catalog)
        {
            if (catalog.Active) return;
            catalog.Active = true;
            foreach (CargoDependency dependency in catalog.Package.Dependencies)
            {
                budget.Step(catalog.Package.ManifestPath, dependency.DeclarationSpan);
                if (!dependency.Optional) Enqueue(new(catalog, null, dependency, catalog.Package.ManifestPath, dependency.DeclarationSpan));
            }
        }

        private void ActivateDependency(Catalog owner, CargoDependency dependency, string path, TextSpan span)
        {
            if (!owner.ActiveDependencies.Add(dependency.Name)) return;
            Catalog target = Target(dependency);
            Enqueue(new(target, null, null, path, span));
            if (dependency.DefaultFeatures && target.Features.ContainsKey("default"))
                Enqueue(new(target, "default", null, path, span));
            for (int index = 0; index < dependency.Features.Count && index < limits.MaximumFeatureEdges; index++)
                Enqueue(new(target, dependency.Features[index], null, owner.Package.ManifestPath, DependencyMemberSpan(dependency, index)));
        }

        private void ActivateFeature(Catalog catalog, string feature)
        {
            ActivatePackage(catalog);
            if (!catalog.ActiveFeatures.Add(feature)) return;
            foreach (Reference reference in catalog.Features[feature])
            {
                budget.Step(reference.Path, reference.Span);
                QueueReference(catalog, reference);
            }
        }

        private void QueueReference(Catalog owner, Reference reference)
        {
            if (reference.Dependency is not null) Enqueue(new(owner, null, reference.Dependency, reference.Path, reference.Span));
            if (reference.Feature is not null) Enqueue(new(reference.Target, reference.Feature, null, reference.Path, reference.Span));
        }

        private void Enqueue(Work work)
        {
            budget.Step(work.Path, work.Span);
            if (_queue.Count >= limits.MaximumOperations)
                Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature work queue exceeds its item limit.", work.Path, work.Span);
            _queue.Enqueue(work);
        }

        private void CountEdge(string path, TextSpan span)
        {
            if (++_rawEdges > limits.MaximumFeatureEdges)
                Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature graph exceeds its raw edge limit.", path, span);
        }

        private Catalog Target(CargoDependency dependency) => _catalogs[dependency.ResolvedManifestPath!];
        private static bool IsName(string name) => name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
            name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
        private static TextSpan SpanFor(Catalog catalog, string name) => catalog.Package.FeatureSpans.TryGetValue(name, out TextSpan span) ? span : catalog.Package.DeclarationSpan;
        private static TextSpan MemberSpan(CargoPackage package, string feature, int index) =>
            package.FeatureMemberSpans.TryGetValue(feature, out IReadOnlyList<TextSpan>? spans) && index < spans.Count ? spans[index] : package.FeatureSpans[feature];
        private static TextSpan DependencyMemberSpan(CargoDependency dependency, int index) => index < dependency.FeatureSpans.Count ? dependency.FeatureSpans[index] : dependency.DeclarationSpan;
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        private static void Fail(string code, string message, string path, TextSpan span) => throw new CargoLoadException(code, message, path, span);
    }
}
