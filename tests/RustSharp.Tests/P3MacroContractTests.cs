using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroContractTests
{
    private static readonly string ManifestPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/profiles/macros-v1.candidate.json"));
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 macro candidate validates 82 planned cases without claiming macro execution", ValidatesCandidateAsync),
        new("P3 macro candidate rejects bypassed formal dependencies and acceptance", RejectsAcceptanceAsync),
        new("P3 macro candidate rejects removed duplicate and invented case identities", RejectsCaseInventoryAsync),
        new("P3 macro candidate rejects grammar hygiene and repetition weakening", RejectsRulesAsync),
        new("P3 macro candidate rejects budget inflation and missing limit boundaries", RejectsBudgetsAsync),
        new("P3 macro candidate rejects unsupported fragment and diagnostic weakening", RejectsExclusionsAsync),
        new("P3 macro candidate rejects malformed duplicate and unknown schema members", RejectsSchemaAsync),
        new("P3 macro candidate rejects null and scalar diagnostic or budget values", RejectsMalformedScalarsAsync),
        new("P3 macro candidate preserves caller cancellation", CancellationAsync),
        new("P3 macro candidate distinguishes byte provenance from content identity", FormattingAsync),
    ];

    private static Task ValidatesCandidateAsync()
    {
        P3MacroContractResult result = P3MacroContract.Load(ManifestPath);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(static issue => issue.Message)));
        AssertEx.Equal(82, result.ValidatedCaseCount);
        AssertEx.Equal(P3MacroContract.FrozenContentSha256, result.ContentSha256);
        AssertEx.Equal(0, result.ExecutedRuntimeCaseCount);
        AssertEx.False(result.FormalAcceptanceAllowed, "Candidate validation cannot accept a P3 leaf.");
        return Task.CompletedTask;
    }
    private static Task RejectsAcceptanceAsync() => Mutations([
        root => root["hardDependencies"] = new JsonArray("P1-01"),
        root => root["formalAcceptanceAllowed"] = true,
        root => root["runtimeCasesExecuted"] = 82,
        root => root["state"] = "complete",
    ]);
    private static Task RejectsCaseInventoryAsync() => Mutations([
        root => root["cases"]!.AsArray().RemoveAt(0),
        root => root["cases"]![1]!["id"] = root["cases"]![0]!["id"]!.DeepClone(),
        root => root["cases"]![0]!["id"] = "invented",
        root => root["denominator"] = 81,
    ]);
    private static Task RejectsRulesAsync() => Mutations([
        root => root["features"]![0]!["rule"] = "Expand arbitrary tokens without any grammar restriction.",
        root => root["features"]![14]!["rule"] = "All names resolve at caller site; accidental capture allowed.",
        root => root["features"]![8]!["rule"] = "Repeat arbitrarily and ignore capture nesting.",
        root => root["cases"]![0]!["expected"] = "anything",
    ]);
    private static Task RejectsBudgetsAsync() => Mutations([
        root => root["limits"]!["matcherSteps"] = 100001,
        root => root["limits"]!.AsObject().Remove("recursionDepth"),
        root => root["cases"]![2]!["budgetValue"] = 100000,
        root => root["cases"]![2]!["diagnostic"] = "",
    ]);
    private static Task RejectsExclusionsAsync() => Mutations([
        root => root["unsupportedFragments"]!.AsArray().RemoveAt(0),
        root => root["excludedBuiltins"]!.AsArray().RemoveAt(0),
        root => root["cases"]![54]!["diagnostic"] = "RSMAC9999",
        root => root["cases"]![54]!["kind"] = "pass",
    ]);
    private static Task RejectsSchemaAsync()
    {
        Invalid(P3MacroContract.Validate("{"));
        Invalid(P3MacroContract.Validate("{\"schemaVersion\":1,\"schemaVersion\":1}"));
        Invalid(P3MacroContract.Validate(new string(' ', P3MacroContract.MaximumContractBytes + 1)));
        return Mutations([root => root["allowEverything"] = true, root => root["features"]![0]!["unknown"] = true]);
    }
    private static Task CancellationAsync()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P3MacroContract.Validate("{}", canceled.Token));
        AssertEx.Throws<OperationCanceledException>(() => P3MacroContract.Load(ManifestPath, canceled.Token));
        return Task.CompletedTask;
    }
    private static Task RejectsMalformedScalarsAsync() => Mutations([
        root => root["cases"]![0]!["diagnostic"] = null,
        root => root["cases"]![0]!["diagnostic"] = 42,
        root => root["cases"]![0]!["budget"] = null,
        root => root["cases"]![0]!["budget"] = false,
    ]);
    private static Task FormattingAsync()
    {
        string text = File.ReadAllText(ManifestPath);
        P3MacroContractResult original = P3MacroContract.Validate(text);
        P3MacroContractResult compact = P3MacroContract.Validate(Seed().ToJsonString());
        AssertEx.True(original.IsSuccessful && compact.IsSuccessful, "Formatting changes preserve scope identity.");
        AssertEx.Equal(original.ContentSha256, compact.ContentSha256);
        AssertEx.False(original.RawSha256 == compact.RawSha256, "Raw provenance retains different serialized bytes.");
        return Task.CompletedTask;
    }
    private static Task Mutations(Action<JsonObject>[] mutations)
    {
        AssertEx.True(mutations.Length is > 0 and <= 4, "Mutation batches have at most four inputs.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonObject seed = Seed();
        AssertEx.True(P3MacroContract.Validate(seed.ToJsonString(), deadline.Token).IsSuccessful, "Small unchanged seed trial must pass first.");
        for (int index = 0; index < mutations.Length && index < 4; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            JsonObject changed = seed.DeepClone().AsObject();
            mutations[index](changed);
            Invalid(P3MacroContract.Validate(changed.ToJsonString(), deadline.Token));
        }
        return Task.CompletedTask;
    }
    private static JsonObject Seed() => JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
    private static void Invalid(P3MacroContractResult result)
    {
        AssertEx.False(result.IsSuccessful, "Modified scope must fail closed.");
        AssertEx.Equal(0, result.ValidatedCaseCount);
        AssertEx.Equal(0, result.ExecutedRuntimeCaseCount);
        AssertEx.False(result.FormalAcceptanceAllowed, "Invalid contract cannot accept P3.");
        AssertEx.True(result.Diagnostics.Count > 0 && result.Diagnostics.All(static issue => issue.Code == P3MacroContract.DiagnosticCode), "Contract rejection retains stable diagnostics.");
    }
}
