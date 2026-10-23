using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RustSharp.Compiler;

public sealed record CoreContractValidation(
    bool IsValid,
    string Profile,
    string LeafId,
    int CaseDenominator,
    int VerifiedCaseCount,
    string ManifestSha256,
    string ContractSha256,
    bool RuntimeEvidence,
    IReadOnlyList<string> Families,
    IReadOnlyList<string> Issues);

/// <summary>Frozen P2-01.01 inventory. It describes the core surface; it never claims implementation or runtime execution.</summary>
public static class CoreContract
{
    public const string Profile = "core-v1";
    public const string LeafId = "P2-01.01";
    public const int CaseDenominator = 42;
    public const int MaximumManifestBytes = 512_000;
    public const int MaximumManifestDepth = 24;
    public const string ContractDiagnostic = "RSCORE1090";
    public const string FrozenContractSha256 = "18C6D1B60FDEA05883FBD893DB06961EF9780C69365EE35AEFABDE1C963237A2";

    private static readonly string[] RequiredFamilies =
        ["primitives", "option", "result", "formatting", "comparison", "hashing", "iterators"];

    private static readonly string[] RequiredMembers =
    [
        "core::bool", "core::i32", "core::u32", "core::usize",
        "core::option::Option", "core::option::Option::none", "core::option::Option::some", "core::option::Option::is_some",
        "core::result::Result", "core::result::Result::ok", "core::result::Result::err", "core::result::Result::is_ok",
        "core::fmt::Display", "core::fmt::Debug", "core::fmt::write", "core::fmt::format",
        "core::cmp::PartialEq", "core::cmp::Eq", "core::cmp::Ord", "core::cmp::Ordering",
        "core::hash::Hash", "core::hash::Hasher", "core::hash::DefaultHasher",
        "core::iter::Iterator", "core::iter::IntoIterator", "core::iter::empty", "core::iter::once", "core::iter::map"
    ];

    private static readonly string[] RequiredCaseIds =
    [
        "primitive-bool", "primitive-i32", "primitive-u32", "primitive-usize", "primitive-overflow", "primitive-invalid-cast",
        "option-none", "option-some", "option-match", "option-map", "option-boundary", "option-invalid-use",
        "result-ok", "result-err", "result-match", "result-map", "result-boundary", "result-invalid-use",
        "format-display", "format-debug", "format-golden-utf8", "format-invalid-specifier", "format-missing-impl", "format-output-budget",
        "comparison-equality", "comparison-order", "comparison-float", "comparison-incompatible", "comparison-boundary", "comparison-invalid-impl",
        "hash-stable", "hash-collision", "hash-empty", "hash-incompatible", "hash-boundary", "hash-invalid-impl",
        "iterator-empty", "iterator-single", "iterator-many", "iterator-map", "iterator-order", "iterator-invalid-item"
    ];

