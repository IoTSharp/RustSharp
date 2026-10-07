using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustSharp.Conformance;

/// <summary>
/// Reconciles the original 40 requirements with current leaves and actual registered tests.
/// The immutable 160-row catalogue is provenance, never execution evidence. Native gaps
/// remain named obligations; this contract cannot manufacture a platform or phase pass.
/// </summary>
internal static class P1GateCoverageContract
{
    internal const string ManifestFileName = "p1-gate-coverage-v1-manifest.json";
    internal const int RequirementDenominator = 40;
    internal const int CatalogueDenominator = 160;
    private const int MaximumManifestCharacters = 262_144;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly string[] Categories = ["positive", "negative", "boundary", "budget"];
    private static readonly string[] FrozenHarnessFiles =
    [
        "LexerClosureTests,SyntaxManifestTests", "SafeCoreWorkspaceTests,WorkspaceSourceMapTests",
        "SafeCoreTypeConformanceTests,SafeCoreTypeProfileTests", "SafeCoreGenericCompilationTests,SafeCoreGenericProfileTests",
        "SafeCoreMirValidationTests", "SafeCoreMirLoweringTests", "SafeCoreMirPlaceTests", "SafeCoreMirReferenceProvenanceTests",
        "SafeCoreMirV2ProfileTests", "SafeCoreMirSliceTests", "SafeCoreMirReferenceAbiTests", "SafeCoreMirReferenceAbiTests",
        "SafeCoreMirPatternExecutionTests", "SafeCoreMirClosureCaptureTests", "SafeCoreMirConstantExecutionTests",
        "SafeCoreMirFamilyEvidenceTests", "WorkspaceSourceMapTests", "SafeCoreMirFamilyEvidenceTests", "SafeCoreMirFamilyEvidenceTests",
        "SafeCoreMirScalarExecutionTests", "SafeCoreMirEnumTests", "SafeCoreMirProjectionBackendTests", "SafeCoreOwnershipTests",
        "SafeCoreMirReferenceProvenanceTests", "P1OwnershipResourceContractTests,P1OwnershipDiagnosticGoldenTests",
        "P1DifferentialProfileTests,P1ExpandedOwnershipEvidenceTests", "P1DropReceiverTests,P1DropFlagGenerationTests,P1AggregateDropCodegenTests",
        "P1ControlFlowDropTests,P1GeneratedUnwindEvidenceTests,P1NativeUnwindClosureTests", "P1DropDifferentialCodegenTests,P1NativeUnwindClosureTests",
        "P1SourcePackageExecutionTests,P1SourcePackageContractTests", "P1SourcePackageContractTests,P1SourceTypeMetadataTests",
        "P1CoverageProfileTests,SafeCoreRegressionV2Tests", "P1EvidenceBindingTests,P1HarnessEvidenceTests", "",
        "SafeCoreTypeProfileTests", "SafeCoreMirV2ProfileTests", "SyntaxProfileBoundaryTests", "SafeCoreGenericProfileTests",
        "SyntaxProfileBoundaryTests,SemanticAstBoundaryTests", "NativeAotTests",
    ];
    private static readonly Dictionary<string, string> FrozenBackendGaps = new(StringComparer.Ordinal)
    {
        ["P1-REQ-009"] = "MIR v2 emits deterministic metadata and evaluates repeated operands once",
        ["P1-REQ-012"] = "MIR slice ABI call and return preserve mutable subslice ownership",
        ["P1-REQ-014"] = "MIR closure mutable captures write back across calls",
        ["P1-REQ-015"] = "safe-core-mir constants inline const functions and bounded loops",
        ["P1-REQ-020"] = "MIR scalar signed division remainder bitwise and shifts execute",
        ["P1-REQ-022"] = "MIR CLR nested named tuple array places retain owner storage",
    };

