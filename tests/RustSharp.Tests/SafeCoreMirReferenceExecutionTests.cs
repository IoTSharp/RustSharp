using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirReferenceExecutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR v2 dereference reads observe preceding writes", ReadAfterWriteAsync),
        new("MIR v2 mutable reborrow resumes its parent", MutableReborrowAsync),
        new("MIR v2 shared reborrow ends before parent write", SharedReborrowAsync),
        new("MIR v2 shared reference copies retain the owner loan", SharedCopyAsync),
        new("MIR v2 shared reference copies block owner writes", SharedCopyOwnerWriteAsync),
        new("MIR v2 rejects two live exclusive borrows", ConflictingBorrowAsync),
        new("MIR v2 rejects owner writes while a shared borrow is live", OwnerWriteAsync),
        new("MIR v2 rejects references escaping an owner block", EscapeAsync),
        new("MIR v1 retains its reference rejection boundary", VersionBoundaryAsync),
        new("MIR v2 borrow lowering respects the local budget", ReferenceBudgetAsync),
    ];

    private static Task ReadAfterWriteAsync() => RunAsync(
        "fn main() { let mut value = 7; let reference = &mut value; *reference = 9; println!(\"{}\", *reference); println!(\"{}\", value); let shared = &value; println!(\"{}\", *shared); value = 11; let next = &value; println!(\"{}\", *next); }",
        "9\n9\n9\n11\n");

    private static Task MutableReborrowAsync() => RunAsync(
        "fn main() { let mut value = 7; let parent = &mut value; { let child = &mut *parent; *child = 13; println!(\"{}\", *child); } println!(\"{}\", *parent); println!(\"{}\", value); }",
        "13\n13\n13\n");

    private static Task SharedReborrowAsync() => RunAsync(
        "fn main() { let mut value = 7; let parent = &mut value; let child = &*parent; println!(\"{}\", *child); *parent = 12; println!(\"{}\", *parent); }",
        "7\n12\n");

    private static Task SharedCopyAsync() => RunAsync(
        "fn main() { let value = 7; let first = &value; let second = first; println!(\"{}\", *first); println!(\"{}\", *second); }",
        "7\n7\n");

    private static Task SharedCopyOwnerWriteAsync() => RejectAsync(
        "fn main() { let mut value = 7; let first = &value; let second = first; value = 9; println!(\"{}\", *second); }",
        SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task ConflictingBorrowAsync() => RejectAsync(
        "fn main() { let mut value = 7; let first = &mut value; let second = &mut value; println!(\"{}\", *first); println!(\"{}\", *second); }",
        SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task OwnerWriteAsync() => RejectAsync(
        "fn main() { let mut value = 7; let shared = &value; value = 11; println!(\"{}\", *shared); }",
        SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task EscapeAsync() => RejectAsync(
        "fn main() { let shared = { let owner = 7; &owner }; println!(\"{}\", *shared); }",
        SafeCoreOwnershipDiagnosticCodes.Escape);

    private static Task VersionBoundaryAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CompilationResult result = CompilerDriver.Check(
            "fn main() { let owner = 7; let shared = &owner; println!(\"{}\", *shared); }",
            "reference-v1.rs", CompilationProfile.SafeCoreMir, timeout.Token);
        AssertEx.False(result.Success, "v1 must retain the original reference rejection contract.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirLowering.UnsupportedSyntax),
            Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task ReferenceBudgetAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(
            "fn main() { let owner = 7; let shared = &owner; println!(\"{}\", *shared); }",
            "reference-budget.rs", new()
            {
                EnableP1Extensions = true, MaximumLocalsPerFunction = 1,
                RequireOwnershipEvidence = true, Timeout = TimeSpan.FromSeconds(5), CancellationToken = timeout.Token,
            });
        AssertEx.False(result.IsSuccessful, "Borrow destinations must count against the local arena budget.");
        AssertEx.Equal(SafeCoreMirLowering.LimitReached, result.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task RejectAsync(string source, string code) => WithWorkspaceAsync((directory, token) =>
    {
        CompilationResult checkedResult = CompilerDriver.Check(source, "reference-negative.rs", CompilationProfile.SafeCoreMirV2, token);
        AssertEx.False(checkedResult.Success, "Unsound reference code must fail checking.");
        AssertEx.True(checkedResult.Diagnostics.Any(diagnostic => diagnostic.Code == code), Format(checkedResult.Diagnostics));
        string output = Path.Combine(directory, "rejected.dll");
        CompilationResult compiled = CompilerDriver.Compile(source, "reference-negative.rs", output,
            assemblyName: "ReferenceNegative", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.False(compiled.Success, "Checking and compilation must enforce the same borrow contract.");
        AssertEx.True(compiled.Diagnostics.Any(diagnostic => diagnostic.Code == code), Format(compiled.Diagnostics));
        AssertEx.False(File.Exists(output), "A rejected reference program must not leave an executable assembly.");
        return Task.CompletedTask;
    });

    private static Task RunAsync(string source, string expected) => WithWorkspaceAsync(async (directory, token) =>
    {
        string output = Path.Combine(directory, "reference.dll");
        CompilationResult compiled = CompilerDriver.Compile(source, "reference-run.rs", output,
            assemblyName: "ReferenceRun", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.True(compiled.Success, Format(compiled.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ",
        diagnostics.Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Message));

    private static async Task WithWorkspaceAsync(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "mir-reference-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Reference cleanup may delete only its own workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
