using System.Text;
using System.Text.Json.Nodes;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class P2CargoContractTests
{
    private static readonly string ManifestPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json"));

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2 cargo contract freezes all package graph families and 78 cases", ValidatesFrozenInventoryAsync),
        new("P2 cargo contract rejects missing accepted key families", RejectsMissingKeysAsync),
        new("P2 cargo contract rejects altered rules and graph budgets", RejectsRulesAndBudgetsAsync),
        new("P2 cargo contract rejects missing duplicate and unexpected cases", RejectsCaseInventoryAsync),
        new("P2 cargo contract rejects wrong diagnostics and weakened scenarios", RejectsSemanticMutationAsync),
        new("P2 cargo contract rejects changed denominators and leaf mappings", RejectsDenominatorsAsync),
        new("P2 cargo contract rejects missing commands platform and evidence fields", RejectsEvidenceMutationAsync),
        new("P2 cargo contract rejects malformed unknown and oversized schema", RejectsSchemaAsync),
        new("P2 cargo contract propagates caller cancellation", PropagatesCancellationAsync),
        new("P2 cargo contract retains raw hash with normalized line endings and bounded file cleanup", VerifiesFileBoundaryAsync),
    ];

    private static Task ValidatesFrozenInventoryAsync()
    {
        CargoContractValidationResult result = CargoContract.Load(ManifestPath);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(static item => item.Message)));
        CargoContractManifest manifest = AssertEx.NotNull(result.Manifest, "Valid manifest must provide immutable inventory data.");
        AssertEx.Equal(78, manifest.Denominator);
        AssertEx.Equal(78, result.ValidatedCaseCount);
        AssertEx.Equal(0, result.ExecutedRuntimeCaseCount, "Scope validation must not be recorded as Cargo runtime execution.");
        AssertEx.Equal(CargoContract.FrozenNormalizedSha256, result.NormalizedManifestSha256);
        AssertEx.Equal(CargoContract.FrozenContentSha256, result.ContentSha256);
        AssertEx.Equal(29, manifest.AcceptedKeys.Count);
        AssertEx.Equal(26, manifest.Rules.Count);
        AssertEx.Equal(11, manifest.Limits.Count);
        AssertEx.Equal(6, manifest.TestTargets.Count);
        AssertEx.Equal("P2CargoContractTests.All", manifest.TestTargets[0].Registration);
        AssertEx.True(manifest.Cases.Any(static item => item.Family == "feature" && item.Kind == "error"), "Feature rejection cases cannot disappear.");
        AssertEx.True(manifest.Cases.Any(static item => item.Family == "cfg" && item.Kind == "boundary"), "Cfg boundary cases cannot disappear.");
        AssertEx.True(manifest.Cases.Any(static item => item.Family == "lock" && item.Kind == "positive"), "Lock serialization cannot disappear.");
        AssertEx.True(manifest.Cases.Any(static item => item.Id == "graph-cycle" && item.ExpectedDiagnostic == CargoWorkspace.DependencyCycleDiagnostic), "Cycle diagnostics preserve the existing public code.");
        return Task.CompletedTask;
    }

    private static Task RejectsMissingKeysAsync() => Mutations(
    [
        root => root["acceptedKeys"]!.AsArray().RemoveAt(0),
        root => root["acceptedKeys"]!.AsArray().RemoveAt(18),
        root => root["acceptedKeys"]!.AsArray().Add((JsonNode?)JsonValue.Create("package.metadata")),
    ], "acceptedKeys");

    private static Task RejectsRulesAndBudgetsAsync() => Mutations(
    [
        root => root["rules"]!.AsArray().RemoveAt(0),
        root => root["limits"]!["maximumPackages"] = 65,
        root => root["limits"]!.AsObject().Remove("maximumGraphDepth"),
        root => root["rules"]![0]!["contract"] = "Ignore unknown keys and accept any TOML value.",
    ], null);

    private static Task RejectsCaseInventoryAsync() => Mutations(
    [
        root => root["cases"]!.AsArray().RemoveAt(0),
        root => root["cases"]![1]!["id"] = "package-minimal",
        root => root["cases"]![0]!["id"] = "invented-case",
        root => root["cases"]![0]!["covers"] = new JsonArray("unsupported-requirement"),
    ], null);

    private static Task RejectsSemanticMutationAsync() => Mutations(
    [
        root => Case(root, "feature-unknown")["expectedDiagnostic"] = "RSCARGO9999",
        root => Case(root, "package-minimal")["expectedDiagnostic"] = "RSCARGO1001",
        root => Case(root, "feature-cycle")["kind"] = "positive",
        root => Case(root, "graph-cycle")["scenario"] = "Cycles may be silently accepted.",
    ], null);

    private static Task RejectsDenominatorsAsync() => Mutations(
    [
        root => root["denominator"] = 77,
        root => root["testTargets"]![1]!["denominator"] = 37,
        root => Case(root, "feature-default")["leafId"] = "P2-04.02",
        root => root["hardDependencies"] = new JsonArray("P1-GATE"),
    ], null);

    private static Task RejectsEvidenceMutationAsync() => Mutations(
    [
        root => root["testTargets"]![0]!["command"] = "dotnet run --project tests/RustSharp.Tests",
        root => root["testTargets"]!.AsArray().RemoveAt(5),
        root => root["platforms"] = new JsonArray("win-x64"),
        root => root["evidenceRequiredFields"]!.AsArray().RemoveAt(13),
        root => root["inventoryKind"] = "executed-cargo-scenarios",
    ], null);

    private static Task RejectsSchemaAsync()
    {
        AssertInvalid(CargoContract.Validate("{"));
        AssertInvalid(CargoContract.Validate("{\"schemaVersion\":1,\"schemaVersion\":1}"));
        AssertInvalid(CargoContract.Validate(new string(' ', CargoContract.MaximumContractBytes + 1)));
        JsonObject seed = Seed();
        seed["allowUnsupportedKeys"] = true;
        AssertInvalid(CargoContract.Validate(seed.ToJsonString()));
        return Task.CompletedTask;
    }

    private static Task PropagatesCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CargoContract.Load(ManifestPath, cancellation.Token));
        AssertEx.Throws<OperationCanceledException>(() => CargoContract.Validate("{}", cancellation.Token));
        return Task.CompletedTask;
    }

    private static Task VerifiesFileBoundaryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string original = File.ReadAllText(ManifestPath);
        CargoContractValidationResult lf = CargoContract.Validate(original, deadline.Token);
        CargoContractValidationResult crlf = CargoContract.Validate(original.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal), deadline.Token);
        AssertEx.True(lf.IsSuccessful && crlf.IsSuccessful, "Git line-ending conversion preserves normalized immutable scope.");
        AssertEx.Equal(lf.NormalizedManifestSha256, crlf.NormalizedManifestSha256);
        AssertEx.False(lf.ManifestSha256 == crlf.ManifestSha256, "Provenance retains the actual raw bytes hash.");

        string temporaryParent = Path.GetFullPath(Path.GetTempPath());
        string ownedRoot = Path.Combine(temporaryParent, "rustsharp-p2-cargo-contract-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(ownedRoot, "contract.json");
        try
        {
            Directory.CreateDirectory(ownedRoot);
            File.WriteAllBytes(file, [0xff]);
            AssertInvalid(CargoContract.Load(file, deadline.Token));
            File.WriteAllText(file, new string(' ', CargoContract.MaximumContractBytes + 1), new UTF8Encoding(false));
            AssertInvalid(CargoContract.Load(file, deadline.Token));
            File.WriteAllText(file, original, new UTF8Encoding(false));
            AssertEx.True(CargoContract.Load(file, deadline.Token).IsSuccessful, "Owned fixture loads after invalid input replacement.");
            AssertInvalid(CargoContract.Load(Path.Combine(ownedRoot, "missing.json"), deadline.Token));
        }
        finally
        {
            string full = Path.GetFullPath(ownedRoot);
            AssertEx.True(full.StartsWith(temporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Cleanup must remain within the verified temporary parent.");
            AssertEx.True(Path.GetFileName(full).StartsWith("rustsharp-p2-cargo-contract-", StringComparison.Ordinal), "Only the task-owned unique fixture may be deleted.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Task Mutations(Action<JsonObject>[] mutations, string? requiredPath)
    {
        AssertEx.True(mutations.Length is > 0 and <= 5, "Mutation batch is fixed at at most five inputs.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonObject seed = Seed();
        // One unchanged input is the small trial before each bounded mutation batch.
        AssertEx.True(CargoContract.Load(ManifestPath, deadline.Token).IsSuccessful, "Frozen seed must validate before negative cases.");
        AssertEx.True(CargoContract.Validate(seed.ToJsonString(), deadline.Token).IsSuccessful,
            "The unchanged reserialized seed must pass so a mutation cannot fail only from formatting.");
        for (int index = 0; index < mutations.Length && index < 5; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            JsonObject root = seed.DeepClone().AsObject();
            mutations[index](root);
            CargoContractValidationResult result = CargoContract.Validate(root.ToJsonString(), deadline.Token);
            AssertInvalid(result);
            if (requiredPath is not null)
                AssertEx.True(result.Diagnostics.Any(item => item.JsonPath.Contains(requiredPath, StringComparison.Ordinal)), "Mutation must reject at its schema path.");
        }
        return Task.CompletedTask;
    }

    private static JsonObject Seed() => JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
    private static JsonObject Case(JsonObject root, string id) => root["cases"]!.AsArray()
        .Single(item => item!["id"]!.GetValue<string>() == id)!.AsObject();

    private static void AssertInvalid(CargoContractValidationResult result)
    {
        AssertEx.False(result.IsSuccessful, "Altered scope must never be recorded as accepted.");
        AssertEx.True(result.Manifest is null && result.Diagnostics.Count > 0, "Invalid input returns diagnostics and no usable partial inventory.");
        AssertEx.True(result.Diagnostics.All(static issue => issue.Code == CargoContract.ContractDiagnostic), "Schema rejection has the stable contract diagnostic.");
        AssertEx.Equal(0, result.ExecutedRuntimeCaseCount);
    }
}
