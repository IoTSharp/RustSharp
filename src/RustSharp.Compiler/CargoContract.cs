using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RustSharp.Compiler;

/// <summary>The immutable cargo-v1 scope inventory. Validation does not execute its later Cargo scenarios.</summary>
public static class CargoContract
{
    public const string Profile = "cargo-v1";
    public const string LeafId = "P2-04.01";
    public const string ContractDiagnostic = "RSCARGO1090";
    public const int Denominator = 78;
    public const int MaximumContractBytes = 512_000;
    public const int ValidationTimeoutMilliseconds = 5_000;
    public const string FrozenNormalizedSha256 = "CB890EA7A2C96CC3BA8618DD7E6C0DF490B3A2802B14211A0074AA6027DF1731";
    public const string FrozenContentSha256 = "97D95FF1CDBFF523F52B694AC6D47C445668BAD77C471CF7710532C88A40DB44";

    private static readonly string[] RequiredKeys =
    [
        "package.name", "package.version", "package.edition", "workspace.members", "workspace.exclude", "workspace.resolver",
        "lib.name", "lib.path", "lib.crate-type", "bin.name", "bin.path", "bin.required-features",
        "dependencies.<name>.path", "dependencies.<name>.package", "dependencies.<name>.version",
        "dependencies.<name>.features", "dependencies.<name>.optional", "dependencies.<name>.default-features",
        "target.<cfg>.dependencies.<name>.path", "target.<cfg>.dependencies.<name>.package", "target.<cfg>.dependencies.<name>.version",
        "target.<cfg>.dependencies.<name>.features", "target.<cfg>.dependencies.<name>.optional", "target.<cfg>.dependencies.<name>.default-features",
        "features.<name>", "lock.version", "lock.package.name", "lock.package.version", "lock.package.dependencies"
    ];

    private static readonly string[] RequiredRules =
    [
        "toml", "unknown-members", "workspace-root", "targets", "package-identity", "dependency-forms", "dependency-sources",
        "dependency-order", "paths", "features-default", "features-optional", "features-transitive", "features-unification",
        "features-cycles", "features-unsupported", "cfg-platform", "cfg-feature", "cfg-combinators", "cfg-items", "cfg-unsupported",
        "lock-format", "lock-determinism", "locked-mode", "dependency-cycles", "cancellation", "source-assembly"
    ];

    private static readonly string[] FixedCaseIds =
    [
        "package-minimal", "package-metadata", "workspace-members", "workspace-exclude", "workspace-resolver", "virtual-workspace",
        "combined-workspace", "default-targets", "explicit-lib", "explicit-bin", "bin-required-features", "dependency-inline",
        "dependency-table", "dependency-rename", "dependency-version", "dependency-order", "malformed-toml", "duplicate-key",
        "duplicate-package", "missing-member", "missing-source", "unknown-key", "registry-dependency", "git-dependency", "path-escape",
        "quoted-hash", "utf8-invalid", "unsupported-value", "edition-unsupported", "resolver-unsupported", "feature-default",
        "feature-no-default", "feature-optional-dependency", "feature-transitive", "feature-unification", "feature-unknown",
        "feature-cycle", "feature-weak-unsupported", "feature-dependency-default", "feature-empty", "feature-edge-budget",
        "cfg-windows", "cfg-linux", "cfg-x64", "cfg-feature", "cfg-all", "cfg-any", "cfg-not", "cfg-unknown", "cfg-invalid-combination",
        "cfg-depth", "cfg-required-features", "cfg-source-items", "lock-serialize", "lock-reordered", "locked-current", "locked-missing",
        "locked-stale", "lock-version", "lock-duplicate", "lock-identity", "graph-cycle", "lock-byte-boundary", "package-count-budget",
        "manifest-byte-budget", "dependency-count-budget", "target-count-budget", "graph-depth-budget", "operation-budget",
        "deadline-budget", "cancelled-resolution", "consumer-two-package", "consumer-multi-package", "consumer-feature-cfg",
        "consumer-missing-export", "consumer-private-export", "consumer-incompatible-export", "consumer-deterministic"
    ];