    internal sealed record HarnessSource(string File, string Sha256, IReadOnlyList<string> CaseIds);
    internal sealed record BackendGap(string CaseId, string OwnerLeaf, IReadOnlyList<string> Backends, IReadOnlyList<string> Rids);
    internal sealed record Requirement(string Id, string Classification, string Role,
        IReadOnlyList<string> ImplementationLeaves, IReadOnlyList<string> GateOwners,
        IReadOnlyList<string> CatalogueCases, IReadOnlyList<HarnessSource> HarnessSources,
        IReadOnlyList<BackendGap> PendingBackendEvidence);
    internal sealed record Manifest(string Sha256, IReadOnlyList<Requirement> Requirements);
    internal sealed record HarnessAudit(string RuntimeIdentifier, string CandidateSha, int RequirementDenominator,
        int CatalogueDenominator, int BoundTestCount, bool HarnessEvidenceClosed,
        bool NativeExecutionClaim, IReadOnlyList<string> PendingBackendCells);

    /// <summary>Validates ownership against the frozen scope ledger, test source and legacy catalogue.</summary>
    internal static Manifest ParseManifest(string json, string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json); ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        long started = Stopwatch.GetTimestamp(); Guard(started, cancellationToken);
        Require(json.Length is > 0 and <= MaximumManifestCharacters, "Gate mapping exceeds its size bound.");
        using JsonDocument document = StrictJson(json, 24, started, cancellationToken);
        JsonElement root = document.RootElement;
        Fields(root, ["schemaVersion", "profile", "version", "taskId", "requirementDenominator", "catalogueDenominator",
            "scopeLedger", "legacyCatalogue", "evidencePolicy", "testTarget", "requirements"]);
        Require(Int(root, "schemaVersion") == 1 && Int(root, "version") == 1 && Text(root, "profile") == "p1-gate-coverage-v1" &&
            Text(root, "taskId") == "P1-GATE.01" && Int(root, "requirementDenominator") == 40 && Int(root, "catalogueDenominator") == 160,
            "Gate mapping identity or denominator changed.");
        Require(Text(root, "evidencePolicy") == "catalogue-is-not-execution; source-bound-full-harness; native-obligations-remain-open; aggregate-ownership-is-not-prerequisite",
            "Gate mapping cannot equate inventory or status text with execution.");
        Require(Text(root, "testTarget") == "dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter P1-GATE.01 --timeout 30 --deadline 120", "Gate target changed.");
        string ledgerText = ValidateBoundFile(root.GetProperty("scopeLedger"), "docs/p1-exit-scope-v1.md", repositoryRoot);
        string catalogueText = ValidateBoundFile(root.GetProperty("legacyCatalogue"), "tools/RustSharp.Conformance/fixtures/p1-coverage-v1-manifest.json", repositoryRoot);
        P1CoverageProfileRunner.Manifest catalogue = P1CoverageProfileRunner.ParseManifest(catalogueText, repositoryRoot);
        Require(catalogue.Denominator == 40 && catalogue.Cases.Count == 160, "Legacy denominator changed.");
        JsonElement rows = root.GetProperty("requirements");
        Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == 40, "Every frozen requirement must remain mapped.");
        var requirements = new List<Requirement>(40);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Guard(started, cancellationToken);
            Fields(row, ["id", "classification", "role", "implementationLeaves", "gateOwners", "catalogueCases", "harnessSources", "pendingBackendEvidence"]);
            string id = Text(row, "id"), classification = Text(row, "classification"), role = Text(row, "role");
            Require(seen.Add(id) && catalogue.Requirements.Any(item => item.Id == id && item.Classification == classification), "Unknown, duplicate or reclassified requirement.");
            string[] leaves = Strings(row.GetProperty("implementationLeaves"), 32), gates = Strings(row.GetProperty("gateOwners"), 6);
            Match ledgerRow = Regex.Match(ledgerText, @"(?m)^\| " + Regex.Escape(id) + @" \| `([^`]+)` \| (.+) \| (.+) \|\r?$", RegexOptions.CultureInvariant, RegexTimeout);
            Require(ledgerRow.Success && ledgerRow.Groups[1].Value == classification, "Requirement is absent from the frozen ledger.");
            string[] owners = ExpandOwners(ledgerRow.Groups[3].Value);
            Require(leaves.ToHashSet(StringComparer.Ordinal).SetEquals(owners.Where(item => !item.StartsWith("P1-GATE.", StringComparison.Ordinal))) &&
                gates.ToHashSet(StringComparer.Ordinal).SetEquals(owners.Where(item => item.StartsWith("P1-GATE.", StringComparison.Ordinal))), "Actual implementation leaves or aggregate ownership changed.");
            Require(role == (id == "P1-REQ-034" ? "aggregate-ownership" : id == "P1-REQ-040" ? "platform-boundary" :
                classification == "check-only" ? "check-only" : classification == "excluded" ? "rejection-boundary" : "implementation"), "Requirement role changed.");
            string[] cases = Strings(row.GetProperty("catalogueCases"), 4);
            Require(cases.Length == 4 && cases.ToHashSet(StringComparer.Ordinal).SetEquals(Categories.Select(category => id.ToLowerInvariant() + "-" + category)), "Legacy category mapping changed.");
            var sources = new List<HarnessSource>(4);
            var sourceFiles = new HashSet<string>(StringComparer.Ordinal);
            JsonElement sourceRows = row.GetProperty("harnessSources");
            Require(sourceRows.ValueKind == JsonValueKind.Array && sourceRows.GetArrayLength() <= 4, "Harness source bound changed.");
            foreach (JsonElement source in sourceRows.EnumerateArray())
            {
                Guard(started, cancellationToken); Fields(source, ["file", "sha256", "caseIds"]);
                string file = Text(source, "file");
                Require(sourceFiles.Add(file), "Duplicate harness source file.");
                Require(file.StartsWith("tests/RustSharp.Tests/", StringComparison.Ordinal) && file.EndsWith("Tests.cs", StringComparison.Ordinal), "Evidence must name an actual regression source.");
                string sourceText = ValidateBoundFile(source, file, repositoryRoot);
                string[] ids = Strings(source.GetProperty("caseIds"), 64);
                int declaration = sourceText.IndexOf("All { get; }", StringComparison.Ordinal);
                Require(declaration >= 0 && ids.Length > 0, "Test registration must be statically addressable.");
                string declarationText = sourceText[declaration..];
                // Embedded Rust arrays may contain ]; inside C# strings. The reviewed
                // All arrays close on a standalone C# line at the class's member indent.
                Match closingLine = Regex.Match(declarationText, @"(?m)^    \];\r?$", RegexOptions.CultureInvariant, RegexTimeout);
                Require(closingLine.Success && closingLine.Index > 0, "Test registration must have its own array closing line.");
                MatchCollection names = Regex.Matches(declarationText[..closingLine.Index], "new\\(\"([^\"]+)\"", RegexOptions.CultureInvariant, RegexTimeout);
                Require(ids.ToHashSet(StringComparer.Ordinal).SetEquals(names.Select(item => item.Groups[1].Value)), "Named tests do not equal the actual source registration.");
                sources.Add(new(file, Text(source, "sha256"), ids));
            }
            Require(role == "aggregate-ownership" ? sources.Count == 0 : sources.Count > 0, "Only aggregate ownership has no implementation test evidence.");
            int requirementIndex = int.Parse(id[^3..], System.Globalization.CultureInfo.InvariantCulture) - 1;
            string[] expectedFiles = FrozenHarnessFiles[requirementIndex].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => "tests/RustSharp.Tests/" + name + ".cs").ToArray();
            Require(sources.Select(source => source.File).ToHashSet(StringComparer.Ordinal).SetEquals(expectedFiles), "Required implementation test sources were omitted or substituted.");
            var gaps = new List<BackendGap>(8);
            JsonElement gapRows = row.GetProperty("pendingBackendEvidence");
            Require(gapRows.ValueKind == JsonValueKind.Array && gapRows.GetArrayLength() <= 8, "Pending backend cells exceed their finite bound.");
            var gapIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement gap in gapRows.EnumerateArray())
            {
                Guard(started, cancellationToken); Fields(gap, ["caseId", "ownerLeaf", "backends", "rids"]);
                string caseId = Text(gap, "caseId"), owner = Text(gap, "ownerLeaf");
                Require(gapIds.Add(caseId) && leaves.Contains(owner, StringComparer.Ordinal) && sources.Any(source => source.CaseIds.Contains(caseId, StringComparer.Ordinal)), "Backend gap must name an existing case and its actual owning leaf.");
                string[] backends = Strings(gap.GetProperty("backends"), 2), rids = Strings(gap.GetProperty("rids"), 2);
                Require(classification == "executable" && role == "implementation" && backends.ToHashSet(StringComparer.Ordinal).SetEquals(["ilverify", "native-aot"]) &&
                    rids.ToHashSet(StringComparer.Ordinal).SetEquals(["win-x64", "linux-x64"]), "Check-only/rejection rows cannot invent native requirements.");
                gaps.Add(new(caseId, owner, backends, rids));
            }
            Require(gapIds.SetEquals(FrozenBackendGaps.TryGetValue(id, out string? requiredGap) ? [requiredGap] : []), "A reviewed backend gap was removed or substituted.");
            requirements.Add(new(id, classification, role, leaves, gates, cases, sources, gaps));
        }
        Guard(started, cancellationToken);
        return new(Hash(json), requirements.AsReadOnly());
    }

    /// <summary>
    /// Binds the implementation cases to a complete process-isolated Release harness on one
    /// native RID. Native AOT/ILVerify and later aggregate ownership are deliberately not asserted.
    /// </summary>
    internal static HarnessAudit ValidateHarnessEvidence(Manifest manifest, string reportJson,
        string candidateSha, string candidateTreeSha, string runtimeIdentifier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest); ArgumentNullException.ThrowIfNull(reportJson);
        long started = Stopwatch.GetTimestamp(); Guard(started, cancellationToken);
        Require(candidateSha.Length == 40 && candidateSha.All(Uri.IsHexDigit) && candidateTreeSha.Length == 40 && candidateTreeSha.All(Uri.IsHexDigit), "Candidate and tree must be actual full identities supplied by the caller.");
        Require(runtimeIdentifier is "win-x64" or "linux-x64", "Only frozen native x64 RIDs are accepted.");
        Require(reportJson.Length is > 0 and <= 16_777_216, "Harness evidence exceeds its byte bound.");
        using JsonDocument report = StrictJson(reportJson, 32, started, cancellationToken);
        JsonElement root = report.RootElement;
        Require(Text(root, "evidenceKind") == "p1-full-regression-harness" && Text(root, "candidateSha") == candidateSha &&
            Text(root, "buildConfiguration") == "Release" && Bool(root, "fullSuite") && Bool(root, "suiteSucceeded") &&
            Bool(root, "processIsolated") && Bool(root, "cleanupComplete") && !Bool(root, "deadlineExpired") && !Bool(root, "cancelled"), "Filtered, stale, non-Release or incomplete harness is not leaf execution evidence.");
        Require(root.GetProperty("harnessError").ValueKind == JsonValueKind.Null, "Harness failure cannot count as evidence.");
        string rid = Text(root, "runtimeIdentifier");
        Require(rid == runtimeIdentifier || (runtimeIdentifier == "linux-x64" && rid.StartsWith("ubuntu.", StringComparison.Ordinal) && rid.EndsWith("-x64", StringComparison.Ordinal)), "Host RID is not the required native platform.");
        JsonElement provenance = root.GetProperty("sourceProvenance");
        Require(Text(provenance, "candidateSha") == candidateSha && Text(provenance, "candidateTreeSha") == candidateTreeSha &&
            Bool(provenance, "candidateMatchesWorkingTree") && Int(provenance, "checkedFileCount") > 0 &&
            provenance.GetProperty("errors").GetArrayLength() == 0, "Working source does not bind to the requested candidate tree.");
        JsonElement summary = root.GetProperty("summary");
        int schemaVersion = Int(root, "schemaVersion");
        Require(schemaVersion is 1 or 2, "Unsupported harness evidence schema.");
        int maximumTests = schemaVersion == 2 ? 4096 : 1024;
        if (schemaVersion == 2)
            Require(Int(root.GetProperty("bounds"), "maximumTests") == maximumTests, "Version 2 harness must declare its 4096 registration bound.");
        int denominator = Int(summary, "registeredDenominator");
        Require(denominator >= 464 && denominator <= maximumTests && Int(summary, "selected") == denominator && Int(summary, "executed") == denominator &&
            Int(summary, "passed") == denominator && Int(summary, "failed") == 0 && Int(summary, "skipped") == 0 && Int(summary, "notExecuted") == 0, "Fresh registered denominator is not fully executed.");
        string[] registered = Strings(root.GetProperty("registeredIds"), maximumTests);
        Require(registered.Length == denominator && Text(root, "registeredIdsSha256") == RawHash(string.Join('\n', registered)), "Fresh registration inventory was reduced or substituted.");
        JsonElement results = root.GetProperty("cases");
        Require(results.ValueKind == JsonValueKind.Array && results.GetArrayLength() == denominator, "Case execution denominator changed.");
        var executed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement result in results.EnumerateArray())
        {
            Guard(started, cancellationToken);
            Require(executed.Add(Text(result, "id")) && Text(result, "status") == "passed" && result.GetProperty("error").ValueKind == JsonValueKind.Null, "Missing, duplicate or skipped implementation case.");
            JsonElement process = result.GetProperty("process");
            ValidateWorkerExecution(process);
        }
        Require(executed.SetEquals(registered), "Execution cases disagree with fresh registrations.");
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>(256);
        foreach (Requirement requirement in manifest.Requirements)
        {
            Guard(started, cancellationToken);
            foreach (HarnessSource source in requirement.HarnessSources)
                foreach (string caseId in source.CaseIds)
                {
                    Require(executed.Contains(caseId), "Required implementation case did not execute: " + requirement.Id + ": " + caseId);
                    bound.Add(caseId);
                }
            foreach (BackendGap gap in requirement.PendingBackendEvidence)
                foreach (string backend in gap.Backends)
                    foreach (string target in gap.Rids)
                        pending.Add(requirement.Id + "|" + gap.OwnerLeaf + "|" + gap.CaseId + "|" + backend + "|" + target);
        }
        Guard(started, cancellationToken);
        return new(runtimeIdentifier, candidateSha, 40, 160, bound.Count, true, false, pending.AsReadOnly());
    }

    private static string ValidateBoundFile(JsonElement binding, string expectedPath, string repositoryRoot)
    {
        Fields(binding, binding.TryGetProperty("caseIds", out _) ? ["file", "sha256", "caseIds"] : ["file", "sha256"]);
        string relative = Text(binding, "file");
        Require(relative == expectedPath && !relative.Contains("..", StringComparison.Ordinal) && !relative.Contains(':') && !relative.Contains('\\'), "Source binding escaped its declared repository path.");
        string path = Path.GetFullPath(Path.Combine(repositoryRoot, relative));
        Require(File.Exists(path) && new FileInfo(path).Length <= 1_048_576 && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Bound source is missing, oversized or redirected.");
        string text = File.ReadAllText(path);
        Require(Text(binding, "sha256") == Hash(text), "Bound source hash changed: " + relative);
        return text;
    }
    private static string[] ExpandOwners(string text)
    {
        var owners = new List<string>(32);
        string[] parts = text.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        Require(parts.Length <= 16, "Owner list exceeded its bound.");
        foreach (string part in parts)
        {
            string[] range = part.Split('–');
            Require(range.Length is 1 or 2 && Regex.IsMatch(range[0], @"^P1-(?:\d{2}|GATE)\.\d{2}$", RegexOptions.CultureInvariant, RegexTimeout), "Old bucket owner is not an implementation leaf.");
            if (range.Length == 1) { owners.Add(part); continue; }
            string prefix = range[0][..^2];
            Require(range[1].StartsWith(prefix, StringComparison.Ordinal), "Leaf range crosses owner families.");
            int first = int.Parse(range[0][^2..], System.Globalization.CultureInfo.InvariantCulture), last = int.Parse(range[1][^2..], System.Globalization.CultureInfo.InvariantCulture);
            Require(first > 0 && last >= first && last - first < 20, "Leaf range exceeded its bound.");
            for (int leaf = first; leaf <= last; leaf++) owners.Add(prefix + leaf.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        }
        Require(owners.Count <= 32 && owners.Distinct(StringComparer.Ordinal).Count() == owners.Count, "Leaf ownership is oversized or duplicated.");
        return owners.ToArray();
    }
    private static JsonDocument StrictJson(string json, int depth, long started, CancellationToken cancellationToken)
    {
        // Duplicate keys are rejected at every depth before JsonDocument's last-value lookup.
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = depth });
        var fields = new Stack<HashSet<string>>();
        int tokens = 0;
        while (reader.Read())
        {
            Guard(started, cancellationToken);
            Require(++tokens <= 262_144, "JSON token bound exceeded.");
            if (reader.TokenType == JsonTokenType.StartObject) fields.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) fields.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName) Require(fields.Peek().Add(reader.GetString()!), "Duplicate JSON property.");
        }
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = depth });
    }
    private static void Fields(JsonElement value, string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == fields.Length && value.EnumerateObject().All(item => fields.Contains(item.Name, StringComparer.Ordinal)), "Missing or unexpected contract property.");
    }
    private static string[] Strings(JsonElement value, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum, "Inventory count exceeded its bound.");
        string[] result = value.EnumerateArray().Select(item => item.GetString() ?? throw new ArgumentException("Inventory member is null.")).ToArray();
        Require(result.All(item => item.Length is > 0 and <= 512) && result.Distinct(StringComparer.Ordinal).Count() == result.Length, "Empty or duplicate inventory member.");
        return result;
    }
    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString() ?? throw new ArgumentException("Required string is absent: " + field);
    private static int Int(JsonElement value, string field) => value.GetProperty(field).GetInt32();
    private static bool Bool(JsonElement value, string field) => value.GetProperty(field).GetBoolean();
    internal static string Hash(string text) => RawHash(text.Replace("\r\n", "\n", StringComparison.Ordinal));
    private static string RawHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static void ValidateWorkerExecution(JsonElement process)
    {
        JsonElement started = process.GetProperty("startedProcess");
        Require(Int(started, "processId") > 0 && Int(started, "parentProcessId") > 0 &&
            Text(started, "startedAt").Length > 0 && Text(started, "fileName").Length > 0 &&
            started.GetProperty("arguments").GetArrayLength() > 0 && Int(process, "exitCode") == 0 &&
            Int(process, "termination") == 0 && Bool(process, "succeeded") &&
            !Bool(process, "outputTruncated") && !Bool(process, "outputReadTimedOut") &&
            !Bool(process, "outputDrainTimedOut") && !Bool(process, "outputReadLimitReached") &&
            !Bool(process, "processTreeCleanupIncomplete"), "Case has no successful owned worker execution record.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    private static void Guard(long started, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) > Deadline) throw new TimeoutException("P1 coverage validation exceeded fifteen seconds.");
    }
}
