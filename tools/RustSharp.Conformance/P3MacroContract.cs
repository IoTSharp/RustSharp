using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RustSharp.Conformance;

/// <summary>Validates the proposed P3 scope only; it never expands or executes a macro.</summary>
public static class P3MacroContract
{
    public const string Profile = "macros-v1.candidate";
    public const string DiagnosticCode = "RSMAC1090";
    public const int Denominator = 82;
    public const int MaximumContractBytes = 256_000;
    public const int ValidationTimeoutMilliseconds = 5_000;
    public const string FrozenContentSha256 = "029BC741A5573580826EE4734CDEBCE54F1FEB9132F4460B530EC1676752F0CF";
    private const int MaximumNodes = 10_000;
    private static readonly string[] RootFields = ["schemaVersion", "profile", "contractVersion", "leafId", "state", "inventoryKind", "formalAcceptanceAllowed", "runtimeCasesExecuted", "hardDependencies", "targetPlatforms", "edition", "comparisonOracle", "limits", "features", "unsupportedFragments", "excludedBuiltins", "excludedBuiltinDiagnostic", "diagnostics", "denominator", "cases", "evidenceRequiredFields", "formalBlockers"];
    private static readonly string[] CaseFields = ["id", "feature", "kind", "source", "setup", "expected", "diagnostic", "budget", "budgetValue"];

