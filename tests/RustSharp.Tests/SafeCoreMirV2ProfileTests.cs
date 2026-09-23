using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class SafeCoreMirV2ProfileTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCoreMirV2;
    private const string Source = "fn seed() -> i32 { println!(\"seed\"); 7 } " +
        "fn main() { let values = [seed(); 3]; println!(\"{}\", values[0]); " +
        "println!(\"{}\", values[2]); let empty: [i32; 0] = [seed(); 0]; }";
    private const string ExpectedOutput = "seed\n7\n7\nseed\n";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR v2 preserves the v1 repeated-array rejection contract", PreservesV1Async),
        new("MIR v2 produces deterministic source ownership and cleanup evidence", EvidenceAsync),
        new("MIR v2 emits deterministic metadata and evaluates repeated operands once", CompilesAsync),
        new("MIR v2 CLI checks compiles and executes the versioned profile", CliAsync),
        new("MIR v2 repeated arrays obey length work snapshot and cancellation bounds", BoundsAsync),
    ];

    private static Task PreservesV1Async() => WithWorkspace((directory, token) =>
    {
        const string source = "fn main() { let values = [7; 3]; println!(\"{}\", values[2]); }";
        CompilationResult legacy = CompilerDriver.Check(source, "v1-repeat.rs", CompilationProfile.SafeCoreMir, token);
        AssertEx.False(legacy.Success, "The v1 profile must retain its repeated-array rejection contract.");
        AssertEx.Equal(SafeCoreMirLowering.UnsupportedSyntax, legacy.Diagnostics.Single().Code);
        AssertEx.Equal("[7; 3]", source.Substring(legacy.Diagnostics[0].Span.Start, legacy.Diagnostics[0].Span.Length));
        string rejectedOutput = Path.Combine(directory, "rejected", "program.dll");
        CompilationResult rejected = CompilerDriver.Compile(source, "v1-repeat.rs", rejectedOutput,
            profile: CompilationProfile.SafeCoreMir, cancellationToken: token);
        AssertEx.False(rejected.Success, "The v1 compiler must reject before producing output.");
        AssertEx.Equal(SafeCoreMirLowering.UnsupportedSyntax, rejected.Diagnostics.Single().Code);
        AssertEx.False(Directory.Exists(Path.GetDirectoryName(rejectedOutput)), "Rejected v1 output must not be created.");
        CompilationResult accepted = CompilerDriver.Check(source, "v2-repeat.rs", Profile, token);
        AssertEx.True(accepted.Success, string.Join("; ", accepted.Diagnostics));
        AssertEx.True(accepted.Output is null, "V2 checking must not emit artifacts.");
        return Task.CompletedTask;
    });

    private static Task EvidenceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        const string source = "fn main() { let values = [7; 3]; println!(\"{}\", values[1]); }";
        SafeCoreMirPipelineOptions options = new()
        {
            EnableRepeatedArrays = true,
            RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5),
            CancellationToken = deadline.Token,
        };
        SafeCoreMirPipelineResult first = SafeCoreMirPipeline.Analyze(source, "mir-v2-evidence.rs", options);
        SafeCoreMirPipelineResult second = SafeCoreMirPipeline.Analyze(source, "mir-v2-evidence.rs", options);
        AssertEx.True(first.IsSuccessful, string.Join("; ", first.Diagnostics));
        AssertEx.True(second.IsSuccessful, string.Join("; ", second.Diagnostics));
        AssertEx.Equal(first.MirSnapshot!, second.MirSnapshot!);
        AssertEx.Equal(first.Cleanup!.Snapshot!, second.Cleanup!.Snapshot!);
        AssertEx.True(first.Ownership!.IsSuccessful && second.Ownership!.IsSuccessful,
            "V2 must retain validated ownership evidence in both runs.");
        AssertEx.True(first.MirSnapshot!.Contains("array(const 7:i32, const 7:i32, const 7:i32)", StringComparison.Ordinal),
            "V2 evidence must explicitly represent repeated operands.");
        AssertEx.True(first.MirSnapshot.Contains("mir-v2-evidence.rs:", StringComparison.Ordinal),
            "V2 snapshots must preserve source mapping.");
        SafeCoreMirPipelineResult legacy = SafeCoreMirPipeline.Analyze(source, "mir-v2-evidence.rs",
            options with { EnableRepeatedArrays = false });
        AssertEx.False(legacy.IsSuccessful, "The default pipeline contract must remain v1.");
        AssertEx.Equal(SafeCoreMirLowering.UnsupportedSyntax, legacy.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task CompilesAsync() => WithWorkspace(async (directory, token) =>
    {
        string output = Path.Combine(directory, "program.dll");
        CompilationResult first = CompilerDriver.Compile(Source, "mir-v2-runtime.rs", output,
            assemblyName: "MirV2", profile: Profile, cancellationToken: token);
        AssertEx.True(first.Success, string.Join("; ", first.Diagnostics));
        byte[] firstImage = await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
        byte[] firstPdb = await File.ReadAllBytesAsync(Path.ChangeExtension(output, ".pdb"), token).ConfigureAwait(false);
        RustSharpMetadataDocument document = ReadMetadata(output);
        AssertEx.True(document.MirSnapshot is not null && document.CleanupSnapshot is not null && document.Ownership.Length > 0,
            "V2 metadata must carry MIR, ownership and cleanup evidence.");
        CompilationResult second = CompilerDriver.Compile(Source, "mir-v2-runtime.rs", output,
            assemblyName: "MirV2", profile: Profile, cancellationToken: token);
        AssertEx.True(second.Success, string.Join("; ", second.Diagnostics));
        byte[] secondImage = await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
        byte[] secondPdb = await File.ReadAllBytesAsync(Path.ChangeExtension(output, ".pdb"), token).ConfigureAwait(false);
        AssertEx.True(firstImage.SequenceEqual(secondImage),
            "Identical v2 inputs must emit identical PE images.");
        AssertEx.True(firstPdb.SequenceEqual(secondPdb),
            "Identical v2 inputs must emit identical source-mapped PDB images.");
        AssertEx.Equal(document.MirSnapshot!, ReadMetadata(output).MirSnapshot!);
        BoundedProcessResult run = await RunAsync([output], directory, token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded, run.StandardError);
        AssertEx.Equal(ExpectedOutput, Normalize(run.StandardOutput));
    });

    private static Task CliAsync() => WithWorkspace(async (directory, token) =>
    {
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string cli = Path.Combine(repository, "src", "RustSharp.Cli", "bin", configuration, "net10.0", "rsc.dll");
        AssertEx.True(File.Exists(cli), "Build the solution before running the v2 CLI tests.");
        string source = Path.Combine(directory, "source.rs");
        await File.WriteAllTextAsync(source, Source, token).ConfigureAwait(false);
        BoundedProcessResult help = await RunAsync([cli, "--help"], directory, token).ConfigureAwait(false);
        AssertEx.True(help.Succeeded && help.StandardOutput.Contains(SafeCoreMirPipeline.ProfileV2, StringComparison.Ordinal),
            "CLI help must advertise the versioned v2 profile.");
        BoundedProcessResult check = await RunAsync([cli, "check", source, "--profile", SafeCoreMirPipeline.ProfileV2],
            directory, token).ConfigureAwait(false);
        AssertEx.True(check.Succeeded, check.StandardError);
        BoundedProcessResult legacy = await RunAsync([cli, "check", source, "--profile", SafeCoreMirPipeline.Profile],
            directory, token).ConfigureAwait(false);
        AssertEx.Equal(1, legacy.ExitCode!.Value);
        AssertEx.True(legacy.StandardError.Contains(SafeCoreMirLowering.UnsupportedSyntax, StringComparison.Ordinal), legacy.StandardError);
        string output = Path.Combine(directory, "cli", "program.dll");
        BoundedProcessResult compile = await RunAsync([cli, "compile", source, "--profile", SafeCoreMirPipeline.ProfileV2,
            "--output", output], directory, token).ConfigureAwait(false);
        AssertEx.True(compile.Succeeded, compile.StandardError);
        _ = ReadMetadata(output);
        BoundedProcessResult run = await RunAsync([output], directory, token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded, run.StandardError);
        AssertEx.Equal(ExpectedOutput, Normalize(run.StandardOutput));
    });

    private static Task BoundsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string acceptedSource = "fn main() { let single = [7; 1]; let maximum = [7; 256]; println!(\"{}\", maximum[255]); }";
        CompilationResult accepted = CompilerDriver.Check(acceptedSource, "v2-bounds.rs", Profile, deadline.Token);
        AssertEx.True(accepted.Success, string.Join("; ", accepted.Diagnostics));
        CompilationResult overLimit = CompilerDriver.Check("fn main() { let values = [7; 257]; }", "v2-too-large.rs", Profile, deadline.Token);
        AssertEx.False(overLimit.Success, "V2 must not exceed the CLR aggregate layout bound.");
        AssertEx.Equal(SafeCoreMirClrLowering.LimitReached, overLimit.Diagnostics.Single().Code);
        CompilationResult nonCopy = CompilerDriver.Check("struct Item(i32); fn main() { let values = [Item(1); 2]; }",
            "v2-non-copy.rs", Profile, deadline.Token);
        AssertEx.False(nonCopy.Success, "V2 must not duplicate an ownership-bearing value.");
        AssertEx.Equal("RST2001", nonCopy.Diagnostics.Single().Code);
        const string source = "fn main() { let values = [7; 3]; }";
        SafeCoreMirPipelineOptions options = new()
        {
            EnableRepeatedArrays = true,
            MaximumSnapshotCharacters = 1,
            CancellationToken = deadline.Token,
        };
        SafeCoreMirPipelineResult snapshotLimit = SafeCoreMirPipeline.Analyze(source, "v2-limits.rs", options);
        AssertEx.False(snapshotLimit.IsSuccessful, "V2 must enforce the shared snapshot bound.");
        AssertEx.True(snapshotLimit.IsTruncated && snapshotLimit.MirSnapshot is null,
            "An exhausted snapshot budget must publish no partial snapshot.");
        SafeCoreMirPipelineResult workLimit = SafeCoreMirPipeline.Analyze(source, "v2-limits.rs",
            options with { MaximumSnapshotCharacters = 4_000_000, MaximumOperations = 1 });
        AssertEx.False(workLimit.IsSuccessful, "V2 must enforce the shared operation bound.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CompilerDriver.Check(source, "v2-cancelled.rs", Profile, cancelled.Token));
        return Task.CompletedTask;
    }

    private static RustSharpMetadataDocument ReadMetadata(string output)
    {
        RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(output, SafeCoreMirPipeline.ProfileV2);
        AssertEx.True(imported.IsSuccessful, string.Join("; ", imported.Diagnostics));
        AssertEx.Equal(SafeCoreMirPipeline.ProfileV2, imported.Document!.Profile);
        return imported.Document;
    }

    private static async Task<BoundedProcessResult> RunAsync(string[] arguments, string directory, CancellationToken token)
    {
        BoundedProcessResult result = await new BoundedProcessRunner().RunAsync(
            new("dotnet", arguments, directory, TimeSpan.FromSeconds(10),
                static started => Console.WriteLine($"MIR v2 process PID={started.ProcessId}; parent={started.ParentProcessId}; " +
                    $"started={started.StartedAt:O}; command={started.CommandLine}; timeout=10s")), token).ConfigureAwait(false);
        AssertEx.False(result.ProcessTreeCleanupIncomplete || result.OutputTruncated || result.OutputReadTimedOut || result.OutputDrainTimedOut,
            "V2 process capture and cleanup must complete.");
        AssertEx.Equal(BoundedProcessTermination.Exited, result.Termination);
        return result;
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task WithWorkspace(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "mir-v2-profile-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                Path.GetFileName(directory).StartsWith("mir-v2-profile-", StringComparison.Ordinal),
                "Cleanup must target only this v2 test's owned workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
