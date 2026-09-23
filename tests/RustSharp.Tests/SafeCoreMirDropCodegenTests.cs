using RustSharp.Compiler;

namespace RustSharp.Tests;

/// <summary>
/// Real generated-assembly Drop checks.  The semantic cleanup snapshots are
/// covered separately; these tests execute the emitted PE so a backend can not
/// claim Drop support from metadata alone.
/// </summary>
internal static class SafeCoreMirDropCodegenTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generated MIR Drop runs exactly once in reverse order", ReverseOrderAsync),
        new("generated MIR Drop runs on an exception fault path", FaultPathAsync),
        new("generated MIR Drop rejects unsupported destructor failure stably", DestructorFailureBoundaryAsync),
    ];

    private static async Task ReverseOrderAsync()
    {
        const string source = """
            struct First;
            impl Drop for First { fn drop(&mut self) { println!("first"); } }
            struct Second;
            impl Drop for Second { fn drop(&mut self) { println!("second"); } }
            fn emit() { let first = First; let second = Second; println!("body"); }
            fn main() { emit(); }
            """;
        BoundedProcessResult result = await CompileAndRunAsync(source, "body\nsecond\nfirst\n");
        AssertEx.True(result.Succeeded, "Generated Drop program failed: " + result.StandardError);
        AssertEx.Equal("body\nsecond\nfirst\n", Normalize(result.StandardOutput));
    }

    private static async Task FaultPathAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            fn emit() { let marker = Marker; let max: i32 = 2147483647; println!("{}", max + 1); }
            fn main() { emit(); }
            """;
        BoundedProcessResult result = await CompileAndRunAsync(source, expectedOutput: null);
        AssertEx.False(result.Succeeded, "An overflowing body must propagate its panic/exception.");
        AssertEx.Equal("drop\n", Normalize(result.StandardOutput));
        AssertEx.True(result.StandardError.Contains("OverflowException", StringComparison.Ordinal) ||
            result.StandardError.Contains("Unhandled exception", StringComparison.Ordinal),
            "The fault path must retain the original exception boundary: " + result.StandardError);
    }

    private static Task DestructorFailureBoundaryAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker {
                fn drop(&mut self) {
                    let max: i32 = 2147483647;
                    println!("{}", max + 1);
                }
            }
            fn main() { let marker = Marker; }
            """;
        CompilationResult result = CompilerDriver.Check(
            source, "drop-destructor-failure.rs", CompilationProfile.SafeCoreMirV2);
        AssertEx.False(result.Success,
            "A destructor body outside the generated failure contract must not compile silently.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "RSM2002"),
            "Unsupported destructor failure must retain the stable MIR diagnostic: " +
            string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        return Task.CompletedTask;
    }

    private static async Task<BoundedProcessResult> CompileAndRunAsync(string source, string? expectedOutput)
    {
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "mir-drop-codegen-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source, sourcePath, outputPath, "MirDropCodegen", CompilationProfile.SafeCoreMirV2,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "Generated Drop compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult result = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10)), deadline.Token)
                .ConfigureAwait(false);
            if (expectedOutput is not null)
                AssertEx.Equal(expectedOutput, Normalize(result.StandardOutput));
            return result;
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
