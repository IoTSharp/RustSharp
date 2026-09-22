using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>A bounded source-linked package graph with separate Rust crate scopes.</summary>
public sealed record GenericPackageWorkspaceResult(string RootSourcePath, string SourceText,
    SafeCoreSourceMap? SourceMap, ImmutableArray<SafeCoreCrate> Crates, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => SourceMap is not null && Diagnostics.Count == 0;
}

public static class GenericPackageWorkspace
{
    public static GenericPackageWorkspaceResult Load(string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        CargoWorkspaceResult cargo = CargoWorkspace.Load(manifestPath, cancellationToken: cancellationToken);
        if (!cargo.IsSuccessful) return new(manifestPath, string.Empty, null, [], cargo.Diagnostics);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var packages = cargo.Packages.ToDictionary(static package => package.ManifestPath, comparer);
        string rootDirectory = Path.GetDirectoryName(cargo.RootPackage.ManifestPath)!;
        var scopes = new Dictionary<string, string>(comparer);
        var identities = new Dictionary<string, string>(comparer);
        var documents = new List<SafeCoreSourceDocument>();
        var documentsByPath = new Dictionary<string, SafeCoreSourceDocument>(comparer);
        var segments = new List<SafeCoreSourceMapSegment>();
        var source = new StringBuilder();
        var crates = ImmutableArray.CreateBuilder<SafeCoreCrate>();
        try
        {
            foreach (CargoPackage package in cargo.Packages)
            {
                Step();
                if (package.Name.Length is < 1 or > 128 || package.Version.Length is < 1 or > 128 ||
                    !package.Name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-') ||
                    package.Dependencies.Count > 256)
                    throw new ArgumentException("Generic package identities and direct dependency lists exceed their declared bounds.");
                string origin = Path.GetRelativePath(rootDirectory, package.ManifestPath).Replace('\\', '/');
                string identity = package.Name + "@" + package.Version + "#" +
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)));
                identities.Add(package.ManifestPath, identity);
                scopes.Add(package.ManifestPath, comparer.Equals(package.ManifestPath, cargo.RootPackage.ManifestPath)
                    ? "crate" : "crate::__rsc_pkg_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
            }
            var active = new HashSet<string>(comparer);
            var visited = new HashSet<string>(comparer);
            Visit(cargo.RootPackage, 0);
            foreach (CargoPackage package in cargo.Packages)
            {
                Step();
                var dependencies = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                foreach (CargoDependency dependency in package.Dependencies)
                {
                    Step();
                    string alias = dependency.Name.Replace('-', '_');
                    if (alias.Length is < 1 or > 256 || !alias.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') || char.IsAsciiDigit(alias[0]) ||
                        !dependencies.TryAdd(alias, scopes[DependencyPath(package, dependency)]))
                        throw new ArgumentException("Cargo dependency aliases must be distinct bounded Rust identifiers.");
                }
                crates.Add(new(scopes[package.ManifestPath], identities[package.ManifestPath], dependencies.ToImmutable()));
            }
            // Root source comes first so entry-point spans and primary source bytes retain their usual meaning.
            foreach (CargoPackage package in cargo.Packages.OrderBy(package => package == cargo.RootPackage ? "" : scopes[package.ManifestPath], StringComparer.Ordinal))
            {
                Step();
                bool root = package == cargo.RootPackage;
                string entry = root ? package.SourcePath : package.LibrarySourcePath ??
                    throw new ArgumentException("A path dependency must provide a library target.");
                SafeCoreWorkspaceResult workspace = SafeCoreWorkspace.Load(entry, cancellationToken: cancellationToken);
                if (!workspace.IsSuccessful) return new(cargo.RootPackage.SourcePath, string.Empty, null, [], workspace.Diagnostics);
                SafeCoreSourceMap map = workspace.SourceMap!;
                if (documents.Count + map.Documents.Count > 1024 ||
                    documents.Sum(static document => (long)document.Bytes.Length) + map.Documents.Sum(static document => (long)document.Bytes.Length) > 32_000_000)
                    throw new TimeoutException("Package sources exceeded their document or byte limit.");
                foreach (SafeCoreSourceDocument document in map.Documents)
                {
                    Step();
                    if (documentsByPath.TryGetValue(document.Path, out SafeCoreSourceDocument? existing))
                    {
                        if (existing.Text != document.Text || !existing.Bytes.Span.SequenceEqual(document.Bytes.Span))
                            throw new IOException("A shared package source changed during linking.");
                    }
                    else { documentsByPath.Add(document.Path, document); documents.Add(document); }
                }
                SafeCoreSourceDocument origin = documentsByPath[map.Documents[0].Path];
                if (!root) AppendSynthetic("\npub mod " + scopes[package.ManifestPath][7..] + " {\n", origin);
                int offset = source.Length;
                string text = workspace.SourceText;
                if (!root && text.StartsWith("#!", StringComparison.Ordinal) && !text.StartsWith("#![", StringComparison.Ordinal))
                {
                    int newline = text.IndexOf('\n');
                    if (newline < 0) newline = text.Length;
                    text = new string(' ', newline) + text[newline..];
                }
                source.Append(text);
                foreach (SafeCoreSourceMapSegment segment in map.Segments)
                {
                    Step();
                    segments.Add(segment with { ExpandedSpan = new(segment.ExpandedSpan.Start + offset, segment.ExpandedSpan.Length),
                        Document = documentsByPath[segment.Document.Path] });
                }
                if (!root) AppendSynthetic("\n}\n", origin);
                Step();
            }
            return new(cargo.RootPackage.SourcePath, source.ToString(),
                new SafeCoreSourceMap(documents, segments, source.Length, cancellationToken), crates.ToImmutable(), []);

            void Visit(CargoPackage package, int depth)
            {
                Step();
                if (depth > 64 || !active.Add(package.ManifestPath)) throw new ArgumentException("Cargo path dependencies require a finite acyclic package graph.");
                if (visited.Add(package.ManifestPath))
                    foreach (CargoDependency dependency in package.Dependencies) Visit(packages[DependencyPath(package, dependency)], depth + 1);
                active.Remove(package.ManifestPath);
            }
            void AppendSynthetic(string text, SafeCoreSourceDocument origin)
            {
                Step();
                segments.Add(new(new(source.Length, text.Length), origin, new(0, 0), true));
                source.Append(text);
            }
            void Step()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(20) || source.Length > 1_000_000 || segments.Count > 100_000)
                    throw new TimeoutException("Generic package linking exceeded its source, work or time budget.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            return new(cargo.RootPackage.SourcePath, string.Empty, null, [],
                [new Diagnostic(exception is TimeoutException ? "RSG0002" : "RSG1001", exception.Message, new(0, 0)) { SourcePath = manifestPath }]);
        }
    }

    private static string DependencyPath(CargoPackage package, CargoDependency dependency) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(package.ManifestPath)!, dependency.Path!, "Cargo.toml"));
}
