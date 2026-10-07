using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Reconciles exactly the six frozen emitted-backend gaps; never substitutes a platform-suite denominator.</summary>
internal static class P1BackendCoverageEvidence
{
    internal const string Profile = "p1-backend-coverage-v1";
    internal const int FixtureDenominator = 6;
    internal const int CellDenominator = 24;
    internal const int LocalCellDenominator = 12;
    internal const int MaximumReportBytes = 16 * 1024 * 1024;
    // Candidate provenance may consume 120 seconds; retained-byte and semantic checks share the remaining bounded budget.
    internal static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(240);
    internal static IReadOnlyList<string> Rids { get; } = System.Array.AsReadOnly(new[] { "win-x64", "linux-x64" });
    internal static IReadOnlyList<string> Backends { get; } = System.Array.AsReadOnly(new[] { "ilverify", "native-aot" });
    internal sealed record Fixture(string Id, string RequirementId, string OwnerLeaf, string CaseId,
        string RegistrationFile, string RegistrationSha256, string Source, string ExpectedOutput, bool HandMir = false)
    {
        internal string SourceSha256 => HashText(Source);
        internal IReadOnlyList<string> WitnessIds => HandMir ? [Id + ":frozen-hand-mir", Id + ":rust-source"] : [Id];
    }
    internal static IReadOnlyList<Fixture> Fixtures { get; } = System.Array.AsReadOnly(new[]
    {
        new Fixture("repeated-array-once", "P1-REQ-009", "P1-06.06", "MIR v2 emits deterministic metadata and evaluates repeated operands once",
            "tests/RustSharp.Tests/SafeCoreMirV2ProfileTests.cs", "FFDE51BD1CAB1AFA60A3C71AFFE20DC3502B56F8654A836B1ED572E040C65BE5",
            "fn seed() -> i32 { println!(\"seed\"); 7 } fn main() { let values = [seed(); 3]; println!(\"{}\", values[0]); " +
            "println!(\"{}\", values[2]); let empty: [i32; 0] = [seed(); 0]; }", "seed\n7\n7\nseed\n"),
        new Fixture("mutable-subslice-call-return", "P1-REQ-012", "P1-06.09", "MIR slice ABI call and return preserve mutable subslice ownership",
            "tests/RustSharp.Tests/SafeCoreMirReferenceAbiTests.cs", "AC6F9CC2990466FCCC5BFDE7405BD75CC89E3AE276EC124166D04C36B80FA520",
            "fn tail(values: &mut [i32], start: usize) -> &mut [i32] { &mut values[start..] } " +
            "fn main() { let mut values = [1, 2, 3, 4]; let view: &mut [i32] = &mut values; " +
            "let selected = tail(view, 1); selected[1] = 42; println!(\"{}\", selected.len()); " +
            "println!(\"{}\", selected[1]); println!(\"{}\", values[2]); }", "3\n42\n42\n"),
        new Fixture("mutable-closure-captures", "P1-REQ-014", "P1-06.11", "MIR closure mutable captures write back across calls",
            "tests/RustSharp.Tests/SafeCoreMirClosureCaptureTests.cs", "39DE7DE8DB5E2923611AA8BD9C6492C37892D38BBFB4BD9C3B93A9751824B4B9",
            "fn main() { let mut base = 2; let mut add = |value: i32| { base += value; base }; " +
            "println!(\"{}\", add(3)); println!(\"{}\", add(4)); println!(\"{}\", base); }", "5\n9\n9\n"),
        new Fixture("inline-const-loop", "P1-REQ-015", "P1-06.12", "safe-core-mir constants inline const functions and bounded loops",
            "tests/RustSharp.Tests/SafeCoreMirConstantExecutionTests.cs", "6E43177E33D18463A8355A49EDF9E977FCDB5B52D0D127042D08ABEBB1B3BB72",
            "const fn sum(n: i32) -> i32 { let mut total = 0; let mut index = 0; while index < n { total += index; index += 1; } total } " +
            "const TOTAL: i32 = sum(6); fn main() { let value = const { let (x, y) = (2, 3); x * y }; println!(\"{}\", TOTAL + value); }", "21\n"),
        new Fixture("signed-scalar-ops", "P1-REQ-020", "P1-06.17", "MIR scalar signed division remainder bitwise and shifts execute",
            "tests/RustSharp.Tests/SafeCoreMirScalarExecutionTests.cs", "09817C470BA8B1FC6AAD7548939B75143985A277B0F9F38FAE89D733B8A5B550",
            "fn main() { let a = -17; let b = 5; println!(\"{}\", a / b); println!(\"{}\", a % b); " +
            "println!(\"{}\", 6 & 3); println!(\"{}\", 6 | 3); println!(\"{}\", 6 ^ 3); println!(\"{}\", !6); " +
            "println!(\"{}\", 1 << 31); println!(\"{}\", -8 >> 2); let mut n = 21; n /= 3; n %= 5; n <<= 2; n |= 1; n ^= 3; " +
            "println!(\"{}\", n); }", "-3\n-2\n2\n7\n5\n-7\n-2147483648\n-2\n10\n"),
        new Fixture("nested-place-owner", "P1-REQ-022", "P1-06.19", "MIR CLR nested named tuple array places retain owner storage",
            "tests/RustSharp.Tests/SafeCoreMirProjectionBackendTests.cs", "19114FDBBD220326F9A32CD814B430117D3436FEC96BDA5D356BE6DB7F93DA90",
            "struct Container { payload: (bool, [i32; 2]) } fn main() { let mut value = Container { payload: (true, [3, 7]) }; " +
            "let index: usize = 1; let selected = &mut value.payload.1[index]; *selected = 41; println!(\"{}\", value.payload.1[1]); }", "41\n", true),
    });
    internal sealed record Expectation(string CandidateSha, string CandidateTreeSha, string RuntimeIdentifier);
    internal sealed record ValidationResult(bool Valid, bool ArtifactContentVerified, int ClosedCells, IReadOnlyList<string> Errors)
    {
        internal bool SatisfiesNativeGate => Valid && ArtifactContentVerified && ClosedCells == LocalCellDenominator;
        internal bool SatisfiesMatrixGate => Valid && ArtifactContentVerified && ClosedCells == CellDenominator;
    }

