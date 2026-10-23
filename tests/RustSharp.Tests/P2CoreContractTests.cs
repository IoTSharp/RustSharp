using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class P2CoreContractTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.01 freezes the seven-family core-v1 inventory", ValidatesInventoryAsync),
        new("P2-01.01 preserves the fixed member surface", PreservesMembersAsync),
        new("P2-01.01 preserves positive error and boundary cases", PreservesCasesAsync),
        new("P2-01.01 rejects runtime and completion claims", RejectsFalseClaimsAsync),
        new("P2-01.01 rejects reduced or substituted inventory", RejectsInventoryChangesAsync),
        new("P2-01.01 rejects malformed oversized and deep input", BoundsInputAsync),
        new("P2-01.01 honors cancellation before parsing", HonorsCancellationAsync),
        new("P2-01.01 keeps all leaf test targets bounded", PreservesLeafTargetsAsync),
        new("P2-01.01 rejects duplicate properties and changed scenarios", RejectsDuplicateAndScenarioAsync),
    ];

    private static Task ValidatesInventoryAsync()
    {
        CoreContractValidation result = CoreContract.Load(ManifestPath);
        AssertEx.True(result.IsValid, string.Join("; ", result.Issues));
        AssertEx.Equal(CoreContract.Profile, result.Profile);
        AssertEx.Equal(CoreContract.LeafId, result.LeafId);
        AssertEx.Equal(42, result.CaseDenominator);
        AssertEx.Equal(42, result.VerifiedCaseCount);
        AssertEx.Equal(7, result.Families.Count);
        AssertEx.False(result.RuntimeEvidence, "A scope manifest cannot claim runtime evidence.");
        AssertEx.Equal(64, result.ManifestSha256.Length);
        AssertEx.Equal(CoreContract.FrozenContractSha256, result.ContractSha256);
        return Task.CompletedTask;
    }

    private static Task PreservesMembersAsync()
    {
        using JsonDocument document = JsonDocument.Parse(ReadManifest());
        JsonElement root = document.RootElement;
        string[] families = ["primitives", "option", "result", "formatting", "comparison", "hashing", "iterators"];
        AssertEx.Equal(7, root.GetProperty("families").GetArrayLength());
        AssertEx.True(root.GetProperty("families").EnumerateArray().Select(static item => item.GetString()!).SequenceEqual(families),
            "The family order is part of the frozen contract.");
        AssertEx.Equal(28, root.GetProperty("members").GetArrayLength());
        AssertEx.True(root.GetProperty("members").EnumerateArray().Any(static item => item.GetString() == "core::option::Option"));
        AssertEx.True(root.GetProperty("members").EnumerateArray().Any(static item => item.GetString() == "core::result::Result"));
        AssertEx.True(root.GetProperty("members").EnumerateArray().Any(static item => item.GetString() == "core::iter::Iterator"));
        return Task.CompletedTask;
    }

    private static Task PreservesCasesAsync()
    {
        using JsonDocument document = JsonDocument.Parse(ReadManifest());
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        AssertEx.Equal(42, cases.Length);
        foreach (string family in new[] { "primitives", "option", "result", "formatting", "comparison", "hashing", "iterators" })
        {
            JsonElement[] familyCases = cases.Where(item => item.GetProperty("family").GetString() == family).ToArray();
            AssertEx.Equal(6, familyCases.Length);
            AssertEx.True(familyCases.Any(static item => item.GetProperty("kind").GetString() == "positive"));
            AssertEx.True(familyCases.Any(static item => item.GetProperty("kind").GetString() == "error"));
            AssertEx.True(familyCases.Any(static item => item.GetProperty("kind").GetString() == "boundary"));
        }
        return Task.CompletedTask;
    }

    private static Task RejectsFalseClaimsAsync() => Mutations(
    [
        root => root["runtimeEvidence"] = true,
        root => root["inventoryKind"] = "executed-core-runtime",
        root => root["denominator"] = 41,
        root => root["testTargets"]!.AsArray().RemoveAt(0),
    ]);

    private static Task RejectsInventoryChangesAsync() => Mutations(
    [
        root => root["families"]!.AsArray().RemoveAt(0),
        root => root["members"]![0] = "core::changed",
        root => root["cases"]!.AsArray()[0]!.AsObject()["id"] = "invented-case",
        root => root["cases"]!.AsArray()[1]!.AsObject()["family"] = "unknown",
    ]);

    private static Task BoundsInputAsync()
    {
        AssertEx.False(CoreContract.Validate("{").IsValid, "Malformed JSON must reject.");
        AssertEx.False(CoreContract.Validate(new string(' ', CoreContract.MaximumManifestBytes + 1)).IsValid,
            "Oversized input must reject before parsing.");
        string deep = new string('[', CoreContract.MaximumManifestDepth + 1) + "0" + new string(']', CoreContract.MaximumManifestDepth + 1);
        AssertEx.False(CoreContract.Validate(deep).IsValid, "Deep input must reject within the parser bound.");
        return Task.CompletedTask;
    }

    private static Task HonorsCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CoreContract.Validate(ReadManifest(), cancellation.Token));
        AssertEx.Throws<OperationCanceledException>(() => CoreContract.Load(ManifestPath, cancellation.Token));
        return Task.CompletedTask;
    }

    private static Task PreservesLeafTargetsAsync()
    {
        using JsonDocument document = JsonDocument.Parse(ReadManifest());
        JsonElement[] targets = document.RootElement.GetProperty("testTargets").EnumerateArray().ToArray();
        AssertEx.Equal(6, targets.Length);
        AssertEx.Equal(6, targets.Select(static item => item.GetProperty("leafId").GetString()).Distinct(StringComparer.Ordinal).Count());
        foreach (JsonElement target in targets)
        {
            string command = target.GetProperty("command").GetString()!;
            AssertEx.True(command.Contains("--filter", StringComparison.Ordinal));
            AssertEx.True(command.Contains("--timeout", StringComparison.Ordinal));
            AssertEx.True(command.Contains("--deadline", StringComparison.Ordinal));
            AssertEx.True(target.GetProperty("denominator").GetInt32() > 0);
        }
        return Task.CompletedTask;
    }

    private static Task RejectsDuplicateAndScenarioAsync()
    {
        string original = ReadManifest();
        int end = original.IndexOf("\"schemaVersion\"", StringComparison.Ordinal);
        AssertEx.True(end >= 0);
        string duplicate = "{\"schemaVersion\":1,\"schemaVersion\":1," + original[(original.IndexOf(',') + 1)..];
        AssertEx.False(CoreContract.Validate(duplicate).IsValid, "Duplicate properties must reject.");
        JsonObject changed = JsonNode.Parse(original)!.AsObject();
        changed["cases"]!.AsArray()[0]!.AsObject()["scenario"] = "changed scenario";
        AssertEx.False(CoreContract.Validate(changed.ToJsonString()).IsValid, "Changed frozen scenario must reject.");
        return Task.CompletedTask;
    }

    private static Task Mutations(Action<JsonObject>[] mutations)
    {
        string original = ReadManifest();
        AssertEx.True(CoreContract.Validate(original).IsValid, "The frozen seed must validate before mutation tests.");
        JsonObject seed = JsonNode.Parse(original)!.AsObject();
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject changed = seed.DeepClone().AsObject();
            mutate(changed);
            CoreContractValidation result = CoreContract.Validate(changed.ToJsonString());
            AssertEx.False(result.IsValid, "Changed frozen inventory must reject.");
            AssertEx.Equal(0, result.VerifiedCaseCount);
        }
        return Task.CompletedTask;
    }

    private static string ReadManifest()
    {
        AssertEx.True(File.Exists(ManifestPath), "The frozen P2-01 manifest must exist.");
        return File.ReadAllText(ManifestPath);
    }

    private static string ManifestPath => FindManifest();

    private static string FindManifest()
    {
        foreach (string seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(seed));
            for (int depth = 0; directory is not null && depth < 16; depth++, directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "tools", "RustSharp.Conformance", "fixtures", "p2-core-v1-manifest.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
            "tools", "RustSharp.Conformance", "fixtures", "p2-core-v1-manifest.json");
    }
}
