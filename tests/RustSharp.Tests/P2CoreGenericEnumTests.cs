using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

/// <summary>Source-level P2-01.02 evidence for generic enum construction and matching.</summary>
internal static class P2CoreGenericEnumTests
{
    private const string Source = "enum Option<T> { None, Some(T) } " +
        "fn read(value: Option<i32>) -> i32 { match value { Option::Some(x) => x, Option::None => 0 } } " +
        "fn main() { println!(\"{}\", read(Option::Some(42))); println!(\"{}\", read(Option::None)); }";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.02 generic enum source constructs and matches variants", SourceAnalysisAsync),
        new("P2-01.02 generic enum consumer executes through closed CLR lowering", ConsumerAsync),
    ];

    private static Task SourceAnalysisAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(Source, "p2-01-02-option.rs", null, deadline.Token);
        SafeCoreGenericAnalysisResult result = SafeCoreGenericAnalysis.Check(syntax,
            new SafeCoreGenericAnalysisOptions { EnableCoreEnums = true }, deadline.Token);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
        SafeCoreGenericNominalDefinition option = result.Program!.NominalTypes.Single(static value => value.Id == "crate::Option");
        AssertEx.True(option.IsEnum);
        AssertEx.Equal("None,Some", string.Join(',', option.Variants.Select(static value => value.Name)));
        AssertEx.Equal(1, option.Variants.Single(static value => value.Name == "Some").Fields.Length);
        return Task.CompletedTask;
    }

    private static async Task ConsumerAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "RustSharp.Tests");
        string directory = Path.Combine(root, "p2-01-02-option-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "option.dll");
            CompilationResult result = CompilerDriver.Compile(Source, Path.Combine(directory, "option.rs"), output,
                profile: CompilationProfile.SafeCoreGenericsCore, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete, "The generated enum consumer must clean its process tree.");
            AssertEx.Equal("42\n0\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Cleanup must remain within the task-owned temporary root.");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
