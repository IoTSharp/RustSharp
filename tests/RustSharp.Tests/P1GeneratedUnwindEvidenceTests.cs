using RustSharp.Compiler;

namespace RustSharp.Tests;

/// <summary>
/// Evidence that emitted PE methods carry Drop obligations across calls and
/// return/fault edges.  These fixtures intentionally use the currently
/// supported overflow exception as the generated panic trigger; an explicit
/// unsupported-panic diagnostic is retained below so the boundary cannot be
/// mistaken for a source-level panic implementation.
/// </summary>
internal static class P1GeneratedUnwindEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 generated nested calls unwind each owner once", NestedCallUnwindAsync),
        new("P1 generated return paths clean nested scopes once", NestedReturnCleanupAsync),
        new("P1 generated argument panic cleans the caller scope once", ArgumentPanicUnwindAsync),
        new("P1 unsupported source panic keeps a stable diagnostic boundary", UnsupportedPanicBoundaryAsync),
    ];

    private static async Task NestedCallUnwindAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            fn inner() { let marker = Marker; let max: i32 = 2147483647; println!("{}", max + 1); }
            fn outer() { let owner = Marker; inner(); }
            fn main() { outer(); }
            """;

        BoundedProcessResult result = await CompileAndRunAsync(source, "nested-call");
        AssertEx.False(result.Succeeded, "The nested generated body must propagate its overflow panic.");
        AssertEx.Equal("drop\ndrop\n", Normalize(result.StandardOutput));
        AssertEx.True(
            result.StandardError.Contains("OverflowException", StringComparison.Ordinal),
            "The generated nested call must retain the originating exception type: " + result.StandardError);
    }

    private static async Task NestedReturnCleanupAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            fn inner() { let marker = Marker; return; }
            fn outer() { let owner = Marker; inner(); println!("after"); }
            fn main() { outer(); }
            """;

        BoundedProcessResult result = await CompileAndRunAsync(source, "nested-return");
        AssertEx.True(result.Succeeded, "The generated return path failed: " + result.StandardError);
        AssertEx.Equal("drop\nafter\ndrop\n", Normalize(result.StandardOutput));
    }

    private static async Task ArgumentPanicUnwindAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            fn consume(value: i32) { println!("value"); }
            fn outer() { let owner = Marker; let max: i32 = 2147483647; consume(max + 1); println!("after"); }
            fn main() { outer(); }
            """;

        BoundedProcessResult result = await CompileAndRunAsync(source, "argument-panic");
        AssertEx.False(result.Succeeded, "An overflowing call argument must propagate its panic.");
        AssertEx.Equal("drop\n", Normalize(result.StandardOutput));
        AssertEx.True(
            result.StandardError.Contains("OverflowException", StringComparison.Ordinal),
            "Argument evaluation must retain the originating exception type: " + result.StandardError);
    }

    private static Task UnsupportedPanicBoundaryAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            fn inner() { panic!("boom"); }
            fn main() { let marker = Marker; inner(); }
            """;

        CompilationResult result = CompilerDriver.Check(
            source, "p1-generated-panic-boundary.rs", CompilationProfile.SafeCoreMirV2);
        AssertEx.False(result.Success, "The unsupported source panic must not compile silently.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == "RSP1003"),
            "Unsupported panic syntax must preserve RSP1003: " +
            string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("expression continuation", StringComparison.Ordinal)),
            "The rejection must explain the unsupported panic boundary.");
        return Task.CompletedTask;
    }

    private static async Task<BoundedProcessResult> CompileAndRunAsync(string source, string name)
    {
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "p1-generated-unwind-" + name + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source, sourcePath, outputPath, "P1GeneratedUnwind", CompilationProfile.SafeCoreMirV2,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "Generated unwind fixture failed to compile: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10)), runDeadline.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
