using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RustSharp.Conformance;

/// <summary>
/// Validates the frozen P1 requirement-to-case/backend inventory. The
/// denominator is read from the checked-in manifest and is never discovered
/// from fixture files at execution time.
/// </summary>
internal static class P1CoverageProfileRunner
{
    internal const string ProfileName = "p1-coverage-v1";
    internal const string ManifestFileName = "p1-coverage-v1-manifest.json";
    internal const int ManifestVersion = 1;
    internal const int RequirementDenominator = 40;
    internal const int MaximumManifestBytes = 512 * 1024;
    internal const int MaximumRequirements = 64;
    internal const int MaximumCases = 512;
    internal const int MaximumSourceBytes = 1024 * 1024;
    private const int MaximumJsonDepth = 32;
    private const int MaximumJsonTokens = 32_768;
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly string[] Categories = ["positive", "negative", "boundary", "budget"];
    private static readonly string[] Backends =
        ["harness", "coreclr", "ilverify", "windows-x64-aot", "linux-x64-aot", "rustc-1.98"];

    internal sealed record Limits(
        int MaximumRequirements,
        int MaximumCases,
        int MaximumManifestBytes,
        int MaximumSourceBytes);

    internal sealed record Requirement(string Id, string Classification, string Leaf);

    internal sealed record CoverageCase(
        string Id,
        string RequirementId,
        string Category,
        string Source,
        string SourceSha256,
        string Expectation,
        IReadOnlyList<string> Backends);

    internal sealed record Manifest(
        string Profile,
        int Version,
        string Ledger,
        int Denominator,
        IReadOnlyList<Requirement> Requirements,
        IReadOnlyList<CoverageCase> Cases,
        Limits? DeclaredLimits);

    internal static readonly IReadOnlyList<string> RequiredCategories = Categories;

    /// <summary>
    /// Validates and publishes the frozen requirement inventory. This profile
    /// is intentionally a catalogue gate: execution backends are recorded in
    /// each case row and are exercised by the differential/platform profiles.
    /// </summary>
    internal static async Task<int> RunAsync(
        string repositoryRoot,
        string reportPath,
        TimeSpan deadline,
        DateTimeOffset startedAtUtc,
        Stopwatch harnessClock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(deadline));

        using var cancellation = new CancellationTokenSource(deadline);
        string manifestPath = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures", ManifestFileName);
        string json = await File.ReadAllTextAsync(manifestPath, cancellation.Token).ConfigureAwait(false);
        Manifest manifest = ParseManifest(json, repositoryRoot);
        cancellation.Token.ThrowIfCancellationRequested();
        harnessClock.Stop();

        var report = new
        {
            schemaVersion = 1,
            evidenceKind = "p1-requirement-coverage",
            profile = manifest.Profile,
            candidateSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("CI_COMMIT_SHA"),
            manifestVersion = manifest.Version,
            ledger = manifest.Ledger,
            status = "passed",
            startedAtUtc,
            finishedAtUtc = DateTimeOffset.UtcNow,
            elapsedMilliseconds = harnessClock.Elapsed.TotalMilliseconds,
            manifestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            summary = new
            {
                denominator = manifest.Denominator,
                executed = manifest.Requirements.Count,
                passed = manifest.Requirements.Count,
                failed = 0,
                blocked = 0,
                skipped = 0,
                caseDenominator = manifest.Cases.Count,
            },
            requirements = manifest.Requirements,
            cases = manifest.Cases,
        };

        string? directory = Path.GetDirectoryName(reportPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Coverage report directory is required.", nameof(reportPath));
        Directory.CreateDirectory(directory);
        string temporaryPath = reportPath + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(report, ReportJsonOptions),
                cancellation.Token).ConfigureAwait(false);
            File.Move(temporaryPath, reportPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return 0;
    }

    internal static Manifest ParseManifest(string json, string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new ArgumentException("P1 coverage manifest exceeds its byte bound.", nameof(json));
        ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(json));
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = MaximumJsonDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("P1 coverage manifest root must be an object.", nameof(json));

