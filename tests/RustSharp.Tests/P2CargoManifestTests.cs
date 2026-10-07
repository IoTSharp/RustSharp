using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P2CargoManifestTests
{
    private const string Prefix = "P2 cargo manifests ";
    private const string PackageHeader = "[package]\nname = \"app\"\nversion = \"0.1.0\"\n";
    private static readonly int[] ManifestByteLimits = [512, 1_000_000];
    private static readonly string FixtureManifestPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifests-cases.json"));
    private static readonly string ContractPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json"));

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new(Prefix + "package-minimal", PackageMinimal),
        new(Prefix + "package-metadata", PackageMetadata),
        new(Prefix + "workspace-members", WorkspaceMembers),
        new(Prefix + "workspace-exclude", WorkspaceExclude),
        new(Prefix + "workspace-resolver", WorkspaceResolver),
        new(Prefix + "virtual-workspace", VirtualWorkspace),
        new(Prefix + "combined-workspace", CombinedWorkspace),
        new(Prefix + "default-targets", DefaultTargets),
        new(Prefix + "explicit-lib", ExplicitLib),
        new(Prefix + "explicit-bin", ExplicitBin),
        new(Prefix + "bin-required-features", BinRequiredFeatures),
        new(Prefix + "dependency-inline", DependencyInline),
        new(Prefix + "dependency-table", DependencyTable),
        new(Prefix + "dependency-rename", DependencyRename),
        new(Prefix + "dependency-version", DependencyVersion),
        new(Prefix + "dependency-order", DependencyOrder),
        new(Prefix + "malformed-toml", MalformedToml),
        new(Prefix + "duplicate-key", DuplicateKey),
        new(Prefix + "duplicate-package", DuplicatePackage),
        new(Prefix + "missing-member", MissingMember),
        new(Prefix + "missing-source", MissingSource),
        new(Prefix + "unknown-key", UnknownKey),
        new(Prefix + "registry-dependency", RegistryDependency),
        new(Prefix + "git-dependency", GitDependency),
        new(Prefix + "path-escape", PathEscape),
        new(Prefix + "quoted-hash", QuotedHash),
        new(Prefix + "utf8-invalid", Utf8Invalid),
        new(Prefix + "unsupported-value", UnsupportedValue),
        new(Prefix + "edition-unsupported", EditionUnsupported),
        new(Prefix + "resolver-unsupported", ResolverUnsupported),
        new(Prefix + "package-count-budget", PackageCountBudget),
        new(Prefix + "manifest-byte-budget", ManifestByteBudget),
        new(Prefix + "dependency-count-budget", DependencyCountBudget),
        new(Prefix + "target-count-budget", TargetCountBudget),
        new(Prefix + "graph-depth-budget", GraphDepthBudget),
        new(Prefix + "operation-budget", OperationBudget),
        new(Prefix + "deadline-budget", DeadlineBudget),
        new(Prefix + "cancelled-resolution", CancelledResolution),
    ];

    private static Task PackageMinimal() => WithFixture(f =>
    {
        ValidateInventory(f);
        f.Package("", "app");
        CargoWorkspaceResult result = f.Load();
        Success(result);
        AssertEx.Equal(1, result.Packages.Count);
        AssertEx.Equal("app", result.RootPackage.Name);
        AssertEx.Equal("0.1.0", result.RootPackage.Version);
        AssertEx.Equal("2015", result.RootPackage.Edition);
        AssertEx.Equal(f.PathOf("Cargo.toml"), result.RootManifestPath);
        AssertEx.False(result.IsVirtualWorkspace, "A package root must remain a package root.");
        AssertEx.True(ReferenceEquals(result.RootPackage, result.RootPackageOrNull), "Root identity must be explicit.");
        Variants(f, ["_app", "app-name_1"], name =>
        {
            f.Package("", name);
            AssertEx.Equal(name, SuccessfulPackage(f.Load()).Name);
        });
        Variants(f, ["9bad", "has space", "café", "-bad"], name =>
        {
            f.Write("Cargo.toml", $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\n");
            Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
        });
        Variants(f, ["1.2", "1.2.3-alpha", "v1.2.3", "1.2.3.4"], version =>
        {
            f.Write("Cargo.toml", $"[package]\nname = \"app\"\nversion = \"{version}\"\n");
            Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
        });
    });

    private static Task PackageMetadata() => WithFixture(f =>
    {
        f.Package("", "app");
        Success(f.Load()); // The single input trial precedes the fixed four-edition batch.
        Variants(f, ["2015", "2018", "2021", "2024"], edition =>
        {
            f.Write("Cargo.toml", PackageHeader + $"edition = \"{edition}\"\n");
            CargoWorkspaceResult result = f.Load();
            Success(result);
            AssertEx.Equal(edition, result.RootPackage.Edition);
            AssertEx.Equal("0.1.0", result.RootPackage.Version);
        });
    });

    private static Task WorkspaceMembers() => WithFixture(f =>
    {
        f.Package("z", "zeta"); f.Package("a", "alpha");
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"z\", \"a\", \"a/.\"]\n");
        CargoWorkspaceResult result = f.Load();
        Success(result);
        AssertEx.Equal(2, result.Packages.Count);
        Sequence([f.PathOf("a/Cargo.toml"), f.PathOf("z/Cargo.toml")], result.WorkspaceMemberPaths);
        Sequence(["alpha", "zeta"], result.Packages.Select(p => p.Name));
    });

    private static Task WorkspaceExclude() => WithFixture(f =>
    {
        f.Package("a", "alpha"); f.Package("b", "beta");
        f.Write("b/Cargo.toml", "malformed excluded package");
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"b\", \"a\"]\nexclude = [\"b\"]\n");
        CargoWorkspaceResult result = f.Load();
        Success(result);
        Sequence(["alpha"], result.Packages.Select(p => p.Name));
        Sequence([f.PathOf("a/Cargo.toml")], result.WorkspaceMemberPaths);
        Variants(f, ["members = [\"*\"]", "members = [\"a\"]\nexclude = [\"b*\"]"], text =>
        {
            f.Write("Cargo.toml", "[workspace]\n" + text + "\n");
            Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
        });
    });

    private static Task WorkspaceResolver() => WithFixture(f =>
    {
        f.Package("member", "member");
        Variants(f, ["", "resolver = \"2\"\n"], resolver =>
        {
            f.Write("Cargo.toml", "[workspace]\nmembers = [\"member\"]\n" + resolver);
            Success(f.Load());
        });
    });

    private static Task VirtualWorkspace() => WithFixture(f =>
    {
        f.Package("member", "member");
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"member\"]\n");
        CargoWorkspaceResult result = f.Load();
        Success(result);
        AssertEx.True(result.IsVirtualWorkspace && result.RootPackageOrNull is null, "Virtual roots have no synthetic package.");
        AssertEx.Equal(1, result.Packages.Count);
        AssertEx.Throws<InvalidOperationException>(() => _ = result.RootPackage);
        Failure(CargoWorkspace.Load(f.PathOf("Cargo.toml"), cancellationToken: f.Token), CargoWorkspace.UnsupportedManifestDiagnostic);
        f.Write("Cargo.toml", "[workspace]\nmembers = []\n");
        CargoWorkspaceResult empty = f.Load();
        Success(empty);
        AssertEx.True(empty.IsVirtualWorkspace && empty.RootPackageOrNull is null, "Empty virtual roots are still valid workspace metadata.");
        AssertEx.Equal(0, empty.Packages.Count);
    });

    private static Task CombinedWorkspace() => WithFixture(f =>
    {
        f.Package("", "root"); f.Package("child", "child");
        f.Write("Cargo.toml", "[package]\nname = \"root\"\nversion = \"0.1.0\"\n[workspace]\nmembers = [\"child\", \".\", \"./\"]\n");
        CargoWorkspaceResult result = f.Load();
        Success(result);
        AssertEx.False(result.IsVirtualWorkspace, "A combined root retains its actual package.");
        AssertEx.Equal("root", result.RootPackage.Name);
        AssertEx.Equal(2, result.Packages.Count);
        AssertEx.Equal(1, result.Packages.Count(p => p.ManifestPath == f.PathOf("Cargo.toml")));
    });

    private static Task DefaultTargets() => WithFixture(f =>
    {
        f.Package("", "app"); f.Write("src/lib.rs", "pub fn value() -> i32 { 1 }");
        CargoPackage package = SuccessfulPackage(f.Load());
        AssertEx.Equal(2, package.Targets.Count);
        AssertEx.Equal(CargoTargetKind.Library, package.Targets[0].Kind);
        AssertEx.Equal(CargoTargetKind.Binary, package.Targets[1].Kind);
        AssertEx.Equal(f.PathOf("src/lib.rs"), package.LibrarySourcePath!);
        AssertEx.Equal(f.PathOf("src/main.rs"), package.SourcePath);
        f.DeleteFile("src/main.rs");
        package = SuccessfulPackage(f.Load());
        AssertEx.Equal(1, package.Targets.Count);
        AssertEx.Equal(f.PathOf("src/lib.rs"), package.SourcePath);
    });

    private static Task ExplicitLib() => WithFixture(f =>
    {
        f.Write("custom/library.rs", "pub fn value() -> i32 { 1 }");
        const string text = PackageHeader + "[lib]\nname = \"custom_lib\"\npath = \"custom/library.rs\"\ncrate-type = [\"rlib\"]\n";
        f.Write("Cargo.toml", text);
        CargoPackage package = SuccessfulPackage(f.Load());
        CargoTarget target = package.Targets.Single();
        AssertEx.Equal(CargoTargetKind.Library, target.Kind);
        AssertEx.Equal("custom_lib", target.Name);
        AssertEx.Equal(f.PathOf("custom/library.rs"), target.SourcePath);
        AssertEx.Equal(target.SourcePath, package.SourcePath);
        AssertEx.Equal(target.SourcePath, package.LibrarySourcePath!);
        AssertEx.Equal(new TextSpan(text.IndexOf("[lib]", StringComparison.Ordinal), 5), target.DeclarationSpan);
        f.Write("Cargo.toml", text.Replace("rlib", "cdylib", StringComparison.Ordinal));
        Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
    });

    private static Task ExplicitBin() => WithFixture(f =>
    {
        f.Write("a.rs", "fn main() {}"); f.Write("z.rs", "fn main() {}");
        f.Write("Cargo.toml", PackageHeader + "[[bin]]\nname = \"zeta\"\npath = \"z.rs\"\n[[bin]]\nname = \"alpha\"\npath = \"a.rs\"\n");
        CargoPackage package = SuccessfulPackage(f.Load());
        Sequence(["alpha", "zeta"], package.Targets.Select(t => t.Name));
        Sequence([f.PathOf("a.rs"), f.PathOf("z.rs")], package.Targets.Select(t => t.SourcePath));
        AssertEx.True(package.Targets.All(t => t.Kind == CargoTargetKind.Binary), "Both binary tables must survive.");
        AssertEx.Equal(f.PathOf("a.rs"), package.SourcePath);
        f.Write("Cargo.toml", PackageHeader + "[[bin]]\nname = \"same\"\npath = \"z.rs\"\n[[bin]]\nname = \"same\"\npath = \"a.rs\"\n");
        Failure(f.Load(), CargoWorkspace.DuplicateIdentityDiagnostic);
    });

    private static Task BinRequiredFeatures() => WithFixture(f =>
    {
        f.Write("app.rs", "fn main() {}");
        f.Write("Cargo.toml", PackageHeader + "[[bin]]\nname = \"app\"\npath = \"app.rs\"\nrequired-features = [\"gui\", \"logging\"]\n[features]\ndefault = [\"gui\"]\ngui = []\nlogging = []\n");
        CargoPackage package = SuccessfulPackage(f.Load());
        Sequence(["gui", "logging"], package.Targets.Single().RequiredFeatures);
        Sequence(["gui"], package.Features["default"]);
        AssertEx.Equal(0, package.Features["gui"].Count);
        AssertEx.Equal(0, package.Features["logging"].Count);
    });

    private static Task DependencyInline() => WithFixture(f =>
    {
        f.Package("", "app"); f.Package("dep", "dep");
        const string text = PackageHeader + "[dependencies]\ndep = { path = \"dep\", version = \"0.1.0\", features = [\"std\", \"extra\"], optional = true, default-features = false }\n";
        f.Write("Cargo.toml", text);
        CargoWorkspaceResult result = f.Load();
        Success(result);
        AssertEx.Equal(2, result.Packages.Count);
        CargoDependency dependency = result.RootPackage.Dependencies.Single();
        AssertEx.Equal("dep", dependency.Name);
        AssertEx.Equal("dep", dependency.Path!);
        AssertEx.Equal("0.1.0", dependency.Version!);
        AssertEx.True(dependency.Optional, "Optional nodes are retained for later selection.");
        AssertEx.False(dependency.DefaultFeatures, "Explicit default-features=false must survive parsing.");
        Sequence(["std", "extra"], dependency.Features);
        AssertEx.Equal(f.PathOf("dep/Cargo.toml"), dependency.ResolvedManifestPath!);
        AssertEx.Equal(new TextSpan(text.IndexOf("\"dep\"", StringComparison.Ordinal), 5), dependency.PathSpan);
    });

    private static Task DependencyTable() => WithFixture(f =>
    {
        f.Package("", "app"); f.Package("dep", "dep");
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\ndep = { path = \"dep\", package = \"dep\", version = \"0.1.0\", features = [\"std\"], optional = false, default-features = true }\n");
        string inline = GraphSnapshot(Successful(f.Load()), f);
        f.Write("Cargo.toml", PackageHeader + "[dependencies.dep]\npath = \"dep\"\npackage = \"dep\"\nversion = \"0.1.0\"\nfeatures = [\"std\"]\noptional = false\ndefault-features = true\n");
        AssertEx.Equal(inline, GraphSnapshot(Successful(f.Load()), f));
        f.Write("Cargo.toml", PackageHeader + "[target.'cfg(windows)'.dependencies.dep]\npath = \"dep\"\nfeatures = [\"std\"]\noptional = true\ndefault-features = false\n");
        CargoDependency conditional = SuccessfulPackage(f.Load()).Dependencies.Single();
        AssertEx.Equal("cfg(windows)", conditional.CfgCondition!);
        AssertEx.True(conditional.Optional && !conditional.DefaultFeatures, "Cfg dependency metadata remains lexical in the manifest phase.");
    });

    private static Task DependencyRename() => WithFixture(f =>
    {
        f.Package("", "app"); f.Package("dep", "actual_name");
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\nalias = { path = \"dep\", package = \"actual_name\" }\n");
        CargoWorkspaceResult result = Successful(f.Load());
        CargoDependency dependency = result.RootPackage.Dependencies.Single();
        AssertEx.Equal("alias", dependency.Name);
        AssertEx.Equal("actual_name", dependency.PackageName!);
        AssertEx.True(result.Packages.Any(p => p.Name == "actual_name"), "Renamed edge resolves its package identity.");
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\nalias = { path = \"dep\", package = \"wrong_name\" }\n");
        Failure(f.Load(), CargoWorkspace.DuplicateIdentityDiagnostic);
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\nalias = { path = \"dep\" }\n");
        Failure(f.Load(), CargoWorkspace.DuplicateIdentityDiagnostic);
        CargoWorkspaceResult legacy = CargoWorkspace.Load(f.PathOf("Cargo.toml"), cancellationToken: f.Token);
        Success(legacy);
        AssertEx.Equal("alias", legacy.RootPackage.Dependencies.Single().Name);
        AssertEx.True(legacy.Packages.Any(p => p.Name == "actual_name"), "The explicitly retained legacy entry point supports an implicit package alias.");
    });

    private static Task DependencyVersion() => WithFixture(f =>
    {
        f.Package("", "app"); f.Package("dep", "dep");
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\ndep = { path = \"dep\", version = \"0.1.0\" }\n");
        AssertEx.Equal("0.1.0", SuccessfulPackage(f.Load()).Dependencies.Single().Version!);
        Variants(f, ["^0.1", ">=0.1.0", "*", "0.1"], version =>
        {
            f.Write("Cargo.toml", PackageHeader + $"[dependencies]\ndep = {{ path = \"dep\", version = \"{version}\" }}\n");
            Failure(f.Load(), CargoWorkspace.UnsupportedDependencyDiagnostic);
        });
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\ndep = { path = \"dep\", version = \"0.2.0\" }\n");
        Failure(f.Load(), CargoWorkspace.DuplicateIdentityDiagnostic);
    });

    private static Task DependencyOrder() => WithFixture(f =>
    {
        f.Package("", "app"); f.Package("z", "zeta"); f.Package("a", "alpha");
        f.Write("a.rs", "fn main() {}"); f.Write("z.rs", "fn main() {}");
        const string alpha = "alpha = { path = \"a\" }\n", zeta = "zeta = { path = \"z\" }\n";
        const string aBin = "[[bin]]\nname = \"alpha\"\npath = \"a.rs\"\n", zBin = "[[bin]]\nname = \"zeta\"\npath = \"z.rs\"\n";
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\n" + zeta + alpha + zBin + aBin);
        CargoWorkspaceResult first = Successful(f.Load());
        string snapshot = GraphSnapshot(first, f);
        Sequence(["alpha", "app", "zeta"], first.Packages.Select(p => p.Name));
        Sequence(["alpha", "zeta"], first.RootPackage.Dependencies.Select(d => d.Name));
        f.Write("Cargo.toml", PackageHeader + "[dependencies]\n" + alpha + zeta + aBin + zBin);
        AssertEx.Equal(snapshot, GraphSnapshot(Successful(f.Load()), f));
    });

    private static Task MalformedToml() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        Variants(f, ["name = \"unterminated", "[package", "[[bin]", "[package] trailing"], bad =>
        {
            string text = "# first line\r\n\r\n" + (bad.StartsWith("name", StringComparison.Ordinal) ? "[package]\r\n" : "") + bad + "\r\n";
            f.Write("Cargo.toml", text);
            string token = bad.StartsWith("name", StringComparison.Ordinal) ? "\"unterminated" : bad;
            AtToken(Failure(f.Load(), CargoWorkspace.ManifestDiagnostic), f.PathOf("Cargo.toml"), text, token);
        });
    });

    private static Task DuplicateKey() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        const string text = "# café\r\n[package]\r\nname = \"app\"\r\nversion = \"0.1.0\"\r\nname = \"other\"\r\n";
        f.Write("Cargo.toml", text);
        AtToken(Failure(f.Load(), CargoWorkspace.ManifestDiagnostic), f.PathOf("Cargo.toml"), text, "name", last: true);
        const string table = PackageHeader + "\n[package]\n";
        f.Write("Cargo.toml", table);
        AtToken(Failure(f.Load(), CargoWorkspace.ManifestDiagnostic), f.PathOf("Cargo.toml"), table, "[package]", last: true);
        f.Package("dep", "dep");
        const string inline = PackageHeader + "[dependencies]\ndep = { path = \"dep\", path = \"dep\" }\n";
        f.Write("Cargo.toml", inline);
        AtToken(Failure(f.Load(), CargoWorkspace.ManifestDiagnostic), f.PathOf("Cargo.toml"), inline, "path", last: true);
    });

    private static Task DuplicatePackage() => WithFixture(f =>
    {
        f.Package("a", "same"); f.Package("b", "same");
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"a\", \"b\"]\n");
        Failure(f.Load(), CargoWorkspace.DuplicateIdentityDiagnostic);
        f.Write("b/Cargo.toml", "[package]\nname = \"same\"\nversion = \"0.2.0\"\n");
        AssertEx.Equal(2, Successful(f.Load()).Packages.Count);
    });

    private static Task MissingMember() => WithFixture(f =>
    {
        f.Package("existing", "existing");
        const string text = "# original line\r\n[workspace]\r\nmembers = [\"existing\", \"absent\"]\r\n";
        f.Write("Cargo.toml", text);
        AtToken(Failure(f.Load(), CargoWorkspace.MissingInputDiagnostic), f.PathOf("Cargo.toml"), text, "\"absent\"");
        const string dependency = PackageHeader + "[dependencies]\nabsent = { path = \"missing\" }\n";
        f.Write("src/main.rs", "fn main() {}"); f.Write("Cargo.toml", dependency);
        AtToken(Failure(f.Load(), CargoWorkspace.MissingInputDiagnostic), f.PathOf("Cargo.toml"), dependency, "\"missing\"");
    });

    private static Task MissingSource() => WithFixture(f =>
    {
        f.Write("Cargo.toml", PackageHeader);
        AtToken(Failure(f.Load(), CargoWorkspace.MissingInputDiagnostic), f.PathOf("Cargo.toml"), PackageHeader, "[package]");
        const string text = PackageHeader + "[lib]\npath = \"missing.rs\"\n";
        f.Write("Cargo.toml", text);
        AtToken(Failure(f.Load(), CargoWorkspace.MissingInputDiagnostic), f.PathOf("Cargo.toml"), text, "\"missing.rs\"");
        const string binary = PackageHeader + "[[bin]]\nname = \"absent\"\npath = \"missing-bin.rs\"\n";
        f.Write("Cargo.toml", binary);
        AtToken(Failure(f.Load(), CargoWorkspace.MissingInputDiagnostic), f.PathOf("Cargo.toml"), binary, "\"missing-bin.rs\"");
    });

    private static Task UnknownKey() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load()); f.Package("dep", "dep");
        (string Tail, string Token)[] cases =
        [
            ("authors = [\"someone\"]\n", "authors"),
            ("[workspace]\nunknown = []\n", "unknown"),
            ("[lib]\nmagic = true\n", "magic"),
            ("[[bin]]\nmagic = true\n", "magic"),
            ("[dependencies]\ndep = { path = \"dep\", mystery = false }\n", "mystery"),
            ("[dependencies.dep]\npath = \"dep\"\nmystery = false\n", "mystery"),
            ("[profile.release]\n", "[profile.release]"),
            ("[dev-dependencies]\n", "[dev-dependencies]"),
        ];
        AssertEx.True(cases.Length <= 10, "Unknown-member variants stay bounded.");
        for (int index = 0; index < cases.Length && index < 10; index++)
        {
            f.Check();
            string text = PackageHeader + cases[index].Tail;
            f.Write("Cargo.toml", text);
            AtToken(Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic), f.PathOf("Cargo.toml"), text, cases[index].Token);
        }
    });

    private static Task RegistryDependency() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        Variants(f, ["serde = \"1.0.0\"", "serde = { version = \"1.0.0\" }", "[dependencies.serde]\nversion = \"1.0.0\""], declaration =>
        {
            f.Write("Cargo.toml", PackageHeader + "[dependencies]\n" + declaration + "\n");
            Failure(f.Load(), CargoWorkspace.UnsupportedDependencyDiagnostic);
        });
    });

    private static Task GitDependency() => WithFixture(f =>
    {
        f.Package("", "app"); f.Package("dep", "dep"); Success(f.Load());
        Variants(f, ["git = \"https://invalid.example/dep.git\"", "registry = \"private\"", "source = \"registry+https://invalid.example\""], origin =>
        {
            f.Write("Cargo.toml", PackageHeader + "[dependencies]\ndep = { path = \"dep\", " + origin + " }\n");
            Failure(f.Load(), CargoWorkspace.UnsupportedDependencyDiagnostic);
        });
        f.Write("Cargo.toml", PackageHeader + "[dependencies.dep]\ngit = \"https://invalid.example/dep.git\"\n");
        Failure(f.Load(), CargoWorkspace.UnsupportedDependencyDiagnostic);
    });

    private static Task PathEscape() => WithFixture(f =>
    {
        f.Package("app", "app"); f.Package("dep", "dep");
        f.Write("app/Cargo.toml", PackageHeader + "[dependencies]\ndep = { path = \"../dep\" }\n");
        Success(f.Load(manifest: "app/Cargo.toml"));
        const string escape = PackageHeader + "[lib]\npath = \"../outside.rs\"\n";
        f.Write("outside.rs", "pub fn value() {}"); f.Write("app/Cargo.toml", escape);
        AtToken(Failure(f.Load(manifest: "app/Cargo.toml"), CargoWorkspace.UnsupportedManifestDiagnostic), f.PathOf("app/Cargo.toml"), escape, "\"../outside.rs\"");
        CargoWorkspaceResult legacy = CargoWorkspace.Load(f.PathOf("app/Cargo.toml"), cancellationToken: f.Token);
        Success(legacy);
        AssertEx.Equal(f.PathOf("outside.rs"), legacy.RootPackage.LibrarySourcePath!, "Legacy source linking retains its established shared-source path support.");
        f.Write("actual.rs", "pub fn value() {}"); f.FileLink("linked.rs", "actual.rs");
        f.Write("Cargo.toml", PackageHeader + "[lib]\npath = \"linked.rs\"\n");
        Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
        f.Write("actual-dir/lib.rs", "pub fn value() {}"); f.DirectoryLink("linked-dir", "actual-dir");
        f.Write("Cargo.toml", PackageHeader + "[lib]\npath = \"linked-dir/lib.rs\"\n");
        Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
        f.Package("actual-package", "actual"); f.DirectoryLink("linked-package", "actual-package");
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"linked-package\"]\n");
        Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
        f.FileLink("linked-manifest.toml", "actual-package/Cargo.toml");
        Failure(f.Load(manifest: "linked-manifest.toml"), CargoWorkspace.UnsupportedManifestDiagnostic);
    });

    private static Task QuotedHash() => WithFixture(f =>
    {
        f.Write("src/hash#equals=.rs", "pub fn value() {}");
        const string basic = PackageHeader + "[lib]\nname = \"app\"\npath = \"src/hash#equals=.rs\" # outside comment\n";
        f.Write("Cargo.toml", basic);
        AssertEx.Equal(f.PathOf("src/hash#equals=.rs"), SuccessfulPackage(f.Load()).LibrarySourcePath!);
        f.Write("Cargo.toml", basic.Replace("\"src/hash#equals=.rs\"", "'src/hash#equals=.rs'", StringComparison.Ordinal));
        AssertEx.Equal(f.PathOf("src/hash#equals=.rs"), SuccessfulPackage(f.Load()).SourcePath);
    });

    private static Task Utf8Invalid() => WithFixture(f =>
    {
        f.Package("", "app");
        f.WriteBytes("Cargo.toml", [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(PackageHeader)]);
        Success(f.Load());
        byte[][] malformed = [[0xff], [0xc0, 0xaf], [0xe2, 0x82]];
        for (int index = 0; index < malformed.Length && index < 3; index++)
        {
            f.Check(); f.WriteBytes("Cargo.toml", malformed[index]);
            Diagnostic diagnostic = Failure(f.Load(), CargoWorkspace.ManifestDiagnostic);
            AssertEx.Equal(f.PathOf("Cargo.toml"), diagnostic.SourcePath!);
        }
        f.WriteBytes("Cargo.toml", [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(PackageHeader)]);
        Failure(f.Load(), CargoWorkspace.ManifestDiagnostic);
    });

    private static Task UnsupportedValue() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load()); f.Package("dep", "dep");
        (string Text, string Token)[] cases =
        [
            ("[package]\nname = \"\"\"multiline\nvalue\"\"\"\nversion = \"0.1.0\"\n", "\"\"\"multiline"),
            (PackageHeader + "[features]\ndefault = [\n\"gui\"\n]\n", "["),
            ("package.name = \"app\"\n", "package.name"),
            ("[package]\nname = \"app\"\nversion = 2026-10-08\n", "2026-10-08"),
            ("[package]\nname = \"app\"\nversion = 1.2\n", "1.2"),
            (PackageHeader + "[dependencies]\ndep = { path = { nested = \"dep\" } }\n", "{ nested = \"dep\" } }"),
            (PackageHeader + "[dependencies]\ndep = { path = \"dep\", optional = \"true\" }\n", "\"true\""),
            (PackageHeader + "[dependencies]\ndep = { path = \"dep\", features = [false] }\n", "[false]"),
        ];
        for (int index = 0; index < cases.Length && index < 8; index++)
        {
            f.Check(); f.Write("Cargo.toml", cases[index].Text);
            Diagnostic diagnostic = Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
            if (index == 1)
                AssertEx.Equal(new TextSpan(cases[index].Text.LastIndexOf('['), 1), diagnostic.Span);
            else AtToken(diagnostic, f.PathOf("Cargo.toml"), cases[index].Text, cases[index].Token);
        }
    });

    private static Task EditionUnsupported() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        Variants(f, ["2016", "2025", "", "2021-preview"], edition =>
        {
            string token = "\"" + edition + "\"", text = PackageHeader + "edition = " + token + "\n";
            f.Write("Cargo.toml", text);
            AtToken(Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic), f.PathOf("Cargo.toml"), text, token, last: true);
        });
    });

    private static Task ResolverUnsupported() => WithFixture(f =>
    {
        f.Package("member", "member");
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"member\"]\nresolver = \"2\"\n");
        Success(f.Load());
        Variants(f, ["1", "3"], resolver =>
        {
            string text = "[workspace]\nmembers = [\"member\"]\nresolver = \"" + resolver + "\"\n";
            f.Write("Cargo.toml", text);
            AtToken(Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic), f.PathOf("Cargo.toml"), text, "\"" + resolver + "\"");
        });
        Variants(f, ["[workspace.package]", "[workspace.dependencies]", "[workspace]\ndefault-members = []"], tail =>
        {
            string text = tail + "\n"; f.Write("Cargo.toml", text);
            Failure(f.Load(), CargoWorkspace.UnsupportedManifestDiagnostic);
        });
    });

    private static Task PackageCountBudget() => WithFixture(f =>
    {
        f.Package("p00", "p00");
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"p00\"]\n");
        Success(f.Load());
        for (int index = 1; index <= 64; index++) { f.Check(); f.Package($"p{index:D2}", $"p{index:D2}"); }
        string Members(int count) => "[workspace]\nmembers = [" + string.Join(",", Enumerable.Range(0, count).Select(i => $"\"p{i:D2}\"")) + "]\n";
        f.Write("Cargo.toml", Members(64));
        AssertEx.Equal(64, Successful(f.Load()).Packages.Count);
        f.Write("Cargo.toml", Members(65));
        Failure(f.Load(), CargoWorkspace.LimitDiagnostic);
    });

    private static Task ManifestByteBudget() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        foreach (int limit in ManifestByteLimits)
        {
            f.Check();
            string text = PackageHeader + "#" + new string('x', limit - Encoding.UTF8.GetByteCount(PackageHeader) - 1);
            f.Write("Cargo.toml", text);
            AssertEx.Equal(limit, new FileInfo(f.PathOf("Cargo.toml")).Length);
            Success(f.Load(new CargoWorkspaceOptions { MaximumManifestBytes = limit }));
            f.Write("Cargo.toml", text + "x");
            Failure(f.Load(new CargoWorkspaceOptions { MaximumManifestBytes = limit }), CargoWorkspace.LimitDiagnostic);
        }
    });

    private static Task DependencyCountBudget() => WithFixture(f =>
    {
        f.Package("", "app"); f.Package("dep", "dep"); Success(f.Load());
        string Dependencies(int count) => PackageHeader + "[dependencies]\n" + string.Concat(Enumerable.Range(0, count)
            .Select(i => $"alias{i:D2} = {{ path = \"dep\", package = \"dep\" }}\n"));
        f.Write("Cargo.toml", Dependencies(64));
        CargoWorkspaceResult exact = Successful(f.Load());
        AssertEx.Equal(64, exact.RootPackage.Dependencies.Count);
        AssertEx.Equal(2, exact.Packages.Count);
        f.Write("Cargo.toml", Dependencies(65));
        Failure(f.Load(), CargoWorkspace.LimitDiagnostic);
    });

    private static Task TargetCountBudget() => WithFixture(f =>
    {
        f.Write("main.rs", "fn main() {}");
        string Targets(int count) => PackageHeader + string.Concat(Enumerable.Range(0, count)
            .Select(i => $"[[bin]]\nname = \"bin{i:D2}\"\npath = \"main.rs\"\n"));
        f.Write("Cargo.toml", Targets(1)); Success(f.Load());
        f.Write("Cargo.toml", Targets(32));
        AssertEx.Equal(32, SuccessfulPackage(f.Load()).Targets.Count);
        f.Write("Cargo.toml", Targets(33));
        Failure(f.Load(), CargoWorkspace.LimitDiagnostic);
    });

    private static Task GraphDepthBudget() => WithFixture(f =>
    {
        f.Package("n01", "n01"); Success(f.Load(manifest: "n01/Cargo.toml"));
        for (int depth = 2; depth <= 33; depth++) { f.Check(); f.Package($"n{depth:D2}", $"n{depth:D2}"); }
        for (int depth = 1; depth < 32; depth++)
        {
            f.Check();
            f.Write($"n{depth:D2}/Cargo.toml", $"[package]\nname = \"n{depth:D2}\"\nversion = \"0.1.0\"\n[dependencies]\nn{depth + 1:D2} = {{ path = \"../n{depth + 1:D2}\" }}\n");
        }
        AssertEx.Equal(32, Successful(f.Load(manifest: "n01/Cargo.toml")).Packages.Count);
        f.Write("n32/Cargo.toml", "[package]\nname = \"n32\"\nversion = \"0.1.0\"\n[dependencies]\nn33 = { path = \"../n33\" }\n");
        Failure(f.Load(manifest: "n01/Cargo.toml"), CargoWorkspace.LimitDiagnostic);
    });

    private static Task OperationBudget() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        Failure(f.Load(new CargoWorkspaceOptions { MaximumOperations = 1 }), CargoWorkspace.LimitDiagnostic);
        int low = 1, high = 20_000;
        // At most 15 halving steps find the real small-input work threshold, using actual manifest IO.
        for (int iteration = 0; low < high && iteration < 15; iteration++)
        {
            f.Check(); int middle = low + (high - low) / 2;
            CargoWorkspaceResult result = f.Load(new CargoWorkspaceOptions { MaximumOperations = middle });
            if (result.IsSuccessful) high = middle;
            else { Failure(result, CargoWorkspace.LimitDiagnostic); low = middle + 1; }
        }
        AssertEx.Equal(low, high, "The bounded search must converge within 15 iterations.");
        AssertEx.True(low > 1 && low < 20_000, "Every manifest token and graph node must consume work.");
        Success(f.Load(new CargoWorkspaceOptions { MaximumOperations = low }));
        Failure(f.Load(new CargoWorkspaceOptions { MaximumOperations = low - 1 }), CargoWorkspace.LimitDiagnostic);
    });

    private static Task DeadlineBudget() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        AssertEx.Equal(TimeSpan.FromSeconds(10), new CargoWorkspaceOptions().Timeout);
        Stopwatch clock = Stopwatch.StartNew();
        Failure(f.Load(new CargoWorkspaceOptions { Timeout = TimeSpan.FromTicks(1) }), CargoWorkspace.LimitDiagnostic);
        Failure(CargoWorkspace.Load(f.PathOf("Cargo.toml"), new CargoWorkspaceOptions { Timeout = TimeSpan.FromTicks(1) }, f.Token), CargoWorkspace.LimitDiagnostic);
        AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(2), "A tiny real-IO deadline must stop promptly.");
        Success(f.Load());
    });

    private static Task CancelledResolution() => WithFixture(f =>
    {
        f.Package("", "app"); Success(f.Load());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CargoWorkspace.LoadV1(f.PathOf("Cargo.toml"), cancellationToken: cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => CargoWorkspace.Load(f.PathOf("Cargo.toml"), cancellationToken: cancelled.Token));
        using (var manifest = new FileStream(f.PathOf("Cargo.toml"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            AssertEx.True(manifest.Length > 0, "Cancelled resolution must release its input handle.");
        Success(f.Load());
    });

    private static void ValidateInventory(Fixture fixture)
    {
        fixture.Check();
        CargoContractValidationResult contract = CargoContract.Load(ContractPath, fixture.Token);
        AssertEx.True(contract.IsSuccessful, "The frozen cargo-v1 inventory must still validate.");
        CargoContractCase[] required = contract.Manifest!.Cases.Where(c => c.LeafId == "P2-04.02").ToArray();
        AssertEx.Equal(38, required.Length);
        AssertEx.Equal(38, All.Count);
        AssertEx.True(new FileInfo(FixtureManifestPath).Length is > 0 and <= 100_000, "The runtime case map has a bounded file size.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FixtureManifestPath));
        JsonElement root = document.RootElement;
        AssertEx.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal("cargo-v1", root.GetProperty("profile").GetString()!);
        AssertEx.Equal("P2-04.02", root.GetProperty("leafId").GetString()!);
        AssertEx.Equal("CargoWorkspace.LoadV1", root.GetProperty("entryPoint").GetString()!);
        AssertEx.Equal("CargoWorkspace.Load", root.GetProperty("compatibilityEntryPoint").GetString()!);
        AssertEx.Equal(38, root.GetProperty("denominator").GetInt32());
        AssertEx.Equal("runtime-test-case-map-not-execution-result", root.GetProperty("inventoryKind").GetString()!);
        AssertEx.Equal(CargoContract.FrozenContentSha256, root.GetProperty("contractContentSha256").GetString()!);
        JsonElement[] entries = root.GetProperty("cases").EnumerateArray().ToArray();
        AssertEx.Equal(38, entries.Length);
        for (int index = 0; index < 38; index++)
        {
            fixture.Check();
            AssertEx.Equal(required[index].Id, entries[index].GetProperty("id").GetString()!);
            AssertEx.Equal(required[index].ExpectedDiagnostic, entries[index].GetProperty("expectedDiagnostic").GetString()!);
            AssertEx.Equal(Prefix + required[index].Id, entries[index].GetProperty("testName").GetString()!);
            AssertEx.Equal(Prefix + required[index].Id, All[index].Name);
            AssertEx.True(entries[index].GetProperty("assertions").GetArrayLength() is > 0 and <= 10,
                "Every mapped scenario must describe concrete runtime assertions.");
        }
    }

    private static Task WithFixture(Action<Fixture> action)
    {
        using var fixture = new Fixture();
        action(fixture);
        fixture.Check();
        return Task.CompletedTask;
    }

    private static void Variants(Fixture fixture, string[] values, Action<string> action)
    {
        AssertEx.True(values.Length is > 0 and <= 10, "Each variant batch is bounded at ten inputs.");
        for (int index = 0; index < values.Length && index < 10; index++)
        { fixture.Check(); action(values[index]); }
    }

    private static CargoWorkspaceResult Successful(CargoWorkspaceResult result) { Success(result); return result; }
    private static CargoPackage SuccessfulPackage(CargoWorkspaceResult result) { Success(result); return result.RootPackage; }
    private static void Success(CargoWorkspaceResult result)
    {
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message} ({d.Span})")));
        AssertEx.Equal(0, result.Diagnostics.Count);
        AssertEx.True(result.IsVirtualWorkspace || result.RootPackageOrNull is not null, "A successful package graph retains its actual root.");
    }

    private static Diagnostic Failure(CargoWorkspaceResult result, string code)
    {
        AssertEx.False(result.IsSuccessful, "Rejected manifests cannot claim resolution success.");
        AssertEx.Equal(0, result.Packages.Count, "No usable partial graph may escape a failed load.");
        AssertEx.True(result.RootPackageOrNull is null, "Failures cannot expose a usable root package.");
        Diagnostic? diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == code);
        return AssertEx.NotNull(diagnostic, $"Expected {code}; got " + string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
    }

    private static void AtToken(Diagnostic diagnostic, string path, string text, string token, bool last = false)
    {
        int start = last ? text.LastIndexOf(token, StringComparison.Ordinal) : text.IndexOf(token, StringComparison.Ordinal);
        AssertEx.True(start >= 0, "The diagnostic expectation must name an actual original token.");
        AssertEx.Equal(path, diagnostic.SourcePath!);
        AssertEx.Equal(new TextSpan(start, token.Length), diagnostic.Span, "Diagnostics must retain the exact original-file token, including CRLF and Unicode offsets.");
        AssertEx.Equal(token, text.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }

    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) =>
        AssertEx.Equal(string.Join("\n", expected), string.Join("\n", actual));

    private static string GraphSnapshot(CargoWorkspaceResult result, Fixture fixture) => string.Join("\n", result.Packages.Select(p =>
        p.Name + "@" + p.Version + ":" + Path.GetRelativePath(fixture.Root, p.ManifestPath) + ":" +
        string.Join("|", p.Dependencies.Select(d => $"{d.Name}:{d.PackageName}:{d.Path}:{d.Version}:{d.Optional}:{d.DefaultFeatures}:{string.Join(',', d.Features)}:{d.CfgCondition}")) + ":" +
        string.Join("|", p.Targets.Select(t => $"{t.Kind}:{t.Name}:{Path.GetRelativePath(fixture.Root, t.SourcePath)}:{string.Join(',', t.RequiredFeatures)}"))));

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryParent = Path.GetFullPath(Path.GetTempPath());
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
        private readonly List<(string Path, bool Directory)> _links = [];
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private int _writes;
        public string Root { get; }
        public CancellationToken Token => _deadline.Token;
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(_temporaryParent, "rustsharp-p2-cargo-manifests-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Root);
        }
        public void Check() => _deadline.Token.ThrowIfCancellationRequested();
        public string PathOf(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(Root, relative));
            AssertEx.True(full.StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                "Fixtures may only create or mutate files within their owned unique root.");
            return full;
        }
        public string Write(string relative, string text) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(text));
        public string WriteBytes(string relative, byte[] bytes)
        {
            Check(); AssertEx.True(++_writes <= 512, "Each scenario is bounded at 512 fixture writes.");
            string path = PathOf(relative);
            string parent = Path.GetDirectoryName(path)!;
            for (int depth = 0; parent != Root && depth < 16; depth++)
            {
                Check(); _directories.Add(parent);
                AssertEx.True(_directories.Count <= 1024, "Fixture directories have a fixed 1024-item bound.");
                parent = Path.GetDirectoryName(parent)!;
            }
            AssertEx.Equal(Root, parent, "Fixture directory creation is bounded at sixteen levels.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _files.Add(path); File.WriteAllBytes(path, bytes); return path;
        }
        public void Package(string relative, string name)
        {
            string prefix = relative.Length == 0 ? "" : relative.TrimEnd('/') + "/";
            Write(prefix + "Cargo.toml", $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\n");
            Write(prefix + "src/main.rs", "fn main() {}\n");
        }
        public CargoWorkspaceResult Load(CargoWorkspaceOptions? options = null, string manifest = "Cargo.toml")
        { Check(); return CargoWorkspace.LoadV1(PathOf(manifest), options, _deadline.Token); }
        public void DeleteFile(string relative) { Check(); File.Delete(PathOf(relative)); }
        public void FileLink(string relative, string target)
        {
            Check(); string link = PathOf(relative); File.CreateSymbolicLink(link, PathOf(target)); _links.Add((link, false));
        }
        public void DirectoryLink(string relative, string target)
        {
            Check(); string link = PathOf(relative); Directory.CreateSymbolicLink(link, PathOf(target)); _links.Add((link, true));
        }
        public void Dispose()
        {
            Stopwatch cleanupClock = Stopwatch.StartNew();
            void CheckCleanup(string path)
            {
                AssertEx.True(cleanupClock.Elapsed < TimeSpan.FromSeconds(20), "Owned fixture cleanup has a twenty-second wall deadline.");
                AssertEx.True(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Delete only verified task-owned paths.");
            }
            try
            {
                AssertEx.True(Path.GetFullPath(Root).StartsWith(_temporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Cleanup must stay in the verified temporary parent.");
                AssertEx.True(Path.GetFileName(Root).StartsWith("rustsharp-p2-cargo-manifests-", StringComparison.Ordinal), "Only the task-owned fixture is removable.");
                AssertEx.True(_links.Count <= 8, "Owned link cleanup has a fixed eight-item bound.");
                // Native links are unlinked before any owned files or directories are removed.
                for (int index = _links.Count - 1; index >= 0 && index < 8; index--)
                {
                    CheckCleanup(_links[index].Path);
                    if (_links[index].Directory) Directory.Delete(_links[index].Path); else File.Delete(_links[index].Path);
                }
                string[] files = _files.ToArray();
                AssertEx.True(files.Length <= 512, "Only at most 512 tracked files can be cleaned.");
                for (int index = 0; index < files.Length && index < 512; index++)
                { CheckCleanup(files[index]); File.Delete(files[index]); }
                string[] directories = _directories.OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();
                AssertEx.True(directories.Length <= 1024, "Only at most 1024 tracked directories can be cleaned.");
                for (int index = 0; index < directories.Length && index < 1024; index++)
                { CheckCleanup(directories[index]); if (Directory.Exists(directories[index])) Directory.Delete(directories[index]); }
                AssertEx.True(cleanupClock.Elapsed < TimeSpan.FromSeconds(20), "Final root cleanup has the same wall deadline.");
                if (Directory.Exists(Root)) Directory.Delete(Root);
                AssertEx.False(Directory.Exists(Root), "Successful fixture cleanup leaves no owned root behind.");
            }
            finally { _deadline.Dispose(); }
        }
    }
}
