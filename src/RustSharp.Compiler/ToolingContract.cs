using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RustSharp.Compiler;

/// <summary>A source fixture frozen before implementing the formatter.</summary>
public sealed record ToolingCorpusCase(string Id, string File, string SourceSha256,
    string Expected, string? DiagnosticCode, string? DiagnosticText);

/// <summary>A finite acceptance obligation; this record does not claim its implementation has passed.</summary>
public sealed record ToolingScenarioCase(string Id, string Area, string Expected,
    string? DiagnosticCode, string Scenario);

/// <summary>The validated P2-08 v1 scope. Downstream implementation evidence is separate.</summary>
public sealed record ToolingContractManifest(int Version, int Denominator, string ManifestSha256,
    string SyntaxManifestSha256, IReadOnlyList<ToolingCorpusCase> Corpus,
    IReadOnlyList<ToolingScenarioCase> Cases);

/// <summary>A stable manifest or corpus diagnostic.</summary>
public sealed class ToolingContractException : ArgumentException
{
    /// <summary>Creates a malformed-scope diagnostic.</summary>
    public ToolingContractException() : this(ToolingContract.InvalidManifest, "Invalid tooling contract.") { }
    /// <summary>Creates a malformed-scope diagnostic with a message.</summary>
    public ToolingContractException(string? message) : base(message) => Code = ToolingContract.InvalidManifest;
    /// <summary>Creates a malformed-scope diagnostic retaining its underlying cause.</summary>
    public ToolingContractException(string? message, Exception? innerException) : base(message, innerException) => Code = ToolingContract.InvalidManifest;
    /// <summary>Creates a frozen contract diagnostic with its specific code.</summary>
    public ToolingContractException(string code, string message) : base(message) => Code = code;
    /// <summary>The frozen diagnostic code.</summary>
    public string Code { get; }
}

/// <summary>
/// Strict, bounded reader for the formatting, documentation and incremental-build scope.
/// It validates scope and source provenance only: it never formats, renders or reuses artifacts.
/// </summary>
public static class ToolingContract
{
    /// <summary>The versioned profile.</summary>
    public const string Profile = "tooling-v1";
    /// <summary>The committed manifest file name.</summary>
    public const string ManifestFileName = "p2-tooling-v1-manifest.json";
    /// <summary>All 49 P1 syntax fixtures plus 42 tooling obligations.</summary>
    public const int CaseDenominator = 91;
    /// <summary>A malformed, reduced or extended v1 scope.</summary>
    public const string InvalidManifest = "RSTC1001";
    /// <summary>A source fixture or syntax baseline disagrees with its recorded provenance.</summary>
    public const string CorpusMismatch = "RSTC1002";
    /// <summary>The contract reader exceeded its bounds.</summary>
    public const string BudgetExceeded = "RSTC0001";
    private const int MaximumJsonCharacters = 131_072;
    private const string FixturePrefix = "tools/RustSharp.Conformance/fixtures/";

