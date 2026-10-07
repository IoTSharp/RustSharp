using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P2CargoFeatureTests
{
    private const string Prefix = "P2 cargo features ";
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new(Prefix + "feature-default", Default),
        new(Prefix + "feature-no-default", NoDefault),
        new(Prefix + "feature-optional-dependency", OptionalDependency),
        new(Prefix + "feature-transitive", Transitive),
        new(Prefix + "feature-unification", Unification),
        new(Prefix + "feature-unknown", Unknown),
        new(Prefix + "feature-cycle", Cycle),
        new(Prefix + "feature-weak-unsupported", Unsupported),
        new(Prefix + "feature-dependency-default", DependencyDefault),
        new(Prefix + "feature-empty", Empty),
        new(Prefix + "feature-edge-budget", EdgeBudget),
    ];

    private static Task Default() => WithFixture(f =>
    {
        ValidateInventory(f);
        f.Package("", "app", "[features]\ndefault = [\"first\"]\nfirst = [\"second\"]\nsecond = []\n");
        CargoFeatureResolutionResult result = f.Resolve();
        Features(result, "app", ["default", "first", "second"]);
        AssertEx.Equal(1, result.Activations.Count);
        AssertEx.Equal(0, result.Dependencies.Count);
        AssertEx.True(result.OperationsConsumed > 0, "Whole resolution reports its shared bounded work.");
        f.Package("", "app", "[features]\nother = []\n");
        Features(f.Resolve(), "app", []); // Missing default is valid.
    });

    private static Task NoDefault() => WithFixture(f =>
    {
        f.Package("", "app", "[features]\ndefault = [\"extra\"]\nextra = []\n");
        Features(f.Resolve(new() { NoDefaultFeatures = true }), "app", []);
        Features(f.Resolve(Requests("extra") with { NoDefaultFeatures = true }), "app", ["extra"]);
        Features(f.Resolve(Requests("default") with { NoDefaultFeatures = true }), "app", ["default", "extra"]);
    });

    private static Task OptionalDependency() => WithFixture(f =>
    {
        f.Package("library", "library", "[features]\ndefault = [\"ready\"]\nready = []\n");
        const string dependency = "[dependencies]\nlibrary = { path = \"library\", optional = true }\n";
        f.Package("", "app", dependency);
        AssertEx.Equal(1, Successful(f.Resolve()).Activations.Count);
        CargoFeatureResolutionResult implicitFeature = f.Resolve(Requests("library"));
        Features(implicitFeature, "app", ["library"]);
        Features(implicitFeature, "library", ["default", "ready"]);
        AssertEx.Equal(1, implicitFeature.Dependencies.Count);
        f.Package("", "app", dependency + "[features]\nexpose = [\"dep:library\"]\nunused = []\n");
        Failure(f.Resolve(Requests("library")), CargoFeatureResolver.FeatureDiagnostic);
        CargoFeatureResolutionResult explicitDependency = f.Resolve(Requests("expose"));
        Features(explicitDependency, "app", ["expose"]);
        AssertEx.Equal(2, explicitDependency.Activations.Count);
        // Explicit definitions take precedence even when dep:name suppresses the implicit definition.
        f.Package("", "app", dependency + "[features]\nlibrary = []\nexpose = [\"dep:library\"]\n");
        CargoFeatureResolutionResult overridden = f.Resolve(Requests("library"));
        Features(overridden, "app", ["library"]);
        AssertEx.Equal(1, overridden.Activations.Count);
    });

    private static Task Transitive() => WithFixture(f =>
    {
        f.Package("leaf", "leaf", "[features]\nleaf-feature = []\n");
        f.Package("mid", "mid", "[dependencies]\nleaf = { path = \"../leaf\", features = [\"leaf-feature\"] }\n[features]\nremote = [\"local\"]\nlocal = []\n");
        f.Package("", "app", "[dependencies]\nrenamed = { package = \"mid\", path = \"mid\", optional = true, default-features = false }\n[features]\nstart = [\"next\"]\nnext = [\"renamed/remote\"]\n");
        CargoFeatureResolutionResult result = f.Resolve(Requests("start"));
        Features(result, "app", ["next", "start"]);
        Features(result, "mid", ["local", "remote"]);
        Features(result, "leaf", ["leaf-feature"]);
        AssertEx.Equal(2, result.Dependencies.Count);
        AssertEx.Equal("renamed", result.Dependencies[0].Alias);
        AssertEx.Equal("mid@0.1.0", result.Dependencies[0].TargetIdentity);
    });

    private static Task Unification() => WithFixture(f =>
    {
        Diamond(f, false, false);
        CargoFeatureResolutionResult first = f.Resolve();
        Features(first, "shared", ["left-feature", "right-feature"]);
        AssertEx.Equal(4, first.Activations.Count);
        AssertEx.Equal(4, first.Dependencies.Count);
        string snapshot = Snapshot(first);
        f.Package("", "app", "[dependencies]\nright = { path = \"right\" }\nleft = { path = \"left\" }\n");
        AssertEx.Equal(snapshot, Snapshot(Successful(f.Resolve())), "Traversal/declaration order cannot change feature union or edge order.");
    });

    private static Task Unknown() => WithFixture(f =>
    {
        f.Package("", "app", "[features]\nknown = []\n");
        var request = new CargoFeatureRequest("missing") { SourcePath = "command-line", Span = new(11, 7) };
        Diagnostic option = Failure(f.Resolve(new() { Requests = [request] }), CargoFeatureResolver.FeatureDiagnostic);
        AssertEx.Equal("command-line", option.SourcePath!);
        AssertEx.Equal(request.Span, option.Span);
        string local = "[package]\r\nname = \"app\"\r\nversion = \"0.1.0\"\r\n# 中文位置\r\n[features]\r\nbad = [\"missing\"]\r\n";
        f.Write("Cargo.toml", local);
        AtToken(Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic), f.PathOf("Cargo.toml"), local, "\"missing\"");
        f.Package("library", "library", "[features]\nknown = []\n");
        string dep = "[package]\nname = \"app\"\nversion = \"0.1.0\"\n[dependencies]\nlibrary = { path = \"library\", features = [\"missing\"] }\n";
        f.Write("Cargo.toml", dep);
        AtToken(Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic), f.PathOf("Cargo.toml"), dep, "\"missing\"");
        f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\" }\n[features]\nbad = [\"library/missing\"]\n");
        Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic);
        f.Package("", "app", "[features]\nbad = [\"unknown/known\"]\n");
        Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic);
        f.Write("Cargo.toml", "[workspace]\nmembers = [\"library\"]\n");
        Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic);
        CargoFeatureResolutionResult selected = f.Resolve(new() { RootPackageIdentity = "library@0.1.0", Requests = [new("known")] });
        Features(selected, "library", ["known"]);
        AssertEx.Equal("library", selected.RootPackage!.Name);
        Failure(f.Resolve(new() { RootPackageIdentity = "missing@0.1.0" }), CargoFeatureResolver.FeatureDiagnostic);
    });

    private static Task Cycle() => WithFixture(f =>
    {
        f.Package("", "app", "[features]\na = [\"b\"]\nb = [\"a\"]\n");
        Failure(f.Resolve(Requests("a")), CargoFeatureResolver.FeatureDiagnostic);
        f.Package("library", "library", "[features]\na = [\"b\"]\nb = [\"a\"]\n");
        f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\" }\n[features]\nstart = [\"library/a\"]\n");
        Failure(f.Resolve(Requests("start")), CargoFeatureResolver.FeatureDiagnostic);
        // A true cross-package cycle is already a package graph cycle; preserve loader diagnostic priority.
        f.Package("library", "library", "[dependencies]\napp = { path = \"..\" }\n[features]\na = [\"app/start\"]\n");
        Failure(f.Resolve(Requests("start")), CargoWorkspace.DependencyCycleDiagnostic);
        f.Package("", "app", "[features]\nself = [\"self\"]\n");
        Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic);
    });

    private static Task Unsupported() => WithFixture(f =>
    {
        f.Package("library", "library", "[features]\nknown = []\n");
        f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\", optional = true }\n[features]\nbad = [\"library?/known\"]\n");
        Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic); // Tiny bounded trial before variants.
        string[] expressions = ["library?/known", "library/known/extra", "dep:", "dep:unknown", "known+extra", "", "library:known"];
        for (int index = 0; index < expressions.Length && index < 10; index++)
        {
            f.Check();
            f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\", optional = true }\n[features]\nbad = [\"" + expressions[index] + "\"]\n");
            Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic);
        }
        f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\" }\n[features]\nbad = [\"dep:library\"]\n");
        Failure(f.Resolve(), CargoFeatureResolver.FeatureDiagnostic);
        f.Package("", "app", "[target.'cfg(windows)'.dependencies]\nlibrary = { path = \"library\" }\n");
        Failure(f.Resolve(), CargoWorkspace.UnsupportedManifestDiagnostic);
    });

    private static Task DependencyDefault() => WithFixture(f =>
    {
        Diamond(f, false, false);
        Features(f.Resolve(), "shared", ["left-feature", "right-feature"]);
        Diamond(f, false, true);
        Features(f.Resolve(new() { NoDefaultFeatures = true }), "shared", ["default", "from-default", "left-feature", "right-feature"]);
        Diamond(f, true, false);
        Features(f.Resolve(), "shared", ["default", "from-default", "left-feature", "right-feature"]);
        f.Package("", "app", "[dependencies]\nshared = { path = \"shared\", optional = true, features = [\"left-feature\"] }\n[features]\nactivate = [\"dep:shared\"]\n");
        AssertEx.Equal(1, Successful(f.Resolve()).Activations.Count, "Inactive optional edges cannot contribute dependency features or defaults.");
        Features(f.Resolve(Requests("activate")), "shared", ["default", "from-default", "left-feature"]);
    });

    private static Task Empty() => WithFixture(f =>
    {
        f.Package("", "app", "[features]\nempty = []\n");
        CargoFeatureResolutionResult result = f.Resolve(Requests("empty", "empty"));
        Features(result, "app", ["empty"]);
        AssertEx.Equal(0, result.Dependencies.Count);
        var malformed = new CargoFeatureOptions { Requests = [null!] };
        Failure(f.Resolve(malformed), CargoFeatureResolver.FeatureDiagnostic);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CargoFeatureResolver.ResolveV1(f.PathOf("Cargo.toml"), cancellationToken: cancelled.Token));
        Features(f.Resolve(Requests("empty")), "app", ["empty"]);
    });

    private static Task EdgeBudget() => WithFixture(f =>
    {
        f.Package("", "app", "[features]\nonly = []\n");
        Features(f.Resolve(Requests("only"), new() { MaximumFeaturesPerPackage = 1 }), "app", ["only"]); // Small trial.
        f.Package("", "app", "[features]\nfirst = []\nsecond = []\n");
        Failure(f.Resolve(limits: new() { MaximumFeaturesPerPackage = 1 }), CargoWorkspace.LimitDiagnostic);
        f.Package("", "app", FeatureBoundary(f, 128, false));
        CargoFeatureOptions all = new() { Requests = Enumerable.Range(0, 127).Select(i => new CargoFeatureRequest($"f{i:D3}")).ToArray() };
        CargoFeatureResolutionResult exact = f.Resolve(all);
        AssertEx.Equal(128, Successful(exact).Activations[0].Features.Count);
        AssertEx.True(exact.OperationsConsumed <= 20_000, "Parsing and expansion share one 20000-operation ceiling.");
        f.Package("", "app", FeatureBoundary(f, 128, true));
        Failure(f.Resolve(all), CargoWorkspace.LimitDiagnostic);
        f.Package("", "app", FeatureBoundary(f, 129, false));
        Failure(f.Resolve(), CargoWorkspace.LimitDiagnostic);
        // Implicit optional feature count is also bounded, beyond explicit metadata alone.
        f.Package("library", "library");
        f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\", optional = true }\n[features]\nexplicit = []\n");
        Failure(f.Resolve(limits: new() { MaximumFeaturesPerPackage = 1 }), CargoWorkspace.LimitDiagnostic);
        // Raw edges are global and include dependency-declared requests.
        f.Package("library", "library", "[features]\nlast = []\n");
        f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\", features = [\"last\", \"last\"] }\n");
        Features(f.Resolve(limits: new() { MaximumFeatureEdges = 2 }), "library", ["last"]);
        Failure(f.Resolve(limits: new() { MaximumFeatureEdges = 1 }), CargoWorkspace.LimitDiagnostic);
        string References(int count)
        {
            AssertEx.True(count is > 0 and <= 600, "Cross-package fixture has at most 600 references per package.");
            f.Check();
            string metadata = "[features]\nbulk = [" + string.Join(',', Enumerable.Repeat("\"last\"", count)) + "]\nlast = []\n";
            f.Check();
            return metadata;
        }
        f.Package("library", "library", References(1));
        f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\" }\n" + References(1));
        Features(f.Resolve(Requests("bulk", "library/bulk")), "library", ["bulk", "last"]);
        int[] amounts = [512, 600];
        for (int index = 0; index < amounts.Length && index < 2; index++)
        {
            f.Check();
            f.Package("library", "library", References(amounts[index]));
            f.Package("", "app", "[dependencies]\nlibrary = { path = \"library\" }\n" + References(amounts[index]));
            CargoFeatureResolutionResult cross = f.Resolve(Requests("bulk", "library/bulk"));
            if (amounts[index] == 512)
            {
                Features(cross, "app", ["bulk", "last"]);
                Features(cross, "library", ["bulk", "last"]);
                AssertEx.True(cross.OperationsConsumed <= 20_000, "Both packages share the operation ceiling.");
            }
            else Failure(cross, CargoWorkspace.LimitDiagnostic);
        }
        f.Package("", "app", "[features]\ndefault = [\"only\"]\nonly = []\n");
        CargoFeatureResolutionResult budgeted = f.Resolve();
        Success(budgeted);
        AssertEx.Equal(budgeted.OperationsConsumed, Successful(f.Resolve(limits: new() { MaximumOperations = budgeted.OperationsConsumed })).OperationsConsumed);
        Failure(f.Resolve(limits: new() { MaximumOperations = budgeted.OperationsConsumed - 1 }), CargoWorkspace.LimitDiagnostic);
        Failure(f.Resolve(limits: new() { Timeout = TimeSpan.FromTicks(1) }), CargoWorkspace.LimitDiagnostic);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => f.Resolve(limits: new() { Timeout = TimeSpan.FromSeconds(11) }));
    });

    private static string FeatureBoundary(Fixture fixture, int count, bool extra)
    {
        AssertEx.True(count is > 0 and <= 129, "Feature generator is bounded at 129 entries.");
        var text = new StringBuilder("[features]\n");
        for (int index = 0; index < count && index < 129; index++)
        {
            fixture.Check();
            int edges = index >= 127 ? 0 : index == 0 ? 16 + (extra ? 1 : 0) : 8;
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"f{index:D3} = [");
            for (int edge = 0; edge < edges && edge < 17; edge++)
            { fixture.Check(); if (edge > 0) text.Append(','); text.Append("\"f127\""); }
            text.Append("]\n");
        }
        return text.ToString();
    }

    private static void Diamond(Fixture f, bool leftDefaults, bool rightDefaults)
    {
        f.Package("shared", "shared", "[features]\ndefault = [\"from-default\"]\nfrom-default = []\nleft-feature = []\nright-feature = []\n");
        f.Package("left", "left", "[dependencies]\nshared = { path = \"../shared\", features = [\"left-feature\"], default-features = " + (leftDefaults ? "true" : "false") + " }\n");
        f.Package("right", "right", "[dependencies]\nshared = { path = \"../shared\", features = [\"right-feature\"], default-features = " + (rightDefaults ? "true" : "false") + " }\n");
        f.Package("", "app", "[dependencies]\nleft = { path = \"left\" }\nright = { path = \"right\" }\n");
    }

    private static void ValidateInventory(Fixture f)
    {
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/RustSharp.Conformance/fixtures"));
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "p2-cargo-v1-manifest.json")));
        using JsonDocument map = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "p2-cargo-v1-feature-cases.json")));
        JsonElement.ArrayEnumerator entries = contract.RootElement.GetProperty("cases").EnumerateArray();
        var ids = new List<string>(); var codes = new List<string>();
        AssertEx.Equal(78, contract.RootElement.GetProperty("cases").GetArrayLength());
        int scanned = 0;
        foreach (JsonElement entry in entries)
        {
            f.Check(); AssertEx.True(++scanned <= 78, "Frozen inventory has exactly 78 bounded rows.");
            if (entry.GetProperty("leafId").GetString() != "P2-04.03") continue;
            ids.Add(entry.GetProperty("id").GetString()!); codes.Add(entry.GetProperty("expectedDiagnostic").GetString()!);
        }
        AssertEx.Equal(11, ids.Count);
        AssertEx.Equal(11, All.Count);
        AssertEx.Equal(11, map.RootElement.GetProperty("denominator").GetInt32());
        AssertEx.Equal(11, map.RootElement.GetProperty("cases").GetArrayLength());
        string[] names = All.Select(test => test.Name).ToArray();
        for (int index = 0; index < 11; index++)
        {
            f.Check(); JsonElement row = map.RootElement.GetProperty("cases")[index];
            AssertEx.Equal(ids[index], row.GetProperty("id").GetString()!);
            AssertEx.Equal(Prefix + ids[index], names[index]);
            AssertEx.Equal(names[index], row.GetProperty("testName").GetString()!);
            AssertEx.Equal(codes[index], row.GetProperty("expectedDiagnostic").GetString()!);
        }
    }

    private static CargoFeatureOptions Requests(params string[] values) => new() { Requests = values.Select(static v => new CargoFeatureRequest(v)).ToArray() };
    private static Task WithFixture(Action<Fixture> action) { using var f = new Fixture(); action(f); f.Check(); return Task.CompletedTask; }
    private static CargoFeatureResolutionResult Successful(CargoFeatureResolutionResult result) { Success(result); return result; }
    private static void Success(CargoFeatureResolutionResult result) => AssertEx.True(result.IsSuccessful,
        string.Join("; ", result.Diagnostics.Select(static d => d.Code + ": " + d.Message)));
    private static void Features(CargoFeatureResolutionResult result, string package, string[] expected)
    {
        Success(result);
        CargoPackageFeatureActivation activation = AssertEx.NotNull(result.Activations.FirstOrDefault(p => p.Package.Name == package), "Expected active package " + package);
        AssertEx.Equal(string.Join('|', expected), string.Join('|', activation.Features));
    }
    private static Diagnostic Failure(CargoFeatureResolutionResult result, string code)
    {
        AssertEx.False(result.IsSuccessful, "Rejected feature graphs cannot claim success.");
        AssertEx.True(result.Workspace is null && result.RootPackage is null, "Failures cannot expose a usable workspace/root.");
        AssertEx.Equal(0, result.Activations.Count); AssertEx.Equal(0, result.Dependencies.Count);
        return AssertEx.NotNull(result.Diagnostics.FirstOrDefault(d => d.Code == code), "Expected " + code + "; got " + string.Join(';', result.Diagnostics.Select(d => d.Code)));
    }
    private static void AtToken(Diagnostic diagnostic, string path, string source, string token)
    {
        int start = source.IndexOf(token, StringComparison.Ordinal);
        AssertEx.True(start >= 0, "Expected token exists in original source.");
        AssertEx.Equal(path, diagnostic.SourcePath!); AssertEx.Equal(new TextSpan(start, token.Length), diagnostic.Span);
        AssertEx.Equal(token, source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }
    private static string Snapshot(CargoFeatureResolutionResult result) => string.Join('\n', result.Activations.Select(a => a.Identity + ":" + string.Join(',', a.Features))) + "\n" +
        string.Join('\n', result.Dependencies.Select(d => d.SourceIdentity + ":" + d.Alias + ":" + d.TargetIdentity));

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryParent = Path.GetFullPath(Path.GetTempPath());
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private int _writes;
        public string Root { get; }
        internal Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(_temporaryParent, "rustsharp-p2-cargo-features-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Root);
        }
        internal void Check() => _deadline.Token.ThrowIfCancellationRequested();
        internal string PathOf(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(Root, relative));
            AssertEx.True(full.StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Fixture paths stay within the unique owned root.");
            return full;
        }
        internal void Write(string relative, string text)
        {
            Check(); AssertEx.True(++_writes <= 512, "Each scenario has at most 512 writes.");
            string path = PathOf(relative); string parent = Path.GetDirectoryName(path)!;
            for (int depth = 0; parent != Root && depth < 16; depth++)
            { Check(); _directories.Add(parent); AssertEx.True(_directories.Count <= 1024, "Directory tracking is bounded."); parent = Path.GetDirectoryName(parent)!; }
            AssertEx.Equal(Root, parent);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); _files.Add(path);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        internal void Package(string relative, string name, string metadata = "")
        {
            string prefix = relative.Length == 0 ? "" : relative.TrimEnd('/') + "/";
            Write(prefix + "Cargo.toml", $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\n" + metadata);
            Write(prefix + "src/main.rs", "fn main() {}\n");
        }
        internal CargoFeatureResolutionResult Resolve(CargoFeatureOptions? options = null, CargoWorkspaceOptions? limits = null)
        {
            Check(); CargoFeatureResolutionResult result = CargoFeatureResolver.ResolveV1(PathOf("Cargo.toml"), options, limits, _deadline.Token);
            string[] files = _files.ToArray();
            AssertEx.True(files.Length <= 512, "Exclusive reopen checks are bounded at 512 files.");
            for (int index = 0; index < files.Length && index < 512; index++)
            { Check(); using var reopened = new FileStream(files[index], FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            return result;
        }
        public void Dispose()
        {
            Stopwatch clock = Stopwatch.StartNew();
            void Owned(string path)
            {
                AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Cleanup has its own twenty-second deadline.");
                AssertEx.True(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Delete only tracked paths in this owned root.");
            }
            try
            {
                AssertEx.True(Root.StartsWith(_temporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Cleanup stays in the verified temporary parent.");
                AssertEx.True(Path.GetFileName(Root).StartsWith("rustsharp-p2-cargo-features-", StringComparison.Ordinal), "Unique task prefix confirms ownership.");
                string[] files = _files.ToArray(); AssertEx.True(files.Length <= 512, "Cleanup has at most 512 files.");
                for (int index = 0; index < files.Length && index < 512; index++) { Owned(files[index]); File.Delete(files[index]); }
                string[] dirs = _directories.OrderByDescending(static p => p.Length).ToArray();
                AssertEx.True(dirs.Length <= 1024, "Cleanup has at most 1024 directories.");
                for (int index = 0; index < dirs.Length && index < 1024; index++) { Owned(dirs[index]); if (Directory.Exists(dirs[index])) Directory.Delete(dirs[index]); }
                AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Final root deletion retains the independent cleanup deadline.");
                Directory.Delete(Root); AssertEx.False(Directory.Exists(Root), "Cleanup leaves no owned root.");
            }
            finally { _deadline.Dispose(); }
        }
    }
}