    public static P3MacroContractResult Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumContractBytes) return Failure("$", "Empty or oversized candidate file.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            string json = new UTF8Encoding(false, true).GetString(bytes);
            if (json.StartsWith('\uFEFF')) json = json[1..];
            return Validate(json, cancellationToken) with { RawSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return Failure("$", exception.Message);
        }
    }

    public static P3MacroContractResult Validate(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        if (json.Length > MaximumContractBytes || Encoding.UTF8.GetByteCount(json) > MaximumContractBytes)
            return Failure("$", "Candidate exceeds the byte budget.");
        var clock = Stopwatch.StartNew();
        var validator = new Validator(clock, cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement root = document.RootElement;
            validator.CheckTree(root, "$", 0);
            validator.Properties(root, "$", RootFields);
            validator.Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("contractVersion").GetInt32() == 1, "$", "Version must remain 1.");
            validator.Require(root.GetProperty("profile").GetString() == Profile && root.GetProperty("leafId").GetString() == "P3-01.01", "$", "Wrong candidate identity.");
            validator.Require(root.GetProperty("state").GetString() == "candidate-predependency" && root.GetProperty("inventoryKind").GetString() == "contract-cases-not-macro-execution", "$", "Candidate cannot claim execution or acceptance.");
            validator.Require(!root.GetProperty("formalAcceptanceAllowed").GetBoolean() && root.GetProperty("runtimeCasesExecuted").GetInt32() == 0, "$", "Formal acceptance and runtime execution are prohibited here.");
            validator.Require(root.GetProperty("denominator").GetInt32() == Denominator, "$.denominator", "Frozen case count must be 82.");
            validator.Strings(root.GetProperty("hardDependencies"), "$.hardDependencies", ["P1-01", "P2-GATE"]);
            validator.Strings(root.GetProperty("targetPlatforms"), "$.targetPlatforms", ["win-x64", "linux-x64"]);
            JsonElement features = root.GetProperty("features"), cases = root.GetProperty("cases"), limits = root.GetProperty("limits");
            validator.Require(features.GetArrayLength() == 18 && cases.GetArrayLength() == Denominator, "$", "Feature/case inventory changed.");
            var featureIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement feature in features.EnumerateArray())
            {
                validator.Step();
                validator.Properties(feature, "$.features[]", ["id", "family", "rule"]);
                validator.Require(featureIds.Add(Text(feature, "id")), "$.features", "Duplicate feature.");
                validator.Require(Text(feature, "rule").Length >= 20, "$.features[].rule", "Exact grammar/hygiene rule required.");
            }
            var caseIds = new HashSet<string>(StringComparer.Ordinal);
            var coverage = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in cases.EnumerateArray())
            {
                validator.Step();
                validator.Properties(item, "$.cases[]", CaseFields);
                string id = Text(item, "id"), feature = Text(item, "feature"), kind = Text(item, "kind"), diagnostic = item.GetProperty("diagnostic").GetString() ?? throw new FormatException("Diagnostic must be a string, including empty for success.");
                validator.Require(caseIds.Add(id), "$.cases[].id", "Duplicate case ID.");
                validator.Require(kind is "pass" or "reject" or "limit" or "boundary", "$.cases[].kind", "Unknown case kind.");
                validator.Require((kind is "pass" or "boundary") == (diagnostic.Length == 0), "$.cases[].diagnostic", "Expected outcome and diagnostic disagree.");
                if (diagnostic.Length != 0) validator.Require(root.GetProperty("diagnostics").TryGetProperty(diagnostic, out _), "$.cases[].diagnostic", "Undeclared diagnostic.");
                _ = Text(item, "source"); _ = Text(item, "setup"); _ = Text(item, "expected");
                coverage.Add(feature + ":" + kind);
                string budget = item.GetProperty("budget").GetString() ?? throw new FormatException("Budget must be a string, including empty when unused.");
                int value = item.GetProperty("budgetValue").GetInt32();
                validator.Require(value >= 0, "$.cases[].budgetValue", "Negative budget value.");
                if (budget.Length != 0) validator.Require(limits.TryGetProperty(budget, out _), "$.cases[].budget", "Undeclared resource budget.");
            }
            foreach (string feature in featureIds)
            {
                validator.Step();
                validator.Require(coverage.Contains(feature + ":pass") && coverage.Contains(feature + ":reject") && coverage.Contains(feature + ":limit"), "$.cases", "Every included feature needs pass/reject/limit cases.");
            }
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                root.WriteTo(writer);
                writer.Flush();
            }
            string contentHash = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
            validator.Require(contentHash == FrozenContentSha256, "$", "Frozen candidate content changed; propose a new candidate version.");
            cancellationToken.ThrowIfCancellationRequested();
            return new(validator.Errors.Count == 0, validator.Errors.Count == 0 ? cases.GetArrayLength() : 0,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), contentHash, validator.Errors.AsReadOnly());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException or TimeoutException)
        {
            validator.Add("$", "Malformed or bounded-out candidate: " + exception.Message);
            return new(false, 0, string.Empty, string.Empty, validator.Errors.AsReadOnly());
        }
    }

    private static string Text(JsonElement item, string name)
    {
        string? text = item.GetProperty(name).GetString();
        return string.IsNullOrWhiteSpace(text) ? throw new FormatException("Nonempty text required: " + name) : text;
    }

    private static P3MacroContractResult Failure(string path, string message) => new(false, 0, string.Empty, string.Empty, [new(DiagnosticCode, path, message)]);

    private sealed class Validator(Stopwatch clock, CancellationToken cancellationToken)
    {
        private int _nodes;
        internal List<P3MacroContractIssue> Errors { get; } = [];
        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_nodes > MaximumNodes || clock.ElapsedMilliseconds >= ValidationTimeoutMilliseconds) throw new TimeoutException("Contract validation count/time budget exceeded.");
        }
        internal void Add(string path, string message) { if (Errors.Count < 32) Errors.Add(new(DiagnosticCode, path, message)); }
        internal void Require(bool condition, string path, string message) { if (!condition) Add(path, message); }
        internal void Properties(JsonElement item, string path, string[] required)
        {
            Step();
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in item.EnumerateObject()) { Step(); actual.Add(property.Name); }
            Require(actual.SetEquals(required), path, "Missing or unexpected schema property.");
        }
        internal void Strings(JsonElement item, string path, string[] expected)
        {
            Step();
            Require(item.GetArrayLength() == expected.Length, path, "Fixed array length differs.");
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement value in item.EnumerateArray()) { Step(); Require(actual.Add(value.GetString()!), path, "Duplicate string."); }
            Require(actual.SetEquals(expected), path, "Fixed string inventory differs.");
        }
        internal void CheckTree(JsonElement item, string path, int depth)
        {
            Step();
            if (depth > 24) throw new FormatException("JSON nesting limit.");
            if (item.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in item.EnumerateObject())
                {
                    Step(); Require(names.Add(property.Name), path, "Duplicate JSON property " + property.Name);
                    CheckTree(property.Value, path + "." + property.Name, depth + 1);
                }
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                Require(item.GetArrayLength() <= 128, path, "Array item bound exceeded.");
                foreach (JsonElement child in item.EnumerateArray()) CheckTree(child, path + "[]", depth + 1);
            }
        }
    }
}

public sealed record P3MacroContractIssue(string Code, string JsonPath, string Message);

public sealed record P3MacroContractResult(bool IsSuccessful, int ValidatedCaseCount, string RawSha256,
    string ContentSha256, IReadOnlyList<P3MacroContractIssue> Diagnostics)
{
    public int ExpectedCaseCount { get; } = P3MacroContract.Denominator;
    public int ExecutedRuntimeCaseCount { get; }
    public bool FormalAcceptanceAllowed { get; }
}
