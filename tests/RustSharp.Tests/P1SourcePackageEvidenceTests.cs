using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1SourcePackageEvidenceTests
{
    private const string Candidate = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static byte[] ManifestBytes => File.ReadAllBytes(Path.Combine(Root, "tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json"));
    private static P1SourcePackageEvidenceValidator.Expectation Expected => new(Candidate, Tree, "win-x64");
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-GATE.03 source-package evidence freezes 19 cases and 39 original PE verifications", FrozenAsync),
        new("P1-GATE.03 synthetic structure cannot close an artifact-content gate", SyntheticAsync),
        new("P1-GATE.03 rejects legacy reports and stale candidate tree bindings", CandidateAsync),
        new("P1-GATE.03 rejects missing duplicate and invented source-package rows", RowsAsync),
        new("P1-GATE.03 rejects source expectation and Drop policy substitutions", SourceAsync),
        new("P1-GATE.03 rejects independent original PE PDB and metadata substitutions", BuildsAsync),
        new("P1-GATE.03 rejects missing stale and replaced original PE ILVerify graphs", VerifierAsync),
        new("P1-GATE.03 rejects simulated CoreCLR and native outcome traces", OutcomesAsync),
        new("P1-GATE.03 rejects native host input graph RID and warning substitutions", NativeAsync),
        new("P1-GATE.03 rejects absent duplicate and truncated process lifecycles", ProcessesAsync),
        new("P1-GATE.03 rejects partial summaries expired deadlines and incomplete cleanup", CleanupAsync),
        new("P1-GATE.03 bounds strict JSON cancellation and absent evidence files", BoundsAsync),
    ];

    private static Task FrozenAsync()
    {
        JsonObject manifest = P1SourcePackageEvidenceValidator.ValidateFrozenManifest(ManifestBytes);
        AssertEx.Equal(19, manifest["cases"]!.AsArray().Count);
        AssertEx.Equal(39, manifest["cases"]!.AsArray().Sum(node => node!["wrapperSource"] is null ? 2 : 3));
        AssertEx.Equal(19, P1SourcePackageEvidenceValidator.FrozenIds.Distinct(StringComparer.Ordinal).Count());
        byte[] changed = [.. ManifestBytes, (byte)' '];
        AssertEx.Equal("The frozen 19-case source-package manifest hash changed.", AssertEx.Throws<ArgumentException>(() => P1SourcePackageEvidenceValidator.ValidateFrozenManifest(changed)).Message);
        return Task.CompletedTask;
    }
    private static Task SyntheticAsync()
    {
        P1SourcePackageEvidenceValidator.ValidationResult value = Validate(Fixture());
        AssertEx.True(value.Valid, string.Join("; ", value.Errors));
        AssertEx.False(value.ArtifactContentVerified || value.SatisfiesGate, "A synthetic unit-test structure cannot claim actual source-package executions.");
        return Task.CompletedTask;
    }
    private static Task CandidateAsync() => RejectAsync(
    [
        (node => node["schemaVersion"] = 1, "Source-package closure requires candidate-bound schema 2."),
        (node => node["candidateSha"] = new string('3', 40), "Source-package candidate/tree binding is stale."),
        (node => node["candidateTreeSha"] = new string('3', 40), "Source-package candidate/tree binding is stale."),
        (node => node.Remove("sourceSnapshot"), "Source-package object is missing: sourceSnapshot"),
        (node => node["releaseBuild"]!["raw"]!["candidateSha"] = new string('3', 40), "Source-package fresh Release build is missing or stale."),
        (node => node["releaseBuild"]!["raw"]!["sourceProvenance"]!["candidateMatchesWorkingTree"] = false, "Release build is not bound to actual candidate source inputs."),
    ]);
    private static Task RowsAsync() => RejectAsync(
    [
        (node => node["cases"]!.AsArray().RemoveAt(0), "Source-package array denominator changed: cases"),
        (node => node["cases"]![1]!["id"] = node["cases"]![0]!["id"]!.GetValue<string>(), "Source-package case IDs/status must preserve all 19 frozen rows."),
        (node => node["cases"]![0]!["id"] = "invented-case", "Source-package case IDs/status must preserve all 19 frozen rows."),
        (node => node["cases"]![0]!["status"] = "not-executed", "Source-package case IDs/status must preserve all 19 frozen rows."),
    ]);
    private static Task SourceAsync() => RejectAsync(
    [
        (node => node["cases"]![0]!["sourceProducer"]!["sha256"] = Hash("wrong-source"), "Source-package frozen source binding is stale."),
        (node => node["cases"]![0]!["expectedOutput"] = "simulated\n", "Source-package case profile or expectation changed."),
        (node => node["cases"]![1]!["dropCleanupProfile"] = "legacy-v1", "Source-package case panic or Drop policy changed."),
        (node => node["cases"]![0]!["dropCleanupProfile"] = "native-v2", "Source-package case panic or Drop policy changed."),
        (node => node["cases"]![6]!["producerPanicStrategy"] = "unwind", "Source-package case panic or Drop policy changed."),
    ]);
    private static Task BuildsAsync() => RejectAsync(
    [
        (node => node["cases"]![0]!["independentBuilds"]!.AsArray().RemoveAt(1), "Source-package array denominator changed: independentBuilds"),
        (node => node["cases"]![0]!["independentArtifactsIdentical"] = false, "Source-package independent artifacts are not identical."),
        (node => node["cases"]![0]!["independentBuilds"]![1]!["producer"]!["originalPdb"]!["sha256"] = Hash("wrong-pdb"), "Source-package original PE/PDB independent-build binding changed."),
        (node => node["cases"]![0]!["independentBuilds"]![0]!["sourceHashesReconciled"] = false, "Source-package compilation/source reconciliation failed."),
        (node => node["cases"]![0]!["originalProducer"]!["sha256"] = Hash("wrong-pe"), "Source-package retained original PE differs from build zero."),
    ]);
    private static Task VerifierAsync() => RejectAsync(
    [
        (node => node["cases"]![0]!["producerIlVerify"]!["rawEvidence"]!["Assembly"]!["Sha256"] = Hash("wrong-pe"), "Original PE ILVerify raw binding is stale."),
        (node => node["cases"]![0]!["producerIlVerify"]!["additionalReferenceArtifacts"]!.AsArray().RemoveAt(0), "Source-package array denominator changed: additionalReferenceArtifacts"),
        (node => node["cases"]![0]!["producerIlVerify"]!["verificationReferenceArtifacts"]!.AsArray().RemoveAt(0), "Source-package array denominator changed: verificationReferenceArtifacts"),
        (node => node["cases"]![0]!["producerIlVerify"]!["rawEvidence"]!["Verification"]!["RuntimeReference"]!["Sha256AfterVerification"] = Hash("wrong-runtime"), "Original PE ILVerify runtime proof is stale."),
        (node => node["cases"]![0]!.AsObject().Remove("consumerIlVerify"), "Source-package object is missing: consumerIlVerify"),
    ]);
    private static Task OutcomesAsync() => RejectAsync(
    [
        (node => MutateProcess(node, node["cases"]![0]!["coreClr"]!.AsObject(), value => value["stdout"] = "wrong\n"), "Source-package CoreCLR/native output or outcome differs from its frozen expectation."),
        (node => MutateProcess(node, node["cases"]![6]!["nativeRun"]!.AsObject(), value => value["exitCode"] = 0), "Source-package CoreCLR/native output or outcome differs from its frozen expectation."),
        (node => MutateProcess(node, node["cases"]![15]!["coreClr"]!.AsObject(), value => value["stderr"] = "OverflowException"), "Source-package CoreCLR/native output or outcome differs from its frozen expectation."),
    ]);
    private static Task NativeAsync() => RejectAsync(
    [
        (node => node["cases"]![0]!["nativeInputs"]!.AsArray().RemoveAt(0), "Source-package array denominator changed: nativeInputs"),
        (node => node["cases"]![0]!["nativeInputs"]![0]!["name"] = "fake.csproj", "Source-package native input graph changed."),
        (node => MutateProcess(node, node["cases"]![0]!["nativePublish"]!.AsObject(), value => value["commandLine"] = value["commandLine"]!.GetValue<string>().Replace("-r win-x64", "-r linux-x64", StringComparison.Ordinal)), "Source-package native publish is incomplete, warned, or has the wrong RID."),
        (node => MutateProcess(node, node["cases"]![0]!["nativePublish"]!.AsObject(), value => value["stdout"] = "warning IL2026: changed"), "Source-package native publish is incomplete, warned, or has the wrong RID."),
    ]);
    private static Task ProcessesAsync() => RejectAsync(
    [
        (node => node["processResults"]!.AsArray().RemoveAt(0), "Source-package array denominator changed: processResults"),
        (node => node["processResults"]![1] = node["processResults"]![0]!.DeepClone(), "Source-package process result identity is duplicated."),
        (node => node["processStarts"]![0]!["commandLine"] = "fake process", "Source-package process start/result lifecycle is incomplete or duplicated."),
        (node => MutateProcess(node, node["cases"]![0]!["coreClr"]!.AsObject(), value => value["outputTruncated"] = true), "Source-package owned process output or cleanup is incomplete."),
    ]);
    private static Task CleanupAsync() => RejectAsync(
    [
        (node => node["summary"]!["passed"] = 18, "Source-package summary must close all 19 frozen cases."),
        (node => node["execution"]!["deadlineExpired"] = true, "Source-package execution deadline is incomplete or expired."),
        (node => node["cleanup"]!["completed"] = false, "Source-package process or temporary cleanup is incomplete."),
        (node => node["cases"]![0]!["hostCleanup"]!["existsAfterCleanup"] = true, "Source-package native host cleanup is incomplete."),
        (node => node["retainedArtifacts"]!.AsArray().Add(node["retainedArtifacts"]![0]!.DeepClone()), "Retained source-package artifact identity is duplicated."),
    ]);
    private static async Task BoundsAsync()
    {
        byte[] json = Encoding.UTF8.GetBytes(Fixture().ToJsonString());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.Equal("Source-package validation exceeded its deadline or was cancelled.", P1SourcePackageEvidenceValidator.Validate(json, ManifestBytes, Expected, cancelled.Token).Errors.Single());
        byte[] duplicate = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"SchemaVersion\":2}");
        AssertEx.Equal("Duplicate source-package JSON properties are forbidden.", P1SourcePackageEvidenceValidator.Validate(duplicate, ManifestBytes, Expected).Errors.Single());
        string absent = Path.Combine(Root, "artifacts/p1-source-package/absent-unit-evidence-" + Guid.NewGuid().ToString("N") + ".json");
        P1SourcePackageEvidenceValidator.ValidationResult result = await P1SourcePackageEvidenceValidator.ValidateFileAsync(Root, absent, Expected).ConfigureAwait(false);
        AssertEx.Equal("Source-package evidence report does not exist.", result.Errors.Single());
        AssertEx.False(result.SatisfiesGate, "An absent report must never close P1-GATE.03.");
    }
    private static P1SourcePackageEvidenceValidator.ValidationResult Validate(JsonObject node) => P1SourcePackageEvidenceValidator.Validate(Encoding.UTF8.GetBytes(node.ToJsonString()), ManifestBytes, Expected);
    private static Task RejectAsync((Action<JsonObject> Mutation, string Reason)[] mutations)
    {
        AssertEx.True(mutations.Length is > 0 and <= 8, "Negative unit matrix must remain bounded.");
        JsonObject baseline = Fixture(); AssertEx.True(Validate(baseline).Valid, string.Join("; ", Validate(baseline).Errors));
        var clock = Stopwatch.StartNew();
        foreach ((Action<JsonObject> mutation, string reason) in mutations)
        {
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Negative matrix exceeded its time bound.");
            JsonObject node = baseline.DeepClone().AsObject(); mutation(node);
            P1SourcePackageEvidenceValidator.ValidationResult result = Validate(node);
            AssertEx.False(result.Valid || result.SatisfiesGate, "A rejected unit mutation cannot close the gate.");
            AssertEx.Equal(reason, result.Errors.Single());
        }
        return Task.CompletedTask;
    }
    private static void MutateProcess(JsonObject root, JsonObject process, Action<JsonObject> mutation)
    {
        int pid = process["pid"]!.GetValue<int>(); mutation(process);
        root["processResults"]!.AsArray().Single(node => node!["pid"]!.GetValue<int>() == pid)!.AsObject().Clear();
        JsonObject observed = root["processResults"]!.AsArray().Single(node => node is JsonObject { Count: 0 })!.AsObject();
        foreach ((string key, JsonNode? value) in process) observed[key] = value?.DeepClone();
        JsonObject start = root["processStarts"]!.AsArray().Single(node => node!["pid"]!.GetValue<int>() == pid)!.AsObject();
        start["commandLine"] = process["commandLine"]!.DeepClone();
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Deliberately synthetic: these bytes exercise rejection semantics, and ArtifactContentVerified is always false.
    private static JsonObject Fixture()
    {
        var clock = Stopwatch.StartNew();
        JsonObject manifest = P1SourcePackageEvidenceValidator.ValidateFrozenManifest(ManifestBytes);
        string basePath = Path.Combine(Root, "artifacts/p1-source-package/unit-synthetic");
        var retained = new JsonArray(); var index = new HashSet<string>(StringComparer.Ordinal);
        JsonObject Artifact(string path, string? hash = null, bool keep = true)
        {
            var value = new JsonObject { ["path"] = path, ["sha256"] = hash ?? Hash(path) };
            if (keep && index.Add(path)) retained.Add(value.DeepClone()); return value;
        }
        var starts = new JsonArray(); var processes = new JsonArray(); int pid = 1000;
        JsonObject Process(string command, string stdout = "", string stderr = "", int exit = 0)
        {
            var value = new JsonObject { ["pid"] = pid++, ["parentPid"] = 900, ["startedAtUtc"] = "2026-10-07T17:00:00Z", ["commandLine"] = command,
                ["workingDirectory"] = basePath, ["exitCode"] = exit, ["termination"] = "Exited", ["elapsedMilliseconds"] = 10,
                ["stdout"] = stdout, ["stderr"] = stderr, ["outputTruncated"] = false, ["outputReadTimedOut"] = false,
                ["outputDrainTimedOut"] = false, ["outputReadLimitReached"] = false, ["cleanupAttempted"] = false, ["cleanupIncomplete"] = false, ["cleanupDiagnostic"] = null };
            processes.Add(value.DeepClone()); starts.Add((JsonNode)new JsonObject { ["pid"] = value["pid"]!.DeepClone(), ["parentPid"] = 900,
                ["startedAtUtc"] = "2026-10-07T17:00:00Z", ["commandLine"] = command }); return value;
        }
        string runtimeHash = Hash("runtime");
        string[] names = ["RustSharp.Compiler.dll", "RustSharp.Conformance.dll", "RustSharp.Runtime.dll", "RustSharp.Semantics.dll", "RustSharp.CodeGen.IL.dll", "RustSharp.Syntax.dll"];
        var implementations = new JsonArray(); var releaseOutputs = new JsonArray();
        foreach (string name in names)
        {
            string hash = name == "RustSharp.Runtime.dll" ? runtimeHash : Hash(name);
            JsonObject output = Artifact("tests/RustSharp.Tests/bin/Release/net10.0/" + name, hash, false); releaseOutputs.Add(output.DeepClone());
            implementations.Add((JsonNode)new JsonObject { ["name"] = name, ["loadedAssembly"] = Artifact(Path.Combine(basePath, "loaded", name), hash, false),
                ["releaseOutput"] = output, ["retainedAssembly"] = Artifact(Path.Combine(basePath, name), hash) });
        }
        string[] ids = Enumerable.Range(0, 464).Select(item => "synthetic registration " + item).ToArray();
        JsonObject sourceProvenance = new() { ["candidateSha"] = Candidate, ["candidateTreeSha"] = Tree, ["candidateMatchesWorkingTree"] = true, ["checkedFileCount"] = 1, ["errors"] = new JsonArray() };
        JsonObject build = new() { ["schemaVersion"] = 1, ["evidenceKind"] = "p1-release-build", ["candidateSha"] = Candidate, ["configuration"] = "Release", ["succeeded"] = true,
            ["sourceProvenance"] = sourceProvenance, ["sourceSnapshots"] = new JsonArray(Artifact("before.json", keep: false), Artifact("after.json", keep: false)), ["implementationAssemblies"] = releaseOutputs,
            ["testsAssemblySha256"] = Hash("tests"), ["sdkVersion"] = "10.0.401", ["summary"] = new JsonObject { ["warnings"] = 0, ["errors"] = 0 },
            ["registrationInventory"] = new JsonObject { ["schemaVersion"] = 2, ["evidenceKind"] = "p1-regression-registration-inventory", ["buildConfiguration"] = "Release", ["runtimeIdentifier"] = "win-x64",
                ["assemblySha256"] = Hash("tests"), ["registeredDenominator"] = 464, ["registeredIds"] = new JsonArray(ids.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()), ["registeredIdsSha256"] = Hash(string.Join('\n', ids)) } };
        JsonObject snapshot = new() { ["schemaVersion"] = 1, ["evidenceKind"] = "p1-candidate-source-snapshot", ["candidateSha"] = Candidate, ["treeSha"] = Tree, ["verified"] = true };
        var report = new JsonObject { ["schemaVersion"] = 2, ["evidenceKind"] = "p1-source-package-original-pe-coreclr-ilverify-native-aot", ["profile"] = "p1-source-packages-v1", ["candidateSha"] = Candidate, ["candidateTreeSha"] = Tree,
            ["targetRuntimeIdentifier"] = "win-x64", ["hostRuntimeIdentifier"] = "win-x64", ["processArchitecture"] = "X64", ["runtimeVersion"] = "10.0.12", ["manifestSha256"] = P1SourcePackageEvidenceValidator.FrozenManifestSha256,
            ["retainedManifest"] = Artifact(Path.Combine(basePath, "manifest.json"), P1SourcePackageEvidenceValidator.FrozenManifestSha256),
            ["sourceSnapshot"] = new JsonObject { ["artifact"] = Artifact(Path.Combine(basePath, "snapshot.json")), ["raw"] = snapshot },
            ["releaseBuild"] = new JsonObject { ["artifact"] = Artifact(Path.Combine(basePath, "build.json")), ["raw"] = build }, ["implementationInputs"] = implementations,
            ["compiler"] = implementations[0]!["loadedAssembly"]!.DeepClone(), ["runner"] = implementations[1]!["loadedAssembly"]!.DeepClone(), ["runtime"] = implementations[2]!["loadedAssembly"]!.DeepClone(),
            ["candidateValidation"] = Process("pwsh validate.ps1"), ["tools"] = new JsonObject { ["sdkVersion"] = "10.0.401", ["dotnet"] = Process("dotnet --info", "SDK 10.0.401"), ["powershell"] = Process("pwsh --version", "7.6.6"), ["ilVerifyVersion"] = Process("dotnet ilverify --version", "10.0.11") },
            ["summary"] = new JsonObject { ["status"] = "passed", ["denominator"] = 19, ["passed"] = 19, ["failed"] = 0, ["blocked"] = 0, ["notExecuted"] = 0, ["maximumSelectedCases"] = 19, ["succeeded"] = true },
            ["execution"] = new JsonObject { ["startedAtUtc"] = "2026-10-07T17:00:00Z", ["finishedAtUtc"] = "2026-10-07T17:01:00Z", ["elapsedMilliseconds"] = 60000, ["suiteTimeoutSeconds"] = 1200, ["processTimeoutSeconds"] = 180, ["publishTimeoutSeconds"] = 600, ["deadlineMet"] = true, ["deadlineExpired"] = false },
            ["cleanup"] = new JsonObject { ["completed"] = true, ["temporaryDirectoryExists"] = false, ["temporaryDirectory"] = Path.Combine(basePath, "temp"), ["retainedEvidenceDirectory"] = basePath, ["diagnostic"] = null }, ["harnessError"] = null };
        var cases = new JsonArray();
        foreach (JsonNode? node in manifest["cases"]!.AsArray())
        {
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Synthetic fixture generation exceeded its deadline.");
            JsonObject frozen = node!.AsObject(); string id = frozen["id"]!.GetValue<string>(); string casePath = Path.Combine(basePath, id);
            string[] roles = frozen["wrapperSource"] is null ? ["producer", "consumer"] : ["producer", "consumer", "wrapper"];
            var actual = new JsonObject { ["id"] = id, ["status"] = "passed", ["compilerProfile"] = frozen["compilerProfile"]!.DeepClone(), ["expectedOutput"] = frozen["expectedOutput"]!.DeepClone(), ["expectedOutcome"] = frozen["expectedOutcome"]!.DeepClone(),
                ["producerPanicStrategy"] = frozen["producerPanicStrategy"]?.DeepClone() ?? JsonValue.Create("unwind"), ["dropCleanupProfile"] = frozen["compilerProfile"]!.GetValue<string>() == "safe-core-mir-p1-v2" ? "native-v2" : "legacy-v1", ["independentArtifactsIdentical"] = true };
            var builds = new JsonArray();
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var emitted = new JsonObject { ["sourceHashesReconciled"] = true };
                foreach (string role in roles)
                {
                    string cap = char.ToUpperInvariant(role[0]) + role[1..], assembly = frozen[role + "Assembly"]!.GetValue<string>();
                    string hash = Hash(id + role), pdb = Hash(id + role + "pdb"), temp = Path.Combine(casePath, "temp-build-" + iteration, assembly + ".dll");
                    emitted[role] = new JsonObject { ["success"] = true, ["diagnostics"] = new JsonArray(), ["originalAssembly"] = Artifact(temp, hash, false), ["originalPdb"] = Artifact(Path.ChangeExtension(temp, ".pdb"), pdb, false) };
                    emitted["retainedOriginal" + cap] = Artifact(Path.Combine(casePath, "build-" + iteration, assembly + ".dll"), hash);
                    emitted["retainedOriginal" + cap + "Pdb"] = Artifact(Path.Combine(casePath, "build-" + iteration, assembly + ".pdb"), pdb);
                    emitted[role + "Metadata"] = Artifact(Path.Combine(casePath, role + "-metadata-" + iteration + ".json"));
                    actual["source" + cap] = Artifact(Path.Combine(Root, frozen[role + "Source"]!.GetValue<string>()), frozen[role + "Sha256"]!.GetValue<string>(), false);
                    actual["original" + cap] = Artifact(Path.Combine(casePath, assembly + ".dll"), hash);
                }
                builds.Add((JsonNode)emitted);
            }
            actual["independentBuilds"] = builds;
            foreach (string role in roles)
            {
                JsonObject original = builds[0]![role]!["originalAssembly"]!.AsObject();
                JsonObject[] references = [.. roles.Where(other => other != role).Select(other => builds[0]![other]!["originalAssembly"]!.DeepClone().AsObject()), Artifact(Path.Combine(casePath, "temp-build-0", "RustSharp.Runtime.dll"), runtimeHash, false)];
                JsonObject process = Process("pwsh verify " + original["path"]!.GetValue<string>());
                string command = "dotnet ilverify -s System.Private.CoreLib --statistics " + original["path"]!.GetValue<string>();
                var nested = new JsonObject { ["ProcessId"] = 5000 + process["pid"]!.GetValue<int>(), ["ParentProcessId"] = process["pid"]!.DeepClone(), ["StartedAt"] = "2026-10-07T17:00:00Z", ["CommandLine"] = command, ["ElapsedMilliseconds"] = 1,
                    ["StandardOutput"] = "All Classes and Methods in unit Verified.", ["StandardError"] = "",
                    ["ExitCode"] = 0, ["Termination"] = "Exited", ["StandardOutputTruncated"] = false, ["StandardErrorTruncated"] = false, ["OutputDrainTimedOut"] = false, ["ProcessTreeCleanupIncomplete"] = false };
                actual[role + "IlVerify"] = new JsonObject { ["status"] = "passed", ["originalPeAndReferenceBindingMatches"] = true, ["originalAssembly"] = original.DeepClone(), ["process"] = process,
                    ["additionalReferenceArtifacts"] = new JsonArray(references.Select(item => item.DeepClone()).ToArray()), ["verificationReferenceArtifacts"] = new JsonArray(references.Select(item => item.DeepClone()).ToArray()),
                    ["report"] = Artifact(Path.Combine(casePath, role + "-verify.json")), ["rawEvidence"] = new JsonObject { ["Succeeded"] = true, ["Assembly"] = new JsonObject { ["Path"] = original["path"]!.DeepClone(), ["Sha256"] = original["sha256"]!.DeepClone() },
                        ["Tool"] = new JsonObject { ["PackageId"] = "dotnet-ilverify", ["Version"] = "10.0.11" }, ["VerifyProcess"] = nested, ["ProcessRecords"] = new JsonArray(nested.DeepClone()),
                        ["Verification"] = new JsonObject { ["SystemModule"] = "System.Private.CoreLib", ["RuntimeVersion"] = "10.0.12", ["ReferenceFiles"] = new JsonArray(references.Select(item => item["path"]!.DeepClone()).ToArray()), ["AdditionalReferencePath"] = new JsonArray(references.Select(item => item["path"]!.DeepClone()).ToArray()),
                            ["RuntimeReference"] = new JsonObject { ["Validated"] = true, ["Sha256"] = runtimeHash, ["Sha256AfterVerification"] = runtimeHash } } } };
            }
            string outcome = frozen["expectedOutcome"]!.GetValue<string>(), output = frozen["expectedOutput"]!.GetValue<string>();
            string error = outcome switch { "unwound" => "OverflowException", "aborted" => "RustSharp panic abort: unit", "double-panic" => "RustSharp double panic abort: unit", _ => "" };
            int exit = outcome == "success" ? 0 : outcome == "unwound" ? 1 : 134;
            actual["coreClr"] = Process("dotnet " + builds[0]!["consumer"]!["originalAssembly"]!["path"]!.GetValue<string>(), output, error, exit);
            actual["nativePublish"] = Process("dotnet publish unit.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -warnaserror"); actual["nativeWarningCount"] = 0;
            string executable = frozen["consumerAssembly"]!.GetValue<string>() + ".NativeAotHost.exe";
            actual["nativeRun"] = Process(Path.Combine(casePath, "native", executable), output, error, exit); actual["nativeExecutable"] = Artifact(Path.Combine(casePath, executable));
            actual["nativeHostSource"] = Artifact(Path.Combine(casePath, "native-host.cs"));
            actual["nativeAdditionalAssemblies"] = new JsonArray(roles.Where(role => role != "consumer").Select(role => actual["original" + char.ToUpperInvariant(role[0]) + role[1..]]!.DeepClone()).ToArray());
            actual["hostCleanup"] = new JsonObject { ["attempted"] = true, ["incomplete"] = false, ["existsAfterCleanup"] = false, ["diagnostic"] = null, ["directory"] = Path.Combine(casePath, "host-temp") };
            string[] inputNames = ["RustSharp.NativeAotHost.csproj", "Program.cs", "RustSharp.Runtime.dll", .. roles.Select(role => frozen[role + "Assembly"]!.GetValue<string>() + ".dll")];
            actual["nativeInputs"] = new JsonArray(inputNames.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["originalPath"] = Path.Combine(casePath, "host-temp", name), ["retained"] = Artifact(Path.Combine(casePath, "native-inputs", name)) }).ToArray());
            cases.Add((JsonNode)actual);
        }
        report["cases"] = cases; report["processStarts"] = starts; report["processResults"] = processes; report["retainedArtifacts"] = retained;
        return report;
    }
}
