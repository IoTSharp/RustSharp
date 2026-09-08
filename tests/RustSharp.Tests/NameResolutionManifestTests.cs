using System.Diagnostics;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class NameResolutionManifestTests
{
    private const string ManifestName = "safe-core-name-resolution-manifest.json";
    private const int CorpusDenominator = 25;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Name resolution corpus verifies module bindings and RSN1007 boundaries", VerifiesCorpusAsync),
        new("Name resolution corpus rejects malformed binding and diagnostic contracts", RejectsInvalidContractAsync),
        new("Name resolution corpus fails incorrect bindings and diagnostic spans", RejectsIncorrectExpectationAsync),
    ];

    private static Task VerifiesCorpusAsync() => RunMutationsAsync([static _ => { }], 0);

    private static Task RejectsInvalidContractAsync() => RunMutationsAsync(
    [
        manifest => manifest["denominator"] = CorpusDenominator + 1,
        manifest => manifest["cases"]![0]!["expectedResolutions"]![0]!.AsObject().Remove("candidateCount"),
        manifest => FindCase(manifest, "absolute-imports-remain-unsupported")["expectedDiagnostics"]![0]!["code"] = "RSN1999",
        manifest => FindCase(manifest, "absolute-imports-remain-unsupported")["expectedDiagnostics"]![0]!["occurrences"]![0]!["start"] = -1,
        manifest => FindCase(manifest, "absolute-imports-remain-unsupported")["expectedDiagnostics"]![0]!["occurrences"]!.AsArray().RemoveAt(0),
    ], 2);

    private static Task RejectsIncorrectExpectationAsync() => RunMutationsAsync(
    [
        manifest => manifest["cases"]![0]!["expectedResolutions"]![0]!["symbolQualifiedName"] = "crate::api::model::Other",
        manifest => manifest["cases"]![0]!["expectedResolutions"]!.AsArray().RemoveAt(0),
        manifest => FindCase(manifest, "used-glob-ambiguity-is-diagnosed")["expectedResolutions"]![2]!["candidateCount"] = 1,
        manifest => FindCase(manifest, "absolute-imports-remain-unsupported")["expectedDiagnostics"]![0]!["occurrences"]![0]!["start"] = 0,
        manifest => FindCase(manifest, "outer-attribute-semantics-remain-unsupported")["expectedDiagnostics"]![0]!["occurrences"]![0]!["length"] = 1,
    ], 1);

    private static JsonObject FindCase(JsonObject manifest, string id) =>
        manifest["cases"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == id)!.AsObject();

    private static async Task RunMutationsAsync(Action<JsonObject>[] mutations, int expectedExitCode)
    {
        AssertEx.True(mutations.Length is > 0 and <= 5, "Mutation count is bounded.");
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string originals = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures");
        string artifactRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts", "tests"));
        string taskRoot = Path.Combine(artifactRoot, $"name-resolution-manifest-{Guid.NewGuid():N}");
        string fixtures = Path.Combine(taskRoot, "tools", "RustSharp.Conformance", "fixtures");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        try
        {
            Directory.CreateDirectory(fixtures);
            JsonObject seed = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(originals, ManifestName), deadline.Token).ConfigureAwait(false))!.AsObject();
            JsonArray cases = seed["cases"]!.AsArray();
            AssertEx.Equal(CorpusDenominator, cases.Count);
            AssertEx.Equal(CorpusDenominator, seed["denominator"]!.GetValue<int>());
            for (int index = 0; index < cases.Count && index < CorpusDenominator; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string file = cases[index]!["file"]!.GetValue<string>();
                AssertEx.Equal(file, Path.GetFileName(file));
                File.Copy(Path.Combine(originals, file), Path.Combine(fixtures, file));
            }

            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            // Exercise the harness with one existing fixture before running the bounded batch.
            JsonObject smoke = seed.DeepClone().AsObject();
            smoke["cases"] = new JsonArray(seed["cases"]![0]!.DeepClone());
            smoke["denominator"] = 1;
            await File.WriteAllTextAsync(Path.Combine(fixtures, ManifestName), smoke.ToJsonString(), deadline.Token).ConfigureAwait(false);
            int smokeExitCode = await SafeCoreNameResolutionProfileRunner.RunAsync(
                taskRoot, Path.Combine(taskRoot, "smoke.json"), TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow, Stopwatch.StartNew()).ConfigureAwait(false);
            AssertEx.Equal(0, smokeExitCode);

            for (int index = 0; index < mutations.Length && index < 5; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                JsonObject manifest = seed.DeepClone().AsObject();
                mutations[index](manifest);
                await File.WriteAllTextAsync(Path.Combine(fixtures, ManifestName), manifest.ToJsonString(), deadline.Token).ConfigureAwait(false);
                string reportPath = Path.Combine(taskRoot, "report.json");
                int exitCode = await SafeCoreNameResolutionProfileRunner.RunAsync(
                    taskRoot, reportPath, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow, Stopwatch.StartNew()).ConfigureAwait(false);
                JsonNode report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, deadline.Token).ConfigureAwait(false))!;
                string differences = string.Join("; ", report["cases"]!.AsArray()
                    .Where(static item => item!["status"]!.GetValue<string>() != "passed")
                    .Take(CorpusDenominator).Select(static item => $"{item!["id"]}: {item["difference"]}"));
                AssertEx.True(exitCode == expectedExitCode, $"Expected exit {expectedExitCode}, actual {exitCode}: {report["harnessError"]}; {differences}");
                AssertEx.Equal(expectedExitCode, report["summary"]!["exitCode"]!.GetValue<int>());
                AssertEx.Equal(expectedExitCode != 2, report["manifest"]!["validated"]!.GetValue<bool>());
                AssertEx.Equal(expectedExitCode == 2 ? 0 : CorpusDenominator, report["summary"]!["executed"]!.GetValue<int>());
                AssertEx.Equal(expectedExitCode == 1 ? 1 : 0, report["summary"]!["failed"]!.GetValue<int>());
                if (expectedExitCode == 0)
                {
                    AssertEx.Equal(CorpusDenominator, report["summary"]!["passed"]!.GetValue<int>());
                    AssertEx.Equal(CorpusDenominator, report["summary"]!["resolutionExecuted"]!.GetValue<int>());
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
