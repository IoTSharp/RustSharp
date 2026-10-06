using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RustSharp.Compiler;
using RustSharp.Conformance;

namespace RustSharp.Tests;

// Contract fixtures are synthesized in memory. They do not supply native
// execution evidence and cannot be used to close the platform or P1 gates.
internal static class P1PlatformBindingContractTests
{
    private const string RustcVersion = "rustc 1.98.0 (88d9e12ae 2026-08-18)";
    private static readonly string ManifestHash = new('a', 64);
    private static readonly string CompilerHash = new('b', 64);
    private static readonly string SourceHash = new('c', 64);
    private static readonly string ExpectationHash = new('d', 64);
    private static readonly string AssemblyHash = new('e', 64);
    private static readonly P1EvidenceBindingValidator.BindingExpectation Expected = new(
        "p1-platform-coreclr-ilverify-native-aot", P1ExpandedPlatformRunner.ProfileName, "win-x64", 1, ManifestHash, CompilerHash,
        CaseBindings: [new("drop-return-order", "drop.rs", SourceHash, ExpectationHash, "body\ndrop\n")]);

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 platform binding accepts a synthetic execution contract without semantic closure", AcceptsSyntheticContractAsync),
        new("P1 platform binding rejects untrusted identity source and expectation hashes", RejectsIdentityMutationAsync),
        new("P1 platform binding requires trusted frozen case bindings", RequiresTrustedInventoryAsync),
        new("P1 platform binding rejects forged backend success and incomplete process output", RejectsProcessMutationAsync),
        new("P1 platform binding compares captured output with trusted output", RejectsOutputMutationAsync),
        new("P1 platform binding rejects stale ILVerify JSON and artifact hashes", RejectsArtifactMutationAsync),
        new("P1 platform binding rejects false host tool and execution provenance", RejectsProvenanceMutationAsync),
        new("P1 platform binding rejects non-object roots duplicate properties and cancellation", RejectsMalformedJsonAsync),
    ];

    private static Task AcceptsSyntheticContractAsync()
    {
        JsonObject report = Report();
        P1EvidenceBindingValidator.ValidationResult result = P1EvidenceBindingValidator.Validate(report.ToJsonString(), Expected);
        AssertEx.True(result.Valid, string.Join("; ", result.Errors));
        AssertEx.False(report["semanticClosureEligible"]!.GetValue<bool>(), "A complete execution contract cannot close semantic coverage.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Stopwatch clock = Stopwatch.StartNew();
        var ubuntuExpected = Expected with { RuntimeIdentifier = "linux-x64", ObservedRuntimeIdentifier = "ubuntu.24.04-x64" };
        JsonObject ubuntu = Report();
        ubuntu["platform"]!["name"] = "linux-x64";
        ubuntu["platform"]!["runtimeIdentifier"] = "linux-x64";
        ubuntu["platform"]!["observedRuntimeIdentifier"] = "ubuntu.24.04-x64";
        P1EvidenceBindingValidator.ValidationResult native = P1EvidenceBindingValidator.Validate(ubuntu.ToJsonString(), ubuntuExpected, deadline.Token);
        AssertEx.True(native.Valid, "A trusted Ubuntu native host must accept its distinct linux-x64 target: " + string.Join("; ", native.Errors));
        AssertEx.False(P1EvidenceBindingValidator.Validate(ubuntu.ToJsonString(), ubuntuExpected with { ObservedRuntimeIdentifier = null }, deadline.Token).Valid,
            "Omitting the independently supplied host preserves the existing exact target fallback.");
        (string Field, string Value)[] mutations = [("observedRuntimeIdentifier", "linux-x64"), ("observedRuntimeIdentifier", "win-x64"), ("runtimeIdentifier", "win-x64")];
        for (int index = 0; index < mutations.Length && index < 3; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Ubuntu host/target controls exceeded their three-input, five-second bound.");
            JsonObject changed = ubuntu.DeepClone().AsObject();
            changed["platform"]![mutations[index].Field] = mutations[index].Value;
            P1EvidenceBindingValidator.ValidationResult rejected = P1EvidenceBindingValidator.Validate(changed.ToJsonString(), ubuntuExpected, deadline.Token);
            AssertEx.False(rejected.Valid, "Native host and target identifiers must each match the independent expectation exactly.");
            AssertEx.True(rejected.Errors.Any(error => error.StartsWith("platform." + mutations[index].Field + " does not match", StringComparison.Ordinal)),
                "The mutated host or target field must cause its own binding rejection.");
        }
        return Task.CompletedTask;
    }

    private static Task RejectsIdentityMutationAsync() => RejectMutations(
    [
        report => report["cases"]![0]!["id"] = "unknown-case",
        report => report["cases"]![0]!["source"] = "other.rs",
        report => report["cases"]![0]!["sourceSha256"] = new string('f', 64),
        report => report["cases"]![0]!["expectationSha256"] = new string('f', 64),
    ]);

    private static Task RequiresTrustedInventoryAsync()
    {
        string json = Report().ToJsonString();
        AssertEx.False(P1EvidenceBindingValidator.Validate(json, Expected with { CaseBindings = null }).Valid, "A self-declared case inventory is not trusted.");
        AssertEx.False(P1EvidenceBindingValidator.Validate(json, Expected with { CaseBindings = [] }).Valid, "An empty binding table cannot close the denominator.");
        return Task.CompletedTask;
    }

    private static Task RejectsProcessMutationAsync() => RejectMutations(
    [
        report => report["cases"]![0]!.AsObject().Remove("coreClrRun"),
        report => report["cases"]![0]!["coreClrCompile"]!["exitCode"] = 1,
        report => report["cases"]![0]!["coreClrRun"]!["termination"] = "timedout",
        report => report["cases"]![0]!["coreClrRun"]!["outputTruncated"] = true,
        report => report["cases"]![0]!["ilVerify"]!["process"]!["outputReadTimedOut"] = true,
        report => report["cases"]![0]!["nativeAot"]!["publish"]!["cleanupIncomplete"] = true,
        report => report["cases"]![0]!["nativeAot"]!["run"]!["outputDrainTimedOut"] = true,
        report => report["cases"]![0]!["nativeAot"]!["run"]!["outputReadLimitReached"] = true,
        report => report["cases"]![0]!["nativeAot"]!["hostCleanupIncomplete"] = true,
        report => report["cases"]![0]!["coreClrRun"]!["processId"] = "forged",
    ]);

    private static Task RejectsOutputMutationAsync() => RejectMutations(
    [
        report => report["cases"]![0]!["coreClrRun"]!["standardOutput"] = "drop\nbody\n",
        report => report["cases"]![0]!["nativeAot"]!["run"]!["standardOutput"] = "body\ndrop\nextra\n",
        report => report["cases"]![0]!["coreClrRun"]!["outputMatches"] = false,
    ]);

    private static Task RejectsArtifactMutationAsync() => RejectMutations(
    [
        report => report["cases"]![0]!["ilVerify"]!["evidenceSha256"] = new string('f', 64),
        report => report["cases"]![0]!["assemblySha256"] = new string('f', 64),
        report => report["cases"]![0]!["nativeAot"]!["executableSha256"] = "",
        report => SetIlVerifyJson(report, "{\"Succeeded\":true,\"Succeeded\":false}"),
        report => SetIlVerifyJson(report, "[]"),
        report => MutateIlVerify(report, evidence => evidence["VerifyProcess"]!["ExitCode"] = 1),
        report => MutateIlVerify(report, evidence => evidence["VerifyProcess"]!["ParentProcessId"] = 99),
        report => MutateIlVerify(report, evidence => evidence["Tool"]!["Version"] = "10.0.10"),
    ]);

    private static Task RejectsProvenanceMutationAsync() => RejectMutations(
    [
        report => report["platform"]!["nativeExecution"] = false,
        report => report["platform"]!["observedRuntimeIdentifier"] = "linux-x64",
        report => report["platform"]!["processArchitecture"] = "Arm64",
        report => report["toolVersions"]!["ilverify"] = "unavailable",
        report => report["preflight"]!["rustc"]!["standardOutput"] = "rustc 1.98.0-nightly (forged)",
        report => report["execution"]!["startedAtUtc"] = "not-a-date",
        report => report["cases"]![0]!["coreClrRun"]!["startedAtUtc"] = "2025-01-01T00:00:00Z",
        report => report["cases"]![0]!["coreClrRun"]!["elapsedMilliseconds"] = 30_000,
        report => MutateIlVerify(report, evidence => evidence["VerifyProcess"]!["StartedAt"] = "2025-01-01T00:00:00Z"),
        report => MutateIlVerify(report, evidence => evidence["VerifyProcess"]!["ElapsedMilliseconds"] = 30_000),
        report => MutateIlVerify(report, evidence => evidence["VerifyProcess"]!["StartedAt"] = "2026-10-02T00:00:03Z"),
        report => report["limits"]!["deadlineSeconds"] = 1000,
        report => report["semanticClosureEligible"] = true,
    ]);

    private static Task RejectsMalformedJsonAsync()
    {
        string report = Report().ToJsonString();
        string[] malformed = ["[]", "null", "7", "\"report\"", report.Insert(1, "\"profile\":\"forged\","), report.Replace("\"completed\":true", "\"completed\":true,\"completed\":false", StringComparison.Ordinal)];
        Stopwatch clock = Stopwatch.StartNew();
        foreach (string json in malformed)
        {
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Malformed report batch is bounded to six inputs and five seconds.");
            AssertEx.False(P1EvidenceBindingValidator.Validate(json, Expected).Valid, "Malformed report must fail without throwing.");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.False(P1EvidenceBindingValidator.Validate(report, Expected, cancelled.Token).Valid, "Cancellation must reject validation.");
        return Task.CompletedTask;
    }

    private static Task RejectMutations(IReadOnlyList<Action<JsonObject>> mutations)
    {
        AssertEx.True(mutations.Count <= 16, "Mutation batch is explicitly bounded.");
        Stopwatch clock = Stopwatch.StartNew();
        foreach (Action<JsonObject> mutate in mutations)
        {
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Mutation batch has a five-second wall-clock bound.");
            JsonObject report = Report(); mutate(report);
            AssertEx.False(P1EvidenceBindingValidator.Validate(report.ToJsonString(), Expected).Valid, "A forged success field cannot pass platform binding.");
        }
        return Task.CompletedTask;
    }

    private static JsonObject Report()
    {
        var ilverify = new JsonObject
        {
            ["Succeeded"] = true,
            ["Assembly"] = new JsonObject { ["Sha256"] = AssemblyHash },
            ["Tool"] = new JsonObject { ["PackageId"] = "dotnet-ilverify", ["Version"] = "10.0.11" },
            ["VerifyProcess"] = new JsonObject
            {
                ["ProcessId"] = 11, ["ParentProcessId"] = 10, ["StartedAt"] = "2026-10-02T00:00:01Z", ["CommandLine"] = "dotnet tool run ilverify -- drop.dll",
                ["Termination"] = "Exited", ["ExitCode"] = 0, ["ElapsedMilliseconds"] = 1, ["StandardOutputTruncated"] = false, ["StandardErrorTruncated"] = false,
                ["OutputDrainTimedOut"] = false, ["ProcessTreeCleanupIncomplete"] = false,
            },
        };
        string ilverifyJson = ilverify.ToJsonString();
        return new JsonObject
        {
            ["evidenceKind"] = Expected.EvidenceKind, ["profile"] = Expected.Profile, ["semanticClosureEligible"] = false,
            ["manifest"] = new JsonObject { ["sha256"] = ManifestHash, ["denominator"] = 1, ["validated"] = true },
            ["compiler"] = new JsonObject { ["sha256"] = CompilerHash, ["profile"] = "safe-core-mir-p1-v2" },
            ["platform"] = new JsonObject { ["name"] = "windows-x64", ["runtimeIdentifier"] = "win-x64", ["observedRuntimeIdentifier"] = "win-x64", ["architecture"] = "X64", ["processArchitecture"] = "X64", ["nativeExecution"] = true },
            ["toolVersions"] = new JsonObject { ["dotnet"] = "10.0.11", ["sdkVersion"] = "10.0.401", ["rustc"] = RustcVersion, ["ilverify"] = "ILVerify 10.0.11" },
            ["preflight"] = new JsonObject { ["dotnet"] = Process("10.0.401"), ["rustc"] = Process(RustcVersion), ["ilverify"] = Process("ILVerify 10.0.11") },
            ["limits"] = new JsonObject { ["maximumCases"] = 1, ["caseTimeoutSeconds"] = 30, ["deadlineSeconds"] = 120, ["maximumOutputBytes"] = BoundedProcessRunner.MaximumTotalOutputBytes },
            ["summary"] = new JsonObject { ["status"] = "passed", ["exitCode"] = 0, ["denominator"] = 1, ["executed"] = 1, ["passed"] = 1, ["failed"] = 0, ["blocked"] = 0, ["skipped"] = 0 },
            ["cases"] = new JsonArray(new JsonObject
            {
                ["id"] = "drop-return-order", ["source"] = "drop.rs", ["sourceSha256"] = SourceHash, ["expectationSha256"] = ExpectationHash, ["assemblySha256"] = AssemblyHash, ["status"] = "passed",
                ["coreClrCompile"] = Process(""), ["coreClrRun"] = Process("body\r\ndrop\r\n"),
                ["ilVerify"] = new JsonObject { ["status"] = "passed", ["succeeded"] = true, ["process"] = Process(""), ["evidenceJson"] = ilverifyJson, ["evidenceSha256"] = Hash(ilverifyJson) },
                ["nativeAot"] = new JsonObject { ["status"] = "passed", ["succeeded"] = true, ["publish"] = Process(""), ["run"] = Process("body\ndrop\n"), ["outputMatches"] = true, ["assemblySha256"] = AssemblyHash, ["executablePath"] = "native/drop.exe", ["executableSha256"] = new string('f', 64), ["hostCleanupIncomplete"] = false },
            }),
            ["execution"] = new JsonObject { ["startedAtUtc"] = "2026-10-02T00:00:00Z", ["finishedAtUtc"] = "2026-10-02T00:00:05Z", ["deadlineExpired"] = false },
            ["cleanup"] = new JsonObject { ["completed"] = true, ["diagnostic"] = null },
        };
    }

    private static JsonObject Process(string stdout) => new()
    {
        ["processId"] = 10, ["parentProcessId"] = 2, ["startedAtUtc"] = "2026-10-02T00:00:01Z", ["commandLine"] = "synthetic process", ["fileName"] = "synthetic", ["arguments"] = new JsonArray(), ["workingDirectory"] = ".",
        ["termination"] = "exited", ["exitCode"] = 0, ["elapsedMilliseconds"] = 1, ["standardOutput"] = stdout, ["standardError"] = "", ["outputMatches"] = true,
        ["outputTruncated"] = false, ["outputReadTimedOut"] = false, ["outputDrainTimedOut"] = false, ["outputReadLimitReached"] = false, ["cleanupIncomplete"] = false,
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void SetIlVerifyJson(JsonObject report, string json) { report["cases"]![0]!["ilVerify"]!["evidenceJson"] = json; report["cases"]![0]!["ilVerify"]!["evidenceSha256"] = Hash(json); }
    private static void MutateIlVerify(JsonObject report, Action<JsonObject> mutate)
    {
        JsonObject evidence = JsonNode.Parse(report["cases"]![0]!["ilVerify"]!["evidenceJson"]!.GetValue<string>())!.AsObject(); mutate(evidence); SetIlVerifyJson(report, evidence.ToJsonString());
    }
}
