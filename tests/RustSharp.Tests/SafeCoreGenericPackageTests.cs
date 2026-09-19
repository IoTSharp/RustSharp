using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class SafeCoreGenericPackageTests
{
    private const string Library = "pub trait Mark{} impl Mark for i32{} pub struct Box<T>{pub value:T} " +
        "fn identity<T>(x:T)->T{x} pub fn wrap<T:Mark>(x:T)->Box<T>{Box{value:crate::identity(x)}} " +
        "pub fn unwrap<T>(x:Box<T>)->T{x.value}";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generic packages specialize imported bodies and embed crate metadata", ExecutesPackagesAsync),
        new("generic packages enforce crate orphan rules and allow local nominal impls", OrphansAsync),
        new("generic package scopes isolate private names and crate-relative paths", IsolationAsync),
        new("generic package dependency diamonds preserve one nominal identity", DiamondAsync),
        new("generic packages map dependency errors to original files and reject cycles", DiagnosticsAsync),
    ];

    private static Task ExecutesPackagesAsync() => Workspace(async (directory, token) =>
    {
        string manifest = Create(directory, Library, "use dep; fn main(){let b=dep::wrap(42);println!(\"{}\",dep::unwrap(b));}");
        CompilationResult result = CompilerDriver.CompileFile(manifest, Path.Combine(directory, "out", "generic.dll"),
            profile: CompilationProfile.SafeCoreGenerics, cancellationToken: token);
        AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
        var run = await new BoundedProcessRunner().RunAsync(new("dotnet", [result.Output!.AssemblyPath], directory, TimeSpan.FromSeconds(10)), token);
        AssertEx.True(run.Succeeded, run.StandardError);
        AssertEx.Equal("42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        using var stream = File.OpenRead(result.Output.AssemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        ManifestResource resource = metadata.ManifestResources.Select(metadata.GetManifestResource)
            .Single(value => metadata.GetString(value.Name) == "RustSharp.Generics.v1.json");
        var resourceData = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress)
            .GetReader(checked((int)resource.Offset), pe.PEHeaders.CorHeader.ResourcesDirectory.Size - checked((int)resource.Offset));
        int length = resourceData.ReadInt32();
        using JsonDocument genericMetadata = JsonDocument.Parse(resourceData.ReadBytes(length));
        AssertEx.Equal(1, genericMetadata.RootElement.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal(2, genericMetadata.RootElement.GetProperty("crates").GetArrayLength());
        AssertEx.True(genericMetadata.RootElement.GetProperty("functions").EnumerateArray()
            .Any(function => function.GetProperty("id").GetString()!.EndsWith("::wrap", StringComparison.Ordinal)),
            "The emitted metadata must retain imported generic body templates.");
        AssertEx.True(genericMetadata.RootElement.GetProperty("instances").GetArrayLength() >= 4,
            "The consumer must contain all statically reached generic instances.");
    });

    private static Task OrphansAsync() => Workspace((directory, token) =>
    {
        string manifest = Create(directory, Library, "impl dep::Mark for bool{} fn main(){}");
        Reject(manifest, "RSG1006", token);
        WriteMain(directory, "impl dep::Mark for dep::Box<i32>{} fn main(){}");
        Reject(manifest, "RSG1006", token);
        WriteMain(directory, "struct Local<T>{value:T} impl<T> dep::Mark for Local<T>{} fn main(){let b=dep::wrap(Local{value:7});let n:i32=b.value.value;}");
        AssertEx.True(CompilerDriver.CheckFile(manifest, CompilationProfile.SafeCoreGenerics, token).Success,
            "A local nominal self type permits a foreign marker-trait impl.");
        return Task.CompletedTask;
    });

    private static Task IsolationAsync() => Workspace((directory, token) =>
    {
        string manifest = Create(directory, "pub fn leak()->i32{secret()}", "fn secret()->i32{42} fn main(){dep::leak();}");
        Reject(manifest, "RSN1003", token);
        WriteLibrary(directory, "pub fn leak()->i32{super::secret()}");
        Reject(manifest, "RSN1001", token);
        WriteLibrary(directory, "pub(crate) fn secret()->i32{42} pub fn call()->i32{crate::secret()}");
        WriteMain(directory, "fn main(){dep::secret();}");
        Reject(manifest, "RSN1005", token);
        WriteMain(directory, "fn main(){dep::call();}");
        AssertEx.True(CompilerDriver.CheckFile(manifest, CompilationProfile.SafeCoreGenerics, token).Success, "crate:: must bind to the dependency crate root.");
        WriteLibrary(directory, "pub struct Box<T>{pub(crate) value:T} pub fn make()->Box<i32>{Box{value:1}}");
        WriteMain(directory, "fn main(){dep::make().value;}");
        Reject(manifest, "RSG1005", token);
        return Task.CompletedTask;
    });

    private static Task DiamondAsync() => Workspace((directory, token) =>
    {
        string manifest = Create(directory, Library, "fn main(){let a=dep::wrap(1);let b=middle::forward(a);println!(\"{}\",dep::unwrap(b));}");
        File.AppendAllText(manifest, "middle={path=\"../middle\"}\n");
        string middle = Path.Combine(directory, "middle");
        Directory.CreateDirectory(Path.Combine(middle, "src"));
        File.WriteAllText(Path.Combine(middle, "Cargo.toml"), "[package]\nname=\"middle\"\nversion=\"0.1.0\"\nedition=\"2024\"\n[dependencies]\ndep={path=\"../library\"}\n");
        File.WriteAllText(Path.Combine(middle, "src", "lib.rs"), "pub fn forward<T>(value:dep::Box<T>)->dep::Box<T>{value}");
        GenericPackageWorkspaceResult linked = GenericPackageWorkspace.Load(manifest, token);
        AssertEx.True(linked.IsSuccessful, string.Join("; ", linked.Diagnostics));
        AssertEx.Equal(3, linked.Crates.Length);
        CompilationResult result = CompilerDriver.CheckFile(manifest, CompilationProfile.SafeCoreGenerics, token);
        AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
        File.WriteAllText(Path.Combine(directory, "shared.rs"), "pub fn shared<T>(value:T)->T{value}");
        File.AppendAllText(Path.Combine(directory, "library", "Cargo.toml"), "[lib]\npath=\"../shared.rs\"\n");
        File.AppendAllText(Path.Combine(middle, "Cargo.toml"), "[lib]\npath=\"../shared.rs\"\n");
        WriteMain(directory, "fn main(){println!(\"{}\",dep::shared(middle::shared(42)));}");
        GenericPackageWorkspaceResult shared = GenericPackageWorkspace.Load(manifest, token);
        AssertEx.True(shared.IsSuccessful, string.Join("; ", shared.Diagnostics));
        AssertEx.Equal(2, shared.SourceMap!.Documents.Count, "Two crates sharing a source file need one canonical PDB document.");
        CompilationResult compiled = CompilerDriver.CompileFile(manifest, Path.Combine(directory, "shared.dll"),
            profile: CompilationProfile.SafeCoreGenerics, cancellationToken: token);
        AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        return Task.CompletedTask;
    });

    private static Task DiagnosticsAsync() => Workspace((directory, token) =>
    {
        string manifest = Create(directory, "pub fn invalid<T>(value:T)->bool{value}", "fn main(){}");
        CompilationResult result = CompilerDriver.CheckFile(manifest, CompilationProfile.SafeCoreGenerics, token);
        AssertEx.False(result.Success, "An unreachable invalid dependency body must fail.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "RSG1005" &&
            diagnostic.SourcePath == Path.Combine(directory, "library", "src", "lib.rs") && diagnostic.Span.Length == 5),
            "Dependency body failures must retain their original file and source span.");
        File.AppendAllText(Path.Combine(directory, "library", "Cargo.toml"), "[dependencies]\napp={path=\"../app\"}\n");
        GenericPackageWorkspaceResult cycle = GenericPackageWorkspace.Load(manifest, token);
        AssertEx.False(cycle.IsSuccessful, "Source-linked crates must reject dependency cycles.");
        string libraryRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "custom"));
        File.WriteAllText(Path.Combine(libraryRoot, "custom", "main.rs"), "pub fn value()->i32{1}");
        File.WriteAllText(Path.Combine(libraryRoot, "Cargo.toml"),
            "[package]\nname=\"generic-lib\"\nversion=\"0.1.0\"\n[lib]\npath=\"custom/main.rs\"\n");
        AssertEx.True(CompilerDriver.CheckFile(manifest, CompilationProfile.SafeCoreGenerics, token).Success,
            "An explicit library may use a source file named main.rs.");
        File.Delete(Path.Combine(libraryRoot, "src", "lib.rs"));
        File.WriteAllText(Path.Combine(libraryRoot, "custom", "program.rs"), "fn main(){}");
        File.WriteAllText(Path.Combine(libraryRoot, "Cargo.toml"),
            "[package]\nname=\"generic-lib\"\nversion=\"0.1.0\"\n[[bin]]\npath=\"custom/program.rs\"\n");
        AssertEx.False(GenericPackageWorkspace.Load(manifest, token).IsSuccessful,
            "A binary-only dependency cannot provide generic library templates.");
        File.WriteAllText(Path.Combine(directory, "app", "Cargo.toml"),
            "[package]\nname=\"" + new string('x', 1100) + "\"\nversion=\"0.1.0\"\n");
        AssertEx.False(CompilerDriver.CheckFile(manifest, CompilationProfile.SafeCoreGenerics, token).Success,
            "An oversized package identity must report a diagnostic rather than throw.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => GenericPackageWorkspace.Load(manifest, cancelled.Token));
        return Task.CompletedTask;
    });

    private static void Reject(string manifest, string code, CancellationToken token)
    {
        CompilationResult result = CompilerDriver.CheckFile(manifest, CompilationProfile.SafeCoreGenerics, token);
        AssertEx.False(result.Success, "The invalid crate graph must fail.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == code), string.Join("; ", result.Diagnostics));
    }

    private static string Create(string directory, string library, string main)
    {
        Directory.CreateDirectory(Path.Combine(directory, "app", "src"));
        Directory.CreateDirectory(Path.Combine(directory, "library", "src"));
        string manifest = Path.Combine(directory, "app", "Cargo.toml");
        File.WriteAllText(manifest, "[package]\nname=\"app\"\nversion=\"0.1.0\"\nedition=\"2024\"\n[dependencies]\ndep={path=\"../library\"}\n");
        File.WriteAllText(Path.Combine(directory, "library", "Cargo.toml"), "[package]\nname=\"generic-lib\"\nversion=\"0.1.0\"\nedition=\"2024\"\n");
        WriteMain(directory, main); WriteLibrary(directory, library); return manifest;
    }
    private static void WriteMain(string directory, string source) => File.WriteAllText(Path.Combine(directory, "app", "src", "main.rs"), source);
    private static void WriteLibrary(string directory, string source) => File.WriteAllText(Path.Combine(directory, "library", "src", "lib.rs"), source);
    private static async Task Workspace(Func<string, CancellationToken, Task> action)
    {
        string parent = Path.GetFullPath("artifacts/tests");
        string directory = Path.Combine(parent, "generic-packages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await action(directory, deadline.Token); }
        finally
        {
            AssertEx.Equal(parent, Path.GetDirectoryName(Path.GetFullPath(directory))!);
            AssertEx.True(Path.GetFileName(directory).StartsWith("generic-packages-", StringComparison.Ordinal), "Cleanup must own its run directory.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
