using System.Security.Cryptography;
using System.Text.Json;

namespace RustSharp.Conformance;

/// <summary>
/// Strict validator for P1 platform/differential evidence provenance.
/// A report is valid only when every required provenance and execution field
/// is present and the fixed denominator is closed with passed cases.
/// </summary>
internal static class P1EvidenceBindingValidator
{
    internal const int MaximumReportBytes = 16 * 1024 * 1024;
    internal const int MaximumCaseCount = 256;

    internal sealed record BindingExpectation(
        string EvidenceKind,
        string Profile,
        string RuntimeIdentifier,
        int Denominator,
        string ManifestSha256,
        string CompilerSha256,
        string OracleVersionPrefix = "rustc 1.98.0 (",
        string? CandidateSha = null);

    internal sealed record ValidationResult(
        bool Valid,
        string Status,
        IReadOnlyList<string> Errors)
    {
        internal static ValidationResult Pass() => new(true, "passed", []);
        internal static ValidationResult Fail(IEnumerable<string> errors) =>
            new(false, "failed", errors.ToArray());
    }

    internal static ValidationResult ValidateFile(string path, BindingExpectation expectation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo info = new(path);
        if (!info.Exists)
            return ValidationResult.Fail(["Evidence report does not exist."]);
        if (info.Length is < 1 or > MaximumReportBytes)
            return ValidationResult.Fail(["Evidence report exceeds its byte bound."]);
        try
        {
            return Validate(File.ReadAllBytes(path), expectation);
        }
        catch (IOException exception)
        {
            return ValidationResult.Fail(["Evidence report could not be read: " + exception.Message]);
        }
    }

    internal static ValidationResult Validate(ReadOnlySpan<byte> bytes, BindingExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        if (bytes.Length is < 1 or > MaximumReportBytes)
            return ValidationResult.Fail(["Evidence report exceeds its byte bound."]);

        var errors = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            JsonElement root = document.RootElement;
            RequireString(root, "evidenceKind", expectation.EvidenceKind, errors);
            RequireString(root, "profile", expectation.Profile, errors);
            ValidateManifest(root, expectation, errors);
            ValidateCompiler(root, expectation, errors);
            ValidateCandidate(root, expectation, errors);
            ValidatePlatform(root, expectation, errors);
            ValidateToolVersions(root, expectation, errors);
            ValidateSummaryAndCases(root, expectation, errors);
            ValidateExecutionAndCleanup(root, errors);
        }
        catch (JsonException exception)
        {
            errors.Add("Evidence report is not valid JSON: " + exception.Message);
        }

