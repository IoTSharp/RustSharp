using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class CargoWorkspaceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("cargo workspace resolves package source and local path dependency", LoadsPackageGraphAsync),
        new("cargo workspace rejects registry-only dependency", RejectsRegistryDependencyAsync),
        new("cargo workspace enforces package limit", EnforcesPackageLimitAsync),
    ];

    private static Task LoadsPackageGraphAsync() => WithWorkspace(root =>
    {
        Directory.CreateDirectory(Path.Combine(root, "dep"));
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"app\"\nversion = \"0.1.0\"\n[lib]\npath = \"src.rs\"\n[dependencies]\ndep = { path = \"dep\" }\n");
        File.WriteAllText(Path.Combine(root, "src.rs"), "fn main() {}");
        File.WriteAllText(Path.Combine(root, "dep", "Cargo.toml"), "[package]\nname = \"dep\"\nversion = \"0.1.0\"\n[lib]\npath = \"lib.rs\"\n");
        File.WriteAllText(Path.Combine(root, "dep", "lib.rs"), "pub fn value() -> i32 { 1 }");
        CargoWorkspaceResult result = CargoWorkspace.Load(Path.Combine(root, "Cargo.toml"));
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        AssertEx.Equal(2, result.Packages.Count);
        AssertEx.Equal("app", result.RootPackage.Name);
        AssertEx.Equal(Path.Combine(root, "src.rs"), result.RootPackage.SourcePath);
        return Task.CompletedTask;
    });

    private static Task RejectsRegistryDependencyAsync() => WithWorkspace(root =>
    {
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"app\"\nversion = \"0.1.0\"\n[dependencies]\nserde = \"1\"\n");
        File.WriteAllText(Path.Combine(root, "src", "main.rs"), "fn main() {}");
        CargoWorkspaceResult result = CargoWorkspace.Load(Path.Combine(root, "Cargo.toml"));
        AssertEx.False(result.IsSuccessful, "registry dependencies must be diagnosed");
        AssertEx.Equal(CargoWorkspace.UnsupportedDependencyDiagnostic, result.Diagnostics[0].Code);
        return Task.CompletedTask;
    });

    private static Task EnforcesPackageLimitAsync() => WithWorkspace(root =>
    {
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"app\"\nversion = \"0.1.0\"\n[dependencies]\ndep = { path = \"dep\" }\n");
        Directory.CreateDirectory(Path.Combine(root, "dep"));
        File.WriteAllText(Path.Combine(root, "dep", "Cargo.toml"), "[package]\nname = \"dep\"\nversion = \"0.1.0\"\n");
        File.WriteAllText(Path.Combine(root, "src", "main.rs"), "fn main() {}");
        CargoWorkspaceResult result = CargoWorkspace.Load(Path.Combine(root, "Cargo.toml"), new CargoWorkspaceOptions { MaximumPackages = 0 });
        AssertEx.True(result.Diagnostics.Count != 0, "package limits must be enforced");
        return Task.CompletedTask;
    });

    private static Task WithWorkspace(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "rustsharp-cargo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try { return action(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