    internal static void ValidateMapping(P1GateCoverageContract.Manifest manifest)
    {
        Require(Fixtures.Count == FixtureDenominator, "The fixed backend fixture denominator changed.");
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (Fixture fixture in Fixtures)
        {
            P1GateCoverageContract.Requirement requirement = manifest.Requirements.Single(row => row.Id == fixture.RequirementId);
            P1GateCoverageContract.BackendGap gap = requirement.PendingBackendEvidence.Single();
            Require(gap.CaseId == fixture.CaseId && gap.OwnerLeaf == fixture.OwnerLeaf, "A backend witness substituted its frozen registration.");
            P1GateCoverageContract.HarnessSource source = requirement.HarnessSources.Single(row => row.File == fixture.RegistrationFile);
            Require(source.Sha256 == fixture.RegistrationSha256 && source.CaseIds.Contains(fixture.CaseId, StringComparer.Ordinal), "Backend registration source/hash binding changed.");
            foreach (string backend in gap.Backends) foreach (string rid in gap.Rids) expected.Add(CellId(fixture, backend, rid));
        }
        Require(expected.Count == CellDenominator && manifest.Requirements.Sum(row => row.PendingBackendEvidence.Count) == FixtureDenominator,
            "The exact pending-backend matrix changed.");
    }

    // Shape validation is useful for rejection tests; it never supplies physical artifact closure.
    internal static ValidationResult ValidateNativeReport(ReadOnlySpan<byte> bytes, Expectation expected, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        try { JsonObject report = Parse(bytes, clock, cancellationToken); ValidateShape(report, expected, clock, cancellationToken); return new(true, false, 12, []); }
        catch (Exception exception) when (Failure(exception)) { return new(false, false, 0, [exception.Message]); }
    }

    /// <summary>Validates only the shared envelope. No processes or artifacts are asserted, and zero cells close.</summary>
    internal static ValidationResult ValidateNativeEnvelope(ReadOnlySpan<byte> bytes, Expectation expected, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        try { JsonObject report = Parse(bytes, clock, cancellationToken); ValidateEnvelope(report, expected, clock, cancellationToken); return new(true, false, 0, []); }
        catch (Exception exception) when (Failure(exception)) { return new(false, false, 0, [exception.Message]); }
    }

    internal static async Task<ValidationResult> ValidateNativeReportAsync(string repositoryRoot, string reportPath,
        Expectation expected, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(ValidationTimeout);
        try
        {
            JsonObject report = Parse(Read(reportPath, MaximumReportBytes), clock, deadline.Token);
            ValidateShape(report, expected, clock, deadline.Token);
            await ValidateContentAsync(repositoryRoot, reportPath, report, expected, true, clock, deadline.Token).ConfigureAwait(false);
            return new(true, true, 12, []);
        }
        catch (Exception exception) when (Failure(exception)) { return new(false, false, 0, [exception.Message]); }
    }

