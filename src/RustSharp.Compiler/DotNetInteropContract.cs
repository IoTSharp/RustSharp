using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RustSharp.Compiler;

/// <summary>Validates the frozen P2 interop design inventory, without binding or executing source.</summary>
public static class DotNetInteropContract
{
    public const string Profile = "dotnet-interop-v1";
    public const int CaseDenominator = 36;
    public const int MaximumManifestBytes = 262_144;
    public const int MaximumManifestDepth = 16;
    public const int MaximumValidationMilliseconds = 2_000;
    public const string FrozenContractSha256 = "AFA13CE319C44DB2E0A9BF6F81DE65D8929656AFEC20AF241C6AB03D5B689C56";

    public static DotNetInteropContractValidation Validate(string manifestJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var issues = ImmutableArray.CreateBuilder<string>();
        var caseIds = ImmutableArray.CreateBuilder<string>();
        if (manifestJson.Length > MaximumManifestBytes || Encoding.UTF8.GetByteCount(manifestJson) > MaximumManifestBytes)
            return Result("", "", ["RSDNC0001: Manifest exceeds the 262144-byte contract limit."], []);

        string rawHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));
        string contractHash = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(manifestJson,
                new JsonDocumentOptions { MaxDepth = MaximumManifestDepth });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result(rawHash, "", ["RSDNC0002: Manifest root must be an object."], []);

            CheckTime();
            if (!root.TryGetProperty("schemaVersion", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
                issues.Add("RSDNC0002: schemaVersion must be 1.");
            RequireText(root, "profile", Profile);
            RequireText(root, "leafId", "P2-06.01");
            RequireText(root, "evidenceKind", "p2-interop-design-contract");
            RequireText(root, "implementationState", "design-only");
            if (!root.TryGetProperty("runtimeImplemented", out JsonElement implemented) || implemented.ValueKind != JsonValueKind.False)
                issues.Add("RSDNC0003: Design validation cannot claim runtime implementation.");
            if (!root.TryGetProperty("denominator", out JsonElement denominator) || denominator.ValueKind != JsonValueKind.Number || !denominator.TryGetInt32(out int count) || count != CaseDenominator)
                issues.Add("RSDNC0004: The frozen case denominator must be 36.");

            if (!root.TryGetProperty("cases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() != CaseDenominator)
                issues.Add("RSDNC0004: All 36 case contracts must be present exactly once.");
            else
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                // The exact array length is checked above; every iteration has cancellation and a wall-clock bound.
                for (int index = 0; index < CaseDenominator; index++)
                {
                    CheckTime();
                    JsonElement item = cases[index];
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement id) ||
                        id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        issues.Add($"RSDNC0004: Case {index} has no string identity.");
                        continue;
                    }
                    string identity = id.GetString()!;
                    if (!seen.Add(identity)) issues.Add("RSDNC0004: Duplicate case identity: " + identity);
                    caseIds.Add(identity);
                }
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) root.WriteTo(writer);
            contractHash = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
            if (!string.Equals(contractHash, FrozenContractSha256, StringComparison.Ordinal))
                issues.Add("RSDNC0005: Frozen v1 content changed; revise the profile version rather than shrinking or replacing its contract.");
            CheckTime();
        }
        catch (JsonException)
        {
            issues.Add("RSDNC0002: Invalid JSON or excessive JSON depth.");
        }
        catch (TimeoutException)
        {
            issues.Add("RSDNC0001: Contract validation exceeded its 2000-millisecond deadline.");
        }
        return Result(rawHash, contractHash, issues.ToImmutable(), caseIds.ToImmutable());

        void CheckTime()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds > MaximumValidationMilliseconds) throw new TimeoutException();
        }

        void RequireText(JsonElement parent, string name, string expected)
        {
            if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String || value.GetString() != expected)
                issues.Add($"RSDNC0002: {name} must equal '{expected}'.");
        }
    }

    private static DotNetInteropContractValidation Result(string manifestHash, string contractHash,
        ImmutableArray<string> issues, ImmutableArray<string> caseIds) =>
        new(issues.IsEmpty, Profile, manifestHash, contractHash, CaseDenominator,
            issues.IsEmpty ? caseIds.Length : 0, issues, caseIds);
}

/// <summary>A design inventory result. Verified cases are contract records, never executed semantic cases.</summary>
public sealed record DotNetInteropContractValidation(bool IsValid, string Profile, string ManifestSha256,
    string ContractSha256, int CaseDenominator, int VerifiedCaseCount, ImmutableArray<string> Issues,
    ImmutableArray<string> CaseIds)
{
    public bool RuntimeEvidence { get; }
}
