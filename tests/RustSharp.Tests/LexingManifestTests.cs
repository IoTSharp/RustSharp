using System.Diagnostics;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class LexingManifestTests
{
    private const string ManifestName = "safe-core-lexing-manifest.json";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Lexical corpus rejects missing categories and invalid baseline metadata", RejectsInvalidContractAsync),
        new("Lexical corpus fails when a valid-shaped token expectation is wrong", RejectsIncorrectExpectationAsync),
    ];

    private static Task RejectsInvalidContractAsync() => RunMutationsAsync(
    [
        manifest => manifest["coverage"]!.AsObject().Remove("float-literals"),
        manifest => manifest["coverage"]!["float-literals"] = new JsonArray("undeclared-case"),
        manifest => manifest["rustVersion"] = "1.97.0",
        manifest => manifest["denominator"] = 23,
    ], expectedExitCode: 2);

    private static Task RejectsIncorrectExpectationAsync() => RunMutationsAsync(
    [
        manifest => manifest["cases"]![0]!["tokens"]![0]!["text"] = "if",
    ], expectedExitCode: 1);

    private static async Task RunMutationsAsync(Action<JsonObject>[] mutations, int expectedExitCode)
    {
        AssertEx.True(mutations.Length is > 0 and <= 4, "Mutation denominator is bounded.");
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string originals = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures");
        string artifactRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts", "tests"));
        string taskRoot = Path.Combine(artifactRoot, $"lexical-manifest-{Guid.NewGuid():N}");
        string fixtures = Path.Combine(taskRoot, "tools", "RustSharp.Conformance", "fixtures");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        try
        {
            Directory.CreateDirectory(fixtures);
            string seedText = await File.ReadAllTextAsync(Path.Combine(originals, ManifestName), deadline.Token).ConfigureAwait(false);
            JsonObject seed = JsonNode.Parse(seedText)!.AsObject();
            JsonArray cases = seed["cases"]!.AsArray();
            AssertEx.Equal(24, cases.Count);
            foreach (JsonNode? item in cases)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string file = item!["file"]!.GetValue<string>();
                AssertEx.Equal(file, Path.GetFileName(file));
                File.Copy(Path.Combine(originals, file), Path.Combine(fixtures, file));
            }

            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            foreach (Action<JsonObject> mutate in mutations)
            {
                deadline.Token.ThrowIfCancellationRequested();
                JsonObject manifest = seed.DeepClone().AsObject();
                mutate(manifest);
                await File.WriteAllTextAsync(Path.Combine(fixtures, ManifestName), manifest.ToJsonString(), deadline.Token).ConfigureAwait(false);
                string reportPath = Path.Combine(taskRoot, "report.json");
                int exitCode = await SafeCoreLexingProfileRunner.RunAsync(
                    taskRoot, reportPath, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow, Stopwatch.StartNew()).ConfigureAwait(false);
                AssertEx.Equal(expectedExitCode, exitCode);
                JsonNode report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, deadline.Token).ConfigureAwait(false))!;
                AssertEx.Equal(expectedExitCode, report["summary"]!["exitCode"]!.GetValue<int>());
                if (expectedExitCode == 2)
                {
                    AssertEx.False(report["manifest"]!["validated"]!.GetValue<bool>(), "Invalid contracts cannot be validated.");
                    AssertEx.Equal(0, report["summary"]!["executed"]!.GetValue<int>());
                }
                else
                {
                    AssertEx.True(report["manifest"]!["validated"]!.GetValue<bool>(), "Shape validation must not mask comparison failures.");
                    AssertEx.Equal(24, report["summary"]!["executed"]!.GetValue<int>());
                    AssertEx.Equal(1, report["summary"]!["failed"]!.GetValue<int>());
                    AssertEx.False(report["cases"]![0]!["expectationsMatched"]!.GetValue<bool>(), "Wrong token evidence must fail.");
                }
            }
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
            AssertEx.True(Path.GetFullPath(taskRoot).StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Only this test's temporary tree can be removed.");
            if (Directory.Exists(taskRoot))
            {
                Directory.Delete(taskRoot, recursive: true);
            }
        }
    }
}
