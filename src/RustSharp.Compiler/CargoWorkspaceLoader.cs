using System.Collections.ObjectModel;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

internal sealed class CargoWorkspaceLoader(string manifestPath, CargoWorkspaceOptions options, CancellationToken cancellationToken)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly CargoLoadBudget _budget = new(options, cancellationToken);
    private readonly Dictionary<string, CargoPackage> _packages = new(PathComparer);
    private readonly Dictionary<string, string> _identities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(PathComparer);
    private readonly List<string> _members = [];
    private readonly string _rootManifestPath = manifestPath;
    private string _currentPath = manifestPath;
    private TextSpan _currentSpan;

    internal CargoWorkspaceResult Run()
    {
        try
        {
            CargoParsedManifest root = ReadManifest(_rootManifestPath, _rootManifestPath, default);
            ValidateTables(root);
            CargoTomlTable? packageTable = Singleton(root, "package");
            CargoTomlTable? workspaceTable = Singleton(root, "workspace");
            if (packageTable is null && workspaceTable is null)
                Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest requires [package] or [workspace].", _rootManifestPath, default);
            CargoPackage? rootPackage = null;
            if (packageTable is not null) rootPackage = VisitPackage(_rootManifestPath, 1, _rootManifestPath, packageTable.Span, root);
            else ValidateVirtualRoot(root);
            if (workspaceTable is not null) LoadMembers(root, workspaceTable);
            _budget.Check(_rootManifestPath, default);
            CargoPackage[] packages = _packages.Values.OrderBy(static item => item.Name, StringComparer.Ordinal)
                .ThenBy(static item => item.Version, StringComparer.Ordinal).ThenBy(static item => item.ManifestPath, StringComparer.Ordinal).ToArray();
            return new(_rootManifestPath, rootPackage, packageTable is null, System.Array.AsReadOnly(packages),
                _members.AsReadOnly(), System.Array.Empty<Diagnostic>());
        }
        catch (CargoLoadException exception)
        {
            return Failure(new(exception.Code, exception.Message, exception.Span) { SourcePath = exception.SourcePath });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            string code = exception is FileNotFoundException or DirectoryNotFoundException ? CargoWorkspace.MissingInputDiagnostic : CargoWorkspace.ManifestDiagnostic;
            return Failure(new(code, "Cannot load Cargo input: " + exception.Message, _currentSpan) { SourcePath = _currentPath });
        }
    }

    private CargoWorkspaceResult Failure(Diagnostic diagnostic) =>
        new(_rootManifestPath, null, false, System.Array.Empty<CargoPackage>(), System.Array.Empty<string>(), System.Array.AsReadOnly(new[] { diagnostic }));

    private CargoParsedManifest ReadManifest(string path, string origin, TextSpan span)
    {
        SetLocation(origin, span);
        _budget.Step(origin, span);
        CheckPath(path, origin, span);
        if (!File.Exists(path)) Fail(CargoWorkspace.MissingInputDiagnostic, "Cargo manifest does not exist.", origin, span);
        SetLocation(path, default);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > options.MaximumManifestBytes)
            Fail(CargoWorkspace.LimitDiagnostic, "Cargo manifest exceeds its byte limit.", path, default);
        byte[] bytes = new byte[(int)length];
        int total = 0;
        for (int read = 0; read <= bytes.Length && total < bytes.Length; read++)
        {
            _budget.Check(path, default);
            int count = stream.Read(bytes, total, Math.Min(4096, bytes.Length - total));
            if (count == 0) Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest changed while being read.", path, default);
            total += count;
        }
        _budget.Check(path, default);
        if (stream.ReadByte() != -1 || stream.Length != length)
            Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest changed while being read.", path, default);
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest is not valid UTF-8.", path, default); throw; }
        return new CargoManifestParser(path, text, _budget).Parse();
    }

    private CargoPackage VisitPackage(string path, int depth, string origin, TextSpan span, CargoParsedManifest? parsed = null)
    {
        SetLocation(origin, span);
        _budget.Step(origin, span);
        if (depth > options.MaximumGraphDepth) Fail(CargoWorkspace.LimitDiagnostic, "Cargo graph exceeds its depth limit.", origin, span);
        if (_active.Contains(path)) Fail(CargoWorkspace.DependencyCycleDiagnostic, "Cargo path dependency cycle detected.", origin, span);
        if (_packages.TryGetValue(path, out CargoPackage? known)) return known;
        if (_packages.Count >= options.MaximumPackages) Fail(CargoWorkspace.LimitDiagnostic, "Cargo graph exceeds its package limit.", origin, span);
        _active.Add(path);
        try
        {
            CargoParsedManifest manifest = parsed ?? ReadManifest(path, origin, span);
            ValidateTables(manifest);
            CargoPackage package = BuildPackage(manifest);
            string identity = package.Name + "@" + package.Version;
            if (_identities.TryGetValue(identity, out string? existing) && !PathComparer.Equals(existing, path))
                Fail(CargoWorkspace.DuplicateIdentityDiagnostic, "Different Cargo manifest paths declare the same package identity.", path, package.DeclarationSpan);
            _identities.Add(identity, path);
            _packages.Add(path, package);
            foreach (CargoDependency dependency in package.Dependencies)
            {
                _budget.Step(path, dependency.DeclarationSpan);
                CargoPackage target = VisitPackage(dependency.ResolvedManifestPath!, depth + 1, path, dependency.PathSpan);
                if (target.Name != (dependency.PackageName ?? dependency.Name))
                    Fail(CargoWorkspace.DuplicateIdentityDiagnostic, "Path dependency package name does not match its declared identity.", path, dependency.DeclarationSpan);
                if (dependency.Version is not null && dependency.Version != target.Version)
                    Fail(CargoWorkspace.DuplicateIdentityDiagnostic, "Path dependency exact version does not match its package.", path, dependency.DeclarationSpan);
            }
            return package;
        }
        finally { _active.Remove(path); }
    }

    private void LoadMembers(CargoParsedManifest manifest, CargoTomlTable workspace)
    {
        ValidateKeys(manifest.Path, workspace.Entries, ["members", "exclude", "resolver"]);
        if (workspace.Entries.TryGetValue("resolver", out CargoTomlEntry? resolver) && String(resolver.Value, manifest.Path) != "2")
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Only workspace resolver 2 is admitted.", manifest.Path, resolver.Value.Span);
        var excluded = new HashSet<string>(PathComparer);
        foreach (CargoTomlValue exclude in OptionalArray(workspace, "exclude", manifest.Path))
        {
            _budget.Step(manifest.Path, exclude.Span);
            excluded.Add(WorkspaceDirectory(manifest.Path, exclude));
        }
        var members = new Dictionary<string, TextSpan>(PathComparer);
        foreach (CargoTomlValue member in OptionalArray(workspace, "members", manifest.Path))
        {
            _budget.Step(manifest.Path, member.Span);
            string directory = WorkspaceDirectory(manifest.Path, member);
            if (!excluded.Contains(directory)) members.TryAdd(Path.Combine(directory, "Cargo.toml"), member.Span);
        }
        foreach ((string memberPath, TextSpan memberSpan) in members.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            _budget.Step(manifest.Path, memberSpan);
            VisitPackage(memberPath, 1, manifest.Path, memberSpan);
            _members.Add(memberPath);
        }
    }

    private static string WorkspaceDirectory(string path, CargoTomlValue value)
    {
        string relative = String(value, path);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOfAny(['*', '?', '[', ']']) >= 0)
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Workspace members and excludes must be exact relative directories without globs.", path, value.Span);
        string root = Path.GetDirectoryName(path)!;
        string full = FullPath(Path.Combine(root, relative), path, value.Span, CargoWorkspace.UnsupportedManifestDiagnostic);
        if (!Within(root, full)) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Workspace members and excludes must remain within the workspace root.", path, value.Span);
        return full;
    }

    private CargoPackage BuildPackage(CargoParsedManifest manifest)
    {
        CargoTomlTable? table = Singleton(manifest, "package");
        if (table is null) Fail(CargoWorkspace.ManifestDiagnostic, "A workspace member or dependency must declare [package].", manifest.Path, default);
        ValidateKeys(manifest.Path, table!.Entries, ["name", "version", "edition"]);
        string name = RequiredString(table, "name", manifest.Path);
        string version = RequiredString(table, "version", manifest.Path);
        ValidateName(name, manifest.Path, table.Entries["name"].Value.Span);
        ValidateVersion(version, manifest.Path, table.Entries["version"].Value.Span);
        string edition = table.Entries.TryGetValue("edition", out CargoTomlEntry? editionEntry) ? String(editionEntry.Value, manifest.Path) : "2015";
        if (edition is not "2015" and not "2018" and not "2021" and not "2024")
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Unsupported Cargo package edition.", manifest.Path, editionEntry!.Value.Span);
        ReadOnlyCollection<CargoDependency> dependencies = BuildDependencies(manifest);
        ReadOnlyCollection<CargoTarget> targets = BuildTargets(manifest, name, table.Span);
        var features = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var featureSpans = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
        CargoTomlTable? featureTable = Singleton(manifest, "features");
        int featureEdges = 0;
        if (featureTable is not null)
        {
            foreach (CargoTomlEntry entry in featureTable.Entries.Values.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                _budget.Step(manifest.Path, entry.KeySpan);
                if (features.Count >= options.MaximumFeaturesPerPackage)
                    Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature metadata exceeds its feature count limit.", manifest.Path, entry.KeySpan);
                ValidateName(entry.Key, manifest.Path, entry.KeySpan);
                IReadOnlyList<CargoTomlValue> values = Array(entry.Value, manifest.Path);
                featureEdges += values.Count;
                if (featureEdges > options.MaximumFeatureEdges)
                    Fail(CargoWorkspace.LimitDiagnostic, "Cargo feature metadata exceeds its edge limit.", manifest.Path, entry.Value.Span);
                features.Add(entry.Key, System.Array.AsReadOnly(values.Select(static item => (string)item.Value).ToArray()));
                featureSpans.Add(entry.Key, entry.Value.Span);
            }
        }
        CargoTarget? library = targets.FirstOrDefault(static target => target.Kind == CargoTargetKind.Library);
        bool explicitLibrary = Singleton(manifest, "lib")?.Entries.ContainsKey("path") == true;
        bool explicitBinary = manifest.Tables.Any(static item => item.IsArray && item.Path[0] == "bin");
        CargoTarget primary = explicitLibrary ? library! : explicitBinary ? targets.First(static item => item.Kind == CargoTargetKind.Binary)
            : targets.FirstOrDefault(static item => item.Kind == CargoTargetKind.Binary) ?? targets[0];
        return new(manifest.Path, name, version, edition, primary.SourcePath, dependencies)
        {
            LibrarySourcePath = library?.SourcePath,
            Targets = targets,
            Features = new ReadOnlyDictionary<string, IReadOnlyList<string>>(features),
            FeatureSpans = new ReadOnlyDictionary<string, TextSpan>(featureSpans),
            DeclarationSpan = table.Span,
            Workspace = Singleton(manifest, "workspace") is CargoTomlTable workspace ? new(
                System.Array.AsReadOnly(OptionalArray(workspace, "members", manifest.Path).Select(static item => (string)item.Value).ToArray()),
                System.Array.AsReadOnly(OptionalArray(workspace, "exclude", manifest.Path).Select(static item => (string)item.Value).ToArray()),
                "2", workspace.Span) : null,
        };
    }

    private ReadOnlyCollection<CargoDependency> BuildDependencies(CargoParsedManifest manifest)
    {
        var dependencies = new Dictionary<string, CargoDependency>(StringComparer.Ordinal);
        foreach (CargoTomlTable table in manifest.Tables)
        {
            bool normal = table.Path[0] == "dependencies";
            bool conditional = table.Path[0] == "target";
            if (!normal && !conditional) continue;
            string? condition = conditional ? table.Path[1] : null;
            int baseLength = conditional ? 3 : 1;
            if (table.Path.Count == baseLength)
            {
                foreach (CargoTomlEntry entry in table.Entries.Values)
                {
                    _budget.Step(manifest.Path, entry.KeySpan);
                    if (entry.Value.Value is not Dictionary<string, CargoTomlEntry>)
                        Fail(CargoWorkspace.UnsupportedDependencyDiagnostic, "Only local path dependency tables are admitted; registry dependencies require network resolution.", manifest.Path, entry.Value.Span);
                    CargoDependency dependency = BuildDependency(manifest.Path, entry.Key, (Dictionary<string, CargoTomlEntry>)entry.Value.Value, entry.KeySpan, condition);
                    AddDependency(dependencies, dependency, manifest.Path);
                }
            }
            else
            {
                CargoDependency dependency = BuildDependency(manifest.Path, table.Path[^1], table.Entries, table.Span, condition);
                AddDependency(dependencies, dependency, manifest.Path);
            }
        }
        return System.Array.AsReadOnly(dependencies.Values.OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ThenBy(static item => item.CfgCondition, StringComparer.Ordinal).ToArray());
    }

    private void AddDependency(Dictionary<string, CargoDependency> dependencies, CargoDependency dependency, string path)
    {
        _budget.Step(path, dependency.DeclarationSpan);
        string key = (dependency.CfgCondition ?? string.Empty) + "\0" + dependency.Name;
        if (!dependencies.TryAdd(key, dependency)) Fail(CargoWorkspace.ManifestDiagnostic, "Duplicate Cargo dependency declaration.", path, dependency.DeclarationSpan);
        if (dependencies.Count > options.MaximumDependenciesPerPackage)
            Fail(CargoWorkspace.LimitDiagnostic, "Cargo package exceeds its dependency edge limit.", path, dependency.DeclarationSpan);
    }

    private CargoDependency BuildDependency(string path, string alias, Dictionary<string, CargoTomlEntry> entries, TextSpan span, string? condition)
    {
        ValidateName(alias, path, span);
        foreach (CargoTomlEntry entry in entries.Values)
        {
            _budget.Step(path, entry.KeySpan);
            if (entry.Key is "git" or "registry" or "source" or "branch" or "rev" or "tag")
                Fail(CargoWorkspace.UnsupportedDependencyDiagnostic, "Cargo dependency origins must be local paths.", path, entry.KeySpan);
        }
        ValidateKeys(path, entries, ["path", "package", "version", "features", "optional", "default-features"]);
        if (!entries.TryGetValue("path", out CargoTomlEntry? pathEntry))
            Fail(CargoWorkspace.UnsupportedDependencyDiagnostic, "Cargo dependency has no local path.", path, span);
        string relative = String(pathEntry!.Value, path);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            Fail(CargoWorkspace.UnsupportedDependencyDiagnostic, "Cargo dependency path must be a nonempty relative directory.", path, pathEntry.Value.Span);
        string? packageName = entries.TryGetValue("package", out CargoTomlEntry? packageEntry) ? String(packageEntry.Value, path) : null;
        if (packageName is not null) ValidateName(packageName, path, packageEntry!.Value.Span);
        string? version = entries.TryGetValue("version", out CargoTomlEntry? versionEntry) ? String(versionEntry.Value, path) : null;
        if (version is not null) ValidateVersion(version, path, versionEntry!.Value.Span, dependency: true);
        IReadOnlyList<string> features = entries.TryGetValue("features", out CargoTomlEntry? featuresEntry)
            ? System.Array.AsReadOnly(Array(featuresEntry.Value, path).Select(static item => (string)item.Value).ToArray()) : System.Array.Empty<string>();
        bool optional = entries.TryGetValue("optional", out CargoTomlEntry? optionalEntry) && Boolean(optionalEntry.Value, path);
        bool defaultFeatures = !entries.TryGetValue("default-features", out CargoTomlEntry? defaultsEntry) || Boolean(defaultsEntry.Value, path);
        string target = FullPath(Path.Combine(Path.GetDirectoryName(path)!, relative, "Cargo.toml"), path, pathEntry.Value.Span, CargoWorkspace.UnsupportedDependencyDiagnostic);
        return new(alias, relative)
        {
            PackageName = packageName, Version = version, Features = features, Optional = optional,
            DefaultFeatures = defaultFeatures, CfgCondition = condition, DeclarationSpan = span,
            PathSpan = pathEntry.Value.Span, ResolvedManifestPath = target,
        };
    }

    private ReadOnlyCollection<CargoTarget> BuildTargets(CargoParsedManifest manifest, string packageName, TextSpan packageSpan)
    {
        var targets = new List<CargoTarget>();
        string root = Path.GetDirectoryName(manifest.Path)!;
        CargoTomlTable? library = Singleton(manifest, "lib");
        if (library is not null)
        {
            ValidateKeys(manifest.Path, library.Entries, ["name", "path", "crate-type"]);
            if (library.Entries.TryGetValue("crate-type", out CargoTomlEntry? typeEntry))
            {
                IReadOnlyList<CargoTomlValue> types = Array(typeEntry.Value, manifest.Path);
                if (types.Count != 1 || (string)types[0].Value != "rlib")
                    Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Only one rlib library crate type is admitted.", manifest.Path, typeEntry.Value.Span);
            }
            targets.Add(BuildTarget(manifest.Path, packageName.Replace('-', '_'), library, CargoTargetKind.Library, "src/lib.rs"));
        }
        else if (File.Exists(Path.Combine(root, "src", "lib.rs")))
            targets.Add(InferredTarget(manifest.Path, packageName.Replace('-', '_'), CargoTargetKind.Library, "src/lib.rs", packageSpan));
        CargoTomlTable[] binaryTables = manifest.Tables.Where(static table => table.IsArray && table.Path[0] == "bin").ToArray();
        foreach (CargoTomlTable binary in binaryTables)
        {
            _budget.Step(manifest.Path, binary.Span);
            ValidateKeys(manifest.Path, binary.Entries, ["name", "path", "required-features"]);
            targets.Add(BuildTarget(manifest.Path, packageName, binary, CargoTargetKind.Binary, "src/main.rs"));
            if (targets.Count > options.MaximumTargets) Fail(CargoWorkspace.LimitDiagnostic, "Cargo package exceeds its target count limit.", manifest.Path, binary.Span);
        }
        if (binaryTables.Length == 0 && File.Exists(Path.Combine(root, "src", "main.rs")))
            targets.Add(InferredTarget(manifest.Path, packageName, CargoTargetKind.Binary, "src/main.rs", packageSpan));
        if (targets.Count == 0) Fail(CargoWorkspace.MissingInputDiagnostic, "Cargo package has no existing source target.", manifest.Path, packageSpan);
        if (targets.Count > options.MaximumTargets) Fail(CargoWorkspace.LimitDiagnostic, "Cargo package exceeds its target count limit.", manifest.Path, packageSpan);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (CargoTarget target in targets)
        {
            _budget.Step(manifest.Path, target.DeclarationSpan);
            if (!identities.Add(target.Kind + "\0" + target.Name))
                Fail(CargoWorkspace.DuplicateIdentityDiagnostic, "Duplicate Cargo target name within the same target kind.", manifest.Path, target.DeclarationSpan);
        }
        return targets.OrderBy(static target => target.Kind).ThenBy(static target => target.Name, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    private CargoTarget BuildTarget(string manifest, string defaultName, CargoTomlTable table, CargoTargetKind kind, string defaultPath)
    {
        string name = table.Entries.TryGetValue("name", out CargoTomlEntry? nameEntry) ? String(nameEntry.Value, manifest) : defaultName;
        ValidateName(name, manifest, nameEntry?.Value.Span ?? table.Span);
        string relative = table.Entries.TryGetValue("path", out CargoTomlEntry? pathEntry) ? String(pathEntry.Value, manifest) : defaultPath;
        TextSpan span = pathEntry?.Value.Span ?? table.Span;
        string source = SourcePath(manifest, relative, span);
        IReadOnlyList<string> features = table.Entries.TryGetValue("required-features", out CargoTomlEntry? featuresEntry)
            ? System.Array.AsReadOnly(Array(featuresEntry.Value, manifest).Select(static item => (string)item.Value).ToArray()) : System.Array.Empty<string>();
        return new(kind, name, source, features, table.Span) { PathSpan = span };
    }

    private CargoTarget InferredTarget(string manifest, string name, CargoTargetKind kind, string relative, TextSpan span) =>
        new(kind, name, SourcePath(manifest, relative, span), System.Array.Empty<string>(), span) { PathSpan = span };

    private string SourcePath(string manifest, string relative, TextSpan span)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Source target path must be a nonempty relative path.", manifest, span);
        string root = Path.GetDirectoryName(manifest)!;
        string full = FullPath(Path.Combine(root, relative), manifest, span, CargoWorkspace.UnsupportedManifestDiagnostic);
        if (!Within(root, full)) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Source target escapes its package root.", manifest, span);
        CheckPath(full, manifest, span);
        if (!File.Exists(full)) Fail(CargoWorkspace.MissingInputDiagnostic, "Cargo source target does not exist.", manifest, span);
        return full;
    }

    private void CheckPath(string path, string origin, TextSpan span)
    {
        SetLocation(origin, span);
        string full = Path.GetFullPath(path);
        string volume = Path.GetPathRoot(full)!;
        string relative = full[volume.Length..];
        string component = volume;
        string[] parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 256) Fail(CargoWorkspace.LimitDiagnostic, "Cargo input path exceeds the component limit.", origin, span);
        foreach (string part in parts)
        {
            _budget.Step(origin, span);
            component = Path.Combine(component, part);
            if (new FileInfo(component).LinkTarget is not null || new DirectoryInfo(component).LinkTarget is not null ||
                (File.Exists(component) || Directory.Exists(component)) && (File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Cargo manifests and source targets cannot traverse filesystem links.", origin, span);
        }
    }

    private void ValidateTables(CargoParsedManifest manifest)
    {
        foreach (CargoTomlTable table in manifest.Tables)
        {
            _budget.Step(manifest.Path, table.Span);
            IReadOnlyList<string> parts = table.Path;
            bool admitted = parts.Count == 1 && (parts[0] is "package" or "workspace" or "lib" or "dependencies" or "features" || parts[0] == "bin" && table.IsArray)
                || parts.Count == 2 && parts[0] == "dependencies"
                || parts.Count is 3 or 4 && parts[0] == "target" && parts[2] == "dependencies";
            if (!admitted) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Unknown or inherited Cargo manifest table.", manifest.Path, table.Span);
            if (parts.Count == 1 && parts[0] == "workspace")
            {
                ValidateKeys(manifest.Path, table.Entries, ["members", "exclude", "resolver"]);
                if (table.Entries.TryGetValue("resolver", out CargoTomlEntry? resolver) && String(resolver.Value, manifest.Path) != "2")
                    Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Only workspace resolver 2 is admitted.", manifest.Path, resolver.Value.Span);
                foreach (CargoTomlValue member in OptionalArray(table, "members", manifest.Path))
                    WorkspaceDirectory(manifest.Path, member);
                foreach (CargoTomlValue exclude in OptionalArray(table, "exclude", manifest.Path))
                    WorkspaceDirectory(manifest.Path, exclude);
            }
        }
    }

    private static void ValidateVirtualRoot(CargoParsedManifest manifest)
    {
        foreach (CargoTomlTable table in manifest.Tables)
            if (table.Path[0] != "workspace") Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "A virtual workspace cannot declare package targets, features or dependencies.", manifest.Path, table.Span);
    }

    private void ValidateKeys(string path, Dictionary<string, CargoTomlEntry> entries, string[] admitted)
    {
        foreach (CargoTomlEntry entry in entries.Values)
        {
            _budget.Step(path, entry.KeySpan);
            if (!admitted.Contains(entry.Key, StringComparer.Ordinal))
                Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Unknown Cargo manifest key.", path, entry.KeySpan);
        }
    }

    private static string RequiredString(CargoTomlTable table, string key, string path)
    {
        if (!table.Entries.TryGetValue(key, out CargoTomlEntry? entry)) Fail(CargoWorkspace.ManifestDiagnostic, "Cargo package requires name and exact version.", path, table.Span);
        return String(entry!.Value, path);
    }

    private static string String(CargoTomlValue value, string path)
    {
        if (value.Value is not string) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Cargo value must be a string.", path, value.Span);
        return (string)value.Value;
    }

    private static bool Boolean(CargoTomlValue value, string path)
    {
        if (value.Value is not bool) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Cargo value must be a boolean.", path, value.Span);
        return (bool)value.Value;
    }

    private static IReadOnlyList<CargoTomlValue> Array(CargoTomlValue value, string path)
    {
        if (value.Value is not IReadOnlyList<CargoTomlValue>) Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Cargo value must be a single-line string array.", path, value.Span);
        return (IReadOnlyList<CargoTomlValue>)value.Value;
    }

    private static IReadOnlyList<CargoTomlValue> OptionalArray(CargoTomlTable table, string key, string path) =>
        table.Entries.TryGetValue(key, out CargoTomlEntry? entry) ? Array(entry.Value, path) : System.Array.Empty<CargoTomlValue>();

    private static void ValidateName(string name, string path, TextSpan span)
    {
        if (name.Length is < 1 or > 128 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') ||
            !name.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "Cargo names must be bounded ASCII identifiers beginning with a letter or underscore.", path, span);
    }

    private static void ValidateVersion(string version, string path, TextSpan span, bool dependency = false)
    {
        string[] parts = version.Split('.');
        if (version.Length > 128 || parts.Length != 3 || parts.Any(static part => part.Length == 0 ||
            part.Length > 1 && part[0] == '0' || !part.All(char.IsAsciiDigit)))
            Fail(dependency ? CargoWorkspace.UnsupportedDependencyDiagnostic : CargoWorkspace.UnsupportedManifestDiagnostic,
                "Cargo versions must be exact numeric major.minor.patch values without ranges or prereleases.", path, span);
    }

    private static CargoTomlTable? Singleton(CargoParsedManifest manifest, string name) =>
        manifest.Tables.FirstOrDefault(table => table.Path.Count == 1 && table.Path[0] == name);

    private static bool Within(string root, string path) => PathComparer.Equals(root, path) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FullPath(string path, string origin, TextSpan span, string code)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new CargoLoadException(code, "Invalid Cargo path: " + exception.Message, origin, span);
        }
    }

    private void SetLocation(string path, TextSpan span) { _currentPath = path; _currentSpan = span; }
    private static void Fail(string code, string message, string path, TextSpan span) => throw new CargoLoadException(code, message, path, span);
}