    internal static async Task<ValidationResult> ValidateClosedMatrixAsync(string repositoryRoot, string windowsReportPath,
        string linuxReportPath, string candidateSha, string candidateTreeSha, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(ValidationTimeout);
        try
        {
            Require(Path.GetFullPath(windowsReportPath) != Path.GetFullPath(linuxReportPath), "Two independent native reports are required.");
            var closed = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string file, string rid) in new[] { (windowsReportPath, "win-x64"), (linuxReportPath, "linux-x64") })
            {
                Budget(clock, deadline.Token); var expected = new Expectation(candidateSha, candidateTreeSha, rid);
                JsonObject report = Parse(Read(file, MaximumReportBytes), clock, deadline.Token); ValidateShape(report, expected, clock, deadline.Token);
                // Foreign-host build outputs are retained artifacts; never reinterpret them as execution on this host.
                await ValidateContentAsync(repositoryRoot, file, report, expected, false, clock, deadline.Token).ConfigureAwait(false);
                foreach (JsonNode? node in Arr(report, "cells", 24, 24))
                    if (Str(node!.AsObject(), "status") == "passed") Require(closed.Add(Str(node.AsObject(), "id")), "Native reports duplicated a matrix cell.");
            }
            Require(closed.SetEquals(Fixtures.SelectMany(f => Backends.SelectMany(b => Rids.Select(r => CellId(f, b, r))))), "All 24 exact backend cells must close.");
            return new(true, true, 24, []);
        }
        catch (Exception exception) when (Failure(exception)) { return new(false, false, 0, [exception.Message]); }
    }

    private static void ValidateEnvelope(JsonObject report, Expectation expected, Stopwatch clock, CancellationToken token)
    {
        Budget(clock, token); Require(IsCommit(expected.CandidateSha) && IsCommit(expected.CandidateTreeSha), "Full candidate and tree identities are required.");
        Require(Int(report, "schemaVersion") == 1 && Str(report, "profile") == Profile && Str(report, "evidenceKind") == "p1-exact-backend-gap-native-witnesses", "Backend evidence identity changed.");
        Require(Str(report, "candidateSha") == expected.CandidateSha && Str(report, "candidateTreeSha") == expected.CandidateTreeSha, "Backend candidate/tree binding is stale.");
        Require(expected.RuntimeIdentifier is "win-x64" or "linux-x64" && Str(report, "targetRuntimeIdentifier") == expected.RuntimeIdentifier &&
            HostMatches(Str(report, "hostRuntimeIdentifier"), expected.RuntimeIdentifier) && Str(report, "processArchitecture") == "X64" && Str(report, "osArchitecture") == "X64" && Bool(report, "nativeExecution"), "Backend evidence must come from its native x64 host.");
        Require(Str(report, "compilerProfile") == "safe-core-mir-p1-v2" && Str(report, "dropCleanupProfile") == "legacy-v1", "Backend compiler or Drop policy changed.");
        JsonObject summary = Obj(report, "summary");
        Require(Int(summary, "fixtureDenominator") == 6 && Int(summary, "cellDenominator") == 24 && Int(summary, "localCellDenominator") == 12 &&
            Int(summary, "maximumSelectedFixtures") == 6 && Int(summary, "passed") == 12 && Int(summary, "blocked") == 12 && Int(summary, "failed") == 0 && Bool(summary, "localClosure"), "Filtered, reduced or incomplete backend evidence cannot close the local gate.");
    }

    private static void ValidateShape(JsonObject report, Expectation expected, Stopwatch clock, CancellationToken token)
    {
        ValidateEnvelope(report, expected, clock, token);
        JsonObject execution = Obj(report, "execution"), cleanup = Obj(report, "cleanup");
        Require(Bool(execution, "deadlineMet") && !Bool(execution, "cancelled") && Number(execution, "suiteTimeoutSeconds") == 1200 &&
            Number(execution, "elapsedMilliseconds") is >= 0 and < 1200000 && Date(execution, "finishedAtUtc") >= Date(execution, "startedAtUtc"), "Backend execution deadline is incomplete.");
        Require(Bool(cleanup, "completed") && !Bool(cleanup, "temporaryDirectoryExists") && report["harnessError"] is null, "Backend process/temporary cleanup failed.");
        foreach (string name in new[] { "initialCandidateValidation", "finalCandidateValidation" }) Process(Obj(report, name), 120000, true);
        var registry = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string ProcessKey(JsonObject process) => Int(process, "pid").ToString(CultureInfo.InvariantCulture) + "/" + Str(process, "startedAtUtc");
        foreach (JsonNode? node in Arr(report, "processResults", 30, 30))
        {
            JsonObject process = node!.AsObject(); Process(process, 600000, false); Require(registry.TryAdd(ProcessKey(process), process), "Duplicate backend process result.");
        }
        var startKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in Arr(report, "processStarts", 30, 30))
        {
            JsonObject start = node!.AsObject(); string key = ProcessKey(start);
            Require(startKeys.Add(key) && registry.TryGetValue(key, out JsonObject? result) && Str(start, "commandLine") == Str(result, "commandLine") && Int(start, "parentPid") == Int(result, "parentPid"), "Backend process start/result lifecycle is incomplete.");
        }
        void Registered(JsonObject process) => Require(registry.TryGetValue(ProcessKey(process), out JsonObject? result) && JsonNode.DeepEquals(result, process), "Required backend execution is absent from its owned registry.");
        foreach (string name in new[] { "initialCandidateValidation", "finalCandidateValidation" })
        {
            JsonObject process = Obj(report, name); Registered(process);
            JsonObject proof = Parse(Encoding.UTF8.GetBytes(Str(process, "stdout")), clock, token);
            Require(Str(proof, "candidateSha") == expected.CandidateSha && Str(proof, "treeSha") == expected.CandidateTreeSha && Bool(proof, "verified"), "Actual candidate verification returned a different tree.");
        }
        foreach (string name in new[] { "sourceSnapshot", "releaseBuild", "retainedManifest" }) Artifact(Obj(report, name));
        JsonObject snapshot = Obj(report, "sourceSnapshotRaw"), build = Obj(report, "releaseBuildRaw");
        Require(Str(snapshot, "evidenceKind") == "p1-candidate-source-snapshot" && Str(snapshot, "candidateSha") == expected.CandidateSha &&
            Str(snapshot, "treeSha") == expected.CandidateTreeSha && Bool(snapshot, "verified"), "Actual candidate snapshot is missing.");
        Require(Str(build, "evidenceKind") == "p1-release-build" && Str(build, "candidateSha") == expected.CandidateSha && Str(build, "configuration") == "Release" && Bool(build, "succeeded") &&
            Int(Obj(build, "summary"), "warnings") == 0 && Int(Obj(build, "summary"), "errors") == 0, "A fresh warning-free Release build is required.");
        JsonObject source = Obj(build, "sourceProvenance");
        Require(Str(source, "candidateTreeSha") == expected.CandidateTreeSha && Bool(source, "candidateMatchesWorkingTree") && Int(source, "checkedFileCount") > 0 && Arr(source, "errors", 0, 0).Count == 0, "Release source provenance is incomplete.");
        JsonArray implementations = Arr(report, "implementationInputs", 6, 6);
        string[] names = ["RustSharp.Compiler.dll", "RustSharp.Conformance.dll", "RustSharp.Runtime.dll", "RustSharp.Semantics.dll", "RustSharp.CodeGen.IL.dll", "RustSharp.Syntax.dll"];
        Require(implementations.Select(node => Str(node!.AsObject(), "name")).ToHashSet(StringComparer.Ordinal).SetEquals(names), "Loaded implementation inventory changed.");
        foreach (JsonNode? node in implementations)
        {
            JsonObject item = node!.AsObject(); string hash = Artifact(Obj(item, "retainedAssembly"));
            Require(hash == Artifact(Obj(item, "loadedAssembly")) && hash == Artifact(Obj(item, "releaseOutput")), "Loaded modules are not the verified fresh Release output.");
            JsonObject original = Arr(build, "implementationAssemblies", 6, 6).Select(n => n!.AsObject()).Single(n => Path.GetFileName(Str(n, "path").Replace('\\', '/')) == Str(item, "name"));
            Require(hash == Str(original, "sha256"), "Implementation report differs from the fresh build inventory.");
        }
        JsonArray witnesses = Arr(report, "witnesses", 7, 7); var witnessMap = witnesses.ToDictionary(n => Str(n!.AsObject(), "id"), n => n!.AsObject(), StringComparer.Ordinal);
        Require(witnessMap.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Fixtures.SelectMany(f => f.WitnessIds)), "Missing or substituted backend witnesses.");
        foreach (Fixture fixture in Fixtures)
        {
            Budget(clock, token);
            foreach (string id in fixture.WitnessIds)
            {
                JsonObject witness = witnessMap[id]; bool hand = id.EndsWith(":frozen-hand-mir", StringComparison.Ordinal);
                Require(Str(witness, "fixtureId") == fixture.Id && Str(witness, "requirementId") == fixture.RequirementId && Str(witness, "ownerLeaf") == fixture.OwnerLeaf &&
                    Str(witness, "originalCaseId") == fixture.CaseId && Str(witness, "registrationFile") == fixture.RegistrationFile && Str(witness, "registrationSha256") == fixture.RegistrationSha256, "Original exact-gap registration binding changed.");
                Require(Str(witness, "witnessKind") == (hand ? "frozen-hand-authored-mir" : fixture.HandMir ? "supplementary-rust-source" : "original-rust-source") && Str(witness, "status") == "passed", "Backend witness kind/status changed.");
                Require(Str(witness, "expectedManagedOutput") == (hand ? "" : fixture.ExpectedOutput) && Int(witness, "expectedManagedExitCode") == (hand ? 41 : 0) &&
                    Str(witness, "expectedNativeOutput") == fixture.ExpectedOutput, "Backend semantic expectations changed.");
                if (!hand) Require(Artifact(Obj(witness, "source")) == fixture.SourceSha256, "Rust source witness bytes changed.");
                Artifact(Obj(witness, "registrationSource")); Artifact(Obj(witness, "originalPe")); Artifact(Obj(witness, "originalPdb")); Artifact(Obj(witness, "runtimeConfig")); Artifact(Obj(witness, "runtime"));
                JsonObject managed = Obj(witness, "managedRun"); Process(managed, 30000, false);
                Registered(managed);
                Require(Int(managed, "exitCode") == (hand ? 41 : 0) && Normalize(Str(managed, "stdout")) == (hand ? "" : fixture.ExpectedOutput) && string.IsNullOrWhiteSpace(Str(managed, "stderr")) &&
                    Str(managed, "commandLine").Contains(Str(Obj(witness, "originalPe"), "path"), StringComparison.Ordinal), "Original generated PE did not execute the expected behavior.");
                JsonObject il = Obj(witness, "ilVerify"); Process(Obj(il, "process"), 180000, true); Artifact(Obj(il, "report"));
                Registered(Obj(il, "process"));
                Require(Str(il, "status") == "passed", "Original PE ILVerify did not complete.");
                JsonObject aot = Obj(witness, "nativeAot"); Process(Obj(aot, "publish"), 600000, true); Process(Obj(aot, "run"), 30000, true);
                Registered(Obj(aot, "publish")); Registered(Obj(aot, "run"));
                Require(Str(aot, "status") == "passed" && Bool(aot, "hostCleanupAttempted") && !Bool(aot, "hostCleanupIncomplete") && !Bool(aot, "hostDirectoryExists"), "Native AOT publication/host cleanup failed.");
                Require(Normalize(Str(Obj(aot, "run"), "stdout")) == fixture.ExpectedOutput && string.IsNullOrWhiteSpace(Str(Obj(aot, "run"), "stderr")), "Native behavior differs from its exact source/MIR witness.");
                Require(!HasWarnings(Str(Obj(aot, "publish"), "stdout") + Str(Obj(aot, "publish"), "stderr")), "Native AOT warnings cannot close backend evidence.");
                Artifact(Obj(aot, "executable")); Arr(aot, "retainedHostInputs", 4, 4);
            }
        }
        var cells = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in Arr(report, "cells", 24, 24))
        {
            JsonObject cell = node!.AsObject(); Fixture fixture = Fixtures.Single(f => f.Id == Str(cell, "fixtureId")); string backend = Str(cell, "backend"), rid = Str(cell, "runtimeIdentifier");
            Require(Backends.Contains(backend, StringComparer.Ordinal) && Rids.Contains(rid, StringComparer.Ordinal) && cells.Add(Str(cell, "id")) && Str(cell, "id") == CellId(fixture, backend, rid), "Duplicate or invented exact backend cells.");
            Require(Str(cell, "requirementId") == fixture.RequirementId && Str(cell, "ownerLeaf") == fixture.OwnerLeaf && Str(cell, "originalCaseId") == fixture.CaseId, "Cell owner/registration changed.");
            string[] bound = Arr(cell, "witnessIds", fixture.WitnessIds.Count, fixture.WitnessIds.Count).Select(n => n!.GetValue<string>()).ToArray();
            Require(bound.SequenceEqual(fixture.WitnessIds), "A cell omitted its original or supplementary witness.");
            Require(Str(cell, "status") == (rid == expected.RuntimeIdentifier ? "passed" : "blocked") &&
                (rid == expected.RuntimeIdentifier || Str(cell, "reason") == "Requires independent native " + rid + " execution."), "Foreign native cells cannot be asserted by a local host.");
        }
        Require(cells.Count == 24, "Exact backend matrix denominator changed.");
    }

    private static async Task ValidateContentAsync(string root, string reportPath, JsonObject report, Expectation expected,
        bool reverifyLocal, Stopwatch clock, CancellationToken token)
    {
        string originalRoot = Str(Obj(report, "cleanup"), "retainedEvidenceDirectory"), originalPortable = originalRoot.Replace('\\', '/').TrimEnd('/');
        string directoryName = originalPortable[(originalPortable.LastIndexOf('/') + 1)..];
        Require(directoryName.StartsWith("backend-", StringComparison.Ordinal) && directoryName.Length <= 96 && directoryName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "Invalid retained backend directory identity.");
        string evidenceRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, directoryName);
        string Physical(string original)
        {
            string portable = original.Replace('\\', '/');
            Require(portable.StartsWith(originalPortable + "/", StringComparison.OrdinalIgnoreCase), "Transported artifact escaped its original evidence directory.");
            string relative = portable[(originalPortable.Length + 1)..]; return Child(evidenceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }
        string ArtifactPath(JsonObject item) => Physical(Str(item, "path"));
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal); long total = 0;
        foreach (JsonNode? node in Arr(report, "retainedArtifacts", 1, 192))
        {
            Budget(clock, token); JsonObject item = node!.AsObject(); string path = ArtifactPath(item);
            Require(artifacts.TryAdd(path, Artifact(item)), "Duplicate retained artifact paths."); total += new FileInfo(path).Length;
            Require(total <= 1073741824 && FileHash(path) == Artifact(item), "Retained artifact bytes are stale or exceed the aggregate bound.");
        }
        void Retained(JsonObject item) { string path = ArtifactPath(item); Require(artifacts.TryGetValue(path, out string? hash) && hash == Artifact(item), "Required artifact is absent from the retained inventory."); }
        foreach (string name in new[] { "sourceSnapshot", "releaseBuild", "retainedManifest" }) Retained(Obj(report, name));
        foreach ((string pathName, string rawName) in new[] { ("sourceSnapshot", "sourceSnapshotRaw"), ("releaseBuild", "releaseBuildRaw") })
            Require(JsonNode.DeepEquals(Parse(Read(ArtifactPath(Obj(report, pathName)), 4194304), clock, token), report[rawName]), "Raw candidate evidence differs from retained bytes.");
        string manifestJson = Encoding.UTF8.GetString(Read(ArtifactPath(Obj(report, "retainedManifest")), 262144));
        P1GateCoverageContract.Manifest manifest = P1GateCoverageContract.ParseManifest(manifestJson, root, token); ValidateMapping(manifest);
        Require(manifest.Sha256 == Str(report, "manifestSha256"), "Backend manifest identity changed.");
        foreach (JsonNode? node in Arr(report, "implementationInputs", 6, 6)) Retained(Obj(node!.AsObject(), "retainedAssembly"));
        JsonObject build = Obj(report, "releaseBuildRaw"); JsonArray retainedBuild = Arr(report, "retainedBuildArtifacts", 11, 11);
        var requiredBuild = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string kind in new[] { "compiler", "testsAssembly" }) Require(requiredBuild.TryAdd(Str(build, kind + "ArtifactPath"), Str(build, kind + "Sha256")), "Duplicate Release build artifact identity.");
        Require(requiredBuild.TryAdd(Str(build, "registrationInventoryPath"), Str(build, "registrationInventorySha256")), "Duplicate registration artifact identity.");
        foreach (JsonNode? node in Arr(build, "sourceSnapshots", 2, 2)) { JsonObject item = node!.AsObject(); Require(requiredBuild.TryAdd(Str(item, "path"), Str(item, "sha256")), "Duplicate pre/post source snapshot."); }
        foreach (JsonNode? node in Arr(build, "implementationAssemblies", 6, 6)) { JsonObject item = node!.AsObject(); Require(requiredBuild.TryAdd(Str(item, "artifactPath"), Str(item, "sha256")), "Duplicate Release implementation artifact."); }
        var buildSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in retainedBuild)
        {
            JsonObject item = node!.AsObject(); string original = Str(item, "originalPath"), hash = Str(item, "sha256"); Retained(Obj(item, "retained"));
            Require(buildSeen.Add(original) && requiredBuild.TryGetValue(original, out string? expectedHash) && hash == expectedHash && hash == Artifact(Obj(item, "retained")), "Transported Release build artifact differs from its verified build binding.");
            if (original == Str(build, "registrationInventoryPath")) Require(JsonNode.DeepEquals(Parse(Read(ArtifactPath(Obj(item, "retained")), 1048576), clock, token), build["registrationInventory"]), "Retained registration inventory differs from the fresh build.");
            if (Arr(build, "sourceSnapshots", 2, 2).Any(n => Str(n!.AsObject(), "path") == original))
            {
                JsonObject snap = Parse(Read(ArtifactPath(Obj(item, "retained")), 4194304), clock, token);
                Require(Str(snap, "candidateSha") == expected.CandidateSha && Str(snap, "treeSha") == expected.CandidateTreeSha && Bool(snap, "verified"), "Retained pre/post build source snapshot is stale.");
            }
        }
        Require(buildSeen.SetEquals(requiredBuild.Keys), "Transported fresh Release artifacts were reduced.");
        if (reverifyLocal)
        {
            Require(IsNativeHost(expected.RuntimeIdentifier), "Local report revalidation requires its actual native host.");
            P1SourcePackageEvidenceValidator.CandidateProvenance actual = await P1SourcePackageEvidenceValidator.ValidateCandidateInputsAsync(root, expected.CandidateSha,
                ArtifactPath(Obj(report, "sourceSnapshot")), ArtifactPath(Obj(report, "releaseBuild")), expected.RuntimeIdentifier, null, token).ConfigureAwait(false);
            Require(Str(actual.Snapshot, "treeSha") == expected.CandidateTreeSha, "Actual Git candidate tree differs from the report.");
        }
        foreach (JsonNode? node in Arr(report, "witnesses", 7, 7))
        {
            Budget(clock, token); JsonObject witness = node!.AsObject(); Fixture fixture = Fixtures.Single(f => f.Id == Str(witness, "fixtureId"));
            bool hand = Str(witness, "witnessKind") == "frozen-hand-authored-mir";
            foreach (string member in new[] { "registrationSource", "originalPe", "originalPdb", "runtimeConfig", "runtime" }) Retained(Obj(witness, member));
            Require(HashText(File.ReadAllText(ArtifactPath(Obj(witness, "registrationSource")))) == fixture.RegistrationSha256, "Retained original registration source is stale.");
            if (hand)
            {
                GeneratedAssembly generated = EmitFrozenHandMir(Str(witness, "assemblyName"), token);
                Require(Convert.ToHexString(SHA256.HashData(generated.PeImage)) == Artifact(Obj(witness, "originalPe")) && generated.PdbImage is not null &&
                    Convert.ToHexString(SHA256.HashData(generated.PdbImage)) == Artifact(Obj(witness, "originalPdb")), "Retained hand-authored MIR PE differs from frozen NestedProgram(1).");
            }
            else
            {
                Retained(Obj(witness, "source")); Require(File.ReadAllText(ArtifactPath(Obj(witness, "source"))) == fixture.Source, "Source witness does not match the exact frozen behavior.");
                RustSharpMetadataDocument metadata = RustSharpMetadataReader.ReadAssembly(ArtifactPath(Obj(witness, "originalPe")));
                Require(metadata.SourceSha256 == fixture.SourceSha256, "Original generated PE metadata does not bind its Rust source bytes.");
            }
            Require(Artifact(Obj(witness, "runtime")) == Artifact(Obj(Arr(report, "implementationInputs", 6, 6).Single(n => Str(n!.AsObject(), "name") == "RustSharp.Runtime.dll")!.AsObject(), "retainedAssembly")), "Witness runtime differs from its fresh implementation.");
            JsonObject il = Obj(witness, "ilVerify"); Retained(Obj(il, "report")); JsonObject raw = Parse(Read(ArtifactPath(Obj(il, "report")), 4194304), clock, token);
            JsonObject assembly = Obj(raw, "Assembly"), verification = Obj(raw, "Verification"), tool = Obj(raw, "Tool"), verifyProcess = Obj(raw, "VerifyProcess"), runtime = Obj(verification, "RuntimeReference");
            Require(Bool(raw, "Succeeded") && raw["Failure"] is null && Str(assembly, "Sha256") == Artifact(Obj(witness, "originalPe")) && Str(assembly, "Path") == Str(Obj(witness, "originalPe"), "path") &&
                Str(tool, "PackageId") == "dotnet-ilverify" && Str(tool, "Version") == "10.0.11" && Str(tool, "Command") == "ilverify" && !Bool(tool, "RestoreRequested"), "ILVerify raw input/tool binding is stale.");
            Require(Str(verification, "SystemModule") == "System.Private.CoreLib" && Str(verification, "RuntimeVersion") == Str(report, "runtimeVersion") &&
                Bool(runtime, "Declared") && Bool(runtime, "Validated") && Str(runtime, "Sha256") == Artifact(Obj(witness, "runtime")) && Str(runtime, "Sha256AfterVerification") == Str(runtime, "Sha256") &&
                Str(runtime, "ExpectedSha256") == Str(runtime, "Sha256"), "ILVerify actual runtime reference binding is incomplete.");
            Require(Arr(verification, "AdditionalReferencePath", 1, 1)[0]!.GetValue<string>() == Str(Obj(witness, "runtime"), "path") && Int(verifyProcess, "ExitCode") == 0 && Str(verifyProcess, "Termination") == "Exited" &&
                !Bool(verifyProcess, "StandardOutputTruncated") && !Bool(verifyProcess, "StandardErrorTruncated") && !Bool(verifyProcess, "OutputDrainTimedOut") && !Bool(verifyProcess, "ProcessTreeCleanupIncomplete"), "ILVerify nested execution is incomplete.");
            Require(Int(verifyProcess, "ProcessId") > 0 && Int(verifyProcess, "ParentProcessId") == Int(Obj(il, "process"), "pid") &&
                Str(verifyProcess, "CommandLine").Contains(Str(assembly, "Path"), StringComparison.Ordinal) && Str(verifyProcess, "CommandLine").Contains("--statistics", StringComparison.Ordinal), "ILVerify child does not bind the original PE.");
            JsonObject environment = Obj(raw, "Environment"); Require(Version.TryParse(Str(environment, "PowerShellVersion"), out Version? shellVersion) && shellVersion.Major >= 7 && Str(environment, "ProcessArchitecture") == "X64" &&
                HostMatches(Str(environment, "RuntimeIdentifier"), expected.RuntimeIdentifier), "ILVerify shell/native environment binding changed.");
            JsonObject aot = Obj(witness, "nativeAot"); Retained(Obj(aot, "executable")); NativeHeader(ArtifactPath(Obj(aot, "executable")), expected.RuntimeIdentifier);
            JsonArray hostInputs = Arr(aot, "retainedHostInputs", 4, 4); foreach (JsonNode? input in hostInputs) Retained(input!.AsObject());
            string HostInput(string name) => ArtifactPath(hostInputs.Single(n => Path.GetFileName(Str(n!.AsObject(), "path").Replace('\\', '/')) == name)!.AsObject());
            Require(FileHash(HostInput(Str(witness, "assemblyName") + ".dll")) == Artifact(Obj(witness, "originalPe")) && FileHash(HostInput("RustSharp.Runtime.dll")) == Artifact(Obj(witness, "runtime")), "Native publish used different generated PE/runtime bytes.");
            Require(File.ReadAllText(HostInput("Program.cs")) == NativeHostSource(hand), "Native host source simulates the expected behavior.");
            XDocument project = XDocument.Parse(File.ReadAllText(HostInput("RustSharp.NativeAotHost.csproj")));
            string[] references = project.Descendants("Reference").Select(n => n.Element("HintPath")?.Value ?? "").ToArray();
            Require(references.Length == 2 && references.ToHashSet(StringComparer.Ordinal).SetEquals([Str(witness, "assemblyName") + ".dll", "RustSharp.Runtime.dll"]) && project.Descendants("PublishAot").Single().Value == "true", "Native project does not reference the original generated assembly.");
            JsonObject publish = Obj(aot, "publish"), nativeRun = Obj(aot, "run");
            Require(Arr(publish, "arguments", 1, 32).Any(n => n!.GetValue<string>() == expected.RuntimeIdentifier) && Str(publish, "commandLine").Contains("-warnaserror", StringComparison.Ordinal) &&
                Str(nativeRun, "fileName") == Str(Obj(aot, "executable"), "path") && !Directory.Exists(Str(aot, "hostDirectory")), "Native target/run/cleanup binding is stale.");
        }
        Require(!Directory.Exists(Str(Obj(report, "cleanup"), "temporaryDirectory")), "Backend tooling temporary directory still exists.");
    }

    internal static string NativeHostSource(bool hand) => hand
        ? "using System; using System.Diagnostics.CodeAnalysis; internal static class Program { [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(global::RustSharp.Generated.Program))] private static void Main() => Console.WriteLine(global::RustSharp.Generated.Program.Main()); }"
        : "using System.Diagnostics.CodeAnalysis; internal static class Program { [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(global::RustSharp.Generated.Program))] private static void Main() => global::RustSharp.Generated.Program.Main(); }";

    // Kept structurally identical to the frozen registration's NestedProgram(1), including its dynamic place and indirect store.
    internal static GeneratedAssembly EmitFrozenHandMir(string assemblyName, CancellationToken token = default)
    {
        SafeCoreMirSource source = new("projection-backend.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreType integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32, token), usize = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize, token), boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool, token);
        SafeCoreType mutable = SafeCoreType.Reference(integer, true, token), array = SafeCoreType.Array(integer, 2, token), tuple = SafeCoreType.Tuple([boolean, array], token), adt = SafeCoreType.Adt("crate::Container", token);
        SafeCoreMirLocal Local(int id, SafeCoreType type, bool mut = false) => new(id, "value" + id, type, SafeCoreMirLocalKind.User, mut, source);
        SafeCoreMirOperand Value(int id, SafeCoreType type) => SafeCoreMirOperand.Local(id, type, source);
        SafeCoreMirOperand Int(int value) => SafeCoreMirOperand.Constant(integer, value.ToString(CultureInfo.InvariantCulture), source);
        SafeCoreMirStatement Assign(int id, SafeCoreMirRvalue value) => new(id, value, source);
        SafeCoreMirStatement Use(int id, SafeCoreMirOperand value) => Assign(id, SafeCoreMirRvalue.Use(value, source));
        SafeCoreMirOperand Place(int id, SafeCoreType type, params SafeCoreMirProjection[] projections) => SafeCoreMirOperand.PlaceValue(new(id, projections), type, source);
        SafeCoreMirOperand leaf = Place(2, integer, SafeCoreMirProjection.Field("payload"), SafeCoreMirProjection.TupleIndex(1), SafeCoreMirProjection.DynamicIndex(3));
        SafeCoreMirProgram program = new([new(0, "crate::main", integer,
            [Local(0, array), Local(1, tuple), Local(2, adt, true), Local(3, usize), Local(4, mutable)],
            [new(0, [Assign(0, SafeCoreMirRvalue.Array([Int(3), Int(7)], array, source, token)),
                Assign(1, SafeCoreMirRvalue.Tuple([SafeCoreMirOperand.Constant(boolean, "true", source), Value(0, array)], tuple, source, token)),
                Assign(2, SafeCoreMirRvalue.Adt([Value(1, tuple)], adt, source, token)), Use(3, SafeCoreMirOperand.Constant(usize, "1", source)),
                Assign(4, SafeCoreMirRvalue.Unary("&mut", leaf, mutable, source)), Use(4, Int(41)) with { DestinationPlace = new(4, [SafeCoreMirProjection.Dereference()]) }],
                SafeCoreMirTerminator.Return(Place(2, integer, SafeCoreMirProjection.Field("payload"), SafeCoreMirProjection.TupleIndex(1), SafeCoreMirProjection.ArrayIndex(1)), source), source)], 0, source)],
            [new(adt, [new("payload", tuple, source)], source)]);
        SafeCoreClrResult lowered = SafeCoreMirClrLowering.Lower(program, token);
        Require(lowered.IsSuccessful, "Frozen hand-authored MIR could not lower: " + string.Join("; ", lowered.Diagnostics.Select(n => n.Message)));
        return ClrLirAssemblyEmitter.EmitProgram(lowered, assemblyName, "x", source.SourcePath, assemblyName + ".pdb", cancellationToken: token);
    }

    internal static void NativeHeader(string path, string rid)
    {
        using FileStream stream = File.OpenRead(path); byte[] header = new byte[20]; stream.ReadExactly(header); stream.Position = 0;
        if (rid == "win-x64") { using var pe = new PEReader(stream); Require(pe.PEHeaders.CorHeader is null && pe.PEHeaders.CoffHeader.Machine == Machine.Amd64, "Native binary is not Windows AMD64."); }
        else Require(rid == "linux-x64" && header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' && header[4] == 2 && header[5] == 1 && header[18] == 62 && header[19] == 0, "Native binary is not Linux x64 ELF.");
    }
    internal static string CellId(Fixture fixture, string backend, string rid) => fixture.RequirementId + "|" + fixture.OwnerLeaf + "|" + fixture.CaseId + "|" + backend + "|" + rid;
    internal static bool IsNativeHost(string rid) => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 && (rid == "win-x64" ? OperatingSystem.IsWindows() : rid == "linux-x64" && OperatingSystem.IsLinux());
    internal static JsonObject Parse(ReadOnlySpan<byte> bytes, Stopwatch clock, CancellationToken token)
    {
        Require(bytes.Length is > 0 and <= MaximumReportBytes, "Backend report exceeds its byte bound."); var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 }); var names = new Stack<HashSet<string>>(); bool finished = false;
        for (int count = 0; count < 262144; count++)
        {
            Budget(clock, token); if (!reader.Read()) { finished = true; break; }
            if (reader.TokenType == JsonTokenType.StartObject) names.Push(new(StringComparer.OrdinalIgnoreCase)); else if (reader.TokenType == JsonTokenType.EndObject) names.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName) Require(names.Peek().Add(reader.GetString()!), "Duplicate backend JSON property.");
        }
        Require(finished, "Backend report exceeds its token bound."); return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 48 })?.AsObject() ?? throw new ArgumentException("Backend report root must be an object.");
    }
    internal static byte[] Read(string path, int maximum) { Require(File.Exists(path) && new FileInfo(path).Length is > 0 && new FileInfo(path).Length <= maximum, "Backend input is missing or oversized."); return File.ReadAllBytes(path); }
    internal static string FileHash(string path) { Require(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 134217728, "Backend artifact is missing or oversized."); using FileStream file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));
    internal static string Child(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), full = Path.GetFullPath(path, fullRoot); StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Require(full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison), "Backend path escaped its owned evidence directory.");
        string relative = Path.GetRelativePath(fullRoot, full), current = fullRoot;
        string[] parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries); Require(parts.Length <= 16, "Backend path exceeds its component bound.");
        foreach (string part in parts) { current = Path.Combine(current, part); if (File.Exists(current) || Directory.Exists(current)) Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Backend artifacts cannot use redirected paths."); }
        return full;
    }
    internal static JsonObject Obj(JsonObject value, string key) => value[key] as JsonObject ?? throw new ArgumentException("Backend object missing: " + key);
    internal static JsonArray Arr(JsonObject value, string key, int minimum, int maximum) { JsonArray array = value[key] as JsonArray ?? throw new ArgumentException("Backend array missing: " + key); Require(array.Count >= minimum && array.Count <= maximum, "Backend array denominator changed: " + key); return array; }
    internal static string Str(JsonObject value, string key) => value[key]?.GetValue<string>() ?? throw new ArgumentException("Backend string missing: " + key);
    internal static int Int(JsonObject value, string key) => value[key]?.GetValue<int>() ?? throw new ArgumentException("Backend integer missing: " + key);
    internal static bool Bool(JsonObject value, string key) => value[key]?.GetValue<bool>() ?? throw new ArgumentException("Backend boolean missing: " + key);
    private static double Number(JsonObject value, string key) { double number = value[key]?.GetValue<double>() ?? throw new ArgumentException("Backend number missing: " + key); Require(double.IsFinite(number), "Backend numeric evidence must be finite."); return number; }
    private static DateTimeOffset Date(JsonObject value, string key) => DateTimeOffset.Parse(Str(value, key), CultureInfo.InvariantCulture);
    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool HostMatches(string actual, string rid) => rid == "win-x64" ? actual == rid : rid == "linux-x64" && (actual == rid || actual.StartsWith("ubuntu", StringComparison.Ordinal) && actual.EndsWith("-x64", StringComparison.Ordinal));
    internal static bool Complete(BoundedProcessResult result) => result.Termination == BoundedProcessTermination.Exited && !result.ProcessTreeCleanupIncomplete && !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut && !result.OutputReadLimitReached;
    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
    internal static bool HasWarnings(string text) => Regex.IsMatch(text, @"\bwarning\s+[A-Z]+[0-9]+:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    internal static bool Failure(Exception exception) => exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or FormatException or System.ComponentModel.Win32Exception or BadImageFormatException or System.Xml.XmlException;
    private static string Artifact(JsonObject item) { string hash = Str(item, "sha256"); Require(hash.Length == 64 && hash.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(Str(item, "path")), "Backend artifact identity is invalid."); return hash; }
    private static void Process(JsonObject value, int maximumMilliseconds, bool success)
    {
        Require(Int(value, "pid") > 0 && Int(value, "parentPid") > 0 && Date(value, "startedAtUtc") > DateTimeOffset.UnixEpoch && Str(value, "termination") == "Exited" &&
            Number(value, "elapsedMilliseconds") is >= 0 && Number(value, "elapsedMilliseconds") <= maximumMilliseconds + 5000 && !string.IsNullOrWhiteSpace(Str(value, "commandLine")) && !string.IsNullOrWhiteSpace(Str(value, "workingDirectory")), "Backend process identity/deadline is incomplete.");
        foreach (string flag in new[] { "outputTruncated", "outputReadTimedOut", "outputDrainTimedOut", "outputReadLimitReached", "cleanupIncomplete" }) Require(!Bool(value, flag), "Backend process output/cleanup incomplete.");
        _ = Bool(value, "cleanupAttempted"); // Normally exited, fully drained processes need no forced termination.
        Require(!success || Int(value, "exitCode") == 0, "Backend process failed.");
    }
    internal static void Budget(Stopwatch clock, CancellationToken token) { token.ThrowIfCancellationRequested(); Require(clock.Elapsed < ValidationTimeout, "Backend validation deadline expired."); }
    internal static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
}
