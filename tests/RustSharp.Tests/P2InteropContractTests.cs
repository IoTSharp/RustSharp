using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class P2InteropContractTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2 interop contract validates the frozen 36-case design inventory", ValidatesFrozenInventoryAsync),
        new("P2 interop contract tolerates whitespace without changing contract identity", PreservesCanonicalIdentityAsync),
        new("P2 interop contract rejects reduced duplicated and substituted cases", RejectsCaseChangesAsync),
        new("P2 interop contract rejects missing families signatures and diagnostics", RejectsSurfaceChangesAsync),
        new("P2 interop contract rejects false runtime and platform claims", RejectsFalseClaimsAsync),
        new("P2 interop contract bounds malformed oversized and deep input", BoundsInputAsync),
        new("P2 interop contract honors cancellation before parsing", HonorsCancellationAsync),
        new("P2 interop contract preserves both directions all boundaries and leaf mapping", PreservesRequiredSurfaceAsync),
    ];

    private static Task ValidatesFrozenInventoryAsync()
    {
        DotNetInteropContractValidation result = DotNetInteropContract.Validate(ReadManifest());
        AssertEx.True(result.IsValid, string.Join("; ", result.Issues));
        AssertEx.Equal("dotnet-interop-v1", result.Profile);
        AssertEx.Equal(36, result.CaseDenominator);
        AssertEx.Equal(36, result.VerifiedCaseCount);
        AssertEx.Equal(36, result.CaseIds.Distinct(StringComparer.Ordinal).Count());
        AssertEx.Equal(64, result.ManifestSha256.Length);
        AssertEx.Equal(DotNetInteropContract.FrozenContractSha256, result.ContractSha256);
        AssertEx.False(result.RuntimeEvidence, "Contract records cannot establish binding, generated execution or AOT support.");
        return Task.CompletedTask;
    }

    private static Task PreservesCanonicalIdentityAsync()
    {
        string original = ReadManifest();
        DotNetInteropContractValidation first = DotNetInteropContract.Validate(original);
        DotNetInteropContractValidation second = DotNetInteropContract.Validate(" \n" + original + "\n ");
        AssertEx.True(second.IsValid, string.Join("; ", second.Issues));
        AssertEx.Equal(first.ContractSha256, second.ContractSha256);
        AssertEx.False(first.ManifestSha256 == second.ManifestSha256, "Raw file identity and normalized design identity must be distinguishable.");
        return Task.CompletedTask;
    }

    private static Task RejectsCaseChangesAsync() => MutationsAsync(
    [
        manifest => manifest["denominator"] = 35,
        manifest => manifest["cases"]!.AsArray().RemoveAt(0),
        manifest => manifest["cases"]![1]!["id"] = "import-static-add",
        manifest => manifest["cases"]![0]!["id"] = "substituted-case",
        manifest => manifest["cases"]![0]!["leaf"] = "P2-06.01",
        manifest => manifest["cases"]![0]!["outcome"] = "passed",
    ]);

    private static Task RejectsSurfaceChangesAsync() => MutationsAsync(
    [
        manifest => manifest["families"]!.AsArray().RemoveAt(0),
        manifest => manifest["members"]![0]!["signature"] = "System.Boolean(System.Boolean)",
        manifest => manifest["syntax"]!["export"] = "implicit C# export",
        manifest => manifest["typeMappings"]!.AsArray().RemoveAt(3),
        manifest => manifest["diagnostics"]!["RSDN1007"] = "ownership errors ignored",
        manifest => manifest["members"]![7]!["packageVersion"] = "latest",
        manifest => manifest["baseline"]!["generatedCSharpProgramLogic"] = true,
    ]);

    private static Task RejectsFalseClaimsAsync() => MutationsAsync(
    [
        manifest => manifest["runtimeImplemented"] = true,
        manifest => manifest["implementationState"] = "complete",
        manifest => manifest["runtimeIdentifiers"]!.AsArray().RemoveAt(1),
        manifest => manifest["requiredBackends"]!.AsArray().RemoveAt(2),
        manifest => manifest["prerequisites"]!.AsArray().RemoveAt(1),
        manifest => manifest["runtimeEvidenceRequirements"]!["aggregate"] = "skip missing native cases",
    ]);

    private static Task BoundsInputAsync()
    {
        AssertEx.False(DotNetInteropContract.Validate("{").IsValid, "Malformed JSON rejects.");
        AssertEx.False(DotNetInteropContract.Validate("[]").IsValid, "Nonobject JSON rejects.");
        AssertEx.False(DotNetInteropContract.Validate("{\"schemaVersion\":true,\"denominator\":\"36\"}").IsValid, "Wrong scalar kinds reject without an infrastructure exception.");
        AssertEx.False(DotNetInteropContract.Validate(new string(' ', DotNetInteropContract.MaximumManifestBytes + 1)).IsValid, "Oversized input rejects before parsing.");
        string deep = new('[', DotNetInteropContract.MaximumManifestDepth + 1);
        deep += "0" + new string(']', DotNetInteropContract.MaximumManifestDepth + 1);
        AssertEx.False(DotNetInteropContract.Validate(deep).IsValid, "Nested input exceeding the frozen depth rejects.");
        return Task.CompletedTask;
    }

    private static Task HonorsCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => DotNetInteropContract.Validate(ReadManifest(), cancellation.Token));
        return Task.CompletedTask;
    }

    private static Task PreservesRequiredSurfaceAsync()
    {
        using JsonDocument document = JsonDocument.Parse(ReadManifest());
        JsonElement root = document.RootElement;
        string[] expectedFamilies = ["import-syntax", "export-syntax", "overload-binding", "generic-mapping",
            "ownership", "nullability", "exceptions", "visibility", "ordinary-metadata", "nuget-identity", "aot-reachability", "exclusions"];
        AssertEx.Equal(12, root.GetProperty("families").GetArrayLength());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (string family in expectedFamilies)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.Equal(3, root.GetProperty("cases").EnumerateArray().Count(item => item.GetProperty("family").GetString() == family));
        }
        AssertEx.Equal(5, root.GetProperty("implementationLeaves").GetArrayLength());
        AssertEx.True(root.GetProperty("members").EnumerateArray().Any(item => item.GetProperty("direction").GetString() == "import"), "Import direction is required.");
        AssertEx.True(root.GetProperty("members").EnumerateArray().Any(item => item.GetProperty("direction").GetString() == "export"), "Ordinary .NET export direction is required.");
        AssertEx.Equal("Microsoft.Extensions.Primitives", root.GetProperty("members")[7].GetProperty("package").GetString()!);
        AssertEx.Equal("10.0.0", root.GetProperty("members")[7].GetProperty("packageVersion").GetString()!);
        return Task.CompletedTask;
    }

    private static Task MutationsAsync(Action<JsonObject>[] mutations)
    {
        AssertEx.True(mutations.Length is > 0 and <= 8, "Mutation batch has a fixed maximum of eight cases.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonObject original = JsonNode.Parse(ReadManifest())!.AsObject();
        foreach (Action<JsonObject> mutate in mutations)
        {
            deadline.Token.ThrowIfCancellationRequested();
            JsonObject manifest = original.DeepClone().AsObject();
            mutate(manifest);
            DotNetInteropContractValidation result = DotNetInteropContract.Validate(manifest.ToJsonString(), deadline.Token);
            AssertEx.False(result.IsValid, "Changed frozen content must not close P2-06.01.");
            AssertEx.Equal(0, result.VerifiedCaseCount, "Invalid inventory cannot report verified coverage.");
            AssertEx.False(result.RuntimeEvidence, "A rejected design is never runtime evidence.");
        }
        return Task.CompletedTask;
    }

    private static string ReadManifest()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string path = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", "p2-dotnet-interop-v1-manifest.json");
        AssertEx.True(new FileInfo(path).Length <= DotNetInteropContract.MaximumManifestBytes, "Manifest read has a byte bound.");
        return File.ReadAllText(path);
    }
}
