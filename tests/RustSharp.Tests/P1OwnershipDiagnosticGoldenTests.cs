using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

/// <summary>
/// Freezes source-level ownership diagnostics at the same boundary consumed
/// by the differential runner.  The catalog deliberately records the exact
/// source span and message, so an unsupported lowering diagnostic cannot be
/// mistaken for a semantic ownership rejection.
/// </summary>
internal static class P1OwnershipDiagnosticGoldenTests
{
    private const string CatalogFileName = "p1-ownership-diagnostics-v1.json";
    private const int CatalogVersion = 1;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 ownership diagnostic golden catalog is complete", CatalogContractAsync),
        new("P1 ownership diagnostics preserve code message path and span", SourceDiagnosticsAsync),
        new("P1 ownership compile diagnostics preserve the same golden facts", CompileDiagnosticsAsync),
        new("P1 ownership golden checks reject unsupported lowering", UnsupportedBoundaryAsync),
    ];

    private static Task CatalogContractAsync()
    {
        IReadOnlyList<GoldenCase> cases = LoadCatalog();
        AssertEx.Equal(4, cases.Count);
        AssertEx.Equal(cases.Count, cases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count());
        AssertEx.True(cases.All(static item => item.Code.StartsWith("RSO", StringComparison.Ordinal)),
            "Golden ownership diagnostics must use semantic RSO codes.");
        AssertEx.True(cases.All(static item => item.Message.Length is > 0 and <= 256),
            "Golden ownership diagnostic messages must remain bounded.");
        return Task.CompletedTask;
    }

    private static Task SourceDiagnosticsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (GoldenCase expected in LoadCatalog())
        {
            deadline.Token.ThrowIfCancellationRequested();
            string path = FixturePath(expected.Source);
            string source = File.ReadAllText(path, Encoding.UTF8);
            AssertEx.Equal(expected.SourceSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            AssertEx.Equal(expected.SpanText, source.Substring(expected.SpanStart, expected.SpanLength));
            CompilationResult result = CompilerDriver.Check(source, path,
                CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertGolden(result, expected, path);
        }

        return Task.CompletedTask;
    }

    private static Task CompileDiagnosticsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string directory = Path.Combine(Path.GetTempPath(), "RustSharp.P1OwnershipGolden",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (GoldenCase expected in LoadCatalog())
            {
                deadline.Token.ThrowIfCancellationRequested();
                string path = FixturePath(expected.Source);
                string output = Path.Combine(directory, expected.Id + ".dll");
                CompilationResult result = CompilerDriver.CompileFile(path, output,
                    expected.Id, CompilationProfile.SafeCoreMirV2, deadline.Token);
                AssertGolden(result, expected, path);
                AssertEx.False(File.Exists(output), "A rejected source must not publish a compiled output.");
            }
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RustSharp.P1OwnershipGolden",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
            AssertEx.True(resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Golden diagnostic cleanup must remain inside the process-owned directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task UnsupportedBoundaryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string source = "fn main() { let value: f32 = 1.0; println!(\"{}\", value); }";
        CompilationResult result = CompilerDriver.Check(source, "unsupported-ownership.rs",
            CompilationProfile.SafeCoreMirV2, deadline.Token);
        AssertEx.False(result.Success, "An unsupported ownership source must fail checking.");
        AssertEx.True(result.Diagnostics.Count != 0, "The unsupported source must retain a diagnostic.");
        AssertEx.True(result.Diagnostics.All(static diagnostic =>
                !diagnostic.Code.StartsWith("RSO", StringComparison.Ordinal)),
            "Unsupported lowering/type diagnostics cannot claim an ownership rejection.");
        AssertEx.True(result.Diagnostics.All(static diagnostic => diagnostic.Code is not "RSM3002"),
            "The golden ownership boundary must not classify unsupported MIR lowering as a semantic rejection.");
        return Task.CompletedTask;
    }

    private static void AssertGolden(CompilationResult result, GoldenCase expected, string path)
    {
        AssertEx.False(result.Success, "The frozen ownership-negative source must fail checking.");
        Diagnostic[] matches = result.Diagnostics.Where(item => item.Code == expected.Code).ToArray();
        AssertEx.Equal(1, matches.Length, Format(result.Diagnostics));
        Diagnostic diagnostic = matches[0];
        AssertEx.Equal(expected.Message, diagnostic.Message);
        AssertEx.Equal(path, diagnostic.SourcePath!);
        AssertEx.Equal(expected.SpanStart, diagnostic.Span.Start);
        AssertEx.Equal(expected.SpanLength, diagnostic.Span.Length);
    }

    private static List<GoldenCase> LoadCatalog()
    {
        string path = Path.Combine(RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures", CatalogFileName);
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false });
        JsonElement root = document.RootElement;
        AssertEx.Equal(CatalogVersion, root.GetProperty("schemaVersion").GetInt32());
        AssertEx.Equal("safe-core-mir-p1-v2", root.GetProperty("profile").GetString()!);
        JsonElement entries = root.GetProperty("cases");
        AssertEx.Equal(JsonValueKind.Array, entries.ValueKind);
        var result = new List<GoldenCase>(entries.GetArrayLength());
        foreach (JsonElement item in entries.EnumerateArray())
        {
            result.Add(new(
                item.GetProperty("id").GetString()!,
                item.GetProperty("source").GetString()!,
                item.GetProperty("sourceSha256").GetString()!,
                item.GetProperty("code").GetString()!,
                item.GetProperty("message").GetString()!,
                item.GetProperty("spanStart").GetInt32(),
                item.GetProperty("spanLength").GetInt32(),
                item.GetProperty("spanText").GetString()!));
        }

        return result;
    }

    private static string FixturePath(string source) =>
        Path.Combine(RepositoryRoot(), "tools", "RustSharp.Conformance", "fixtures", source);

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static item => item.Code + ": " + item.Message));

    private sealed record GoldenCase(
        string Id,
        string Source,
        string SourceSha256,
        string Code,
        string Message,
        int SpanStart,
        int SpanLength,
        string SpanText);
}