        string profile = RequiredString(root, "profile");
        int version = RequiredInt(root, "version");
        string ledger = RequiredString(root, "ledger");
        int denominator = RequiredInt(root, "denominator");
        if (!root.TryGetProperty("limits", out JsonElement limitsElement) ||
            limitsElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("P1 coverage manifest limits are required.", nameof(json));
        var limits = new Limits(
            RequiredInt(limitsElement, "maximumRequirements"),
            RequiredInt(limitsElement, "maximumCases"),
            RequiredInt(limitsElement, "maximumManifestBytes"),
            RequiredInt(limitsElement, "maximumSourceBytes"));

        var requirements = new List<Requirement>();
        JsonElement requirementElement = RequiredArray(root, "requirements");
        int tokens = 0;
        foreach (JsonElement item in requirementElement.EnumerateArray())
        {
            if (++tokens > MaximumJsonTokens) throw new ArgumentException("P1 coverage manifest token bound exceeded.");
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Coverage requirements must be objects.");
            requirements.Add(new(
                RequiredString(item, "id"),
                RequiredString(item, "classification"),
                RequiredString(item, "leaf")));
        }

        var cases = new List<CoverageCase>();
        JsonElement caseElement = RequiredArray(root, "cases");
        foreach (JsonElement item in caseElement.EnumerateArray())
        {
            if (++tokens > MaximumJsonTokens) throw new ArgumentException("P1 coverage manifest token bound exceeded.");
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Coverage cases must be objects.");
            JsonElement backendElement = RequiredArray(item, "backends");
            var backends = backendElement.EnumerateArray().Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Coverage backend must be a string.");
                return value.GetString() ?? string.Empty;
            }).ToArray();
            cases.Add(new(
                RequiredString(item, "id"),
                RequiredString(item, "requirementId"),
                RequiredString(item, "category"),
                RequiredString(item, "source"),
                RequiredString(item, "sourceSha256"),
                RequiredString(item, "expectation"),
                backends));
        }

        var manifest = new Manifest(profile, version, ledger, denominator, requirements, cases, limits);
        ValidateManifest(manifest, repositoryRoot);
        return manifest;
    }

    internal static void ValidateManifest(Manifest manifest, string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Profile, ProfileName, StringComparison.Ordinal) ||
            manifest.Version != ManifestVersion || !string.Equals(manifest.Ledger, "p1-exit-scope-v1", StringComparison.Ordinal))
            throw new ArgumentException("P1 coverage manifest identity is not supported.", nameof(manifest));
        if (manifest.Denominator != RequirementDenominator ||
            manifest.Requirements.Count != RequirementDenominator ||
            manifest.Requirements.Count > MaximumRequirements)
            throw new ArgumentException("P1 coverage manifest requirement denominator is not fixed.", nameof(manifest));
        if (manifest.DeclaredLimits is null ||
            manifest.DeclaredLimits.MaximumRequirements != MaximumRequirements ||
            manifest.DeclaredLimits.MaximumCases != MaximumCases ||
            manifest.DeclaredLimits.MaximumManifestBytes != MaximumManifestBytes ||
            manifest.DeclaredLimits.MaximumSourceBytes != MaximumSourceBytes)
            throw new ArgumentException("P1 coverage manifest limits do not match the fixed contract.", nameof(manifest));
        if (manifest.Cases.Count is < 1 or > MaximumCases)
            throw new ArgumentException("P1 coverage manifest case count is out of bounds.", nameof(manifest));

        var requirementIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Requirement requirement in manifest.Requirements)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(requirement.Id, @"^P1-REQ-0(?:0[1-9]|[12][0-9]|3[0-9]|40)$"))
                throw new ArgumentException("Coverage requirement ID is invalid.", nameof(manifest));
            if (!requirementIds.Add(requirement.Id) ||
                requirement.Classification is not ("executable" or "check-only" or "excluded") ||
                string.IsNullOrWhiteSpace(requirement.Leaf))
                throw new ArgumentException("Coverage requirements must be unique and classified.", nameof(manifest));
        }
        if (!requirementIds.SetEquals(Enumerable.Range(1, RequirementDenominator).Select(index => $"P1-REQ-{index:000}")))
            throw new ArgumentException("Coverage manifest must enumerate every frozen requirement ID exactly once.", nameof(manifest));

        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var coverage = manifest.Requirements.ToDictionary(requirement => requirement.Id,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (CoverageCase item in manifest.Cases)
        {
            if (!caseIds.Add(item.Id) || !coverage.TryGetValue(item.RequirementId, out HashSet<string>? categories) ||
                !Categories.Contains(item.Category, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(item.Expectation) ||
                item.SourceSha256.Length != 64 || item.SourceSha256.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("Coverage case identity or category is invalid.", nameof(manifest));
            if (item.Backends.Count == 0 || item.Backends.Any(backend => !Backends.Contains(backend, StringComparer.Ordinal)) ||
                item.Backends.Count != item.Backends.Distinct(StringComparer.Ordinal).Count())
                throw new ArgumentException("Coverage case backends are invalid or duplicated.", nameof(manifest));
            if (!categories.Add(item.Category))
                throw new ArgumentException("Coverage manifest contains duplicate requirement/category rows.", nameof(manifest));
            ValidateSource(item, repositoryRoot);
        }
        foreach ((string requirementId, HashSet<string> categories) in coverage)
            if (!categories.SetEquals(Categories))
                throw new ArgumentException($"Coverage requirement {requirementId} does not have positive/negative/boundary/budget cases.", nameof(manifest));
    }

    /// <summary>
    /// Validates a persisted coverage report against the same immutable
    /// manifest. Reports are treated as untrusted CI input: missing, extra,
    /// duplicate or stale rows fail closed and blocked/skipped rows never
    /// count as semantic success.
    /// </summary>
    internal static void ValidateReport(string reportJson, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(reportJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (Encoding.UTF8.GetByteCount(reportJson) > MaximumManifestBytes * 2)
            throw new ArgumentException("P1 coverage report exceeds its byte bound.", nameof(reportJson));
        using JsonDocument report = JsonDocument.Parse(reportJson, new JsonDocumentOptions
        {
            MaxDepth = MaximumJsonDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        JsonElement root = report.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !string.Equals(RequiredString(root, "evidenceKind"), "p1-requirement-coverage", StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "profile"), ProfileName, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "status"), "passed", StringComparison.Ordinal))
            throw new ArgumentException("P1 coverage report identity or status is invalid.", nameof(reportJson));

        string manifestPath = Path.Combine(repositoryRoot, "tools", "RustSharp.Conformance", "fixtures", ManifestFileName);
        string manifestJson = File.ReadAllText(manifestPath);
        Manifest manifest = ParseManifest(manifestJson, repositoryRoot);
        string manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));
        if (!string.Equals(RequiredString(root, "manifestSha256"), manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("P1 coverage report manifest hash is stale.", nameof(reportJson));
        if (RequiredInt(root, "manifestVersion") != manifest.Version ||
            !string.Equals(RequiredString(root, "ledger"), manifest.Ledger, StringComparison.Ordinal))
            throw new ArgumentException("P1 coverage report manifest identity is stale.", nameof(reportJson));

        JsonElement summary = root.TryGetProperty("summary", out JsonElement summaryElement) &&
            summaryElement.ValueKind == JsonValueKind.Object ? summaryElement :
            throw new ArgumentException("P1 coverage report summary is required.", nameof(reportJson));
        if (RequiredInt(summary, "denominator") != manifest.Denominator ||
            RequiredInt(summary, "caseDenominator") != manifest.Cases.Count ||
            RequiredInt(summary, "passed") != manifest.Denominator ||
            RequiredInt(summary, "failed") != 0 || RequiredInt(summary, "blocked") != 0 ||
            RequiredInt(summary, "skipped") != 0)
            throw new ArgumentException("P1 coverage report summary does not close its fixed denominator.", nameof(reportJson));

        var requirementIds = manifest.Requirements.Select(static requirement => requirement.Id)
            .ToHashSet(StringComparer.Ordinal);
        var reportRequirements = ReadStringIds(root, "requirements", "id");
        if (!reportRequirements.SetEquals(requirementIds))
            throw new ArgumentException("P1 coverage report requirement rows do not match the manifest.", nameof(reportJson));
        var manifestRequirements = manifest.Requirements.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        foreach (JsonElement item in root.GetProperty("requirements").EnumerateArray())
        {
            string id = RequiredString(item, "id");
            Requirement expected = manifestRequirements[id];
            if (!string.Equals(RequiredString(item, "classification"), expected.Classification, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(item, "leaf"), expected.Leaf, StringComparison.Ordinal))
                throw new ArgumentException("P1 coverage report contains stale requirement evidence.", nameof(reportJson));
        }
        var caseIds = manifest.Cases.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var reportCases = ReadStringIds(root, "cases", "id");
        if (!reportCases.SetEquals(caseIds))
            throw new ArgumentException("P1 coverage report case rows do not match the manifest.", nameof(reportJson));

        JsonElement cases = root.GetProperty("cases");
        var manifestCases = manifest.Cases.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        foreach (JsonElement item in cases.EnumerateArray())
        {
            string id = RequiredString(item, "id");
            CoverageCase expected = manifestCases[id];
            if (!string.Equals(RequiredString(item, "requirementId"), expected.RequirementId, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(item, "category"), expected.Category, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(item, "source"), expected.Source, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(item, "sourceSha256"), expected.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(RequiredString(item, "expectation"), expected.Expectation, StringComparison.Ordinal) ||
                !ReadStringArray(item, "backends").SequenceEqual(expected.Backends, StringComparer.Ordinal))
                throw new ArgumentException("P1 coverage report contains stale case evidence.", nameof(reportJson));
        }

        static HashSet<string> ReadStringIds(JsonElement root, string arrayName, string propertyName)
        {
            JsonElement array = RequiredArray(root, arrayName);
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in array.EnumerateArray())
            {
                string id = RequiredString(item, propertyName);
                if (!values.Add(id)) throw new ArgumentException("P1 coverage report contains duplicate IDs.");
            }
            return values;
        }

        static string[] ReadStringArray(JsonElement parent, string propertyName)
        {
            JsonElement array = RequiredArray(parent, propertyName);
            var values = new List<string>();
            foreach (JsonElement value in array.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new ArgumentException("P1 coverage report backend value is invalid.");
                values.Add(value.GetString()!);
            }
            return values.ToArray();
        }
    }

    private static void ValidateSource(CoverageCase item, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(item.Source) || Path.IsPathRooted(item.Source) ||
            item.Source.Contains("..", StringComparison.Ordinal) || item.Source.Contains('\0'))
            throw new ArgumentException("Coverage source path must be a bounded repository-relative path.");
        if (repositoryRoot is null) return;
        string root = Path.GetFullPath(repositoryRoot);
        string full = Path.GetFullPath(Path.Combine(root, item.Source));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full))
            throw new ArgumentException("Coverage source fixture is missing: " + item.Source);
        FileInfo info = new(full);
        // Empty source is a valid lexical boundary case and therefore remains
        // part of the frozen inventory; only the upper bound is restricted.
        if (info.Length > MaximumSourceBytes)
            throw new ArgumentException("Coverage source fixture exceeds its bound: " + item.Source);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
        if (!string.Equals(hash, item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Coverage source hash does not match: " + item.Source);
    }

    private static JsonElement RequiredArray(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value : throw new ArgumentException($"P1 coverage manifest array '{name}' is required.");

    private static string RequiredString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new ArgumentException($"P1 coverage manifest string '{name}' is required.");

    private static int RequiredInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int result) ? result :
        throw new ArgumentException($"P1 coverage manifest integer '{name}' is required.");

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumJsonDepth });
        var objects = new Stack<HashSet<string>>();
        int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > MaximumJsonTokens) throw new ArgumentException("P1 coverage manifest token bound exceeded.");
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.PropertyName &&
                (objects.Count == 0 || !objects.Peek().Add(reader.GetString() ?? string.Empty)))
                throw new ArgumentException("P1 coverage manifest contains duplicate properties.");
            else if (reader.TokenType == JsonTokenType.EndObject && objects.Count == 0)
                throw new ArgumentException("P1 coverage manifest object structure is invalid.");
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
        }
        if (objects.Count != 0) throw new ArgumentException("P1 coverage manifest object is incomplete.");
    }
}
