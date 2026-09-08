using System.Diagnostics;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class SyntaxManifestTests
{
    private const string ManifestName = "safe-core-syntax-manifest.json";

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Syntax corpus rejects missing categories and invalid contracts", RejectsInvalidContractAsync),
        new("Syntax corpus fails incorrect diagnostic text and item expectations", RejectsIncorrectExpectationAsync),
    ];

    private static Task RejectsInvalidContractAsync() => RunMutationsAsync(
    [
        manifest => manifest["coverage"]!.AsObject().Remove("generics"),
        manifest => manifest["coverage"]!["generics"] = new JsonArray("undeclared-case"),
        manifest => manifest["rustVersion"] = "1.97.0",
        manifest => manifest["denominator"] = 35,
        manifest => manifest["cases"]![2]!.AsObject().Remove("diagnosticText"),
        manifest => manifest["cases"]![0]!.AsObject().Remove("snapshotPath"),
    ], 2);

    private static Task RejectsIncorrectExpectationAsync() => RunMutationsAsync(
    [
        manifest => manifest["cases"]![2]!["diagnosticText"] = "fn",
        manifest => manifest["cases"]![0]!["minimumItems"] = 99,
        manifest => manifest["cases"]![0]!["snapshotPath"] = manifest["cases"]![1]!["snapshotPath"]!.GetValue<string>(),
    ], 1);

    private static async Task RunMutationsAsync(Action<JsonObject>[] mutations, int expectedExitCode)
    {
        AssertEx.True(mutations.Length is > 0 and <= 8, "Mutation denominator is bounded.");
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string originals = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures");
        string artifactRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts", "tests"));
        string taskRoot = Path.Combine(artifactRoot, $"syntax-manifest-{Guid.NewGuid():N}");
        string fixtures = Path.Combine(taskRoot, "tools", "RustSharp.Conformance", "fixtures");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        try
        {
            Directory.CreateDirectory(fixtures);
            JsonObject seed = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(originals, ManifestName), deadline.Token).ConfigureAwait(false))!.AsObject();
            JsonArray cases = seed["cases"]!.AsArray();
            AssertEx.True(cases.Count is > 0 and <= 256, "Syntax denominator is bounded.");
            foreach (JsonNode? item in cases)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string file = item!["file"]!.GetValue<string>();
                AssertEx.Equal(file, Path.GetFileName(file));
                File.Copy(Path.Combine(originals, file), Path.Combine(fixtures, file));
                if (item["snapshotPath"] is JsonValue snapshotValue)
                {
                    string snapshot = snapshotValue.GetValue<string>();
                    AssertEx.Equal(snapshot, Path.GetFileName(snapshot));
                    File.Copy(Path.Combine(originals, snapshot), Path.Combine(fixtures, snapshot));
                }
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
                int exitCode = await SafeCoreSyntaxProfileRunner.RunAsync(
                    taskRoot, reportPath, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow, Stopwatch.StartNew()).ConfigureAwait(false);
                AssertEx.Equal(expectedExitCode, exitCode);
                JsonNode report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, deadline.Token).ConfigureAwait(false))!;
                AssertEx.Equal(expectedExitCode, report["summary"]!["exitCode"]!.GetValue<int>());
                AssertEx.Equal(expectedExitCode == 1, report["manifest"]!["validated"]!.GetValue<bool>());
                AssertEx.Equal(expectedExitCode == 1 ? cases.Count : 0, report["summary"]!["executed"]!.GetValue<int>());
                if (expectedExitCode == 1)
                {
                    AssertEx.Equal(1, report["summary"]!["failed"]!.GetValue<int>());
                }
            }
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
            AssertEx.True(Path.GetFullPath(taskRoot).StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Only the owned test tree can be removed.");
            if (Directory.Exists(taskRoot)) Directory.Delete(taskRoot, recursive: true);
        }
    }
}