    private static readonly string[] SyntaxFamilies =
    ["modules", "imports", "functions", "structs", "enums", "aliases-constants", "statements",
        "expressions", "operator-binding", "patterns", "types", "generics", "attributes", "literals",
        "malformed", "unsupported", "control-flow", "traits"];
    private static readonly string[] CacheInputs =
    ["cacheSchemaVersion", "compilerVersion", "compilerBinarySha256", "profileId", "profileVersion",
        "profileManifestSha256", "sourceLogicalPaths", "sourceContentSha256", "referenceAssemblyIdentity",
        "referencePeSha256", "referenceMetadataSha256", "referenceClosure", "cfg", "features", "rid",
        "targetKind", "assemblyName", "entryPoint", "panicStrategy", "optimization", "debugSymbols",
        "languageEdition", "sourcePathMap", "emitterVersion", "metadataSchemaVersion", "workspaceLockSha256"];
    private static readonly string[] DocItems =
    ["module", "function", "struct", "enum", "type-alias", "const", "trait", "associated-function",
        "associated-type", "associated-const", "public-field", "variant"];
    private static readonly string[] CaseIds =
    ["format-basic", "format-comments", "format-doc-comments", "format-nested-comments", "format-literals",
        "format-crlf", "format-bom-shebang", "format-check-drift", "format-idempotent", "format-budget",
        "doc-public-items", "doc-private-items", "doc-generics-signatures", "doc-source-links", "doc-local-links",
        "doc-cross-module-links", "doc-missing-link", "doc-ambiguous-link", "doc-malformed-comment",
        "doc-escape-markup", "doc-unsafe-link", "doc-budget",
        "cache-unchanged", "cache-source", "cache-compiler", "cache-profile", "cache-reference",
        "cache-cfg", "cache-feature", "cache-rid", "cache-reordered-inputs", "cache-dependent-closure",
        "artifact-clean-warm", "artifact-pdb-source-hashes", "artifact-metadata", "artifact-path-remap",
        "artifact-corruption", "artifact-concurrent-writers", "artifact-interruption", "artifact-cancel",
        "artifact-write-failure", "artifact-budget"];
    private static readonly ReadOnlyDictionary<string, string> FailureCodes = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["format-check-drift"] = "RSTF1002", ["format-budget"] = "RSTF0001",
        ["doc-missing-link"] = "RSTD1001", ["doc-ambiguous-link"] = "RSTD1002",
        ["doc-malformed-comment"] = "RSL1014", ["doc-budget"] = "RSTD0001",
        ["doc-unsafe-link"] = "RSTD1003",
        ["artifact-corruption"] = "RSTI1001", ["artifact-interruption"] = "RSTI1002",
        ["artifact-cancel"] = "RSTI1003", ["artifact-write-failure"] = "RSTI1004",
        ["artifact-budget"] = "RSTI0001",
    });

    /// <summary>Reads a v1 manifest with a ten-second deadline and cooperative cancellation.</summary>
    public static ToolingContractManifest Parse(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        long started = Stopwatch.GetTimestamp();
        CheckBudget(started, cancellationToken);
        Require(json.Length is > 0 and <= MaximumJsonCharacters, "Manifest size is outside the fixed bound.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Object(root, "schemaVersion", "profile", "version", "taskId", "dependencies", "rids", "denominator",
                "hashEncoding", "syntaxBasis", "limits", "formatter", "documentation", "cache", "artifacts", "testTargets", "cases");
            Equal(root, "schemaVersion", 1); Equal(root, "version", 1); Equal(root, "denominator", CaseDenominator);
            Equal(root, "profile", Profile); Equal(root, "taskId", "P2-08.01");
            Equal(root, "hashEncoding", "sha256-utf8-lf-no-bom");
            Set(root.GetProperty("dependencies"), ["P1-02", "P1-09"]);
            Set(root.GetProperty("rids"), ["win-x64", "linux-x64"]);
            JsonElement basis = root.GetProperty("syntaxBasis");
            Object(basis, "manifest", "sha256", "profile", "version", "denominator");
            Equal(basis, "manifest", FixturePrefix + "safe-core-syntax-manifest.json");
            Equal(basis, "profile", "safe-core-syntax"); Equal(basis, "version", 3); Equal(basis, "denominator", 49);
            string syntaxHash = Hash(basis, "sha256");
            JsonElement limits = root.GetProperty("limits");
            Object(limits, "timeoutMilliseconds", "maximumSourceCharacters", "maximumTokens", "maximumTrivia",
                "maximumFiles", "maximumDocItems", "maximumLinks", "maximumCacheEntries", "maximumOutputBytes",
                "maximumWriters", "maximumOperations");
            Equal(limits, "timeoutMilliseconds", 10_000); Equal(limits, "maximumSourceCharacters", 1_000_000);
            Equal(limits, "maximumTokens", 250_000); Equal(limits, "maximumTrivia", 500_000);
            Equal(limits, "maximumFiles", 1_024); Equal(limits, "maximumDocItems", 10_000);
            Equal(limits, "maximumLinks", 20_000); Equal(limits, "maximumCacheEntries", 10_000);
            Equal(limits, "maximumOutputBytes", 16_777_216); Equal(limits, "maximumWriters", 2);
            Equal(limits, "maximumOperations", 1_000_000);

            JsonElement formatter = root.GetProperty("formatter");
            Object(formatter, "apis", "syntaxFamilies", "style", "preservation", "corpus");
            Set(formatter.GetProperty("apis"), ["RustFormatter.Format(string source, string sourcePath, FormatterOptions options, CancellationToken cancellationToken) -> FormatterResult",
                "rsc fmt <path>", "rsc fmt --check <path>"]);
            Set(formatter.GetProperty("syntaxFamilies"), SyntaxFamilies);
            Equal(formatter, "style", "four-space-indent; LF; one-final-newline; token-spelling-preserved");
            Set(formatter.GetProperty("preservation"), ["token-kinds-and-spellings", "comment-order-and-spelling",
                "documentation-comments", "nested-block-comments", "literal-content", "bom-and-shebang",
                "ast-equivalence", "idempotence", "check-mode-nonmutation", "failure-nonmutation"]);
            JsonElement corpusJson = formatter.GetProperty("corpus");
            Require(corpusJson.ValueKind == JsonValueKind.Array && corpusJson.GetArrayLength() == 49, "Syntax corpus must contain exactly 49 fixtures.");
            var corpus = new List<ToolingCorpusCase>(49);
            var corpusIds = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in corpusJson.EnumerateArray())
            {
                CheckBudget(started, cancellationToken);
                bool failed = item.TryGetProperty("diagnosticCode", out _);
                if (failed) Object(item, "id", "file", "sourceSha256", "expected", "diagnosticCode", "diagnosticText");
                else Object(item, "id", "file", "sourceSha256", "expected");
                string id = Text(item, "id"), file = Text(item, "file"), expected = Text(item, "expected");
                Require(corpusIds.Add(id) && files.Add(file), "Duplicate syntax case or fixture.");
                Require(file.StartsWith(FixturePrefix, StringComparison.Ordinal) && file.EndsWith(".rs", StringComparison.Ordinal) &&
                    !file.Contains("..", StringComparison.Ordinal) && !file.Contains('\\') && !file.Contains(':'), "Corpus paths must stay in the named fixture directory.");
                string? code = failed ? Text(item, "diagnosticCode") : null;
                Require(expected == (failed ? "format-reject" : "format-preserve"), "Corpus outcome and diagnostic disagree.");
                Require(code is null || code.StartsWith("RSP", StringComparison.Ordinal) || code.StartsWith("RSL", StringComparison.Ordinal), "Unknown syntax rejection diagnostic.");
                corpus.Add(new(id, file, Hash(item, "sourceSha256"), expected, code, failed ? Text(item, "diagnosticText") : null));
            }

            JsonElement docs = root.GetProperty("documentation");
            Object(docs, "apis", "itemKinds", "visibility", "links", "rendering");
            Set(docs.GetProperty("apis"), ["DocumentationModel.Create(SafeCoreSyntaxResult syntax, SafeCoreSourceMap sourceMap, DocumentationOptions options, CancellationToken cancellationToken) -> DocumentationResult",
                "DocumentationRenderer.Render(DocumentationModel model, DocumentationOptions options, CancellationToken cancellationToken) -> DocumentationOutput", "rsc doc <Cargo.toml>"]);
            Set(docs.GetProperty("itemKinds"), DocItems);
            Equal(docs, "visibility", "public reachable items only; private and restricted items excluded; public reexports canonicalized");
            Set(docs.GetProperty("links"), ["crate-local-symbol", "module-qualified-symbol", "public-reexport", "source-relative-path-and-span",
                "https-external", "missing-diagnostic", "ambiguous-diagnostic", "unsafe-scheme-rejected"]);
            Equal(docs, "rendering", "UTF-8 HTML; encode text and attributes; stable ordinal item order; no raw HTML or script execution");

            JsonElement cache = root.GetProperty("cache");
            Object(cache, "apis", "keyInputs", "encoding", "invalidation", "excludedInputs");
            Set(cache.GetProperty("apis"), ["IncrementalCacheKey.Create(IncrementalInputs inputs, CancellationToken cancellationToken) -> string",
                "IncrementalPlan.Create(PackageGraph graph, IReadOnlyDictionary<string, string> previousKeys, IReadOnlyDictionary<string, string> currentKeys, CancellationToken cancellationToken) -> IncrementalPlan"]);
            Set(cache.GetProperty("keyInputs"), CacheInputs);
            Equal(cache, "encoding", "schema-tagged length-prefixed UTF-8; ordinal sorted set inputs; content hashes; SHA-256");
            Equal(cache, "invalidation", "changed node and all reverse dependents; independent nodes retain valid entries; never mtime-only");
            Set(cache.GetProperty("excludedInputs"), ["wall-clock-time", "process-id", "physical-checkout-root-after-path-remap", "input-enumeration-order"]);

            JsonElement artifacts = root.GetProperty("artifacts");
            Object(artifacts, "determinismFields", "storage", "publication");
            Set(artifacts.GetProperty("determinismFields"), ["PE-bytes", "portable-PDB-bytes", "RustSharp-metadata-bytes", "MVID", "PDB-document-paths",
                "PDB-document-sha256", "reference-order", "source-order", "normalized-path-map", "workspace-lock-sha256"]);
            Equal(artifacts, "storage", "content-addressed verified entries; owned same-directory staging; atomic publish; two bounded writers; corrupted entries rejected");
            Equal(artifacts, "publication", "no partial committed results on failure, interruption, cancellation or budget exhaustion; clean and warm bytes equal");
            JsonElement targets = root.GetProperty("testTargets");
            Object(targets, "contract", "formatter", "documentation", "incrementalKeys", "incrementalArtifacts", "workspace");
            Equal(targets, "contract", "dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter P2-08.01 --timeout 30 --deadline 120");
            Equal(targets, "formatter", TestCommand("P2-08.02")); Equal(targets, "documentation", TestCommand("P2-08.03"));
            Equal(targets, "incrementalKeys", TestCommand("P2-08.04")); Equal(targets, "incrementalArtifacts", TestCommand("P2-08.05"));
            Equal(targets, "workspace", TestCommand("P2-08.06"));

            JsonElement caseJson = root.GetProperty("cases");
            Require(caseJson.ValueKind == JsonValueKind.Array && caseJson.GetArrayLength() == CaseIds.Length, "Tooling cases must preserve the 42-case inventory.");
            var cases = new List<ToolingScenarioCase>(CaseIds.Length);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in caseJson.EnumerateArray())
            {
                CheckBudget(started, cancellationToken);
                string id = Text(item, "id");
                bool failed = FailureCodes.TryGetValue(id, out string? code);
                if (failed) Object(item, "id", "area", "expected", "diagnosticCode", "scenario");
                else Object(item, "id", "area", "expected", "scenario");
                Require(ids.Add(id) && CaseIds.Contains(id, StringComparer.Ordinal), "Unknown or duplicate tooling case.");
                string area = id.StartsWith("format-", StringComparison.Ordinal) ? "formatter" :
                    id.StartsWith("doc-", StringComparison.Ordinal) ? "documentation" :
                    id.StartsWith("cache-", StringComparison.Ordinal) ? "incremental-keys" : "incremental-artifacts";
                Equal(item, "area", area); Equal(item, "expected", failed ? "reject" : "pass");
                if (failed) Equal(item, "diagnosticCode", code!);
                string scenario = Text(item, "scenario");
                Require(scenario.Length >= 20, "Every case requires a concrete bounded acceptance obligation.");
                cases.Add(new(id, area, failed ? "reject" : "pass", code, scenario));
            }
            Require(corpus.Count + cases.Count == CaseDenominator, "Fixed case denominator disagrees.");
            CheckBudget(started, cancellationToken);
            return new(1, CaseDenominator, SourceHash(json), syntaxHash, corpus.AsReadOnly(), cases.AsReadOnly());
        }
        catch (JsonException exception) { throw new ToolingContractException(InvalidManifest, exception.Message); }
        catch (InvalidOperationException exception) { throw new ToolingContractException(InvalidManifest, exception.Message); }
        catch (KeyNotFoundException exception) { throw new ToolingContractException(InvalidManifest, exception.Message); }
    }

    /// <summary>Reconciles every fixture against the committed syntax v3 inventory and its normalized source hash.</summary>
    public static void ValidateCorpus(ToolingContractManifest manifest, string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest); ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        long started = Stopwatch.GetTimestamp();
        CheckBudget(started, cancellationToken);
        Require(manifest.Version == 1 && manifest.Denominator == CaseDenominator && manifest.Corpus.Count == 49 &&
            manifest.Cases.Count == 42, "Corpus validation requires the complete v1 scope.");
        string directory = Path.GetFullPath(Path.Combine(repositoryRoot, "tools/RustSharp.Conformance/fixtures"));
        string basisText = ReadBoundedFile(Path.Combine(directory, "safe-core-syntax-manifest.json"), MaximumJsonCharacters);
        CorpusRequire(SourceHash(basisText) == manifest.SyntaxManifestSha256, "Syntax baseline manifest hash changed.");
        using JsonDocument basis = JsonDocument.Parse(basisText, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement originalCases = basis.RootElement.GetProperty("cases");
        CorpusRequire(originalCases.GetArrayLength() == 49, "Syntax baseline denominator changed.");
        var originals = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement item in originalCases.EnumerateArray())
        {
            CheckBudget(started, cancellationToken);
            CorpusRequire(originals.TryAdd(Text(item, "id"), item), "Syntax baseline contains duplicate cases.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ToolingCorpusCase item in manifest.Corpus)
        {
            CheckBudget(started, cancellationToken);
            CorpusRequire(seen.Add(item.Id) && originals.TryGetValue(item.Id, out _), "Syntax corpus was reduced or substituted.");
            JsonElement original = originals[item.Id];
            string expectedPath = FixturePrefix + Text(original, "file");
            CorpusRequire(item.File == expectedPath, "Syntax corpus source path changed.");
            bool success = Text(original, "expected") == "parse-pass";
            CorpusRequire(item.Expected == (success ? "format-preserve" : "format-reject") &&
                item.DiagnosticCode == (success ? null : Text(original, "diagnosticCode")) &&
                item.DiagnosticText == (success ? null : Text(original, "diagnosticText")), "Syntax corpus outcome changed.");
            string path = Path.GetFullPath(Path.Combine(directory, Text(original, "file")));
            CorpusRequire(Path.GetDirectoryName(path) == directory && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0,
                "Syntax corpus escaped its fixture directory.");
            string source = ReadBoundedFile(path, 1_000_000);
            CorpusRequire(SourceHash(source) == item.SourceSha256, "Syntax corpus source hash changed: " + item.Id);
        }
        CheckBudget(started, cancellationToken);
    }

    /// <summary>Hashes LF-normalized UTF-8 text without a leading BOM. Bare CR remains significant.</summary>
    public static string SourceHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Length != 0 && normalized[0] == '\uFEFF') normalized = normalized[1..];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string ReadBoundedFile(string path, int maximumCharacters)
    {
        CorpusRequire(new FileInfo(path).Length <= maximumCharacters * 4L, "Corpus file exceeds its byte budget.");
        string text = File.ReadAllText(path);
        CorpusRequire(text.Length <= maximumCharacters, "Corpus file exceeds its character budget.");
        return text;
    }
    private static string TestCommand(string leaf) => "dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter " + leaf + " --timeout 30 --deadline 120";
    private static void CheckBudget(long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
            throw new ToolingContractException(BudgetExceeded, "Contract validation exceeded ten seconds.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ToolingContractException(InvalidManifest, message);
    }
    private static void CorpusRequire(bool condition, string message)
    {
        if (!condition) throw new ToolingContractException(CorpusMismatch, message);
    }
    private static void Object(JsonElement item, params string[] fields)
    {
        Require(item.ValueKind == JsonValueKind.Object, "Expected JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
            Require(seen.Add(property.Name) && fields.Contains(property.Name, StringComparer.Ordinal), "Unknown or duplicate JSON field: " + property.Name);
        Require(seen.Count == fields.Length, "A required JSON field is missing.");
    }
    private static string Text(JsonElement item, string field)
    {
        JsonElement value = item.GetProperty(field);
        Require(value.ValueKind == JsonValueKind.String, "Expected string: " + field);
        string text = value.GetString()!;
        Require(text.Length is > 0 and <= 4096 && !text.Contains('\0'), "String is empty or exceeds its bound: " + field);
        return text;
    }
    private static string Hash(JsonElement item, string field)
    {
        string hash = Text(item, field);
        Require(hash.Length == 64 && hash.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F'), "Expected uppercase SHA-256: " + field);
        return hash;
    }
    private static void Equal(JsonElement item, string field, string value) => Require(Text(item, field) == value, "Contract field changed: " + field);
    private static void Equal(JsonElement item, string field, int value) => Require(item.GetProperty(field).TryGetInt32(out int actual) && actual == value, "Contract bound changed: " + field);
    private static void Set(JsonElement item, string[] expected)
    {
        Require(item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == expected.Length, "Inventory denominator changed.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in item.EnumerateArray())
        {
            Require(value.ValueKind == JsonValueKind.String, "Expected string inventory member.");
            string member = value.GetString()!;
            Require(seen.Add(member) && expected.Contains(member, StringComparer.Ordinal), "Unknown or duplicate inventory member.");
        }
    }
}
