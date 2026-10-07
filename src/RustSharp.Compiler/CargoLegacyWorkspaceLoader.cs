using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>The pre-cargo-v1 source-linking dialect, including implicit dependency aliases and shared source paths.</summary>
internal sealed class CargoLegacyWorkspaceLoader(string manifestPath, CargoWorkspaceOptions options, CancellationToken cancellationToken)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string _rootManifestPath = manifestPath;
    private readonly CargoLoadBudget _budget = new(options, cancellationToken);
    private readonly List<CargoPackage> _packages = [];
    private readonly Dictionary<string, CargoPackage> _loaded = new(PathComparer);
    private readonly HashSet<string> _active = new(PathComparer);
    private string _currentPath = manifestPath;

    internal CargoWorkspaceResult Run()
    {
        try
        {
            CargoPackage root = LoadPackage(_rootManifestPath, 1);
            _budget.Check(_rootManifestPath, default);
            return new(_rootManifestPath, root, false, _packages.AsReadOnly(), Array.Empty<string>(), Array.Empty<Diagnostic>());
        }
        catch (CargoLoadException exception)
        {
            return Failure(new(exception.Code, exception.Message, exception.Span) { SourcePath = exception.SourcePath });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return Failure(new(CargoWorkspace.ManifestDiagnostic, "Cannot load legacy Cargo input: " + exception.Message, default) { SourcePath = _currentPath });
        }
    }

    private CargoWorkspaceResult Failure(Diagnostic diagnostic) =>
        new(_rootManifestPath, null, false, Array.Empty<CargoPackage>(), Array.Empty<string>(), Array.AsReadOnly(new[] { diagnostic }));

    private CargoPackage LoadPackage(string path, int depth)
    {
        _currentPath = path;
        _budget.Step(path, default);
        string full = Path.GetFullPath(path);
        if (depth > 256) Fail(CargoWorkspace.LimitDiagnostic, "Legacy Cargo graph exceeds its bounded depth.", full);
        if (_active.Contains(full)) Fail(CargoWorkspace.DependencyCycleDiagnostic, "Cargo path dependency cycle detected.", full);
        if (_loaded.TryGetValue(full, out CargoPackage? loaded)) return loaded;
        if (_packages.Count >= options.MaximumPackages) Fail(CargoWorkspace.LimitDiagnostic, "Cargo package count exceeded its limit.", full);
        _active.Add(full);
        try
        {
            CargoPackage package = Parse(full);
            _packages.Add(package);
            _loaded.Add(full, package);
            foreach (CargoDependency dependency in package.Dependencies)
            {
                _budget.Step(full, default);
                if (dependency.Path is null)
                    Fail(CargoWorkspace.UnsupportedDependencyDiagnostic, $"Dependency '{dependency.Name}' has no local path.", full);
                // The legacy alias is deliberately independent of the dependency's package.name.
                LoadPackage(dependency.ResolvedManifestPath!, depth + 1);
            }
            return package;
        }
        finally { _active.Remove(full); }
    }

    private CargoPackage Parse(string path)
    {
        string text = ReadManifest(path);
        string section = string.Empty;
        bool hasPackageTable = false, hasWorkspaceTable = false;
        string? name = null, version = null, edition = null, libPath = null, binPath = null, binName = null;
        var dependencies = new List<CargoDependency>();
        for (int start = 0; start < text.Length;)
        {
            _budget.Step(path, new(start, 0));
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            string raw = text[start..end];
            int comment = raw.IndexOf('#');
            string line = (comment < 0 ? raw : raw[..comment]).Trim();
            start = newline < 0 ? text.Length : newline + 1;
            if (line.Length == 0) continue;
            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            { section = line[2..^2].Trim(); continue; }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                hasPackageTable |= section == "package";
                hasWorkspaceTable |= section == "workspace";
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals <= 0) Fail(CargoWorkspace.ManifestDiagnostic, $"Invalid legacy Cargo manifest line '{line}'.", path);
            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            string? scalar = ParseScalar(value);
            if (section == "package")
            {
                if (key == "name") name = scalar;
                else if (key == "version") version = scalar;
                else if (key == "edition") edition = scalar;
            }
            else if (section == "lib" && key == "path") libPath = scalar;
            else if (section == "bin")
            {
                if (key == "name") binName = scalar;
                else if (key == "path") binPath = scalar;
            }
            else if (section == "dependencies")
            {
                string? relative = ExtractPath(value);
                dependencies.Add(new(key, relative)
                {
                    ResolvedManifestPath = relative is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative, "Cargo.toml")),
                });
                if (dependencies.Count > options.MaximumOperations)
                    Fail(CargoWorkspace.LimitDiagnostic, "Legacy Cargo dependency list exceeds its work limit.", path);
            }
        }
        if (!hasPackageTable && hasWorkspaceTable)
            Fail(CargoWorkspace.UnsupportedManifestDiagnostic, "A virtual Cargo workspace requires explicit package selection; LoadV1 can inspect its metadata.", path);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
            Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest requires [package].name and [package].version.", path);
        string root = Path.GetDirectoryName(path)!;
        string source = libPath ?? binPath ??
            (File.Exists(Path.Combine(root, "src", "main.rs")) ? Path.Combine("src", "main.rs") : Path.Combine("src", "lib.rs"));
        source = Path.GetFullPath(Path.Combine(root, source));
        if (!File.Exists(source)) Fail(CargoWorkspace.ManifestDiagnostic, $"Cargo package source '{source}' does not exist.", path);
        string defaultLibrary = Path.Combine(root, "src", "lib.rs");
        string? library = libPath is not null ? Path.GetFullPath(Path.Combine(root, libPath)) : File.Exists(defaultLibrary) ? defaultLibrary : null;
        var targets = new List<CargoTarget>();
        if (library is not null) targets.Add(new(CargoTargetKind.Library, name!.Replace('-', '_'), library, Array.Empty<string>(), default));
        if (binPath is not null || libPath is null && File.Exists(Path.Combine(root, "src", "main.rs")))
            targets.Add(new(CargoTargetKind.Binary, binName ?? name!, Path.GetFullPath(Path.Combine(root, binPath ?? "src/main.rs")), Array.Empty<string>(), default));
        return new(path, name!, version!, edition ?? "2015", source, dependencies.AsReadOnly())
        { LibrarySourcePath = library, Targets = targets.AsReadOnly() };
    }

    private string ReadManifest(string path)
    {
        _currentPath = path;
        _budget.Step(path, default);
        if (!File.Exists(path)) Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest does not exist.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > options.MaximumManifestBytes) Fail(CargoWorkspace.LimitDiagnostic, "Cargo manifest exceeds its byte limit.", path);
        byte[] bytes = new byte[(int)length];
        int total = 0;
        for (int read = 0; read <= bytes.Length && total < bytes.Length; read++)
        {
            _budget.Check(path, default);
            int count = stream.Read(bytes, total, Math.Min(4096, bytes.Length - total));
            if (count == 0) Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest changed during reading.", path);
            total += count;
        }
        _budget.Check(path, default);
        if (stream.ReadByte() != -1 || stream.Length != length) Fail(CargoWorkspace.ManifestDiagnostic, "Cargo manifest changed during reading.", path);
        // Match the previous File.ReadAllLines UTF-8 reader's BOM detection, within the checked byte budget.
        using var memory = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(memory, StrictUtf8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        _budget.Check(path, default);
        return text;
    }

    private static string? ParseScalar(string value) => value.Length >= 2 &&
        (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')
        ? value[1..^1] : value.Contains('[') || value.Contains('{') ? null : value;

    private static string? ExtractPath(string value)
    {
        int path = value.IndexOf("path", StringComparison.Ordinal);
        if (path < 0) return null;
        int quote = value.IndexOf('"', path);
        if (quote < 0) return null;
        int end = value.IndexOf('"', quote + 1);
        return end > quote ? value[(quote + 1)..end] : null;
    }

    private static void Fail(string code, string message, string path) => throw new CargoLoadException(code, message, path, default);
}