    private static readonly IReadOnlyDictionary<string, int> RequiredLimits = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["maximumPackages"] = 64, ["maximumManifestBytes"] = 1_000_000, ["maximumDependenciesPerPackage"] = 64,
        ["maximumTargets"] = 32, ["maximumFeaturesPerPackage"] = 128, ["maximumFeatureEdges"] = 1024,
        ["maximumCfgDepth"] = 16, ["maximumGraphDepth"] = 32, ["maximumOperations"] = 20_000,
        ["maximumLockBytes"] = 1_000_000, ["timeoutMilliseconds"] = 10_000
    });

    private static readonly IReadOnlyDictionary<string, int> LeafDenominators = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["P2-04.02"] = 38, ["P2-04.03"] = 11, ["P2-04.04"] = 12, ["P2-04.05"] = 10, ["P2-04.06"] = 7
    });

    public static CargoContractValidationResult Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumContractBytes) return Failure("$", "Contract byte limit exceeded or empty file.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            string json = new UTF8Encoding(false, true).GetString(bytes);
            if (json.StartsWith('\uFEFF')) json = json[1..];
            return Validate(json, cancellationToken) with { ManifestSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return Failure("$", exception.Message);
        }
    }

    public static CargoContractValidationResult Validate(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        if (json.Length > MaximumContractBytes || Encoding.UTF8.GetByteCount(json) > MaximumContractBytes)
            return Failure("$", "Contract byte limit exceeded.");
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string rawHash = Convert.ToHexString(SHA256.HashData(bytes));
        string normalizedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.Replace("\r\n", "\n", StringComparison.Ordinal))));
        var clock = Stopwatch.StartNew();
        var validator = new Validator(clock, cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            CargoContractManifest manifest = validator.Read(document.RootElement);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                document.RootElement.WriteTo(writer);
                writer.Flush();
            }
            string contentHash = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
            if (!string.Equals(contentHash, FrozenContentSha256, StringComparison.Ordinal))
                validator.Add("$", "Frozen cargo-v1 content hash differs; expansion requires a new profile version.");
            return new(rawHash, normalizedHash, Denominator, manifest.Cases.Count, 0, clock.Elapsed.TotalMilliseconds,
                validator.Errors.Count == 0 ? manifest : null, validator.Errors.AsReadOnly()) { ContentSha256 = contentHash };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
        {
            validator.Add("$", "Malformed contract schema: " + exception.Message);
            return new(rawHash, normalizedHash, Denominator, 0, 0, clock.Elapsed.TotalMilliseconds, null, validator.Errors.AsReadOnly());
        }
        catch (TimeoutException exception)
        {
            validator.Add("$", exception.Message);
            return new(rawHash, normalizedHash, Denominator, 0, 0, clock.Elapsed.TotalMilliseconds, null, validator.Errors.AsReadOnly());
        }
    }

    private static CargoContractValidationResult Failure(string path, string message) =>
        new(string.Empty, string.Empty, Denominator, 0, 0, 0, null, [new(ContractDiagnostic, path, message)]);

    private sealed class Validator(Stopwatch clock, CancellationToken cancellationToken)
    {
        private int _operations;
        internal List<CargoContractIssue> Errors { get; } = [];

        internal void Add(string path, string message)
        {
            if (Errors.Count < 32) Errors.Add(new(ContractDiagnostic, path, message));
        }

        private void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_operations > 10_000 || clock.ElapsedMilliseconds >= ValidationTimeoutMilliseconds)
                throw new TimeoutException("Contract validation exceeded its operation/time limit.");
        }

        internal CargoContractManifest Read(JsonElement root)
        {
            Step();
            Properties(root, "$", ["schemaVersion", "profile", "leafId", "hardDependencies", "denominator", "inventoryKind", "platforms",
                "acceptedKeys", "limits", "rules", "unsupportedKeys", "diagnostics", "testTargets", "cases", "evidenceRequiredFields"]);
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("profile").GetString() != Profile || root.GetProperty("leafId").GetString() != LeafId)
                Add("$", "Contract schema, profile and leaf identity must match cargo-v1/P2-04.01.");
            if (root.GetProperty("denominator").GetInt32() != Denominator) Add("$.denominator", "Fixed denominator must be 78.");
            Strings(root.GetProperty("hardDependencies"), "$.hardDependencies", ["P1-03"]);
            Strings(root.GetProperty("platforms"), "$.platforms", ["win-x64", "linux-x64"]);
            Strings(root.GetProperty("acceptedKeys"), "$.acceptedKeys", RequiredKeys);
            if (root.GetProperty("inventoryKind").GetString() != "frozen-cargo-contract-cases-not-runtime-execution")
                Add("$.inventoryKind", "Contract inventory must not claim runtime scenario execution.");

            JsonElement limits = root.GetProperty("limits");
            Properties(limits, "$.limits", RequiredLimits.Keys.ToArray());
            foreach ((string id, int value) in RequiredLimits)
            {
                Step();
                if (limits.GetProperty(id).GetInt32() != value) Add("$.limits." + id, "Frozen budget differs.");
            }
            var ruleMap = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonElement rules = Array(root, "rules", 26);
            foreach (JsonElement rule in rules.EnumerateArray())
            {
                Step();
                Properties(rule, "$.rules[]", ["id", "contract"]);
                string id = Text(rule, "id"), text = Text(rule, "contract");
                if (!ruleMap.TryAdd(id, text)) Add("$.rules", "Duplicate rule " + id);
            }
            ExactSet(ruleMap.Keys, RequiredRules, "$.rules");

            var requirements = new HashSet<string>(RequiredKeys.Concat(RequiredRules).Concat(RequiredLimits.Keys), StringComparer.Ordinal);
            var coverage = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cases = new List<CargoContractCase>(Denominator);
            JsonElement diagnostics = root.GetProperty("diagnostics");
            Properties(diagnostics, "$.diagnostics", ["malformedManifest", "unsupportedDependency", "dependencyCycle", "limitExceeded",
                "unsupportedManifest", "duplicateIdentity", "missingInput", "invalidFeature", "invalidCfg", "incompatibleLock",
                "missingExport", "privateExport", "incompatibleExport"]);
            var allowedDiagnostics = diagnostics.EnumerateObject().Select(static item => item.Value.GetString()!).ToHashSet(StringComparer.Ordinal);
            JsonElement caseArray = Array(root, "cases", Denominator);
            foreach (JsonElement item in caseArray.EnumerateArray())
            {
                Step();
                Properties(item, "$.cases[]", ["id", "leafId", "family", "kind", "expectedDiagnostic", "covers", "scenario"]);
                string id = Text(item, "id"), leaf = Text(item, "leafId"), family = Text(item, "family"), kind = Text(item, "kind");
                string diagnostic = item.GetProperty("expectedDiagnostic").GetString() ?? string.Empty;
                if (!ids.Add(id)) Add("$.cases", "Duplicate case " + id);
                if (!LeafDenominators.ContainsKey(leaf)) Add("$.cases." + id, "Unknown implementation leaf.");
                if (family is not ("manifest" or "workspace" or "target" or "dependency" or "feature" or "cfg" or "lock" or "graph" or "integration"))
                    Add("$.cases." + id, "Unknown Cargo family.");
                if (kind is not ("positive" or "error" or "boundary")) Add("$.cases." + id, "Invalid case kind.");
                if ((kind == "positive" && diagnostic.Length != 0) || (kind == "error" && diagnostic.Length == 0) ||
                    (diagnostic.Length != 0 && !allowedDiagnostics.Contains(diagnostic))) Add("$.cases." + id, "Invalid frozen diagnostic expectation.");
                JsonElement covers = item.GetProperty("covers");
                if (covers.ValueKind != JsonValueKind.Array || covers.GetArrayLength() is < 1 or > 66)
                    throw new FormatException("Case coverage must contain 1..66 requirements.");
                string[] covered = covers.EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToArray();
                foreach (string requirement in covered)
                {
                    Step();
                    if (!requirements.Contains(requirement)) Add("$.cases." + id, "Unknown covered requirement " + requirement);
                    coverage.Add(requirement);
                }
                cases.Add(new(id, leaf, family, kind, diagnostic, System.Array.AsReadOnly(covered), Text(item, "scenario")));
            }
            ExactSet(ids, FixedCaseIds, "$.cases");
            ExactSet(coverage, requirements, "$.coverage");
            foreach ((string leaf, int count) in LeafDenominators)
            {
                Step();
                if (cases.Count(item => item.LeafId == leaf) != count) Add("$.cases", "Fixed leaf denominator differs for " + leaf);
            }
            var targets = new List<CargoContractTestTarget>(6);
            foreach (JsonElement target in Array(root, "testTargets", 6).EnumerateArray())
            {
                Step();
                Properties(target, "$.testTargets[]", ["leafId", "target", "command", "denominator", "denominatorKind", "registration"]);
                string leaf = Text(target, "leafId");
                int count = target.GetProperty("denominator").GetInt32();
                if (count != (leaf == LeafId ? Denominator : LeafDenominators.GetValueOrDefault(leaf)))
                    Add("$.testTargets", "Incorrect test target denominator.");
                string command = Text(target, "command");
                if (!command.Contains("--filter", StringComparison.Ordinal) || !command.Contains("--timeout", StringComparison.Ordinal) || !command.Contains("--deadline", StringComparison.Ordinal))
                    Add("$.testTargets", "Test command must be filterable and bounded.");
                targets.Add(new(leaf, Text(target, "target"), command, count, Text(target, "denominatorKind"), Text(target, "registration")));
            }
            ExactSet(targets.Select(static item => item.LeafId), new[] { LeafId }.Concat(LeafDenominators.Keys), "$.testTargets");
            Strings(root.GetProperty("evidenceRequiredFields"), "$.evidenceRequiredFields",
                ["leafId", "manifestSha256", "candidateSha", "tools", "runtimeIdentifier", "command", "denominator", "passed", "failed", "skipped",
                    "timeResourceLimits", "processId", "processStartedAtUtc", "parentProcessId", "parentChain", "cleanup", "failureReasons", "provenance"]);
            Strings(root.GetProperty("unsupportedKeys"), "$.unsupportedKeys",
                ["package.authors", "package.build", "package.metadata", "workspace.package", "workspace.dependencies", "workspace.default-members",
                    "dev-dependencies", "build-dependencies", "patch", "replace", "profile", "dependencies.<name>.git", "dependencies.<name>.registry",
                    "lock.package.source", "lock.package.checksum"]);
            return new(Profile, LeafId, Denominator, System.Array.AsReadOnly(RequiredKeys), RequiredLimits,
                new ReadOnlyDictionary<string, string>(ruleMap), cases.AsReadOnly(), targets.AsReadOnly());
        }

        private void Properties(JsonElement value, string path, string[] expected)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new FormatException(path + " must be an object.");
            string[] names = value.EnumerateObject().Take(101).Select(static property => property.Name).ToArray();
            if (names.Length > 100) throw new FormatException(path + " object exceeds its property bound.");
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) Add(path, "Duplicate JSON properties.");
            ExactSet(names, expected, path);
        }

        private JsonElement Array(JsonElement root, string name, int expectedCount)
        {
            JsonElement value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > expectedCount)
                throw new FormatException(name + " exceeds its fixed array bound.");
            if (value.GetArrayLength() != expectedCount) Add("$." + name, "Missing fixed inventory entries.");
            return value;
        }

        private void Strings(JsonElement value, string path, string[] expected)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100) throw new FormatException(path + " array exceeds its bound.");
            string[] values = value.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) Add(path, "Duplicate inventory entries.");
            ExactSet(values, expected, path);
        }

        private void ExactSet(IEnumerable<string> actual, IEnumerable<string> expected, string path)
        {
            Step();
            if (!actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) Add(path, "Missing or unexpected fixed inventory entries.");
        }

        private static string Text(JsonElement value, string name)
        {
            string text = value.GetProperty(name).GetString() ?? string.Empty;
            if (text.Length is 0 or > 4096) throw new FormatException(name + " must be nonempty and bounded.");
            return text;
        }
    }
}

public sealed record CargoContractIssue(string Code, string JsonPath, string Message);
public sealed record CargoContractCase(string Id, string LeafId, string Family, string Kind, string ExpectedDiagnostic,
    IReadOnlyList<string> Covers, string Scenario);
public sealed record CargoContractTestTarget(string LeafId, string Target, string Command, int Denominator, string DenominatorKind, string Registration);
public sealed record CargoContractManifest(string Profile, string LeafId, int Denominator, IReadOnlyList<string> AcceptedKeys,
    IReadOnlyDictionary<string, int> Limits, IReadOnlyDictionary<string, string> Rules, IReadOnlyList<CargoContractCase> Cases,
    IReadOnlyList<CargoContractTestTarget> TestTargets);
public sealed record CargoContractValidationResult(string ManifestSha256, string NormalizedManifestSha256, int Denominator,
    int ValidatedCaseCount, int ExecutedRuntimeCaseCount, double DurationMilliseconds, CargoContractManifest? Manifest,
    IReadOnlyList<CargoContractIssue> Diagnostics)
{
    public string ContentSha256 { get; init; } = string.Empty;
    public bool IsSuccessful => Manifest is not null && Diagnostics.Count == 0 && ValidatedCaseCount == CargoContract.Denominator;
}
