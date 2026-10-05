using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RustSharp.Conformance;

/// <summary>
/// Validates the frozen P1-10 expanded suite inventory and report envelope.
/// This validator is intentionally independent from the v2 process runner and
/// the existing platform/evidence validators: it freezes the v3/v2 identities
/// before a runner is allowed to claim a denominator.
/// </summary>
internal static class P1ExpandedSuiteValidator
{
    internal const string ManifestFileName = "p1-expanded-suites-v2-manifest.json";
    internal const int ManifestVersion = 2;
    internal const int MaximumManifestBytes = 256 * 1024;
    internal const int MaximumReportBytes = 16 * 1024 * 1024;
    internal const int MaximumSuites = 2;
    internal const int MaximumCasesPerSuite = 64;
    internal const int DifferentialDenominator = 32;
    internal const int PlatformDenominator = 24;
    internal const string DifferentialProfile = "p1-differential-v3";
    internal const string PlatformProfile = "p1-platform-v2";
    internal const string OraclePrefix = "rustc 1.98.0 (";

    private const int MaximumJsonDepth = 24;
    private const int MaximumJsonTokens = 8192;
    private const int MaximumIdLength = 96;

    private static readonly string[] DifferentialIds =
    [
        "borrow-shared", "borrow-mutable-reborrow", "drop-return-order", "drop-early-return",
        "borrow-write-read", "borrow-shared-after-update", "borrow-mutable-reborrow-chain", "borrow-shared-reborrow",
        "borrow-fail-mut-alias", "borrow-fail-shared-write", "borrow-fail-moved-mut-ref", "borrow-fail-escape",
        "drop-reverse-locals", "drop-nested-scopes", "drop-return-reverse", "drop-branch-return",
        "borrow-aggregate-copy", "borrow-partial-move", "borrow-nll-branch", "borrow-reborrow-escape",
        "borrow-loop-join", "borrow-index-projection", "borrow-deref-projection", "borrow-call-return",
        "borrow-move-reinit", "borrow-budget-limit", "drop-aggregate-fields", "drop-partial-move",
        "drop-assignment-replacement", "drop-temporary-scope", "drop-unwind-nested", "drop-double-panic",
    ];

    private static readonly string[] PlatformIds =
    [
        "borrow-shared", "borrow-mutable-reborrow", "drop-return-order", "drop-early-return",
        "borrow-write-read", "borrow-shared-after-update", "borrow-mutable-reborrow-chain", "borrow-shared-reborrow",
        "drop-reverse-locals", "drop-nested-scopes", "drop-return-reverse", "drop-branch-return",
        "aggregate-struct-drop", "aggregate-enum-drop", "slice-unsize", "pattern-capture",
        "panic-unwind-generated", "panic-abort-generated", "generic-import-call", "byref-import-call",
        "metadata-contract", "mir-projection", "mir-family", "source-package",
    ];

    internal sealed record CaseSpec(
        string Id,
        string Source,
        string SourceSha256,
        string ExpectationSha256);

    internal sealed record SuiteSpec(
        string Profile,
        int Version,
        int Denominator,
        string Backend,
        IReadOnlyList<string> RuntimeIdentifiers,
        string Oracle,
        string CompilerSha256,
        string ManifestSha256,
        IReadOnlyList<CaseSpec> Cases);

    internal sealed record ExpandedManifest(
        int Version,
        IReadOnlyList<SuiteSpec> Suites);

    internal sealed record ValidationResult(
        bool Valid,
        IReadOnlyList<string> Errors)
    {
        internal static ValidationResult Pass() => new(true, []);
        internal static ValidationResult Fail(IEnumerable<string> errors) => new(false, errors.ToArray());
    }

