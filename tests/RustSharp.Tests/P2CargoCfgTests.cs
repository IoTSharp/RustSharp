using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P2CargoCfgTests
{
    private const string Prefix = "P2 cargo cfg ";
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new(Prefix + "cfg-windows", Windows),
        new(Prefix + "cfg-linux", Linux),
        new(Prefix + "cfg-x64", X64),
        new(Prefix + "cfg-feature", Feature),
        new(Prefix + "cfg-all", AllPredicates),
        new(Prefix + "cfg-any", AnyPredicates),
        new(Prefix + "cfg-not", NotPredicate),
        new(Prefix + "cfg-unknown", Unknown),
        new(Prefix + "cfg-invalid-combination", InvalidCombination),
        new(Prefix + "cfg-depth", Depth),
        new(Prefix + "cfg-required-features", RequiredFeatures),
        new(Prefix + "cfg-source-items", SourceItems),
    ];

    private static Task Windows() => WithFixture(f =>
    {
        ValidateInventory(f);
        Platforms(f);
        CargoCfgResolutionResult result = f.Resolve("win-x64");
        ActivePackages(result, ["app", "os_windows", "win_library"]);
        CargoDependency renamed = result.RootPackage!.Dependencies.Single(d => d.Name == "win_alias");
        AssertEx.Equal("win_library", renamed.PackageName!);
        AssertEx.Equal("0.1.0", renamed.Version!);
        AssertEx.True(renamed.CfgCondition!.Contains("windows", StringComparison.Ordinal), "Renamed dependency retains its condition.");
        CargoCfgEnvironment environment = result.EnvironmentFor("app@0.1.0");
        AssertEx.Equal("win-x64", environment.RuntimeIdentifier);
        AssertEx.Equal("windows", environment.TargetOs);
        AssertEx.Equal("x86_64", environment.TargetArch);
        SelectedSource(f, environment, "windows", true);
        SelectedSource(f, environment, "target_os = \"windows\"", true);
        SelectedSource(f, environment, "unix", false);
        AssertEx.True(result.OperationsConsumed > 0 && result.OperationsConsumed <= 20_000,
            "Manifest loading, feature closure and cfg selection share one bounded operation counter.");
    });

    private static Task Linux() => WithFixture(f =>
    {
        Platforms(f);
        CargoCfgResolutionResult result = f.Resolve("linux-x64");
        ActivePackages(result, ["app", "linux_library", "os_linux"]);
        CargoCfgEnvironment environment = result.EnvironmentFor("app@0.1.0");
        AssertEx.Equal("linux", environment.TargetOs);
        SelectedSource(f, environment, "unix", true);
        SelectedSource(f, environment, "target_os = \"linux\"", true);
        SelectedSource(f, environment, "windows", false);
        AssertEx.True(Successful(f.Resolve("win-x64")).Features!.Dependencies.Any(static d => d.Alias == "win_alias"), "A later Windows request admits its Windows alias.");
        ActivePackages(f.Resolve("linux-x64"), ["app", "linux_library", "os_linux"]);
    });

    private static Task X64() => WithFixture(f =>
    {
        f.Package("library", "library");
        f.Package("", "app", Conditional("target_arch = \"x86_64\"", "library", "library"));
        CargoCfgResolutionResult windows = f.Resolve("win-x64");
        CargoCfgResolutionResult linux = f.Resolve("linux-x64");
        ActivePackages(windows, ["app", "library"]);
        ActivePackages(linux, ["app", "library"]);
        SelectedSource(f, windows.EnvironmentFor("app@0.1.0"), "target_arch = \"x86_64\"", true);
        SelectedSource(f, linux.EnvironmentFor("app@0.1.0"), "target_arch = \"x86_64\"", true);
        Failure(f.Resolve(new CargoCfgOptions { RuntimeIdentifier = "win-x64", TargetArch = "aarch64" }), CargoCfgResolver.InvalidCfgDiagnostic);
        f.Package("", "app", Conditional("target_arch = \"x86\"", "library", "library"));
        Failure(f.Resolve("linux-x64"), CargoCfgResolver.InvalidCfgDiagnostic);
    });

    private static Task Feature() => WithFixture(f =>
    {
        f.Package("library", "library", "[features]\ndefault = [\"from_default\"]\nfrom_default = []\nedge = []\n");
        f.Package("", "app", "[features]\ndefault = [\"group\"]\ngroup = [\"selected\"]\nselected = []\n" +
            Conditional("feature = \"selected\"", "library", "library", ", features = [\"edge\"], default-features = false"));
        CargoCfgResolutionResult defaults = f.Resolve("win-x64");
        ActivePackages(defaults, ["app", "library"]);
        Features(defaults, "app", ["default", "group", "selected"]);
        Features(defaults, "library", ["edge"]);
        SelectedSource(f, defaults.EnvironmentFor("app@0.1.0"), "feature = \"selected\"", true);
        SelectedSource(f, defaults.EnvironmentFor("app@0.1.0"), "feature = \"inactive_but_valid\"", false);
        ActivePackages(f.Resolve("win-x64", new() { NoDefaultFeatures = true }), ["app"]);
        f.Package("", "app", "[features]\nselected = []\nactivate = [\"selected\", \"dep:library\"]\n" +
            Conditional("feature = \"selected\"", "library", "library", ", optional = true, features = [\"edge\"], default-features = false"));
        ActivePackages(f.Resolve("linux-x64"), ["app"]);
        CargoCfgResolutionResult optional = f.Resolve("linux-x64", Requests("activate"));
        ActivePackages(optional, ["app", "library"]);
        Features(optional, "library", ["edge"]);
        CargoDependency metadata = optional.RootPackage!.Dependencies.Single();
        AssertEx.True(metadata.Optional, "Conditional optional metadata survives resolution.");
        AssertEx.False(metadata.DefaultFeatures, "Conditional default-features false survives resolution.");
        AssertEx.Equal("edge", metadata.Features.Single());
        // The selected package's unified features, rather than only root features, drive its cfg environment.
        f.Package("tail", "tail");
        f.Package("shared", "shared", "[features]\nleft = []\nright = []\n" +
            Conditional("all(feature = \"left\", feature = \"right\")", "tail", "../tail"));
        f.Package("left", "left", "[dependencies]\nshared = { path = \"../shared\", features = [\"left\"] }\n");
        f.Package("right", "right", "[dependencies]\nshared = { path = \"../shared\", features = [\"right\"] }\n");
        f.Package("", "app", "[dependencies]\nleft = { path = \"left\" }\nright = { path = \"right\" }\n");
        CargoCfgResolutionResult unified = f.Resolve("win-x64");
        ActivePackages(unified, ["app", "left", "right", "shared", "tail"]);
        Features(unified, "shared", ["left", "right"]);
        SelectedSource(f, unified.EnvironmentFor("shared@0.1.0"), "all(feature = \"left\", feature = \"right\")", true);
    });

    private static Task AllPredicates() => WithFixture(f =>
    {
        Predicate(f, "all()", "win-x64", true); // Small bounded trial before compound predicates.
        Predicate(f, "all(windows, target_os = \"windows\", target_arch = \"x86_64\")", "win-x64", true);
        Predicate(f, "all(windows, unix)", "win-x64", false);
        Predicate(f, "all(unix, target_os = \"linux\")", "linux-x64", true);
        Predicate(f, "all(any(windows, unix), not(unix))", "win-x64", true);
    });

    private static Task AnyPredicates() => WithFixture(f =>
    {
        Predicate(f, "any()", "win-x64", false);
        Predicate(f, "any(windows, unix)", "win-x64", true);
        Predicate(f, "any(windows, unix)", "linux-x64", true);
        Predicate(f, "any(windows, target_os = \"windows\")", "linux-x64", false);
        Predicate(f, "any(all(), any())", "linux-x64", true);
    });

    private static Task NotPredicate() => WithFixture(f =>
    {
        Predicate(f, "not(unix)", "win-x64", true);
        Predicate(f, "not(windows)", "win-x64", false);
        Predicate(f, "not(windows)", "linux-x64", true);
        Predicate(f, "not(any())", "linux-x64", true);
        Predicate(f, "not(all())", "linux-x64", false);
        // All owners contribute before a downstream negative feature predicate is finalized.
        f.Package("tail", "tail");
        f.Package("shared", "shared", "[features]\nleft = []\nright = []\n" +
            Conditional("not(feature = \"right\")", "tail", "../tail"));
        f.Package("left", "left", "[dependencies]\nshared = { path = \"../shared\", features = [\"left\"] }\n");
        f.Package("right", "right", "[dependencies]\nshared = { path = \"../shared\", features = [\"right\"] }\n");
        f.Package("", "app", "[dependencies]\nleft = { path = \"left\" }\nright = { path = \"right\" }\n");
        CargoCfgResolutionResult finalized = f.Resolve("win-x64");
        ActivePackages(finalized, ["app", "left", "right", "shared"]);
        Features(finalized, "shared", ["left", "right"]);
        SelectedSource(f, finalized.EnvironmentFor("shared@0.1.0"), "not(feature = \"right\")", false);
    });

    private static Task Unknown() => WithFixture(f =>
    {
        f.Package("library", "library");
        f.Package("", "app");
        const string trial = "#[cfg(unknown)]";
        string original = "// 中文原始位置\r\n" + trial + "\r\nfn main() {}\r\n";
        AtToken(SourceFailure(f.Select(original, CargoCfgEnvironment.Create("win-x64")), CargoCfgResolver.InvalidCfgDiagnostic),
            f.PathOf("selected.rs"), original, trial);
        string[] expressions = ["unknown", "target_os = \"macos\"", "target_arch = \"aarch64\"", "feature", "not()",
            "not(windows, unix)", "all(windows,, unix)", "not(windows", "windows = \"true\"", "any(windows, unknown)"];
        for (int index = 0; index < expressions.Length && index < 10; index++)
        {
            f.Check();
            string attribute = "#[cfg(" + expressions[index] + ")]";
            string source = "// 中文\r\n" + attribute + "\r\nfn main() {}\r\n";
            AtToken(SourceFailure(f.Select(source, CargoCfgEnvironment.Create("win-x64")), CargoCfgResolver.InvalidCfgDiagnostic),
                f.PathOf("selected.rs"), source, attribute);
            string manifest = "[package]\r\nname = \"app\"\r\nversion = \"0.1.0\"\r\n# 中文\r\n" +
                Conditional(expressions[index], "library", "library").Replace("\n", "\r\n", StringComparison.Ordinal);
            f.Write("Cargo.toml", manifest);
            AtToken(Failure(f.Resolve("win-x64"), CargoCfgResolver.InvalidCfgDiagnostic),
                f.PathOf("Cargo.toml"), manifest, "'cfg(" + expressions[index] + ")'");
        }
        // Validation must not hide unsupported predicates behind a false or true short-circuit member.
        SourceFailure(f.Select("#[cfg(all(unix, unknown))] fn main() {}", CargoCfgEnvironment.Create("win-x64")), CargoCfgResolver.InvalidCfgDiagnostic);
    });

    private static Task InvalidCombination() => WithFixture(f =>
    {
        f.Package("", "app");
        Success(f.Resolve(new CargoCfgOptions { RuntimeIdentifier = "win-x64", TargetOs = "windows", TargetArch = "x86_64" }));
        CargoCfgOptions[] inputs =
        [
            new() { RuntimeIdentifier = "win-x64", TargetOs = "linux" },
            new() { RuntimeIdentifier = "linux-x64", TargetOs = "windows" },
            new() { RuntimeIdentifier = "win-x64", TargetArch = "x86" },
            new() { RuntimeIdentifier = "linux-x64", TargetArch = "aarch64" },
            new() { RuntimeIdentifier = "win-arm64" },
            new() { RuntimeIdentifier = "linux-arm64" },
            new() { RuntimeIdentifier = "unknown" },
            new() { RuntimeIdentifier = "" },
        ];
        for (int index = 0; index < inputs.Length && index < 10; index++)
        { f.Check(); Failure(f.Resolve(inputs[index]), CargoCfgResolver.InvalidCfgDiagnostic); }
    });

    private static Task Depth() => WithFixture(f =>
    {
        f.Package("library", "library");
        Predicate(f, "not(windows)", "win-x64", false); // Tiny generated-loop trial uses depth two.
        string exact = Nested(f, 15);
        f.Package("", "app", Conditional(exact, "library", "library"));
        Success(f.Resolve("win-x64"));
        AssertEx.True(f.Select("#[cfg(" + exact + ")] fn ignored() {}\nfn main() {}", CargoCfgEnvironment.Create("win-x64")).IsSuccessful, "Source depth exactly sixteen passes.");
        string excessive = Nested(f, 16);
        f.Package("", "app", Conditional(excessive, "library", "library"));
        Failure(f.Resolve("win-x64"), CargoWorkspace.LimitDiagnostic);
        SourceFailure(f.Select("#[cfg(" + excessive + ")] fn main() {}", CargoCfgEnvironment.Create("win-x64")), CargoWorkspace.LimitDiagnostic);
        f.Package("", "app", Conditional("not(windows)", "library", "library"));
        Success(f.Resolve("win-x64", limits: new() { MaximumCfgDepth = 2 }));
        Failure(f.Resolve("win-x64", limits: new() { MaximumCfgDepth = 1 }), CargoWorkspace.LimitDiagnostic);
        CargoCfgResolutionResult measured = f.Resolve("win-x64");
        Success(measured);
        AssertEx.Equal(measured.OperationsConsumed,
            Successful(f.Resolve("win-x64", limits: new() { MaximumOperations = measured.OperationsConsumed })).OperationsConsumed);
        Failure(f.Resolve("win-x64", limits: new() { MaximumOperations = measured.OperationsConsumed - 1 }), CargoWorkspace.LimitDiagnostic);
        Failure(f.Resolve("win-x64", limits: new() { Timeout = TimeSpan.FromTicks(1) }), CargoWorkspace.LimitDiagnostic);
        SourceFailure(f.Select("fn main() {}", CargoCfgEnvironment.Create("win-x64"), new() { Timeout = TimeSpan.FromTicks(1) }), CargoWorkspace.LimitDiagnostic);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CargoCfgResolver.ResolveV1(f.PathOf("Cargo.toml"),
            new() { RuntimeIdentifier = "win-x64" }, cancellationToken: cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => CargoCfgSourceSelector.Select("fn main() {}", f.PathOf("selected.rs"),
            CargoCfgEnvironment.Create("win-x64"), cancellationToken: cancelled.Token));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => f.Resolve("win-x64", limits: new() { Timeout = TimeSpan.FromSeconds(11) }));
    });

    private static Task RequiredFeatures() => WithFixture(f =>
    {
        f.Package("", "app", "[features]\na = []\nb = []\n" +
            "[[bin]]\nname = \"plain\"\npath = \"plain.rs\"\n" +
            "[[bin]]\nname = \"gated\"\npath = \"gated.rs\"\nrequired-features = [\"a\", \"b\"]\n");
        f.Write("plain.rs", "fn main() {}\n"); f.Write("gated.rs", "fn main() {}\n");
        f.Write("src/lib.rs", "pub fn value() -> i32 { 42 }\n");
        Targets(f.Resolve("win-x64"), ["app", "plain"]);
        Targets(f.Resolve("win-x64", Requests("a")), ["app", "plain"]);
        Targets(f.Resolve("linux-x64", Requests("a", "b")), ["app", "gated", "plain"]);
        Failure(f.Resolve(new CargoCfgOptions { RuntimeIdentifier = "win-x64", TargetName = "gated" }), CargoCfgResolver.InvalidCfgDiagnostic);
        Failure(f.Resolve(new CargoCfgOptions { RuntimeIdentifier = "win-x64", TargetName = "gated" }, Requests("a")), CargoCfgResolver.InvalidCfgDiagnostic);
        Targets(f.Resolve(new CargoCfgOptions { RuntimeIdentifier = "win-x64", TargetName = "gated" }, Requests("a", "b")), ["gated"]);
        Targets(f.Resolve(new CargoCfgOptions { RuntimeIdentifier = "linux-x64", TargetName = "plain" }), ["plain"]);
        Failure(f.Resolve(new CargoCfgOptions { RuntimeIdentifier = "win-x64", TargetName = "missing" }), CargoCfgResolver.InvalidCfgDiagnostic);
    });

    private static Task SourceItems() => WithFixture(f =>
    {
        CargoCfgEnvironment windows = CargoCfgEnvironment.Create("win-x64");
        const string source = "// 中文 cfg 原始位置\r\n" +
            "#[cfg(unix)] fn chosen() -> i32 { missing() }\r\n" +
            "#[cfg(windows)] fn chosen() -> i32 { 42 }\r\n" +
            "#[cfg(unix)] struct Gone { field: Missing }\r\n" +
            "#[cfg(unix)] use absent::Thing;\r\n" +
            "#[cfg(unix)] mod hidden { fn bad() { missing(); } }\r\n" +
            "#[cfg(windows)] #[cfg(unix)] fn never() { missing(); }\r\n" +
            "fn main() { println!(\"{}\", chosen()); }\r\n";
        CargoCfgSourceResult selected = f.Select(source, windows);
        SourceSuccess(selected); PreservedOffsets(f, source, selected.SourceText);
        AssertEx.False(selected.SourceText.Contains("missing", StringComparison.Ordinal), "Disabled bodies never reach binding.");
        AssertEx.False(selected.SourceText.Contains("Gone", StringComparison.Ordinal), "Disabled structs never reach binding.");
        AssertEx.False(selected.SourceText.Contains("#[cfg", StringComparison.Ordinal), "Evaluated attributes are masked before binding.");
        Bind(f, selected.SourceText);
        // Nested field, enum variant and associated item selection also precedes type/name binding.
        const string nested = "struct Holder { value: i32, #[cfg(unix)] missing: Missing }\n" +
            "trait Mark { #[cfg(unix)] fn missing(&self) -> Missing; }\n" +
            "impl Mark for i32 { #[cfg(unix)] fn missing(&self) -> Missing { absent() } }\n" +
            "fn main() { let h = Holder { value: 42 }; println!(\"{}\", h.value); }\n";
        CargoCfgSourceResult nestedSelected = f.Select(nested, windows);
        SourceSuccess(nestedSelected); AssertEx.False(nestedSelected.SourceText.Contains("Missing", StringComparison.Ordinal), "Disabled nested fields, variants and associated items are masked.");
        PreservedOffsets(f, nested, nestedSelected.SourceText);
        Bind(f, nestedSelected.SourceText, CompilationProfile.SafeCoreGenerics);
        AssertEx.True(CompilerDriver.Check(nestedSelected.SourceText, f.PathOf("selected.rs"),
            CompilationProfile.SafeCoreMirV2, f.Token).Diagnostics.Any(static d => d.Code == "RST2001"),
            "Cfg selection preserves the existing MIR trait profile boundary.");
        // Enum declarations belong to the MIR profile; retain that profile boundary in cfg evidence.
        const string enumSource = "enum Choice { Good, #[cfg(unix)] Bad(Missing) }\nfn main() { let c = Choice::Good; }\n";
        CargoCfgSourceResult enumSelected = f.Select(enumSource, windows); SourceSuccess(enumSelected);
        AssertEx.False(enumSelected.SourceText.Contains("Missing", StringComparison.Ordinal), "A disabled enum variant never reaches MIR type binding.");
        PreservedOffsets(f, enumSource, enumSelected.SourceText);
        CompilationResult enumBound = CompilerDriver.Check(enumSelected.SourceText, f.PathOf("selected.rs"), CompilationProfile.SafeCoreMirV2, f.Token);
        AssertEx.True(enumBound.Success, string.Join("; ", enumBound.Diagnostics));
        AssertEx.True(enumBound.Output is null, "Enum checking produces no emitted output.");
        const string comments = "// #[cfg(unknown)] is a comment\n/* #[cfg(unknown)] */\nfn main() { println!(\"#[cfg(unknown)]\"); }\n";
        AssertEx.Equal(comments, f.Select(comments, windows).SourceText);
        f.Write("main.rs", "#[cfg(unix)] mod nonexistent;\n#[cfg(unix)] mod hidden { mod never_read; }\n" +
            "#[cfg(windows)] mod child;\nfn main() { println!(\"{}\", child::value()); }\n");
        f.Write("child.rs", "#[cfg(unix)] mod never_read;\n#[cfg(windows)] pub fn value() -> i32 { 42 }\n");
        SafeCoreWorkspaceResult workspace = SafeCoreWorkspace.LoadWithCfgV1(f.PathOf("main.rs"), windows, cancellationToken: f.Token);
        AssertEx.True(workspace.IsSuccessful, string.Join("; ", workspace.Diagnostics));
        AssertEx.True(workspace.SourceMap is not null, "Expanded selected source retains a source map.");
        AssertEx.False(workspace.SourceText.Contains("nonexistent", StringComparison.Ordinal), "Disabled external module is masked.");
        AssertEx.False(workspace.SourceText.Contains("never_read", StringComparison.Ordinal), "Disabled child modules are masked at every loaded file.");
        Bind(f, workspace.SourceText);
        AssertEx.False(File.Exists(f.PathOf("nonexistent.rs")), "A disabled external module is skipped before opening its missing source.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreWorkspace.LoadWithCfgV1(f.PathOf("main.rs"), windows,
            cancellationToken: cancelled.Token));
        // Enabled invalid declarations still fail through the production binder.
        CargoCfgSourceResult enabled = f.Select("#[cfg(windows)] fn bad() -> i32 { missing() }\nfn main() {}", windows);
        SourceSuccess(enabled);
        AssertEx.False(CompilerDriver.Check(enabled.SourceText, f.PathOf("selected.rs"), CompilationProfile.SafeCoreGenerics, f.Token).Success, "Enabled unresolved calls still fail through the production binder.");
        f.Reopen();
    });

    private static void Platforms(Fixture f)
    {
        f.Package("win", "win_library"); f.Package("oswin", "os_windows");
        f.Package("linux", "linux_library"); f.Package("oslinux", "os_linux");
        f.Package("", "app", Conditional("windows", "win_alias", "win", ", package = \"win_library\", version = \"0.1.0\"") +
            Conditional("target_os = \"windows\"", "os_windows", "oswin") +
            Conditional("unix", "linux_library", "linux") + Conditional("target_os = \"linux\"", "os_linux", "oslinux"));
    }
    private static string Conditional(string expression, string alias, string path, string metadata = "") =>
        "[target.'cfg(" + expression + ")'.dependencies]\n" + alias + " = { path = \"" + path + "\"" + metadata + " }\n";
    private static void Predicate(Fixture f, string expression, string rid, bool active)
    {
        f.Check(); f.Package("library", "library");
        f.Package("", "app", Conditional(expression, "library", "library"));
        CargoCfgResolutionResult result = f.Resolve(rid);
        ActivePackages(result, active ? ["app", "library"] : ["app"]);
        SelectedSource(f, result.EnvironmentFor("app@0.1.0"), expression, active);
    }
    private static string Nested(Fixture f, int count)
    {
        AssertEx.True(count is >= 0 and <= 16, "Cfg nesting generator has at most sixteen iterations.");
        string expression = "windows";
        for (int index = 0; index < count && index < 16; index++) { f.Check(); expression = "not(" + expression + ")"; }
        return expression;
    }
    private static void SelectedSource(Fixture f, CargoCfgEnvironment environment, string expression, bool active)
    {
        string source = "#[cfg(" + expression + ")] fn selected() -> i32 { 42 }\nfn main() {}\n";
        CargoCfgSourceResult result = f.Select(source, environment); SourceSuccess(result);
        AssertEx.Equal(active, result.SourceText.Contains("fn selected", StringComparison.Ordinal));
        PreservedOffsets(f, source, result.SourceText); Bind(f, result.SourceText);
    }
    private static void PreservedOffsets(Fixture f, string original, string selected)
    {
        AssertEx.Equal(original.Length, selected.Length, "Masking retains original UTF16 offsets.");
        AssertEx.True(original.Length <= 32_768, "Offset verification is bounded at 32768 characters.");
        for (int index = 0; index < original.Length && index < 32_768; index++)
        {
            f.Check();
            if (original[index] is '\r' or '\n') AssertEx.Equal(original[index], selected[index]);
            else AssertEx.True(selected[index] == original[index] || selected[index] == ' ', "Masking changes only removed text to spaces.");
        }
    }
    private static void Bind(Fixture f, string source, params CompilationProfile[] profiles)
    {
        if (profiles.Length == 0) profiles = [CompilationProfile.SafeCoreGenerics, CompilationProfile.SafeCoreMirV2];
        AssertEx.True(profiles.Length <= 2, "Binding uses at most two explicit compiler profiles.");
        for (int index = 0; index < profiles.Length && index < 2; index++)
        {
            f.Check(); CompilationResult result = CompilerDriver.Check(source, f.PathOf("selected.rs"), profiles[index], f.Token);
            AssertEx.True(result.Success, profiles[index] + ": " + string.Join("; ", result.Diagnostics));
            AssertEx.True(result.Output is null, "Production checking emits no executable output.");
        }
    }
    private static void ValidateInventory(Fixture f)
    {
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/RustSharp.Conformance/fixtures"));
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "p2-cargo-v1-manifest.json")));
        using JsonDocument map = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "p2-cargo-v1-cfg-cases.json")));
        AssertEx.Equal(78, contract.RootElement.GetProperty("cases").GetArrayLength());
        var ids = new List<string>(); var codes = new List<string>(); int scanned = 0;
        foreach (JsonElement entry in contract.RootElement.GetProperty("cases").EnumerateArray())
        {
            f.Check(); AssertEx.True(++scanned <= 78, "Frozen inventory is bounded at seventy-eight rows.");
            if (entry.GetProperty("leafId").GetString() != "P2-04.04") continue;
            ids.Add(entry.GetProperty("id").GetString()!); codes.Add(entry.GetProperty("expectedDiagnostic").GetString()!);
        }
        AssertEx.Equal(12, ids.Count); AssertEx.Equal(12, All.Count);
        AssertEx.Equal(12, map.RootElement.GetProperty("denominator").GetInt32());
        AssertEx.Equal(12, map.RootElement.GetProperty("cases").GetArrayLength());
        for (int index = 0; index < 12; index++)
        {
            f.Check(); JsonElement row = map.RootElement.GetProperty("cases")[index];
            AssertEx.Equal(ids[index], row.GetProperty("id").GetString()!);
            AssertEx.Equal(Prefix + ids[index], All[index].Name);
            AssertEx.Equal(All[index].Name, row.GetProperty("testName").GetString()!);
            AssertEx.Equal(codes[index], row.GetProperty("expectedDiagnostic").GetString()!);
        }
    }
    private static CargoFeatureOptions Requests(params string[] names) => new() { Requests = names.Select(static name => new CargoFeatureRequest(name)).ToArray() };
    private static Task WithFixture(Action<Fixture> action) { using var f = new Fixture(); action(f); f.Check(); return Task.CompletedTask; }
    private static CargoCfgResolutionResult Successful(CargoCfgResolutionResult result) { Success(result); return result; }
    private static void Success(CargoCfgResolutionResult result) => AssertEx.True(result.IsSuccessful,
        string.Join("; ", result.Diagnostics.Select(static d => d.Code + ": " + d.Message)));
    private static void ActivePackages(CargoCfgResolutionResult result, string[] expected)
    {
        Success(result); AssertEx.True(result.Features!.IsSuccessful, "A successful cfg result retains a successful complete feature graph.");
        AssertEx.Equal(string.Join('|', expected), string.Join('|', result.Features.Activations.Select(static a => a.Package.Name)));
    }
    private static void Features(CargoCfgResolutionResult result, string package, string[] expected)
    {
        Success(result); CargoPackageFeatureActivation activation = AssertEx.NotNull(result.Features!.Activations.FirstOrDefault(a => a.Package.Name == package), "Expected active package " + package);
        AssertEx.Equal(string.Join('|', expected), string.Join('|', activation.Features));
    }
    private static void Targets(CargoCfgResolutionResult result, string[] expected)
    { Success(result); AssertEx.Equal(string.Join('|', expected), string.Join('|', result.Targets.Select(static t => t.Name))); }
    private static Diagnostic Failure(CargoCfgResolutionResult result, string code)
    {
        AssertEx.False(result.IsSuccessful, "Rejected cfg graphs cannot claim success."); AssertEx.True(result.Features is null && result.RootPackage is null, "Failures expose no usable partial graph.");
        AssertEx.Equal(0, result.Targets.Count);
        return AssertEx.NotNull(result.Diagnostics.FirstOrDefault(d => d.Code == code), "Expected " + code + "; got " + string.Join(';', result.Diagnostics.Select(static d => d.Code)));
    }
    private static void SourceSuccess(CargoCfgSourceResult result) => AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
    private static Diagnostic SourceFailure(CargoCfgSourceResult result, string code)
    {
        AssertEx.False(result.IsSuccessful, "Rejected cfg source cannot claim success."); AssertEx.Equal(string.Empty, result.SourceText);
        return AssertEx.NotNull(result.Diagnostics.FirstOrDefault(d => d.Code == code), "Expected source diagnostic " + code);
    }
    private static void AtToken(Diagnostic diagnostic, string path, string source, string token)
    {
        int start = source.IndexOf(token, StringComparison.Ordinal); AssertEx.True(start >= 0, "Expected diagnostic token exists in original source.");
        AssertEx.Equal(path, diagnostic.SourcePath!); AssertEx.Equal(new TextSpan(start, token.Length), diagnostic.Span);
        AssertEx.Equal(token, source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryParent = Path.GetFullPath(Path.GetTempPath());
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private int _writes;
        internal string Root { get; }
        internal CancellationToken Token => _deadline.Token;
        internal Fixture() { Root = Path.GetFullPath(Path.Combine(_temporaryParent, "rustsharp-p2-cargo-cfg-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(Root); }
        internal void Check() => Token.ThrowIfCancellationRequested();
        internal string PathOf(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(Root, relative));
            AssertEx.True(full.StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Fixture paths remain within the unique owned root.");
            return full;
        }
        internal void Write(string relative, string text)
        {
            Check(); AssertEx.True(++_writes <= 512, "Each scenario writes at most 512 files."); string path = PathOf(relative); string parent = Path.GetDirectoryName(path)!;
            for (int depth = 0; parent != Root && depth < 16; depth++)
            { Check(); _directories.Add(parent); AssertEx.True(_directories.Count <= 1024, "Directory tracking is bounded at 1024 entries."); parent = Path.GetDirectoryName(parent)!; }
            AssertEx.Equal(Root, parent); Directory.CreateDirectory(Path.GetDirectoryName(path)!); _files.Add(path);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        internal void Package(string relative, string name, string metadata = "")
        {
            string prefix = relative.Length == 0 ? "" : relative.TrimEnd('/') + "/";
            Write(prefix + "Cargo.toml", "[package]\nname = \"" + name + "\"\nversion = \"0.1.0\"\n" + metadata);
            Write(prefix + "src/main.rs", "fn main() {}\n");
        }
        internal CargoCfgResolutionResult Resolve(string rid, CargoFeatureOptions? features = null, CargoWorkspaceOptions? limits = null) =>
            Resolve(new CargoCfgOptions { RuntimeIdentifier = rid }, features, limits);
        internal CargoCfgResolutionResult Resolve(CargoCfgOptions options, CargoFeatureOptions? features = null, CargoWorkspaceOptions? limits = null)
        { Check(); CargoCfgResolutionResult result = CargoCfgResolver.ResolveV1(PathOf("Cargo.toml"), options, features, limits, Token); Reopen(); return result; }
        internal CargoCfgSourceResult Select(string source, CargoCfgEnvironment environment, CargoWorkspaceOptions? limits = null)
        { Check(); return CargoCfgSourceSelector.Select(source, PathOf("selected.rs"), environment, limits, Token); }
        internal void Reopen()
        {
            string[] files = _files.ToArray(); AssertEx.True(files.Length <= 512, "Exclusive reopen checks use at most 512 files.");
            for (int index = 0; index < files.Length && index < 512; index++)
            { Check(); using var reopened = new FileStream(files[index], FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        }
        public void Dispose()
        {
            Stopwatch clock = Stopwatch.StartNew();
            void Owned(string path)
            {
                AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Cleanup has its own twenty-second wall deadline.");
                AssertEx.True(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Delete only tracked files under this owned root.");
            }
            try
            {
                AssertEx.True(Root.StartsWith(_temporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Cleanup stays within the verified temporary parent.");
                AssertEx.True(Path.GetFileName(Root).StartsWith("rustsharp-p2-cargo-cfg-", StringComparison.Ordinal), "The unique task prefix confirms root ownership.");
                string[] files = _files.ToArray(); AssertEx.True(files.Length <= 512, "Cleanup deletes at most 512 tracked files.");
                for (int index = 0; index < files.Length && index < 512; index++) { Owned(files[index]); File.Delete(files[index]); }
                string[] dirs = _directories.OrderByDescending(static p => p.Length).ToArray(); AssertEx.True(dirs.Length <= 1024, "Cleanup deletes at most 1024 tracked directories.");
                for (int index = 0; index < dirs.Length && index < 1024; index++) { Owned(dirs[index]); if (Directory.Exists(dirs[index])) Directory.Delete(dirs[index]); }
                AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Final root deletion retains the cleanup deadline."); Directory.Delete(Root); AssertEx.False(Directory.Exists(Root), "Cleanup leaves no owned root.");
            }
            finally { _deadline.Dispose(); }
        }
    }
}
