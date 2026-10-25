using RustSharp.Compiler;

namespace RustSharp.Tests;

/// <summary>Generated Rust# consumers for the P2-01.02 generic enum boundary.</summary>
internal static class P2CoreGenericOptionResultTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.02 generated Option enum consumer constructs and matches", OptionConsumerAsync),
        new("P2-01.02 generated Result enum consumer propagates the selected value", ResultConsumerAsync),
        new("P2-01.02 generic enum inference failure has no output", InferenceFailureAsync),
    ];

    private static Task OptionConsumerAsync() => RunAsync("""
        enum Option<T> { None, Some(T) }
        fn unwrap_or<T>(value: Option<T>, fallback: T) -> T {
            match value { Option::Some(item) => item, Option::None => fallback }
        }
        fn main() {
            let some: Option<i32> = Option::Some(7);
            let none: Option<i32> = Option::None;
            println!("{}", unwrap_or(some, 0));
            println!("{}", unwrap_or(none, 9));
        }
        """, "7\n9\n");

    private static Task ResultConsumerAsync() => RunAsync("""
        enum Result<T, E> { Ok(T), Err(E) }
        fn unwrap_or<T>(value: Result<T, T>, fallback: T) -> T {
            match value { Result::Ok(item) => item, Result::Err(_) => fallback }
        }
        fn main() {
            let ok: Result<i32, i32> = Result::Ok(11);
            let err: Result<i32, i32> = Result::Err(5);
            println!("{}", unwrap_or(ok, 0));
            println!("{}", unwrap_or(err, 0));
        }
        """, "11\n0\n");

    private static Task InferenceFailureAsync()
    {
        const string source = "enum Option<T> { None, Some(T) } fn main() { Option::None; }";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CompilationResult result = CompilerDriver.Check(source, "p2-option-inference.rs",
            CompilationProfile.SafeCoreGenericsCore, deadline.Token);
        AssertEx.False(result.Success, "A generic enum constructor without expected type must fail closed.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == "RSG1004"),
            string.Join("; ", result.Diagnostics));
        AssertEx.True(result.Output is null, "Rejected generic enum input must not expose an output.");
        return Task.CompletedTask;
    }

    private static async Task RunAsync(string source, string expected)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "p2-core-generic-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(directory, "program.dll");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try
        {
            CompilationResult result = CompilerDriver.Compile(source, Path.Combine(directory, "program.rs"), output,
                profile: CompilationProfile.SafeCoreGenericsCore, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Generic enum cleanup must stay inside the owned test root.");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
