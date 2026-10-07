using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1ReferenceRetentionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 reference retention hashes actual PE bytes in exact input order", PeBytesAsync),
        new("P1 reference retention pre-cancellation performs no file open", CancellationAsync),
        new("P1 reference retention rejects missing files without partial acceptance", MissingAsync),
        new("P1 reference retention preserves the fixed 512-path ceiling", CountAsync),
    ];

    private static async Task PeBytesAsync()
    {
        string[] paths = [Path.Combine(AppContext.BaseDirectory, "RustSharp.Runtime.dll"),
            Path.Combine(AppContext.BaseDirectory, "RustSharp.Conformance.dll")];
        var inputs = new JsonArray(paths.Select(path => (JsonNode)JsonValue.Create(path)!).ToArray());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        JsonArray result = await P1SourcePackagePlatformRunner.CaptureReferenceArtifactsAsync(inputs, deadline.Token).ConfigureAwait(false);
        AssertEx.Equal(2, result.Count);
        for (int index = 0; index < paths.Length; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            byte[] bytes = await File.ReadAllBytesAsync(paths[index], deadline.Token).ConfigureAwait(false);
            AssertEx.Equal(paths[index], result[index]!["path"]!.GetValue<string>());
            AssertEx.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result[index]!["sha256"]!.GetValue<string>());
        }
    }

    private static async Task CancellationAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await P1SourcePackagePlatformRunner.CaptureReferenceArtifactsAsync(new JsonArray("nonexistent-reference"), cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Pre-cancellation must take precedence over file access.");
    }

    private static async Task MissingAsync()
    {
        string absent = Path.Combine(Path.GetTempPath(), "rsc-reference-absent-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await P1SourcePackagePlatformRunner.CaptureReferenceArtifactsAsync(new JsonArray(
                Path.Combine(AppContext.BaseDirectory, "RustSharp.Runtime.dll"), absent), deadline.Token).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return; }
        throw new InvalidOperationException("A missing reference must fail the complete batch.");
    }

    private static async Task CountAsync()
    {
        var inputs = new JsonArray(Enumerable.Range(0, 513).Select(index => (JsonNode)JsonValue.Create(index.ToString(System.Globalization.CultureInfo.InvariantCulture))!).ToArray());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await P1SourcePackagePlatformRunner.CaptureReferenceArtifactsAsync(inputs, deadline.Token).ConfigureAwait(false);
        }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("A batch over 512 paths must fail before opening files.");
    }
}
