using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1DropProfileIntegrationTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCoreMirV2;
    private const SafeCoreDropCleanupProfile Native = SafeCoreDropCleanupProfile.NativeV2;
    private const SafeCoreDropCleanupProfile Legacy = SafeCoreDropCleanupProfile.LegacyV1;
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 Drop profile file compilation executes native cleanup and preserves legacy defaults", FileAsync),
        new("P1 Drop profile Cargo source graph applies the selected policy to dependency destructors", CargoAsync),
        new("P1 Drop profile matching independent producers and consumers execute both contracts", MatchingAsync),
        new("P1 Drop profile mixed direct references reject before changing existing output artifacts", MixedAsync),
        new("P1 Drop profile recursive nominal owners reject a mixed declaration behind a matching wrapper", RecursiveAsync),
        new("P1 Drop profile actual PE unknown and duplicate declarations reject", AttributesAsync),
        new("P1 Drop profile CLI check compile run and publish boundaries retain explicit selection", CliAsync),
        new("P1 Drop profile check compile and metadata selection reject incompatible profiles and cancellation", BoundariesAsync),
    ];

    private const string CleanupSource = """
        fn overflow(value: i32) -> i32 { value + 1 }
        fn divide(value: i32) -> i32 { 10 / value }
        struct Bad;
        impl Drop for Bad { fn drop(&mut self) { println!("bad"); println!("{}", overflow(2147483647)); } }
        struct Owner { divisor: i32, bad: Bad }
        impl Drop for Owner { fn drop(&mut self) { println!("owner"); println!("{}", divide(self.divisor)); } }
        fn main() { let owner = Owner { divisor: 0, bad: Bad }; println!("body"); println!("{}", overflow(2147483647)); }
        """;
    private const string ProducerSource = "pub fn add(left: i32, right: i32) -> i32 { left + right } fn main() {}";
    private const string ConsumerSource = "use DropProducer::add; fn main() { println!(\"{}\", add(20, 22)); }";

    private static Task FileAsync() => WithWorkspaceAsync(async (directory, token) =>
    {
        string source = Path.Combine(directory, "source.rs"), output = Path.Combine(directory, "Native.dll");
        await File.WriteAllTextAsync(source, CleanupSource, token).ConfigureAwait(false);
        Success(CompilerDriver.CheckFileWithDropProfile(source, Native, Profile, token));
        Success(CompilerDriver.CompileFileWithDropProfile(source, output, Native, "Native", Profile, token));
        AssertEx.Equal(Native, Read(output, Native, token).DropCleanupProfile);
        BoundedProcessResult executed = await RunAsync([output], directory, token).ConfigureAwait(false);
        AssertEx.Equal(134, executed.ExitCode ?? -1);
        AssertEx.Equal(OperatingSystem.IsLinux() ? "body\nowner\nbad\n" : "body\nowner\n", Normalize(executed.StandardOutput));
        string legacy = Path.Combine(directory, "Legacy.dll");
        Success(CompilerDriver.CompileFile(source, legacy, "Legacy", Profile, token));
        AssertEx.Equal(Legacy, Read(legacy, Legacy, token).DropCleanupProfile);
        BoundedProcessResult old = await RunAsync([legacy], directory, token).ConfigureAwait(false);
        AssertEx.Equal(134, old.ExitCode ?? -1);
        AssertEx.Equal("body\nowner\n", Normalize(old.StandardOutput));
    });

    private static Task CargoAsync() => WithWorkspaceAsync(async (directory, token) =>
    {
        string app = Path.Combine(directory, "app"), dependency = Path.Combine(directory, "dependency");
        Directory.CreateDirectory(Path.Combine(app, "src")); Directory.CreateDirectory(Path.Combine(dependency, "src"));
        string manifest = Path.Combine(app, "Cargo.toml");
        await File.WriteAllTextAsync(manifest, "[package]\nname = \"drop-app\"\nversion = \"0.1.0\"\nedition = \"2024\"\n[dependencies]\ndep = { path = \"../dependency\" }\n", token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dependency, "Cargo.toml"), "[package]\nname = \"dep\"\nversion = \"0.1.0\"\nedition = \"2024\"\n", token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dependency, "src", "lib.rs"), CleanupSource.Replace("fn main()", "pub fn execute()", StringComparison.Ordinal), token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(app, "src", "main.rs"), "fn main() { dep::execute(); }", token).ConfigureAwait(false);
        Success(CompilerDriver.CheckFileWithDropProfile(manifest, Native, Profile, token));
        string output = Path.Combine(directory, "CargoNative.dll");
        Success(CompilerDriver.CompileFileWithDropProfile(manifest, output, Native, "CargoNative", Profile, token));
        AssertEx.Equal(Native, Read(output, Native, token).DropCleanupProfile);
        BoundedProcessResult executed = await RunAsync([output], directory, token).ConfigureAwait(false);
        AssertEx.Equal(134, executed.ExitCode ?? -1);
        AssertEx.Equal(OperatingSystem.IsLinux() ? "body\nowner\nbad\n" : "body\nowner\n", Normalize(executed.StandardOutput));
    });

    private static Task MatchingAsync() => WithWorkspaceAsync(async (directory, token) =>
    {
        string producer = Emit(directory, "DropProducer", ProducerSource, Native, token);
        string consumer = Path.Combine(directory, "NativeConsumer.dll");
        Success(CompilerDriver.CheckWithMetadataReferences(ConsumerSource, "consumer.rs", Profile, Native, [producer], cancellationToken: token));
        Success(CompilerDriver.CompileWithMetadataReferences(ConsumerSource, "consumer.rs", consumer, "NativeConsumer", Profile, Native, [producer], cancellationToken: token));
        AssertEx.Equal(Native, Read(consumer, Native, token).DropCleanupProfile);
        BoundedProcessResult native = await RunAsync([consumer], directory, token).ConfigureAwait(false);
        AssertEx.True(native.Succeeded, native.StandardError); AssertEx.Equal("42\n", Normalize(native.StandardOutput));
        _ = Emit(directory, "DropProducer", ProducerSource, Legacy, token);
        string oldConsumer = Path.Combine(directory, "LegacyConsumer.dll");
        Success(CompilerDriver.CheckWithMetadataReferences(ConsumerSource, "consumer.rs", Profile, [producer], cancellationToken: token));
        Success(CompilerDriver.CompileWithMetadataReferences(ConsumerSource, "consumer.rs", oldConsumer, "LegacyConsumer", Profile, [producer], cancellationToken: token));
        AssertEx.Equal(Legacy, Read(oldConsumer, Legacy, token).DropCleanupProfile);
        BoundedProcessResult old = await RunAsync([oldConsumer], directory, token).ConfigureAwait(false);
        AssertEx.True(old.Succeeded, old.StandardError); AssertEx.Equal("42\n", Normalize(old.StandardOutput));
    });

    private static Task MixedAsync() => WithWorkspaceAsync((directory, token) =>
    {
        string producer = Emit(directory, "DropProducer", ProducerSource, Native, token);
        RustSharpMetadataImportResult oldImport = RustSharpMetadataConsumer.ReadAssembly(producer, cancellationToken: token);
        AssertEx.False(oldImport.IsSuccessful, "The old import API must retain its implicit LegacyV1 expectation.");
        AssertEx.True(oldImport.Diagnostics.Any(value => value.StartsWith("RSC0012:", StringComparison.Ordinal)), string.Join("; ", oldImport.Diagnostics));
        string output = Path.Combine(directory, "Consumer.dll");
        Success(CompilerDriver.Compile("fn main() { println!(\"preserved\"); }", "preserved.rs", output, "Consumer", Profile, token));
        string peHash = ArtifactHash(output), pdbHash = ArtifactHash(Path.ChangeExtension(output, ".pdb")),
            runtimeHash = ArtifactHash(Path.ChangeExtension(output, ".runtimeconfig.json"));
        CompilationResult check = CompilerDriver.CheckWithMetadataReferences(ConsumerSource, "consumer.rs", Profile, [producer], cancellationToken: token);
        CompilationResult compile = CompilerDriver.CompileWithMetadataReferences(ConsumerSource, "consumer.rs", output, "Consumer", Profile, [producer], cancellationToken: token);
        Mismatch(check); Mismatch(compile);
        UnchangedArtifacts(output, peHash, pdbHash, runtimeHash);
        _ = Emit(directory, "DropProducer", ProducerSource, Legacy, token);
        Mismatch(CompilerDriver.CheckWithMetadataReferences(ConsumerSource, "consumer.rs", Profile, Native, [producer], cancellationToken: token));
        Mismatch(CompilerDriver.CompileWithMetadataReferences(ConsumerSource, "consumer.rs", output, "Consumer", Profile, Native, [producer], cancellationToken: token));
        UnchangedArtifacts(output, peHash, pdbHash, runtimeHash);
        return Task.CompletedTask;
    });

    private static Task RecursiveAsync() => WithWorkspaceAsync((directory, token) =>
    {
        const string ownerSource = "pub struct Item { pub value: i32 } impl Drop for Item { fn drop(&mut self) { println!(\"drop\"); } } pub fn make() -> Item { Item { value: 42 } } fn main() {}";
        const string wrapperSource = "use DropOwner::{Item, make as original_make}; pub fn make() -> Item { original_make() } fn main() {}";
        string owner = Emit(directory, "DropOwner", ownerSource, Native, token), wrapper = Path.Combine(directory, "DropWrapper.dll");
        Success(CompilerDriver.CompileWithMetadataReferences(wrapperSource, "wrapper.rs", wrapper, "DropWrapper", Profile, Native, [owner], cancellationToken: token));
        RustSharpMetadataImportResult original = Read(wrapper, Native, token);
        AssertEx.True(original.ResolvedOwnerPaths.ContainsKey("DropOwner"), "The real wrapper must retain its independently checked destructor-owning nominal producer.");
        byte[] ownerImage = File.ReadAllBytes(owner);
        ReplaceAscii(ownerImage, SafeCoreDropCleanupProfiles.MetadataKey, "RustSharp.DropCleanupLegacyX");
        File.WriteAllBytes(owner, ownerImage);
        AssertEx.Equal(Legacy, Read(owner, Legacy, token).DropCleanupProfile);
        string oldHash = original.Document!.SourceValueTypes.Single(value => value.Owner is not null).Owner!.AssemblySha256;
        string newHash = Convert.ToHexString(SHA256.HashData(ownerImage));
        byte[] wrapperImage = File.ReadAllBytes(wrapper);
        ReplaceAscii(wrapperImage, oldHash, newHash);
        File.WriteAllBytes(wrapper, wrapperImage);
        RustSharpMetadataImportResult rejected = RustSharpMetadataConsumer.ReadAssembly(wrapper, Native, dependencyPaths: [owner], cancellationToken: token);
        AssertEx.False(rejected.IsSuccessful, "A matching wrapper cannot hide a mismatched recursive owner's actual PE declaration, even when the owner hash is reconciled.");
        string output = Path.Combine(directory, "Rejected.dll");
        CompilationResult consumer = CompilerDriver.CompileWithMetadataReferences("use DropWrapper::make; fn main() { let item = make(); println!(\"{}\", item.value); }", "consumer.rs", output, "Rejected", Profile, Native, [wrapper], cancellationToken: token);
        AssertEx.False(consumer.Success, "Recursive graph validation must precede emission.");
        AssertEx.False(File.Exists(output), "A mixed recursive graph must create no output PE.");
        return Task.CompletedTask;
    });

    private static Task AttributesAsync() => WithWorkspaceAsync((directory, token) =>
    {
        string producer = Emit(directory, "DropProducer", ProducerSource, Native, token);
        byte[] original = File.ReadAllBytes(producer), unknown = (byte[])original.Clone();
        ReplaceAscii(unknown, "selection=NativeV2", "selection=NativeV9"); File.WriteAllBytes(producer, unknown);
        RustSharpMetadataImportResult rejected = RustSharpMetadataConsumer.ReadAssembly(producer, Native, cancellationToken: token);
        AssertEx.False(rejected.IsSuccessful, "Unknown explicit declarations cannot fall back to legacy.");
        AssertEx.True(rejected.Diagnostics.Any(value => value.StartsWith("RSC0011:", StringComparison.Ordinal)), string.Join("; ", rejected.Diagnostics));
        byte[] duplicate = (byte[])original.Clone(); DuplicateDeclaration(duplicate, token); File.WriteAllBytes(producer, duplicate);
        RustSharpMetadataImportResult repeated = RustSharpMetadataConsumer.ReadAssembly(producer, Native, cancellationToken: token);
        AssertEx.False(repeated.IsSuccessful, "Duplicate declarations cannot choose their last value.");
        AssertEx.True(repeated.Diagnostics.Any(value => value.Contains("duplicate Drop cleanup declarations", StringComparison.Ordinal)), string.Join("; ", repeated.Diagnostics));
        return Task.CompletedTask;
    });

    private static Task CliAsync() => WithWorkspaceAsync(async (directory, token) =>
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string cli = Path.Combine(RepositoryRoot(), "src", "RustSharp.Cli", "bin", configuration, "net10.0", "rsc.dll");
        AssertEx.True(File.Exists(cli), "Build the solution before CLI integration checks.");
        string source = Path.Combine(directory, "cli.rs"), output = Path.Combine(directory, "CliNative.dll");
        await File.WriteAllTextAsync(source, "fn main() { println!(\"ready\"); }", token).ConfigureAwait(false);
        BoundedProcessResult check = await RunAsync([cli, "check", source, "--profile", SafeCoreMirPipeline.ProfileV2, "--drop-cleanup-profile", "native-v2"], directory, token).ConfigureAwait(false);
        AssertEx.True(check.Succeeded, check.StandardError);
        BoundedProcessResult compile = await RunAsync([cli, "compile", source, "--profile", SafeCoreMirPipeline.ProfileV2, "--drop-cleanup-profile", "native-v2", "--output", output], directory, token).ConfigureAwait(false);
        AssertEx.True(compile.Succeeded, compile.StandardError); AssertEx.Equal(Native, Read(output, Native, token).DropCleanupProfile);
        BoundedProcessResult imported = await RunAsync([cli, "check", source, "--profile", SafeCoreMirPipeline.ProfileV2,
            "--drop-cleanup-profile", "native-v2", "--reference", output], directory, token).ConfigureAwait(false);
        AssertEx.True(imported.Succeeded, imported.StandardError);
        BoundedProcessResult mixed = await RunAsync([cli, "check", source, "--profile", SafeCoreMirPipeline.ProfileV2,
            "--reference", output], directory, token).ConfigureAwait(false);
        AssertEx.Equal(1, mixed.ExitCode ?? -1); AssertEx.True(mixed.StandardError.Contains("RSC0012", StringComparison.Ordinal), mixed.StandardError);
        BoundedProcessResult run = await RunAsync([cli, "run", source, "--profile", SafeCoreMirPipeline.ProfileV2, "--drop-cleanup-profile", "native-v2", "--output", output], directory, token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && Normalize(run.StandardOutput).EndsWith("ready\n", StringComparison.Ordinal), run.StandardError);
        BoundedProcessResult publish = await RunAsync([cli, "publish", source, "--drop-cleanup-profile", "native-v2", "--output", Path.Combine(directory, "publish")], directory, token).ConfigureAwait(false);
        AssertEx.Equal(2, publish.ExitCode ?? -1); AssertEx.False(Directory.Exists(Path.Combine(directory, "publish")), "An incompatible publish selection must reject before creating an AOT host.");
        BoundedProcessResult duplicate = await RunAsync([cli, "check", source, "--profile", SafeCoreMirPipeline.ProfileV2, "--drop-cleanup-profile", "native-v2", "--drop-cleanup-profile", "legacy-v1"], directory, token).ConfigureAwait(false);
        AssertEx.Equal(2, duplicate.ExitCode ?? -1);
        BoundedProcessResult combined = await RunAsync([cli, "check", Path.Combine(directory, "Cargo.toml"), "--profile", SafeCoreMirPipeline.ProfileV2, "--reference", output], directory, token).ConfigureAwait(false);
        AssertEx.Equal(2, combined.ExitCode ?? -1); AssertEx.True(combined.StandardError.Contains("Combining Cargo source packages", StringComparison.Ordinal), combined.StandardError);
    });

    private static Task BoundariesAsync() => WithWorkspaceAsync((directory, token) =>
    {
        string output = Path.Combine(directory, "Unsupported.dll");
        CompilationResult check = CompilerDriver.CheckWithDropProfile("fn main() {}", "source.rs", Native, CompilationProfile.VerticalSlice, token);
        CompilationResult compile = CompilerDriver.CompileFileWithDropProfile("source.rs", output, Native, profile: CompilationProfile.VerticalSlice, cancellationToken: token);
        AssertEx.False(check.Success || compile.Success, "Checking and emission must reject an incompatible capability identically.");
        AssertEx.Equal("RSC0010", check.Diagnostics.Single().Code); AssertEx.Equal("RSC0010", compile.Diagnostics.Single().Code);
        AssertEx.False(File.Exists(output), "Unsupported selections must not publish partial output.");
        AssertEx.Throws<ArgumentOutOfRangeException>(() => CompilerDriver.CheckWithDropProfile("fn main() {}", "source.rs", (SafeCoreDropCleanupProfile)99));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => RustSharpMetadataConsumer.ReadAssembly("producer.dll", (SafeCoreDropCleanupProfile)99));
        string producer = Emit(directory, "DropProducer", ProducerSource, Native, token);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CompilerDriver.CheckWithMetadataReferences(ConsumerSource, "source.rs", Profile, Native, [producer], cancellationToken: cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => RustSharpMetadataConsumer.ReadAssembly(producer, Native, cancellationToken: cancelled.Token));
        return Task.CompletedTask;
    });

    private static void DuplicateDeclaration(byte[] image, CancellationToken token)
    {
        using var stream = new MemoryStream(image, writable: false); using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        CustomAttributeHandle source = default, target = default;
        var clock = Stopwatch.StartNew();
        CustomAttributeHandleCollection attributes = metadata.GetAssemblyDefinition().GetCustomAttributes();
        AssertEx.True(attributes.Count <= 256, "Attribute mutation has a fixed row bound.");
        foreach (CustomAttributeHandle handle in attributes)
        {
            token.ThrowIfCancellationRequested(); AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(2), "Attribute mutation exceeded two seconds.");
            BlobReader blob = metadata.GetBlobReader(metadata.GetCustomAttribute(handle).Value);
            if (blob.ReadUInt16() != 1) continue;
            string? key = blob.ReadSerializedString();
            if (key == SafeCoreDropCleanupProfiles.MetadataKey) source = handle;
            if (key == RustSharpMetadataReader.AttributeKey) target = handle;
        }
        AssertEx.True(!source.IsNil && !target.IsNil, "The real PE must contain both independent metadata declarations.");
        int blobs = metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? sizeof(int) : sizeof(ushort);
        int rowSize = metadata.GetTableRowSize(TableIndex.CustomAttribute);
        int offset = checked(pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.CustomAttribute) +
            (MetadataTokens.GetRowNumber(target) - 1) * rowSize + rowSize - blobs);
        int replacement = MetadataTokens.GetHeapOffset(metadata.GetCustomAttribute(source).Value);
        if (blobs == sizeof(ushort)) BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset, blobs), checked((ushort)replacement));
        else BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset, blobs), replacement);
    }

    private static void ReplaceAscii(byte[] image, string from, string to)
    {
        byte[] original = Encoding.UTF8.GetBytes(from), replacement = Encoding.UTF8.GetBytes(to);
        AssertEx.Equal(original.Length, replacement.Length, "A mutation must preserve actual PE heap boundaries.");
        int offset = image.AsSpan().IndexOf(original);
        AssertEx.True(offset >= 0, "The actual PE must contain the exact declaration being mutated.");
        replacement.CopyTo(image.AsSpan(offset));
    }

    private static string Emit(string directory, string name, string source, SafeCoreDropCleanupProfile profile, CancellationToken token)
    {
        string path = Path.Combine(directory, name + ".dll");
        Success(CompilerDriver.CompileWithDropProfile(source, Path.Combine(directory, name + ".rs"), path, profile,
            assemblyName: name, profile: Profile, cancellationToken: token));
        return path;
    }
    private static RustSharpMetadataImportResult Read(string path, SafeCoreDropCleanupProfile profile, CancellationToken token)
    {
        RustSharpMetadataImportResult result = RustSharpMetadataConsumer.ReadAssembly(path, profile, cancellationToken: token);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics)); return result;
    }
    private static void Success(CompilationResult result) => AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
    private static string ArtifactHash(string path)
    {
        AssertEx.True(new FileInfo(path).Length <= 16 * 1024 * 1024, "Fixture artifact hash reads are bounded.");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
    private static void UnchangedArtifacts(string output, string pe, string pdb, string runtime)
    {
        AssertEx.Equal(pe, ArtifactHash(output), "Rejected imports must preserve the actual original PE.");
        AssertEx.Equal(pdb, ArtifactHash(Path.ChangeExtension(output, ".pdb")), "Rejected imports must preserve the actual original PDB.");
        AssertEx.Equal(runtime, ArtifactHash(Path.ChangeExtension(output, ".runtimeconfig.json")), "Rejected imports must preserve the actual original runtime configuration sidecar.");
    }
    private static void Mismatch(CompilationResult result)
    {
        AssertEx.False(result.Success, "A producer cleanup contract mismatch must fail closed.");
        AssertEx.True(result.Diagnostics.Any(value => value.Code == "RSC0012"), string.Join("; ", result.Diagnostics));
    }
    private static async Task<BoundedProcessResult> RunAsync(IReadOnlyList<string> arguments, string directory, CancellationToken token)
    {
        string? configured = Environment.GetEnvironmentVariable("RUSTSHARP_DOTNET_PATH");
        BoundedProcessResult result = await new BoundedProcessRunner().RunAsync(new(
            string.IsNullOrWhiteSpace(configured) ? OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet" : configured,
            arguments, directory, TimeSpan.FromSeconds(10), started => Console.WriteLine(
                $"Drop profile process: pid={started.ProcessId} parent={started.ParentProcessId} start={started.StartedAt:O} command={started.CommandLine}")), token).ConfigureAwait(false);
        AssertEx.Equal(BoundedProcessTermination.Exited, result.Termination);
        AssertEx.False(result.ProcessTreeCleanupIncomplete || result.OutputTruncated || result.OutputDrainTimedOut, "Incomplete owned process evidence cannot pass integration.");
        return result;
    }
    private static async Task WithWorkspaceAsync(Func<string, CancellationToken, Task> action)
    {
        string parent = Path.Combine(RepositoryRoot(), "artifacts", "tests");
        string directory = Path.Combine(parent, "p1-drop-profile-" + Guid.NewGuid().ToString("N"));
        AssertEx.False(Directory.Exists(directory), "A fixture must own a new exclusive directory.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            string fullPath = Path.GetFullPath(directory), prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
            AssertEx.True(fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Path.GetFileName(fullPath).StartsWith("p1-drop-profile-", StringComparison.Ordinal), "Only this fixture's verified owned directory may be removed.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