        return errors.Count == 0 ? ValidationResult.Pass() : ValidationResult.Fail(errors);
    }

    internal static ValidationResult Validate(string json, BindingExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Validate(System.Text.Encoding.UTF8.GetBytes(json), expectation);
    }

    private static void ValidateManifest(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("manifest", out JsonElement manifest) || manifest.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Manifest provenance is missing.");
            return;
        }
        RequireHash(manifest, "sha256", expected.ManifestSha256, "manifest", errors);
        RequirePositiveInt(manifest, "denominator", expected.Denominator, "manifest", errors);
        RequireBoolean(manifest, "validated", true, "manifest", errors);
    }

    private static void ValidateCompiler(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("compiler", out JsonElement compiler) || compiler.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Compiler provenance is missing.");
            return;
        }
        RequireHash(compiler, "sha256", expected.CompilerSha256, "compiler", errors);
        RequireNonEmpty(compiler, "profile", "compiler", errors);
    }

    private static void ValidateCandidate(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(expected.CandidateSha)) return;
        if (!IsCandidateSha(expected.CandidateSha))
        {
            errors.Add("The expected candidate SHA must be a 40-64 character hexadecimal commit identifier.");
            return;
        }
        string? actual = OptionalString(root, "candidateSha");
        if (!IsCandidateSha(actual)) errors.Add("candidateSha must be a 40-64 character hexadecimal commit identifier.");
        else if (!actual!.Equals(expected.CandidateSha, StringComparison.OrdinalIgnoreCase)) errors.Add("candidateSha does not match the expected candidate hash.");
    }

    private static void ValidatePlatform(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("platform", out JsonElement platform) || platform.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Platform provenance is missing.");
            return;
        }
        RequireString(platform, "runtimeIdentifier", expected.RuntimeIdentifier, errors, "platform");
        RequireNonEmpty(platform, "name", "platform", errors);
    }

    private static void ValidateToolVersions(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("toolVersions", out JsonElement versions) || versions.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Tool version provenance is missing.");
            return;
        }
        string? rustc = OptionalString(versions, "rustc");
        if (rustc is null || !rustc.StartsWith(expected.OracleVersionPrefix, StringComparison.Ordinal))
            errors.Add("Tool version rustc does not identify the required stable 1.98.0 oracle.");
        RequireNonEmpty(versions, "dotnet", "toolVersions", errors);
        RequireNonEmpty(versions, "sdkVersion", "toolVersions", errors);
    }

    private static void ValidateSummaryAndCases(JsonElement root, BindingExpectation expected, List<string> errors)
    {
        if (!root.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Summary is missing.");
            return;
        }
        RequireString(summary, "status", "passed", errors, "summary");
        RequirePositiveInt(summary, "denominator", expected.Denominator, "summary", errors);
        RequirePositiveInt(summary, "executed", expected.Denominator, "summary", errors);
        RequirePositiveInt(summary, "passed", expected.Denominator, "summary", errors);
        RequirePositiveInt(summary, "failed", 0, "summary", errors, allowZero: true);
        RequirePositiveInt(summary, "blocked", 0, "summary", errors, allowZero: true);
        RequirePositiveInt(summary, "skipped", 0, "summary", errors, allowZero: true);
        if (summary.TryGetProperty("exitCode", out JsonElement exitCode) &&
            (exitCode.ValueKind != JsonValueKind.Number || !exitCode.TryGetInt32(out int code) || code != 0))
            errors.Add("Summary exitCode must be zero for passed evidence.");

        if (!root.TryGetProperty("cases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Case evidence is missing.");
            return;
        }
        if (cases.GetArrayLength() != expected.Denominator || cases.GetArrayLength() > MaximumCaseCount)
        {
            errors.Add("Case count does not equal the fixed denominator.");
            return;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in cases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Case entry must be an object.");
                continue;
            }
            string? id = OptionalString(item, "id");
            if (id is null || !ids.Add(id)) errors.Add("Case IDs must be non-empty and unique.");
            RequireString(item, "status", "passed", errors, "case");
            string? sourceHash = OptionalString(item, "sourceSha256");
            if (!IsSha256(sourceHash)) errors.Add("Every case must contain a valid sourceSha256.");
        }
    }

    private static void ValidateExecutionAndCleanup(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("execution", out JsonElement execution) || execution.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Execution provenance is missing.");
        }
        else
        {
            RequireNonEmpty(execution, "startedAtUtc", "execution", errors);
            RequireNonEmpty(execution, "finishedAtUtc", "execution", errors);
            if (execution.TryGetProperty("deadlineExpired", out JsonElement expired) &&
                (expired.ValueKind != JsonValueKind.False))
                errors.Add("Execution deadlineExpired must be false.");
        }
        if (!root.TryGetProperty("cleanup", out JsonElement cleanup) || cleanup.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Cleanup provenance is missing.");
        }
        else
        {
            RequireBoolean(cleanup, "completed", true, "cleanup", errors);
            if (cleanup.TryGetProperty("diagnostic", out JsonElement diagnostic) && diagnostic.ValueKind != JsonValueKind.Null &&
                diagnostic.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(diagnostic.GetString()))
                errors.Add("Cleanup diagnostic must be empty for passed evidence.");
        }
    }

    private static void RequireHash(JsonElement parent, string name, string expected, string scope, List<string> errors)
    {
        string? actual = OptionalString(parent, name);
        if (!IsSha256(actual)) errors.Add($"{scope}.{name} must be a 64-character SHA-256 hash.");
        else if (!actual!.Equals(expected, StringComparison.OrdinalIgnoreCase)) errors.Add($"{scope}.{name} does not match the expected candidate hash.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private static bool IsCandidateSha(string? value) => value is { Length: >= 40 and <= 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private static void RequireString(JsonElement parent, string name, string expected, List<string> errors, string scope = "report")
    {
        if (OptionalString(parent, name) is not string actual || !actual.Equals(expected, StringComparison.Ordinal)) errors.Add($"{scope}.{name} does not match the expected value.");
    }

    private static void RequireNonEmpty(JsonElement parent, string name, string scope, List<string> errors)
    {
        if (OptionalString(parent, name) is null) errors.Add($"{scope}.{name} is missing or empty.");
    }

    private static void RequireBoolean(JsonElement parent, string name, bool expected, string scope, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False || value.GetBoolean() != expected)
            errors.Add($"{scope}.{name} must be {expected.ToString().ToLowerInvariant()}.");
    }

    private static void RequirePositiveInt(JsonElement parent, string name, int expected, string scope, List<string> errors, bool allowZero = false)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int actual) || actual != expected || (!allowZero && actual < 1))
            errors.Add($"{scope}.{name} must equal {expected}.");
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
