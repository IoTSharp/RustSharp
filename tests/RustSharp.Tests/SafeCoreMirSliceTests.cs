using RustSharp.Compiler;
using RustSharp.CodeGen.IL;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

/// <summary>Bounded executable coverage for array-to-slice unsizing.</summary>
internal static class SafeCoreMirSliceTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCoreMirV2;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR v2 lowers a full array slice, length and constant index", RunFullSliceAsync),
        new("MIR v2 preserves slice provenance in deterministic MIR", SnapshotAsync),
        new("MIR v2 executes dynamic full-array slice indexes", DynamicIndexAsync),
        new("MIR v2 dynamic full-array slice indexes trap outside their bounds", DynamicBoundsAsync),
        new("MIR v2 rejects an out-of-bounds empty slice index", EmptyBoundsAsync),
    ];

    private static Task RunFullSliceAsync() => WithWorkspaceAsync(async (directory, token) =>
    {
        const string source = "fn main() { let values = [10, 20, 30]; let sized: &[i32; 3] = &values; let view: &[i32] = sized; println!(\"{}\", view.len()); println!(\"{}\", view[1]); }";
        string output = Path.Combine(directory, "slice.dll");
        CompilationResult compiled = CompilerDriver.Compile(source, "slice-run.rs", output,
            assemblyName: "SliceRun", profile: Profile, cancellationToken: token);
        AssertEx.True(compiled.Success, Format(compiled.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10),
                static started => Console.WriteLine($"slice PID={started.ProcessId}; parent={started.ParentProcessId}; started={started.StartedAt:O}; timeout=10s")), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        AssertEx.Equal("3\n20\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });

    private static Task SnapshotAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string source = "fn main() { let values = [1, 2]; let view: &[i32] = &values; println!(\"{}\", view.len()); println!(\"{}\", view[0]); }";
        SafeCoreMirPipelineResult first = SafeCoreMirPipeline.Analyze(source, "slice-snapshot.rs", new()
        {
            EnableRepeatedArrays = true,
            EnableP1Extensions = true,
            RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5),
            CancellationToken = deadline.Token,
        });
        SafeCoreMirPipelineResult second = SafeCoreMirPipeline.Analyze(source, "slice-snapshot.rs", new()
        {
            EnableRepeatedArrays = true,
            EnableP1Extensions = true,
            RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5),
            CancellationToken = deadline.Token,
        });
        AssertEx.True(first.IsSuccessful, Format(first.Diagnostics));
        AssertEx.True(second.IsSuccessful, Format(second.Diagnostics));
        AssertEx.Equal(first.MirSnapshot!, second.MirSnapshot!);
        AssertEx.True(first.MirSnapshot!.Contains("slicelength", StringComparison.Ordinal), first.MirSnapshot);
        AssertEx.True(first.MirSnapshot.Contains("index(%", StringComparison.Ordinal), first.MirSnapshot);
        AssertEx.True(first.MirSnapshot.Contains("&[i32]", StringComparison.Ordinal), first.MirSnapshot);
        return Task.CompletedTask;
    }

    private static Task DynamicIndexAsync() => WithWorkspaceAsync(async (directory, token) =>
    {
        const string source = "fn main() { let values = [10, 20]; let view: &[i32] = &values; let index: usize = 1; println!(\"{}\", view[index]); }";
        string output = Path.Combine(directory, "slice.dll");
        CompilationResult result = CompilerDriver.Compile(source, "slice-dynamic.rs", output,
            assemblyName: "DynamicSlice", profile: Profile, cancellationToken: token);
        AssertEx.True(result.Success, Format(result.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10),
                static started => Console.WriteLine($"dynamic slice PID={started.ProcessId}; parent={started.ParentProcessId}; started={started.StartedAt:O}; timeout=10s")), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        AssertEx.Equal("20\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });

    private static Task DynamicBoundsAsync() => WithWorkspaceAsync(async (directory, token) =>
    {
        const string source = "fn main() { let values = [10, 20]; let view: &[i32] = &values; let index: usize = 2; println!(\"{}\", view[index]); }";
        string output = Path.Combine(directory, "slice.dll");
        CompilationResult result = CompilerDriver.Compile(source, "slice-dynamic-bounds.rs", output,
            assemblyName: "DynamicSliceBounds", profile: Profile, cancellationToken: token);
        AssertEx.True(result.Success, Format(result.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10),
                static started => Console.WriteLine($"slice bounds PID={started.ProcessId}; parent={started.ParentProcessId}; started={started.StartedAt:O}; timeout=10s")), token).ConfigureAwait(false);
        AssertEx.False(run.Succeeded, "A dynamic out-of-range slice index must fail at runtime.");
        AssertEx.False(run.ProcessTreeCleanupIncomplete, "The bounded runtime must clean its process tree.");
        AssertEx.True(run.StandardError.Contains("IndexOutOfRangeException", StringComparison.Ordinal), run.StandardError);
    });

    private static Task EmptyBoundsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string source = "fn main() { let values: [i32; 0] = []; let view: &[i32] = &values; println!(\"{}\", view[0]); }";
        CompilationResult result = CompilerDriver.Check(source, "slice-empty.rs", Profile, deadline.Token);
        AssertEx.False(result.Success, "An empty full-array slice must reject a statically out-of-bounds index.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirClrLowering.Invalid),
            Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ",
        diagnostics.Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Message));

    private static async Task WithWorkspaceAsync(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "mir-slice-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Slice cleanup may delete only its own workspace.");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