    internal static ExpandedManifest ParseManifest(string json, string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length is < 1 or > MaximumManifestBytes)
            throw new ArgumentException("P1 expanded manifest exceeds its byte bound.", nameof(json));
        ValidateNoDuplicateProperties(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = MaximumJsonDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("P1 expanded manifest root must be an object.", nameof(json));
        int version = RequiredInt(root, "version");
        if (!root.TryGetProperty("suites", out JsonElement suites) || suites.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("P1 expanded manifest requires a suites array.", nameof(json));
        if (suites.GetArrayLength() > MaximumSuites)
            throw new ArgumentException("P1 expanded manifest exceeds its suite bound.", nameof(json));
        var parsed = new List<SuiteSpec>(suites.GetArrayLength());
        foreach (JsonElement item in suites.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("P1 expanded suite must be an object.", nameof(json));
            string profile = RequiredString(item, "profile");
            int suiteVersion = RequiredInt(item, "suiteVersion");
            int denominator = RequiredInt(item, "denominator");
            string backend = RequiredString(item, "backend");
            string oracle = RequiredString(item, "oracle");
            string compilerHash = RequiredString(item, "compilerSha256");
            string manifestHash = RequiredString(item, "manifestSha256");
            if (!item.TryGetProperty("runtimeIdentifiers", out JsonElement rids) || rids.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("P1 expanded suite runtimeIdentifiers are required.", nameof(json));
            var runtimeIdentifiers = rids.EnumerateArray().Select(static value =>
                value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Runtime identifiers must be strings.")).ToArray();
            if (!item.TryGetProperty("cases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("P1 expanded suite requires a cases array.", nameof(json));
            if (cases.GetArrayLength() > MaximumCasesPerSuite)
                throw new ArgumentException("P1 expanded suite exceeds its case bound.", nameof(json));
            var parsedCases = new List<CaseSpec>(cases.GetArrayLength());
            foreach (JsonElement fixture in cases.EnumerateArray())
            {
                if (fixture.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("P1 expanded case must be an object.", nameof(json));
                parsedCases.Add(new(
                    RequiredString(fixture, "id"),
                    RequiredString(fixture, "source"),
                    RequiredString(fixture, "sourceSha256"),
                    RequiredString(fixture, "expectationSha256")));
            }
            parsed.Add(new(profile, suiteVersion, denominator, backend, runtimeIdentifiers, oracle, compilerHash, manifestHash, parsedCases));
        }
        var manifest = new ExpandedManifest(version, parsed);
        ValidateManifest(manifest, repositoryRoot);
        return manifest;
    }

    internal static void ValidateManifest(ExpandedManifest manifest, string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Version != ManifestVersion || manifest.Suites.Count != MaximumSuites)
            throw new ArgumentException("P1 expanded manifest version or suite count is invalid.", nameof(manifest));
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (SuiteSpec suite in manifest.Suites)
        {
            if (!profiles.Add(suite.Profile) || suite.Version != (suite.Profile == DifferentialProfile ? 4 : suite.Profile == PlatformProfile ? 2 : 0))
                throw new ArgumentException("P1 expanded suite profile/version is invalid or duplicated.", nameof(manifest));
            bool differential = suite.Profile == DifferentialProfile;
            int expectedDenominator = differential ? DifferentialDenominator : PlatformDenominator;
            string[] expectedIds = differential ? DifferentialIds : PlatformIds;
            if (suite.Denominator != expectedDenominator || suite.Cases.Count != expectedDenominator ||
                !suite.Cases.Select(static item => item.Id).SequenceEqual(expectedIds, StringComparer.Ordinal))
                throw new ArgumentException($"{suite.Profile} denominator or case ID sequence is not frozen.", nameof(manifest));
            if (suite.RuntimeIdentifiers.Count is < 1 or > 2 || suite.RuntimeIdentifiers.Any(static id => id is not ("win-x64" or "linux-x64")) ||
                suite.RuntimeIdentifiers.Distinct(StringComparer.Ordinal).Count() != suite.RuntimeIdentifiers.Count)
                throw new ArgumentException($"{suite.Profile} runtime identifiers are invalid.", nameof(manifest));
            if (string.IsNullOrWhiteSpace(suite.Backend) || string.IsNullOrWhiteSpace(suite.Oracle) || !suite.Oracle.StartsWith("rustc 1.98.0", StringComparison.Ordinal) ||
                !IsSha256(suite.CompilerSha256) || !IsSha256(suite.ManifestSha256))
                throw new ArgumentException($"{suite.Profile} backend, oracle or hash contract is invalid.", nameof(manifest));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var sourceHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectationHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CaseSpec fixture in suite.Cases)
            {
                if (fixture.Id.Length is 0 or > MaximumIdLength || !ids.Add(fixture.Id) ||
                    fixture.Id.Any(static c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                    string.IsNullOrWhiteSpace(fixture.Source) || Path.GetFileName(fixture.Source) != fixture.Source ||
                    !IsSha256(fixture.SourceSha256) || !sourceHashes.Add(fixture.SourceSha256) ||
                    !IsSha256(fixture.ExpectationSha256) || !expectationHashes.Add(fixture.ExpectationSha256))
                    throw new ArgumentException($"{suite.Profile} contains an invalid, duplicate or unhashed case.", nameof(manifest));
                if (repositoryRoot is not null)
                {
                    string fixtureDirectory = Path.Combine(Path.GetFullPath(repositoryRoot), "tools", "RustSharp.Conformance", "fixtures");
                    string sourcePath = Path.Combine(fixtureDirectory, fixture.Source);
                    if (!File.Exists(sourcePath))
                        throw new ArgumentException($"{suite.Profile} fixture source is missing: {fixture.Source}", nameof(manifest));
                    FileInfo sourceInfo = new(sourcePath);
                    if (sourceInfo.Length is < 1 or > 1_048_576)
                        throw new ArgumentException($"{suite.Profile} fixture source exceeds its byte bound: {fixture.Source}", nameof(manifest));
                    string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
                    if (!sourceHash.Equals(fixture.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"{suite.Profile} fixture source hash is stale: {fixture.Source}", nameof(manifest));
                }
            }
        }
        if (!profiles.SetEquals([DifferentialProfile, PlatformProfile]))
            throw new ArgumentException("P1 expanded manifest must contain exactly differential-v3 and platform-v2.", nameof(manifest));
    }

    internal static ValidationResult ValidateReport(string json, SuiteSpec suite)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(suite);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length is < 1 or > MaximumReportBytes)
            return ValidationResult.Fail(["Expanded report exceeds its byte bound."]);
        var errors = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            JsonElement root = document.RootElement;
            RequireString(root, "profile", suite.Profile, errors);
            RequireSha256(root, "compilerSha256", "report", errors);
            if (root.TryGetProperty("declaredCompilerSha256", out JsonElement declaredCompiler))
                RequireHash(root, "declaredCompilerSha256", suite.CompilerSha256, "report", errors);
            if (root.TryGetProperty("compiler", out JsonElement compiler) && compiler.ValueKind == JsonValueKind.Object && compiler.TryGetProperty("declaredSha256", out _))
                RequireHash(compiler, "declaredSha256", suite.CompilerSha256, "compiler", errors);
            RequireString(root, "backend", suite.Backend, errors);
            if (!root.TryGetProperty("toolVersions", out JsonElement tools) || tools.ValueKind != JsonValueKind.Object)
                errors.Add("Report toolVersions provenance is missing.");
            else
            {
                RequireNonEmpty(tools, "dotnet", "toolVersions", errors);
                RequireNonEmpty(tools, "sdkVersion", "toolVersions", errors);
                if (OptionalString(tools, "rustc") is not { } rustc || !rustc.StartsWith("rustc 1.98.0", StringComparison.Ordinal))
                    errors.Add("toolVersions.rustc must identify stable rustc 1.98.0.");
            }
            if (!root.TryGetProperty("platform", out JsonElement platform) || platform.ValueKind != JsonValueKind.Object)
                errors.Add("Report platform provenance is missing.");
            else
            {
                string? rid = OptionalString(platform, "runtimeIdentifier");
                if (rid is null || !suite.RuntimeIdentifiers.Contains(rid, StringComparer.Ordinal)) errors.Add("Report runtimeIdentifier is not in the frozen RID set.");
                RequireString(platform, "backend", suite.Backend, errors, "platform");
                RequireString(platform, "oracle", suite.Oracle, errors, "platform");
            }
            if (!root.TryGetProperty("manifest", out JsonElement manifest) || manifest.ValueKind != JsonValueKind.Object)
                errors.Add("Report manifest provenance is missing.");
            else
            {
                RequireSha256(manifest, "sha256", "manifest", errors);
                if (manifest.TryGetProperty("declaredSha256", out _))
                    RequireHash(manifest, "declaredSha256", suite.ManifestSha256, "manifest", errors);
                RequireInt(manifest, "version", ManifestVersion, "manifest", errors);
                RequireInt(manifest, "denominator", suite.Denominator, "manifest", errors);
                RequireBoolean(manifest, "validated", true, "manifest", errors);
            }
            if (!root.TryGetProperty("oracle", out JsonElement oracle) || oracle.ValueKind != JsonValueKind.Object)
                errors.Add("Report oracle provenance is missing.");
            else RequireString(oracle, "version", suite.Oracle, errors, "oracle");
            ValidateSemanticClosure(root, suite, errors);
            ValidateSummaryAndCases(root, suite, errors);
            if (!root.TryGetProperty("execution", out JsonElement execution) || execution.ValueKind != JsonValueKind.Object)
                errors.Add("Report execution provenance is missing.");
            if (!root.TryGetProperty("cleanup", out JsonElement cleanup) || cleanup.ValueKind != JsonValueKind.Object)
                errors.Add("Report cleanup provenance is missing.");
            else if (!cleanup.TryGetProperty("completed", out JsonElement completed) || completed.ValueKind != JsonValueKind.True)
                errors.Add("Report cleanup must be complete.");
        }
        catch (JsonException exception) { errors.Add("Expanded report is not valid JSON: " + exception.Message); }
        return errors.Count == 0 ? ValidationResult.Pass() : ValidationResult.Fail(errors);
    }

    private static void ValidateSemanticClosure(JsonElement root, SuiteSpec suite, List<string> errors)
    {
        bool expectedEligible = suite.Cases.All(fixture => ExpectedSemanticEligibility(suite, fixture.Id));
        if (!root.TryGetProperty("semanticClosureEligible", out JsonElement eligible) ||
            eligible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add("Report semanticClosureEligible is missing or not a boolean.");
        }
        else if (eligible.GetBoolean() != expectedEligible)
        {
            string reason = expectedEligible
                ? "for the frozen suite"
                : "because the frozen suite still contains placeholder semantic cases";
            errors.Add($"Report semanticClosureEligible must be {expectedEligible.ToString().ToLowerInvariant()} {reason}.");
        }

        if (root.TryGetProperty("semanticClosure", out JsonElement closure))
        {
            if (closure.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Report semanticClosure must be an object when present.");
            }
            else
            {
                RequireBoolean(closure, "eligible", expectedEligible, "semanticClosure", errors);
                RequireInt(closure, "eligibleCases", suite.Cases.Count(fixture => ExpectedSemanticEligibility(suite, fixture.Id)), "semanticClosure", errors);
                RequireInt(closure, "placeholderCases", suite.Cases.Count(fixture => !ExpectedSemanticEligibility(suite, fixture.Id)), "semanticClosure", errors);
            }
        }

        if (root.TryGetProperty("coverageLimitations", out JsonElement limitations))
        {
            if (limitations.ValueKind != JsonValueKind.Array)
            {
                errors.Add("Report coverageLimitations must be an array when present.");
            }
            else
            {
                string[] expected = suite.Cases.Where(fixture => !ExpectedSemanticEligibility(suite, fixture.Id)).Select(static fixture => fixture.Id).ToArray();
                string[] actual = limitations.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String).Select(static value => value.GetString()!).ToArray();
                if (actual.Length != limitations.GetArrayLength() || !actual.SequenceEqual(expected, StringComparer.Ordinal))
                    errors.Add("Report coverageLimitations does not match the frozen semantic placeholder IDs.");
            }
        }

        // The expanded differential suite also carries Drop cases.  Keep a
        // separate borrow-only closure gate so the executable P1-07 borrow
        // corpus can be evaluated while the still-open Drop placeholders
        // remain visible in the suite-level gate above.
        if (suite.Profile == DifferentialProfile)
        {
            CaseSpec[] borrowCases = suite.Cases.Where(static fixture =>
                fixture.Id.StartsWith("borrow-", StringComparison.Ordinal)).ToArray();
            bool borrowExpected = borrowCases.Length > 0 && borrowCases.All(fixture =>
                ExpectedSemanticEligibility(suite, fixture.Id));
            if (!root.TryGetProperty("borrowSemanticClosureEligible", out JsonElement borrowEligible) ||
                borrowEligible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add("Report borrowSemanticClosureEligible is missing or not a boolean.");
            }
            else if (borrowEligible.GetBoolean() != borrowExpected)
            {
                errors.Add($"Report borrowSemanticClosureEligible must be {borrowExpected.ToString().ToLowerInvariant()} for the frozen borrow subset.");
            }

            if (!root.TryGetProperty("borrowSemanticClosure", out JsonElement borrowClosure) ||
                borrowClosure.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Report borrowSemanticClosure is missing or not an object.");
            }
            else
            {
                RequireBoolean(borrowClosure, "eligible", borrowExpected, "borrowSemanticClosure", errors);
                RequireInt(borrowClosure, "eligibleCases", borrowCases.Count(fixture =>
                    ExpectedSemanticEligibility(suite, fixture.Id)), "borrowSemanticClosure", errors);
                RequireInt(borrowClosure, "placeholderCases", borrowCases.Count(fixture =>
                    !ExpectedSemanticEligibility(suite, fixture.Id)), "borrowSemanticClosure", errors);
            }
        }
    }

    private static void ValidateSummaryAndCases(JsonElement root, SuiteSpec suite, List<string> errors)
    {
        if (!root.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Report summary is missing.");
            return;
        }
        RequireString(summary, "status", "passed", errors, "summary");
        RequireInt(summary, "denominator", suite.Denominator, "summary", errors);
        RequireInt(summary, "executed", suite.Denominator, "summary", errors);
        RequireInt(summary, "passed", suite.Denominator, "summary", errors);
        RequireInt(summary, "failed", 0, "summary", errors);
        RequireInt(summary, "blocked", 0, "summary", errors);
        RequireInt(summary, "skipped", 0, "summary", errors);
        if (!root.TryGetProperty("cases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() != suite.Denominator)
        {
            errors.Add("Report cases do not equal the frozen denominator.");
            return;
        }
        var expected = suite.Cases.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in cases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { errors.Add("Report case must be an object."); continue; }
            string? id = OptionalString(item, "id");
            if (id is null || !seen.Add(id) || !expected.TryGetValue(id, out CaseSpec? fixture))
            {
                errors.Add("Report case IDs must be unique and belong to the frozen suite.");
                continue;
            }
            RequireString(item, "status", "passed", errors, "case");
            RequireHash(item, "sourceSha256", fixture.SourceSha256, "case", errors);
            RequireHash(item, "expectationSha256", fixture.ExpectationSha256, "case", errors);
            bool expectedEligible = ExpectedSemanticEligibility(suite, fixture.Id);
            if (!item.TryGetProperty("semanticClosureEligible", out JsonElement eligible) ||
                eligible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add($"Case '{fixture.Id}' semanticClosureEligible is missing or not a boolean.");
            else if (eligible.GetBoolean() != expectedEligible)
            {
                string reason = expectedEligible ? "" : "; placeholder semantic coverage cannot claim closure";
                errors.Add($"Case '{fixture.Id}' semanticClosureEligible must be {expectedEligible.ToString().ToLowerInvariant()}{reason}.");
            }
            string expectedCoverage = expectedEligible ? "ownership-drop-scenario" : "placeholder";
            RequireString(item, "semanticCoverage", expectedCoverage, errors, $"case '{fixture.Id}'");
            if (suite.Profile == PlatformProfile)
            {
                RequireObject(item, "coreClrCompile", $"case '{fixture.Id}'", errors);
                RequireObject(item, "coreClrRun", $"case '{fixture.Id}'", errors);
                RequireObject(item, "ilVerify", $"case '{fixture.Id}'", errors);
                RequireObject(item, "nativeAot", $"case '{fixture.Id}'", errors);
            }
        }
        if (seen.Count != suite.Denominator) errors.Add("Report case IDs are incomplete.");
    }

    private static bool ExpectedSemanticEligibility(SuiteSpec suite, string id) => suite.Profile switch
    {
        DifferentialProfile => !P1ExpandedDifferentialRunner.IsPlaceholderCase(id),
        PlatformProfile => P1ExpandedPlatformRunner.IsSemanticClosureEligible(id),
        _ => false,
    };

    private static void RequireObject(JsonElement parent, string name, string scope, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            errors.Add($"{scope}.{name} evidence is missing.");
        else if (!value.EnumerateObject().Any())
            errors.Add($"{scope}.{name} evidence is empty.");
    }

    private static void RequireHash(JsonElement parent, string name, string expected, string scope, List<string> errors)
    {
        string? actual = OptionalString(parent, name);
        if (!IsSha256(actual)) errors.Add($"{scope}.{name} must be a 64-character SHA-256 hash.");
        else if (!actual!.Equals(expected, StringComparison.OrdinalIgnoreCase)) errors.Add($"{scope}.{name} does not match the frozen hash.");
    }

    private static void RequireSha256(JsonElement parent, string name, string scope, List<string> errors)
    {
        if (!IsSha256(OptionalString(parent, name)))
            errors.Add($"{scope}.{name} must be a 64-character SHA-256 hash.");
    }

    private static void RequireString(JsonElement parent, string name, string expected, List<string> errors, string scope = "report")
    {
        if (OptionalString(parent, name) is not string actual || !actual.Equals(expected, StringComparison.Ordinal)) errors.Add($"{scope}.{name} does not match the frozen value.");
    }

    private static void RequireInt(JsonElement parent, string name, int expected, string scope, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int actual) || actual != expected) errors.Add($"{scope}.{name} must equal {expected}.");
    }

    private static void RequireBoolean(JsonElement parent, string name, bool expected, string scope, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != expected) errors.Add($"{scope}.{name} must be {expected.ToString().ToLowerInvariant()}.");
    }

    private static void RequireNonEmpty(JsonElement parent, string name, string scope, List<string> errors)
    {
        if (OptionalString(parent, name) is not { Length: > 0 }) errors.Add($"{scope}.{name} is missing or empty.");
    }

    private static string RequiredString(JsonElement parent, string name) =>
        OptionalString(parent, name) is { Length: > 0 } value ? value : throw new ArgumentException($"Manifest field '{name}' is required.");

    private static int RequiredInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : throw new ArgumentException($"Manifest integer '{name}' is required.");

    private static string? OptionalString(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
        int tokens = 0;
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (++tokens > MaximumJsonTokens) throw new ArgumentException("P1 expanded manifest token bound exceeded.");
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) { if (objects.Count == 0) throw new ArgumentException("Malformed JSON object."); objects.Pop(); }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (objects.Count == 0 || !objects.Peek().Add(reader.GetString()!)) throw new ArgumentException("P1 expanded manifest contains duplicate properties.");
            }
        }
        if (objects.Count != 0) throw new ArgumentException("P1 expanded manifest object is incomplete.");
    }
}
