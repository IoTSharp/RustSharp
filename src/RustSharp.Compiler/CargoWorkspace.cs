using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>Bounded Cargo manifest and local path dependency discovery for the safe-core workspace.</summary>
public static class CargoWorkspace
{
    public const string ManifestDiagnostic = "RSCARGO1001";
    public const string UnsupportedDependencyDiagnostic = "RSCARGO1002";
    public const string DependencyCycleDiagnostic = "RSCARGO1003";
    public const string LimitDiagnostic = "RSCARGO1004";

    public static CargoWorkspaceResult Load(string manifestPath, CargoWorkspaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        options ??= new CargoWorkspaceOptions();
        options = options with
        {
            MaximumPackages = Math.Clamp(options.MaximumPackages, 1, 256),
            MaximumManifestBytes = Math.Clamp(options.MaximumManifestBytes, 1, 4_000_000),
            MaximumOperations = Math.Clamp(options.MaximumOperations, 1, 100_000)
        };
        var loader = new Loader(Path.GetFullPath(manifestPath), options, cancellationToken);
        return loader.Run();
    }

    private sealed class Loader(string manifestPath, CargoWorkspaceOptions options, CancellationToken cancellationToken)
    {
        private readonly List<CargoPackage> _packages = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
        private int _operations;

        internal CargoWorkspaceResult Run()
        {
            try
            {
                LoadPackage(manifestPath);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Add(LimitDiagnostic, "Cargo manifest loading exceeded its time budget.", manifestPath);
            }
            catch (IOException exception) { Add(ManifestDiagnostic, exception.Message, manifestPath); }
            catch (UnauthorizedAccessException exception) { Add(ManifestDiagnostic, exception.Message, manifestPath); }
            catch (DecoderFallbackException exception) { Add(ManifestDiagnostic, "Cargo manifest is not valid UTF-8: " + exception.Message, manifestPath); }
            catch (ArgumentException exception) { Add(ManifestDiagnostic, exception.Message, manifestPath); }
            return new CargoWorkspaceResult(_packages.AsReadOnly(), _diagnostics.AsReadOnly());
        }

        private void LoadPackage(string path)
        {
            Step();
            string full = Path.GetFullPath(path);
            if (_packages.Any(p => PathsEqual(p.ManifestPath, full))) return;
            if (_active.Contains(full))
            {
                Add(DependencyCycleDiagnostic, "Cargo path dependency cycle detected.", full);
                return;
            }
            if (_packages.Count >= options.MaximumPackages) { Add(LimitDiagnostic, "Cargo package count exceeded its limit.", full); return; }
            _active.Add(full);
            try
            {
                CargoPackage package = Parse(full);
                _packages.Add(package);
                foreach (CargoDependency dependency in package.Dependencies)
                {
                    Step();
                    if (dependency.Path is null)
                    {
                        Add(UnsupportedDependencyDiagnostic,
                            $"Dependency '{dependency.Name}' has no local path and cannot be loaded by the safe-core profile.", full);
                        continue;
                    }
                    string dependencyManifest = Path.Combine(Path.GetDirectoryName(full)!, dependency.Path, "Cargo.toml");
                    LoadPackage(dependencyManifest);
                }
            }
            finally { _active.Remove(full); }
        }

        private CargoPackage Parse(string path)
        {
            Step();
            if (!File.Exists(path)) throw new IOException($"Cargo manifest '{path}' does not exist.");
            FileInfo info = new(path);
            if (info.Length > options.MaximumManifestBytes) { Add(LimitDiagnostic, "Cargo manifest exceeds its byte limit.", path); return CargoPackage.Empty(path); }
            string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
            string section = string.Empty;
            string? name = null, version = null, edition = null, libPath = null;
            string? binName = null, binPath = null;
            var dependencies = new List<CargoDependency>();
            foreach (string raw in lines)
            {
                Step();
                string line = raw.Split('#', 2)[0].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
                { section = line[2..^2].Trim(); continue; }
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                int equals = line.IndexOf('=');
                if (equals <= 0) { Add(ManifestDiagnostic, $"Invalid Cargo manifest line '{raw}'.", path); continue; }
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
                    string? dependencyPath = ExtractPath(value);
                    dependencies.Add(new CargoDependency(key, dependencyPath));
                }
            }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                Add(ManifestDiagnostic, "Cargo manifest requires [package].name and [package].version.", path);
            string root = Path.GetDirectoryName(path)!;
            string source = libPath ?? binPath ??
                (File.Exists(Path.Combine(root, "src", "main.rs")) ? Path.Combine("src", "main.rs") : Path.Combine("src", "lib.rs"));
            source = Path.GetFullPath(Path.Combine(root, source));
            if (!File.Exists(source)) Add(ManifestDiagnostic, $"Cargo package source '{source}' does not exist.", path);
            string defaultLibrary = Path.Combine(root, "src", "lib.rs");
            string? library = libPath is not null ? Path.GetFullPath(Path.Combine(root, libPath))
                : File.Exists(defaultLibrary) ? defaultLibrary : null;
            return new CargoPackage(path, name ?? string.Empty, version ?? string.Empty, edition ?? "2015", source,
                dependencies.AsReadOnly()) { LibrarySourcePath = library };
        }

        private void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_operations > options.MaximumOperations) throw new OperationCanceledException();
        }

        private void Add(string code, string message, string path) => _diagnostics.Add(new Diagnostic(code, message, new TextSpan(0, 0)) { SourcePath = path });
        private static bool PathsEqual(string a, string b) => OperatingSystem.IsWindows() ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase) : string.Equals(a, b, StringComparison.Ordinal);
        private static string? ParseScalar(string value) => value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')) ? value[1..^1] : value.Contains('[') || value.Contains('{') ? null : value;
        private static string? ExtractPath(string value)
        {
            int pathIndex = value.IndexOf("path", StringComparison.Ordinal);
            if (pathIndex < 0) return value.StartsWith('"') ? null : null;
            int quote = value.IndexOf('"', pathIndex);
            if (quote < 0) return null;
            int end = value.IndexOf('"', quote + 1);
            return end > quote ? value[(quote + 1)..end] : null;
        }
    }
}

public sealed record CargoWorkspaceOptions
{
    public int MaximumPackages { get; init; } = 64;
    public int MaximumManifestBytes { get; init; } = 1_000_000;
    public int MaximumOperations { get; init; } = 20_000;
}

public sealed record CargoDependency(string Name, string? Path);

public sealed record CargoPackage(string ManifestPath, string Name, string Version, string Edition,
    string SourcePath, IReadOnlyList<CargoDependency> Dependencies)
{
    public string? LibrarySourcePath { get; init; }
    internal static CargoPackage Empty(string path) => new(path, string.Empty, string.Empty, "2015", string.Empty, Array.Empty<CargoDependency>());
}

public sealed class CargoWorkspaceResult
{
    internal CargoWorkspaceResult(IReadOnlyList<CargoPackage> packages, IReadOnlyList<Diagnostic> diagnostics)
    { Packages = packages; Diagnostics = diagnostics; }
    public IReadOnlyList<CargoPackage> Packages { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsSuccessful => Diagnostics.Count == 0 && Packages.Count != 0;
    public CargoPackage RootPackage => Packages[0];
}
