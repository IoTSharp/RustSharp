using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

/// <summary>
/// Focused source-level checks for the P2-01.03 formatting boundary.  The
/// shared registration list deliberately remains root-owned; this file can be
/// registered after the implementation is reviewed.
/// </summary>
internal static class P2CoreFormattingIntegrationTests
{
    private const string SourcePath = "p2-core-formatting.rs";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.03 generic Display bound is consumed by println", DisplayBoundAsync),
        new("P2-01.03 generic Debug bound is consumed by println", DebugBoundAsync),
        new("P2-01.03 formatting without a trait proof is rejected", MissingProofAsync),
    ];

    private static Task DisplayBoundAsync()
    {
        const string source =
            "trait Display {} impl Display for i32 {} " +
            "fn show<T: Display>(value: T) -> T { println!(\"{}\", value); value } " +
            "fn main() { show(1); }";
        SafeCoreGenericAnalysisResult result = Check(source);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
        AssertEx.NotNull(result.Program, "A successful formatting consumer must expose complete generic evidence.");
        return Task.CompletedTask;
    }

    private static Task DebugBoundAsync()
    {
        const string source =
            "trait Debug {} impl Debug for bool {} " +
            "fn show<T: Debug>(value: T) -> T { println!(\"{:?}\", value); value } " +
            "fn main() { show(true); }";
        SafeCoreGenericAnalysisResult result = Check(source);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
        AssertEx.NotNull(result.Program, "A successful debug consumer must expose complete generic evidence.");
        return Task.CompletedTask;
    }

    private static Task MissingProofAsync()
    {
        const string source = "fn show<T>(value: T) -> T { println!(\"{}\", value); value } fn main() {}";
        SafeCoreGenericAnalysisResult result = Check(source);
        AssertEx.False(result.IsSuccessful, "A generic formatting call without a trait proof must fail.");
        AssertEx.True(result.Program is null, "A failed formatting check must not publish partial generic evidence.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreGenericDiagnosticCodes.Trait),
            string.Join("; ", result.Diagnostics));
        return Task.CompletedTask;
    }

    private static SafeCoreGenericAnalysisResult Check(string source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, SourcePath, null, deadline.Token);
        AssertEx.True(syntax.IsSuccessful, string.Join("; ", syntax.Diagnostics));
        return SafeCoreGenericAnalysis.Check(syntax, cancellationToken: deadline.Token);
    }
}