    public static CoreContractValidation Validate(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<string>();
        string manifestHash = Hash(Encoding.UTF8.GetBytes(json));
        try
        {
            if (json.Length == 0 || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
                return Invalid(manifestHash, issues, "Manifest is empty or exceeds the byte bound.");
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumManifestDepth });
            JsonElement root = document.RootElement;
            Properties(root, ["schemaVersion", "profile", "leafId", "hardDependencies", "platforms", "denominator", "inventoryKind", "families", "members", "diagnostics", "limits", "testTargets", "cases", "evidenceRequiredFields", "runtimeEvidence"], issues, "$", cancellationToken);
            Equal(root, "schemaVersion", 1, issues, "$.schemaVersion");
            Equal(root, "profile", Profile, issues, "$.profile");
            Equal(root, "leafId", LeafId, issues, "$.leafId");
            Equal(root, "denominator", CaseDenominator, issues, "$.denominator");
            Equal(root, "inventoryKind", "frozen-core-contract-inventory", issues, "$.inventoryKind");
            if (root.GetProperty("runtimeEvidence").GetBoolean()) issues.Add("$.runtimeEvidence must remain false until executable consumers pass.");
            ExactStrings(root.GetProperty("hardDependencies"), ["P1-GATE"], issues, "$.hardDependencies", cancellationToken);
            ExactStrings(root.GetProperty("platforms"), ["win-x64", "linux-x64"], issues, "$.platforms", cancellationToken);
            ExactStrings(root.GetProperty("families"), RequiredFamilies, issues, "$.families", cancellationToken);
            ExactStrings(root.GetProperty("members"), RequiredMembers, issues, "$.members", cancellationToken);

            JsonElement diagnostics = root.GetProperty("diagnostics");
            Properties(diagnostics, ["invalidInput", "overflow", "missingImplementation", "invalidSpecifier", "incompatibleTypes", "budgetExceeded"], issues, "$.diagnostics", cancellationToken);
            JsonElement limits = root.GetProperty("limits");
            Properties(limits, ["maximumMembers", "maximumCases", "maximumOutputBytes", "maximumOperations", "timeoutMilliseconds"], issues, "$.limits", cancellationToken);
            foreach (string name in new[] { "maximumMembers", "maximumCases", "maximumOutputBytes", "maximumOperations", "timeoutMilliseconds" })
                if (limits.GetProperty(name).GetInt32() <= 0) issues.Add("$.limits." + name + " must be positive.");

            JsonElement cases = root.GetProperty("cases");
            if (cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() != CaseDenominator)
                issues.Add("$.cases must contain exactly 42 frozen cases.");
            var caseIds = new HashSet<string>(StringComparer.Ordinal);
            int verified = 0;
            foreach (JsonElement item in cases.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Properties(item, ["id", "family", "kind", "expected", "scenario"], issues, "$.cases[]", cancellationToken);
                string id = item.GetProperty("id").GetString() ?? string.Empty;
                string family = item.GetProperty("family").GetString() ?? string.Empty;
                string kind = item.GetProperty("kind").GetString() ?? string.Empty;
                string expected = item.GetProperty("expected").GetString() ?? string.Empty;
                string scenario = item.GetProperty("scenario").GetString() ?? string.Empty;
                if (!caseIds.Add(id)) issues.Add("$.cases duplicate id: " + id);
                if (!RequiredCaseIds.Contains(id, StringComparer.Ordinal)) issues.Add("$.cases unknown id: " + id);
                if (!RequiredFamilies.Contains(family, StringComparer.Ordinal)) issues.Add("$.cases invalid family: " + family);
                if (kind is not ("positive" or "error" or "boundary")) issues.Add("$.cases invalid kind: " + id);
                if (expected is not ("pass" or "reject")) issues.Add("$.cases invalid expected result: " + id);
                if (scenario.Length < 20 || scenario.Length > 4096) issues.Add("$.cases scenario is outside its bound: " + id);
                string expectedScenario = $"Bounded {family} contract case '{id}' with fixed input, output or diagnostic and no skipped execution.";
                if (!string.Equals(scenario, expectedScenario, StringComparison.Ordinal)) issues.Add("$.cases scenario changed: " + id);
                verified++;
            }
            if (!caseIds.SetEquals(RequiredCaseIds)) issues.Add("$.cases must preserve the exact 42-case inventory.");

            JsonElement targets = root.GetProperty("testTargets");
            if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() != 6) issues.Add("$.testTargets must contain six bounded leaf targets.");
            foreach (JsonElement target in targets.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Properties(target, ["leafId", "command", "denominator", "registration"], issues, "$.testTargets[]", cancellationToken);
                string leaf = target.GetProperty("leafId").GetString() ?? string.Empty;
                int denominator = target.GetProperty("denominator").GetInt32();
                string command = target.GetProperty("command").GetString() ?? string.Empty;
                if (!leaf.StartsWith("P2-01.", StringComparison.Ordinal) || denominator <= 0 || !command.Contains("--filter", StringComparison.Ordinal) || !command.Contains("--timeout", StringComparison.Ordinal) || !command.Contains("--deadline", StringComparison.Ordinal))
                    issues.Add("$.testTargets contains an unbounded or invalid target.");
            }
            ExactStrings(root.GetProperty("evidenceRequiredFields"), ["leafId", "manifestSha256", "candidateSha", "tools", "runtimeIdentifier", "command", "denominator", "passed", "failed", "skipped", "timeResourceLimits", "cleanup", "failureReasons", "provenance"], issues, "$.evidenceRequiredFields", cancellationToken);
            string contractHash = Hash(Encoding.UTF8.GetBytes(Canonicalize(root)));
            if (!string.Equals(contractHash, FrozenContractSha256, StringComparison.Ordinal))
                issues.Add("The frozen core-v1 canonical contract hash differs; create a new profile version for expansion.");
            return new(issues.Count == 0, Profile, LeafId, CaseDenominator, issues.Count == 0 ? verified : 0,
                manifestHash, contractHash, false,
                new ReadOnlyCollection<string>(RequiredFamilies), new ReadOnlyCollection<string>(issues));
        }
        catch (JsonException exception) { return Invalid(manifestHash, issues, exception.Message); }
        catch (InvalidOperationException exception) { return Invalid(manifestHash, issues, exception.Message); }
        catch (KeyNotFoundException exception) { return Invalid(manifestHash, issues, exception.Message); }
    }

    public static CoreContractValidation Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.GetFullPath(path));
            if (bytes.Length > MaximumManifestBytes) return Invalid(string.Empty, [], "Manifest exceeds the byte bound.");
            return Validate(new UTF8Encoding(false, true).GetString(bytes), cancellationToken) with { ManifestSha256 = Hash(bytes) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Invalid(string.Empty, [], exception.Message);
        }
    }

    private static CoreContractValidation Invalid(string manifestHash, List<string> issues, string message)
    {
        issues.Add(message);
        return new(false, Profile, LeafId, CaseDenominator, 0, manifestHash, string.Empty, false,
            new ReadOnlyCollection<string>(RequiredFamilies), new ReadOnlyCollection<string>(issues));
    }

    private static void Properties(JsonElement value, string[] expected, List<string> issues, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Object) { issues.Add(path + " must be an object."); return; }
        string[] actual = value.EnumerateObject().Select(static item => item.Name).ToArray();
        if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count()) issues.Add(path + " contains duplicate properties.");
        if (!actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) issues.Add(path + " has missing or unexpected properties.");
    }

    private static void ExactStrings(JsonElement value, string[] expected, List<string> issues, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Array) { issues.Add(path + " must be an array."); return; }
        string[] actual = value.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();
        if (!actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) issues.Add(path + " differs from the frozen inventory.");
    }

    private static void Equal(JsonElement root, string name, string expected, List<string> issues, string path)
    { if (!string.Equals(root.GetProperty(name).GetString(), expected, StringComparison.Ordinal)) issues.Add(path + " differs from the frozen value."); }
    private static void Equal(JsonElement root, string name, int expected, List<string> issues, string path)
    { if (root.GetProperty(name).GetInt32() != expected) issues.Add(path + " differs from the frozen value."); }
    private static string Canonicalize(JsonElement root) => JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = false });
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
