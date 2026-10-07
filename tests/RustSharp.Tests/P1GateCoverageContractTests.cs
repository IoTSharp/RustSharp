using System.Diagnostics;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1GateCoverageContractTests
{
    private const string Candidate = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string Text => File.ReadAllText(Path.Combine(Root, "tools/RustSharp.Conformance/fixtures", P1GateCoverageContract.ManifestFileName));
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-GATE.01 maps 40 frozen requirements to actual leaves and registered source cases", ManifestAsync),
        new("P1-GATE.01 isolates aggregate ownership and platform boundary without self cycles", AggregateAsync),
        new("P1-GATE.01 rejects old buckets substituted owners and reclassification", OwnersAsync),
        new("P1-GATE.01 rejects reduced duplicate and invented source test bindings", SourceBindingsAsync),
        new("P1-GATE.01 preserves immutable 160 catalogue rows without claiming execution", CatalogueAsync),
        new("P1-GATE.01 retains exact uncovered emitted backend cases and owners", BackendGapsAsync),
        new("P1-GATE.01 rejects wrong SHA non Release filtered and incomplete source provenance", ProvenanceAsync),
        new("P1-GATE.01 rejects skipped reduced and unexecuted harness denominators", ExecutionAsync),
        new("P1-GATE.01 rejects counterfeit platform and absent owned worker records", PlatformAsync),
        new("P1-GATE.01 bounds strict JSON source inventories and cancellation", BoundsAsync),
    ];

    private static P1GateCoverageContract.Manifest Parse() => P1GateCoverageContract.ParseManifest(Text, Root);
    private static JsonObject Node() => JsonNode.Parse(Text)!.AsObject();
    private static Task ManifestAsync()
    {
        P1GateCoverageContract.Manifest manifest = Parse();
        AssertEx.Equal(40, manifest.Requirements.Count);
        AssertEx.Equal(160, manifest.Requirements.Sum(item => item.CatalogueCases.Count));
        AssertEx.True(manifest.Requirements.SelectMany(item => item.ImplementationLeaves).All(leaf => leaf.Contains('.', StringComparison.Ordinal)), "An old parent bucket cannot own a closed leaf.");
        AssertEx.True(manifest.Requirements.Where(item => item.Role != "aggregate-ownership").All(item => item.HarnessSources.Count > 0), "Every nonaggregate requirement needs actual tests.");
        AssertEx.True(manifest.Requirements.Single(item => item.Id == "P1-REQ-009").HarnessSources.Single().CaseIds.Contains(
            "MIR v2 emits deterministic metadata and evaluates repeated operands once", StringComparer.Ordinal), "Repeated-array evidence must execute generated code.");
        P1GateCoverageContract.HarnessSource slices = manifest.Requirements.Single(item => item.Id == "P1-REQ-012").HarnessSources.Single();
        AssertEx.Equal(18, slices.CaseIds.Count, "Embedded Rust array terminators must not truncate the C# All registrations.");
        AssertEx.True(slices.CaseIds.Contains("MIR slice ABI call and return preserve mutable subslice ownership", StringComparer.Ordinal), "The required subslice case must remain in the complete source registration.");
        P1GateCoverageContract.HarnessSource constants = manifest.Requirements.Single(item => item.Id == "P1-REQ-015").HarnessSources.Single();
        AssertEx.Equal(16, constants.CaseIds.Count, "Const fixture array terminators must not truncate the C# All registrations.");
        AssertEx.True(constants.CaseIds.Contains("safe-core-mir constants inline const functions and bounded loops", StringComparer.Ordinal), "The required const-loop case must remain in the complete source registration.");
        return Task.CompletedTask;
    }
    private static Task AggregateAsync()
    {
        P1GateCoverageContract.Manifest manifest = Parse();
        P1GateCoverageContract.Requirement aggregate = manifest.Requirements.Single(item => item.Id == "P1-REQ-034");
        AssertEx.Equal("aggregate-ownership", aggregate.Role);
        AssertEx.Equal(0, aggregate.ImplementationLeaves.Count);
        AssertEx.Equal(0, aggregate.HarnessSources.Count);
        AssertEx.Equal(6, aggregate.GateOwners.Count);
        P1GateCoverageContract.Requirement boundary = manifest.Requirements.Single(item => item.Id == "P1-REQ-040");
        AssertEx.Equal("platform-boundary", boundary.Role);
        AssertEx.Equal("P1-10.06", boundary.ImplementationLeaves.Single());
        AssertEx.Equal("P1-GATE.05", boundary.GateOwners.Single());
        AssertEx.Equal(0, boundary.PendingBackendEvidence.Count);
        return Task.CompletedTask;
    }
    private static Task OwnersAsync() => MutationsAsync(
    [node => node["requirements"]![0]!["implementationLeaves"] = new JsonArray("P1-001"),
        node => node["requirements"]![0]!["implementationLeaves"]![0] = "P1-08.01",
        node => node["requirements"]![2]!["classification"] = "executable",
        node => node["requirements"]![33]!["role"] = "implementation",
        node => node["requirements"]![39]!["gateOwners"]!.AsArray().RemoveAt(0)]);
    private static Task SourceBindingsAsync() => MutationsAsync(
    [node => node["requirements"]![0]!["harnessSources"]!.AsArray().RemoveAt(0),
        node => node["requirements"]![8]!["harnessSources"]![0]!["caseIds"]![0] = "invented pass",
        node => node["requirements"]![8]!["harnessSources"]![0]!["caseIds"]![1] = node["requirements"]![8]!["harnessSources"]![0]!["caseIds"]![0]!.GetValue<string>(),
        node => node["requirements"]![8]!["harnessSources"]![0]!["sha256"] = new string('0', 64),
        node => node["requirements"]![8]!["harnessSources"]![0]!["status"] = "passed",
        node => node["requirements"]![8]!["harnessSources"]!.AsArray().Add(node["requirements"]![8]!["harnessSources"]![0]!.DeepClone())]);
    private static Task CatalogueAsync() => MutationsAsync(
    [node => node["catalogueDenominator"] = 159,
        node => node["requirements"]!.AsArray().RemoveAt(0),
        node => node["requirements"]![1]!["id"] = "P1-REQ-001",
        node => node["requirements"]![0]!["catalogueCases"]!.AsArray().RemoveAt(0),
        node => node["evidencePolicy"] = "catalogue validated means source program ran",
        node => node["legacyCatalogue"]!["sha256"] = new string('0', 64)]);
    private static Task BackendGapsAsync()
    {
        P1GateCoverageContract.Manifest manifest = Parse();
        var gaps = manifest.Requirements.SelectMany(item => item.PendingBackendEvidence.Select(gap => (item.Id, Gap: gap))).ToArray();
        AssertEx.Equal(6, gaps.Length);
        AssertEx.Equal(24, gaps.Sum(item => item.Gap.Backends.Count * item.Gap.Rids.Count));
        AssertEx.True(gaps.All(item => item.Gap.Backends.Contains("ilverify", StringComparer.Ordinal) && item.Gap.Backends.Contains("native-aot", StringComparer.Ordinal)), "CoreCLR-only execution cannot close emitted backends.");
        return MutationsAsync(
        [node => node["requirements"]![8]!["pendingBackendEvidence"]!.AsArray().RemoveAt(0),
            node => node["requirements"]![8]!["pendingBackendEvidence"]![0]!["backends"] = new JsonArray("harness"),
            node => node["requirements"]![8]!["pendingBackendEvidence"]![0]!["rids"] = new JsonArray("win-x64"),
            node => node["requirements"]![8]!["pendingBackendEvidence"]![0]!["ownerLeaf"] = "P1-06.02",
            node => node["requirements"]![2]!["pendingBackendEvidence"] = node["requirements"]![8]!["pendingBackendEvidence"]!.DeepClone()]);
    }
    private static Task ProvenanceAsync() => RejectHarnessMutationsAsync(
    [(report => report["candidateSha"] = new string('3', 40), "Filtered, stale, non-Release or incomplete harness is not leaf execution evidence."),
        (report => report["buildConfiguration"] = "Debug", "Filtered, stale, non-Release or incomplete harness is not leaf execution evidence."),
        (report => report["fullSuite"] = false, "Filtered, stale, non-Release or incomplete harness is not leaf execution evidence."),
        (report => report["sourceProvenance"]!["candidateTreeSha"] = new string('3', 40), "Working source does not bind to the requested candidate tree."),
        (report => report["sourceProvenance"]!["candidateMatchesWorkingTree"] = false, "Working source does not bind to the requested candidate tree.")]);
    private static Task ExecutionAsync() => RejectHarnessMutationsAsync(
    [(report => report["summary"]!["skipped"] = 1, "Fresh registered denominator is not fully executed."),
        (report => report["summary"]!["executed"] = 463, "Fresh registered denominator is not fully executed."),
        (report => report["summary"]!["registeredDenominator"] = 40, "Fresh registered denominator is not fully executed."),
        (report => report["summary"]!["notExecuted"] = 1, "Fresh registered denominator is not fully executed."),
        (report => report["cleanupComplete"] = false, "Filtered, stale, non-Release or incomplete harness is not leaf execution evidence.")]);
    private static Task PlatformAsync() => RejectHarnessMutationsAsync(
    [(report => report["runtimeIdentifier"] = "linux-x64", "Host RID is not the required native platform."),
        (report => report["runtimeIdentifier"] = "win-arm64", "Host RID is not the required native platform."),
        (report => report["runtimeIdentifier"] = "osx-x64", "Host RID is not the required native platform."),
        (report => report["processIsolated"] = false, "Filtered, stale, non-Release or incomplete harness is not leaf execution evidence."),
        (report => report["registeredIds"] = new JsonArray("inventory-only-with-no-worker"), "Fresh registration inventory was reduced or substituted.")]);
    private static Task BoundsAsync()
    {
        AssertEx.Throws<ArgumentException>(() => P1GateCoverageContract.ParseManifest(new string(' ', 262_145), Root));
        AssertEx.Throws<ArgumentException>(() => P1GateCoverageContract.ParseManifest(Text.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal), Root));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P1GateCoverageContract.ParseManifest(Text, Root, cancellation.Token));
        AssertEx.Throws<OperationCanceledException>(() => P1GateCoverageContract.ValidateHarnessEvidence(Parse(), "{}", Candidate, Tree, "win-x64", cancellation.Token));
        return Task.CompletedTask;
    }
    private static Task MutationsAsync(IReadOnlyList<Action<JsonObject>> mutations)
    {
        AssertEx.True(mutations.Count is > 0 and <= 6, "Mutation batch must remain fixed and bounded.");
        long started = Stopwatch.GetTimestamp();
        foreach (Action<JsonObject> mutate in mutations)
        {
            AssertEx.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20), "Mapping mutation deadline exceeded.");
            JsonObject node = Node(); mutate(node);
            AssertEx.Throws<ArgumentException>(() => P1GateCoverageContract.ParseManifest(node.ToJsonString(), Root));
        }
        return Task.CompletedTask;
    }
    private static Task RejectHarnessMutationsAsync(IReadOnlyList<(Action<JsonObject> Mutate, string Reason)> mutations)
    {
        AssertEx.True(mutations.Count is > 0 and <= 5, "Harness rejection batch must remain fixed and bounded.");
        P1GateCoverageContract.Manifest manifest = Parse();
        long started = Stopwatch.GetTimestamp();
        foreach ((Action<JsonObject> mutate, string reason) in mutations)
        {
            AssertEx.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20), "Harness rejection deadline exceeded.");
            JsonObject report = UnexecutedEnvelope(); mutate(report);
            ArgumentException exception = AssertEx.Throws<ArgumentException>(() =>
                P1GateCoverageContract.ValidateHarnessEvidence(manifest, report.ToJsonString(), Candidate, Tree, "win-x64"));
            AssertEx.Equal(reason, exception.Message,
                "The mutated field must trigger its designated validation branch before the deliberately absent worker inventory.");
        }
        return Task.CompletedTask;
    }
    // Deliberately no positive synthetic execution report: this envelope has no workers and must fail.
    private static JsonObject UnexecutedEnvelope() => new()
    {
        ["schemaVersion"] = 1, ["evidenceKind"] = "p1-full-regression-harness", ["candidateSha"] = Candidate,
        ["runtimeIdentifier"] = "win-x64", ["buildConfiguration"] = "Release", ["fullSuite"] = true,
        ["suiteSucceeded"] = true, ["processIsolated"] = true, ["cleanupComplete"] = true,
        ["deadlineExpired"] = false, ["cancelled"] = false, ["harnessError"] = null,
        ["sourceProvenance"] = new JsonObject { ["candidateSha"] = Candidate, ["candidateTreeSha"] = Tree,
            ["candidateMatchesWorkingTree"] = true, ["checkedFileCount"] = 631, ["errors"] = new JsonArray() },
        ["summary"] = new JsonObject { ["registeredDenominator"] = 464, ["selected"] = 464, ["executed"] = 464,
            ["passed"] = 464, ["failed"] = 0, ["skipped"] = 0, ["notExecuted"] = 0 },
        ["registeredIds"] = new JsonArray(), ["registeredIdsSha256"] = new string('0', 64), ["cases"] = new JsonArray(),
    };
}
