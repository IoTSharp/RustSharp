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

namespace RustSharp.Conformance;

/// <summary>Candidate-bound validation of the frozen source packages, including retained original PE bytes.</summary>
internal static class P1SourcePackageEvidenceValidator
{
    internal const int Denominator = 19;
    internal const int OriginalPeVerificationDenominator = 39;
    internal const int MaximumReportBytes = 32 * 1024 * 1024;
    internal const string FrozenManifestSha256 = "72D4CEC65E90895598704660E2BEE12357528A3A5857574D0A23F7F7569BECD8";
    private const int MaximumTokens = 524288;
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
    internal static IReadOnlyList<string> FrozenIds { get; } = System.Array.AsReadOnly(new[]
    {
        "scalar-positional-copy", "reference-shared-mutable-origin", "slice-owner-mutation-length",
        "aggregate-identity-projection-move", "drop-ownership-transfer-exactly-once", "imported-unwind-cleans-caller",
        "imported-abort-skips-caller-drop", "tuple-array-producer-clr-layout", "source-named-tuple-unit-constructors",
        "composite-return-reference-origins", "composite-parameter-reference-path", "projected-return-reference-origin",
        "static-parameter-promoted-reference", "enum-variants-construct-match", "enum-active-static-reference-payload",
        "imported-unwind-caller-double-panic", "three-package-tuple-array-unit-owner",
        "owned-tuple-partial-move-caller-drop", "owned-tuple-partial-move-unwind",
    });
    private static readonly string[] ImplementationNames = ["RustSharp.Compiler.dll", "RustSharp.Conformance.dll",
        "RustSharp.Runtime.dll", "RustSharp.Semantics.dll", "RustSharp.CodeGen.IL.dll", "RustSharp.Syntax.dll"];
    internal sealed record Expectation(string CandidateSha, string CandidateTreeSha, string RuntimeIdentifier);
    internal sealed record ValidationResult(bool Valid, bool ArtifactContentVerified, IReadOnlyList<string> Errors)
    {
        public bool SatisfiesGate => Valid && ArtifactContentVerified;
    }
    internal sealed record CandidateProvenance(JsonObject Snapshot, JsonObject ReleaseBuild, BoundedProcessResult Process);

    internal static JsonObject ValidateFrozenManifest(ReadOnlySpan<byte> bytes)
    {
        Require(Convert.ToHexString(SHA256.HashData(bytes)) == FrozenManifestSha256, "The frozen 19-case source-package manifest hash changed.");
        JsonObject manifest = Parse(bytes, Stopwatch.StartNew(), CancellationToken.None);
        Require(Int(manifest, "schemaVersion") == 1 && Str(manifest, "profile") == "p1-source-packages-v1" &&
            Int(manifest, "denominator") == Denominator, "The frozen source-package manifest identity changed.");
        JsonArray cases = Array(manifest, "cases", Denominator, Denominator);
        Require(cases.Select(item => Str(item!.AsObject(), "id")).SequenceEqual(FrozenIds), "The frozen 19 source-package IDs changed.");
        return manifest;
    }

    // This entry checks shape and bindings only. It deliberately cannot close a platform gate.
    internal static ValidationResult Validate(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> manifestBytes,
        Expectation expected, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            Budget(clock, cancellationToken);
            JsonObject manifest = ValidateFrozenManifest(manifestBytes);
            JsonObject report = Parse(bytes, clock, cancellationToken);
            ValidateStructure(report, manifest, expected, clock, cancellationToken);
            return new(true, false, []);
        }
        catch (Exception exception) when (ValidationFailure(exception)) { return new(false, false, [exception.Message]); }
    }

    internal static async Task<ValidationResult> ValidateFileAsync(string repositoryRoot, string reportPath,
        Expectation expected, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ValidationTimeout);
        try
        {
            string root = Path.GetFullPath(repositoryRoot), file = Child(root, reportPath);
            Require(File.Exists(file), "Source-package evidence report does not exist.");
            byte[] bytes = Read(file, MaximumReportBytes);
            JsonObject manifest = ValidateFrozenManifest(Read(Child(root, "tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json"), 262144));
            JsonObject report = Parse(bytes, clock, deadline.Token);
            ValidateStructure(report, manifest, expected, clock, deadline.Token);
            string evidenceRoot = Str(Obj(report, "cleanup"), "retainedEvidenceDirectory");
            Require(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(evidenceRoot))) == Path.GetDirectoryName(file),
                "Retained evidence must belong to the report directory.");
            var index = Index(report);
            long totalBytes = 0;
            foreach ((string path, string hash) in index)
            {
                Budget(clock, deadline.Token);
                string full = Child(evidenceRoot, path);
                Require(File.Exists(full), "A retained source-package artifact is missing.");
                long length = new FileInfo(full).Length;
                totalBytes += length;
                Require(length is > 0 and <= 134217728 && totalBytes <= 536870912, "Retained source-package artifacts exceed the byte bound.");
                Require(FileHash(full) == hash, "A retained source-package artifact hash is stale.");
            }
            foreach (string member in new[] { "sourceSnapshot", "releaseBuild" })
            {
                JsonObject wrapper = Obj(report, member);
                JsonObject raw = Parse(Read(Str(Obj(wrapper, "artifact"), "path"), 4 * 1024 * 1024), clock, deadline.Token);
                Require(JsonNode.DeepEquals(raw, wrapper["raw"]), "Retained candidate input bytes differ from the embedded report.");
            }
            CandidateProvenance actual = await ValidateCandidateInputsAsync(root, expected.CandidateSha,
                Str(Obj(Obj(report, "sourceSnapshot"), "artifact"), "path"),
                Str(Obj(Obj(report, "releaseBuild"), "artifact"), "path"), expected.RuntimeIdentifier, null, deadline.Token).ConfigureAwait(false);
            Require(Str(actual.Snapshot, "treeSha") == expected.CandidateTreeSha, "Actual candidate tree differs from the source-package expectation.");
            ValidateImplementationContent(root, report, actual.ReleaseBuild, clock, deadline.Token);
            JsonArray fixtures = manifest["cases"]!.AsArray(), cases = report["cases"]!.AsArray();
            var referenceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int item = 0; item < Denominator; item++)
            {
                Budget(clock, deadline.Token);
                ValidateCaseContent(cases[item]!.AsObject(), fixtures[item]!.AsObject(), report, referenceHashes, clock, deadline.Token);
            }
            Require(!Directory.Exists(Str(Obj(report, "cleanup"), "temporaryDirectory")), "The source-package temporary directory still exists.");
            return new(true, true, []);
        }
        catch (Exception exception) when (ValidationFailure(exception)) { return new(false, false, [exception.Message]); }
    }

    internal static async Task<CandidateProvenance> ValidateCandidateInputsAsync(string root, string candidateSha,
        string snapshotPath, string releaseBuildPath, string rid, Action<BoundedProcessStarted>? onStarted,
        CancellationToken token)
    {
        Require(IsCommit(candidateSha), "An actual full candidate commit identity is required.");
        var clock = Stopwatch.StartNew();
        JsonObject snapshot = Parse(Read(snapshotPath, 4 * 1024 * 1024), clock, token);
        JsonObject build = Parse(Read(releaseBuildPath, 4 * 1024 * 1024), clock, token);
        Require(Str(snapshot, "candidateSha") == candidateSha && IsCommit(Str(snapshot, "treeSha")), "Source snapshot candidate/tree binding is invalid.");
        ValidateBuildShape(build, new(candidateSha, Str(snapshot, "treeSha"), rid));
        string temporary = Path.Combine(Path.GetTempPath(), "rsc-source-evidence-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            const string script = "param([string]$Root,[string]$Candidate,[string]$Snapshot,[string]$Build,[string]$Rid)\n" +
                "$ErrorActionPreference='Stop'; if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 required'}\n" +
                ". (Join-Path $Root 'eng/P1SuiteEvidenceValidation.ps1')\n" +
                "$source=ConvertFrom-P1StrictJson ([IO.File]::ReadAllBytes($Snapshot)); $release=ConvertFrom-P1StrictJson ([IO.File]::ReadAllBytes($Build))\n" +
                "$errors=@(Test-P1SnapshotEvidence $source $Root $Candidate)+@(Test-P1BuildEvidence $release $Candidate $Rid $Root)\n" +
                "if($errors.Count -gt 0){throw ($errors -join '; ')}\n" +
                "[ordered]@{candidateSha=$Candidate;treeSha=$source.treeSha;verified=$true}|ConvertTo-Json -Compress\n";
            string scriptPath = Path.Combine(temporary, "validate.ps1");
            await File.WriteAllTextAsync(scriptPath, script, token).ConfigureAwait(false);
            string pwsh = Environment.GetEnvironmentVariable("RUSTSHARP_P1_PWSH_PATH") ??
                (OperatingSystem.IsWindows() ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "pwsh");
            Require(!OperatingSystem.IsWindows() || Path.GetFileName(pwsh).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase), "Candidate verification requires PowerShell 7 pwsh.exe.");
            BoundedProcessResult process = await new BoundedProcessRunner().RunAsync(new(pwsh,
                ["-NoLogo", "-NoProfile", "-File", scriptPath, root, candidateSha, snapshotPath, releaseBuildPath, rid], root,
                TimeSpan.FromSeconds(120), onStarted), token).ConfigureAwait(false);
            Require(process.Succeeded && Complete(process), "Actual source snapshot and Release build validation failed: " + process.StandardError.Trim());
            JsonObject proof = Parse(Encoding.UTF8.GetBytes(process.StandardOutput), clock, token);
            Require(Str(proof, "candidateSha") == candidateSha && Str(proof, "treeSha") == Str(snapshot, "treeSha") && Bool(proof, "verified"),
                "Actual source snapshot verification returned a different candidate/tree.");
            ValidateFreshBuildFiles(root, build, clock, token);
            return new(snapshot, build, process);
        }
        finally
        {
            string full = Path.GetFullPath(temporary), parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Require(Path.GetDirectoryName(full) == parent && Path.GetFileName(full).StartsWith("rsc-source-evidence-validation-", StringComparison.Ordinal), "Candidate validation cleanup ownership failed.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private static void ValidateStructure(JsonObject report, JsonObject manifest, Expectation expected, Stopwatch clock, CancellationToken token)
    {
        Require(Int(report, "schemaVersion") == 2, "Source-package closure requires candidate-bound schema 2.");
        Require(Str(report, "evidenceKind") == "p1-source-package-original-pe-coreclr-ilverify-native-aot" && Str(report, "profile") == "p1-source-packages-v1", "Source-package evidence identity is invalid.");
        Require(IsCommit(expected.CandidateSha) && IsCommit(expected.CandidateTreeSha) && Str(report, "candidateSha") == expected.CandidateSha &&
            Str(report, "candidateTreeSha") == expected.CandidateTreeSha, "Source-package candidate/tree binding is stale.");
        Require(expected.RuntimeIdentifier is "win-x64" or "linux-x64" && Str(report, "targetRuntimeIdentifier") == expected.RuntimeIdentifier &&
            HostMatches(Str(report, "hostRuntimeIdentifier"), expected.RuntimeIdentifier) && Str(report, "processArchitecture") == "X64", "Source-package native host/RID binding is invalid.");
        Require(Str(report, "manifestSha256") == FrozenManifestSha256, "Source-package manifest binding is stale.");
        var index = Index(report);
        Indexed(Obj(report, "retainedManifest"), index);
        foreach (string member in new[] { "sourceSnapshot", "releaseBuild" }) Indexed(Obj(Obj(report, member), "artifact"), index);
        JsonObject snapshot = Obj(Obj(report, "sourceSnapshot"), "raw");
        Require(Int(snapshot, "schemaVersion") == 1 && Str(snapshot, "evidenceKind") == "p1-candidate-source-snapshot" &&
            Str(snapshot, "candidateSha") == expected.CandidateSha && Str(snapshot, "treeSha") == expected.CandidateTreeSha && Bool(snapshot, "verified"), "Source-package actual source snapshot is missing or stale.");
        ValidateBuildShape(Obj(Obj(report, "releaseBuild"), "raw"), expected);
        JsonArray inputs = Array(report, "implementationInputs", 6, 6);
        Require(inputs.Select(item => Str(item!.AsObject(), "name")).ToHashSet(StringComparer.Ordinal).SetEquals(ImplementationNames), "Source-package implementation assembly inventory changed.");
        foreach (JsonNode? node in inputs)
        {
            JsonObject item = node!.AsObject();
            string hash = ArtifactHash(Obj(item, "loadedAssembly"));
            Require(hash == ArtifactHash(Obj(item, "releaseOutput")) && hash == ArtifactHash(Obj(item, "retainedAssembly")), "Source-package loaded implementation is not the fresh Release output.");
            Indexed(Obj(item, "retainedAssembly"), index);
        }
        foreach ((string member, string name) in new[] { ("compiler", "RustSharp.Compiler.dll"), ("runner", "RustSharp.Conformance.dll"), ("runtime", "RustSharp.Runtime.dll") })
            Require(ArtifactHash(Obj(report, member)) == ArtifactHash(Obj(inputs.Single(item => Str(item!.AsObject(), "name") == name)!.AsObject(), "loadedAssembly")), "Source-package implementation provenance differs from its loaded assembly.");
        JsonObject summary = Obj(report, "summary");
        Require(Str(summary, "status") == "passed" && Int(summary, "denominator") == Denominator && Int(summary, "passed") == Denominator &&
            Int(summary, "failed") == 0 && Int(summary, "blocked") == 0 && Int(summary, "notExecuted") == 0 &&
            Int(summary, "maximumSelectedCases") == Denominator && Bool(summary, "succeeded"), "Source-package summary must close all 19 frozen cases.");
        JsonObject execution = Obj(report, "execution"), cleanup = Obj(report, "cleanup");
        Require(Bool(execution, "deadlineMet") && !Bool(execution, "deadlineExpired") && Number(execution, "elapsedMilliseconds") is >= 0 and <= 1200000 &&
            Number(execution, "suiteTimeoutSeconds") == 1200 && Number(execution, "processTimeoutSeconds") == 180 && Number(execution, "publishTimeoutSeconds") == 600,
            "Source-package execution deadline is incomplete or expired.");
        Require(Date(execution, "finishedAtUtc") >= Date(execution, "startedAtUtc"), "Source-package execution timestamps are invalid.");
        Require(Bool(cleanup, "completed") && !Bool(cleanup, "temporaryDirectoryExists") && Empty(cleanup["diagnostic"]) && Empty(report["harnessError"]), "Source-package process or temporary cleanup is incomplete.");
        Dictionary<string, JsonObject> registry = Registry(report);
        Registered(Obj(report, "candidateValidation"), registry, 120000);
        Require(Int(Obj(report, "candidateValidation"), "exitCode") == 0, "Source-package candidate validation process failed.");
        JsonObject tools = Obj(report, "tools");
        Registered(Obj(tools, "dotnet"), registry, 30000);
        Require(Str(tools, "sdkVersion") == Str(Obj(Obj(report, "releaseBuild"), "raw"), "sdkVersion") &&
            Str(Obj(tools, "dotnet"), "stdout").Contains(Str(tools, "sdkVersion"), StringComparison.Ordinal), "Source-package actual SDK differs from its fresh Release build.");
        JsonObject version = Obj(tools, "ilVerifyVersion");
        Registered(version, registry, 30000);
        Require(Int(version, "exitCode") == 0 && Regex.IsMatch(Str(version, "stdout").Trim(), "^10\\.0\\.11(?:[-+\\s]|$)", RegexOptions.CultureInvariant, PatternTimeout), "Source-package ILVerify version must be 10.0.11.");
        if (expected.RuntimeIdentifier == "win-x64")
        {
            JsonObject shell = Obj(tools, "powershell"); Registered(shell, registry, 30000);
            Require(Int(shell, "exitCode") == 0 && Version.TryParse(Str(shell, "stdout").Trim(), out Version? observed) && observed.Major >= 7, "Source-package Windows verification requires PowerShell 7.");
        }
        JsonArray fixtures = manifest["cases"]!.AsArray(), cases = Array(report, "cases", Denominator, Denominator);
        int verified = 0;
        for (int item = 0; item < Denominator; item++)
        {
            Budget(clock, token);
            JsonObject actual = cases[item]!.AsObject(), frozen = fixtures[item]!.AsObject();
            Require(Str(actual, "id") == FrozenIds[item] && Str(actual, "status") == "passed", "Source-package case IDs/status must preserve all 19 frozen rows.");
            foreach (string member in new[] { "compilerProfile", "expectedOutput", "expectedOutcome" })
                Require(Str(actual, member) == Str(frozen, member), "Source-package case profile or expectation changed.");
            Require(Str(actual, "producerPanicStrategy") == (frozen["producerPanicStrategy"]?.GetValue<string>() ?? "unwind") &&
                Str(actual, "dropCleanupProfile") == (Str(frozen, "compilerProfile") == "safe-core-mir-p1-v2" ? "native-v2" : "legacy-v1"), "Source-package case panic or Drop policy changed.");
            string[] roles = frozen["wrapperSource"] is null ? ["producer", "consumer"] : ["producer", "consumer", "wrapper"];
            JsonArray builds = Array(actual, "independentBuilds", 2, 2);
            Require(Bool(actual, "independentArtifactsIdentical"), "Source-package independent artifacts are not identical.");
            foreach (string role in roles)
            {
                JsonObject source = Obj(actual, "source" + Capital(role));
                Require(ArtifactHash(source) == Str(frozen, role + "Sha256") && Str(source, "path").Replace('\\', '/').EndsWith('/' + Str(frozen, role + "Source"), StringComparison.Ordinal), "Source-package frozen source binding is stale.");
                string? firstPe = null, firstPdb = null;
                foreach (JsonNode? build in builds)
                {
                    JsonObject result = build!.AsObject(), compiled = Obj(result, role);
                    Require(Bool(compiled, "success") && Array(compiled, "diagnostics", 0, 0).Count == 0 && Bool(result, "sourceHashesReconciled"), "Source-package compilation/source reconciliation failed.");
                    string pe = ArtifactHash(Obj(compiled, "originalAssembly")), pdb = ArtifactHash(Obj(compiled, "originalPdb"));
                    JsonObject keptPe = Obj(result, "retainedOriginal" + Capital(role)), keptPdb = Obj(result, "retainedOriginal" + Capital(role) + "Pdb");
                    Require(pe == ArtifactHash(keptPe) && pdb == ArtifactHash(keptPdb) && (firstPe is null || firstPe == pe && firstPdb == pdb), "Source-package original PE/PDB independent-build binding changed.");
                    firstPe = pe; firstPdb = pdb;
                    Indexed(keptPe, index); Indexed(keptPdb, index); Indexed(Obj(result, role + "Metadata"), index);
                }
                Require(ArtifactHash(Obj(actual, "original" + Capital(role))) == firstPe, "Source-package retained original PE differs from build zero.");
                Indexed(Obj(actual, "original" + Capital(role)), index);
                ValidateVerifier(Obj(actual, role + "IlVerify"), roles.Where(other => other != role).Select(other => Obj(builds[0]!.AsObject(), other)["originalAssembly"]!.AsObject()).ToArray(),
                    Obj(builds[0]!.AsObject(), role)["originalAssembly"]!.AsObject(), report, registry, index);
                verified++;
            }
            JsonObject core = Obj(actual, "coreClr"), native = Obj(actual, "nativeRun"), publish = Obj(actual, "nativePublish");
            Registered(core, registry, 180000); Registered(native, registry, 180000); Registered(publish, registry, 600000);
            Require(Str(core, "commandLine").Contains(Str(Obj(Obj(builds[0]!.AsObject(), "consumer"), "originalAssembly"), "path"), StringComparison.Ordinal), "CoreCLR did not execute the original consumer PE.");
            Require(Matches(core, frozen) && Matches(native, frozen), "Source-package CoreCLR/native output or outcome differs from its frozen expectation.");
            Require(Int(publish, "exitCode") == 0 && Str(publish, "commandLine").Contains("-p:PublishAot=true", StringComparison.Ordinal) &&
                Str(publish, "commandLine").Contains("-r " + expected.RuntimeIdentifier, StringComparison.Ordinal) && Str(publish, "commandLine").Contains("-c Release", StringComparison.Ordinal) &&
                Str(publish, "commandLine").Contains("--self-contained true", StringComparison.Ordinal) && Str(publish, "commandLine").Contains("-warnaserror", StringComparison.Ordinal) &&
                Int(actual, "nativeWarningCount") == 0 && !Regex.IsMatch(Str(publish, "stdout") + Str(publish, "stderr"), "\\bwarning [A-Z]+[0-9]+:", RegexOptions.CultureInvariant, PatternTimeout), "Source-package native publish is incomplete, warned, or has the wrong RID.");
            Indexed(Obj(actual, "nativeExecutable"), index); Indexed(Obj(actual, "nativeHostSource"), index);
            Require(Path.GetFileName(Str(Obj(actual, "nativeExecutable"), "path")) == Path.GetFileName(Str(native, "commandLine").Trim('"')), "Source-package native execution is not bound to the published executable.");
            JsonArray nativeInputs = Array(actual, "nativeInputs", roles.Length + 3, roles.Length + 3);
            string[] nativeNames = ["RustSharp.NativeAotHost.csproj", "Program.cs", "RustSharp.Runtime.dll", .. roles.Select(role => Str(frozen, role + "Assembly") + ".dll")];
            Require(nativeInputs.Select(node => Str(node!.AsObject(), "name")).ToHashSet(StringComparer.Ordinal).SetEquals(nativeNames), "Source-package native input graph changed.");
            foreach (JsonNode? input in nativeInputs) Indexed(Obj(input!.AsObject(), "retained"), index);
            JsonObject host = Obj(actual, "hostCleanup");
            Require(Bool(host, "attempted") && !Bool(host, "incomplete") && !Bool(host, "existsAfterCleanup") && Empty(host["diagnostic"]), "Source-package native host cleanup is incomplete.");
            JsonArray additional = Array(actual, "nativeAdditionalAssemblies", roles.Length - 1, roles.Length - 1);
            Require(additional.Select(node => ArtifactHash(node!.AsObject())).SequenceEqual(roles.Where(role => role != "consumer").Select(role => ArtifactHash(Obj(actual, "original" + Capital(role))))), "Source-package native additional assemblies differ from the original producer graph.");
        }
        Require(verified == OriginalPeVerificationDenominator, "Source-package ILVerify must close all 39 original PE cells.");
    }

    private static void ValidateBuildShape(JsonObject build, Expectation expected)
    {
        Require(Int(build, "schemaVersion") == 1 && Str(build, "evidenceKind") == "p1-release-build" && Str(build, "candidateSha") == expected.CandidateSha &&
            Str(build, "configuration") == "Release" && Bool(build, "succeeded") && Int(Obj(build, "summary"), "warnings") == 0 && Int(Obj(build, "summary"), "errors") == 0, "Source-package fresh Release build is missing or stale.");
        JsonObject source = Obj(build, "sourceProvenance");
        Require(Str(source, "candidateSha") == expected.CandidateSha && Str(source, "candidateTreeSha") == expected.CandidateTreeSha && Bool(source, "candidateMatchesWorkingTree") &&
            Int(source, "checkedFileCount") > 0 && Array(source, "errors", 0, 0).Count == 0, "Release build is not bound to actual candidate source inputs.");
        Array(build, "sourceSnapshots", 2, 2);
        JsonArray assemblies = Array(build, "implementationAssemblies", 6, 6);
        Require(assemblies.Select(node => Path.GetFileName(Str(node!.AsObject(), "path"))).ToHashSet(StringComparer.Ordinal).SetEquals(ImplementationNames), "Fresh Release build implementation inventory changed.");
        foreach (JsonNode? assembly in assemblies) ArtifactHash(assembly!.AsObject());
        JsonObject inventory = Obj(build, "registrationInventory");
        int schema = Int(inventory, "schemaVersion");
        Require(schema is 1 or 2 && Str(inventory, "evidenceKind") == "p1-regression-registration-inventory" && Str(inventory, "buildConfiguration") == "Release" &&
            HostMatches(Str(inventory, "runtimeIdentifier"), expected.RuntimeIdentifier) && Str(inventory, "assemblySha256") == Str(build, "testsAssemblySha256"), "Source-package fresh test registration binding is stale.");
        JsonArray ids = Array(inventory, "registeredIds", 464, schema == 1 ? 1024 : 4096);
        string[] names = ids.Select(node => node!.GetValue<string>()).ToArray();
        Require(names.All(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 4096) && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            Int(inventory, "registeredDenominator") == names.Length && Str(inventory, "registeredIdsSha256") == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', names)))), "Source-package fresh test registration denominator/hash changed.");
    }

    private static void ValidateFreshBuildFiles(string root, JsonObject build, Stopwatch clock, CancellationToken token)
    {
        foreach ((string path, string hash) in new[] { (Str(build, "compilerPath"), Str(build, "compilerSha256")), (Str(build, "testsAssemblyPath"), Str(build, "testsAssemblySha256")),
            (Str(build, "registrationInventoryPath"), Str(build, "registrationInventorySha256")) })
        {
            Budget(clock, token);
            Require(FileHash(Child(root, path)) == hash, "Fresh Release build compiler/tests/inventory physical hash is stale.");
        }
        foreach (string kind in new[] { "compiler", "testsAssembly" })
        {
            Budget(clock, token);
            Require(FileHash(Child(root, Str(build, kind + "ArtifactPath"))) == Str(build, kind + "Sha256"), "Retained Release compiler/tests artifact hash is stale.");
        }
        foreach (JsonNode? node in build["implementationAssemblies"]!.AsArray())
        {
            Budget(clock, token); JsonObject assembly = node!.AsObject(); string name = Path.GetFileName(Str(assembly, "path"));
            Require(Str(assembly, "path").Replace('\\', '/').Contains("/bin/Release/net10.0/", StringComparison.Ordinal) && FileHash(Child(root, Str(assembly, "path"))) == ArtifactHash(assembly) &&
                FileHash(Child(root, "tests/RustSharp.Tests/bin/Release/net10.0/" + name)) == ArtifactHash(assembly) &&
                FileHash(Child(root, Str(assembly, "artifactPath"))) == ArtifactHash(assembly), "Fresh Release implementation physical hash is stale.");
        }
        foreach (JsonNode? node in build["sourceSnapshots"]!.AsArray())
        {
            Budget(clock, token); JsonObject artifact = node!.AsObject();
            Require(FileHash(Child(root, Str(artifact, "path"))) == ArtifactHash(artifact), "Release build pre/post source snapshot bytes are stale.");
            JsonObject source = Parse(Read(Child(root, Str(artifact, "path")), 4 * 1024 * 1024), clock, token);
            Require(Str(source, "candidateSha") == Str(build, "candidateSha") && Str(source, "treeSha") == Str(Obj(build, "sourceProvenance"), "candidateTreeSha") && Bool(source, "verified"), "Release build pre/post candidate input binding changed.");
        }
    }

    private static void ValidateImplementationContent(string root, JsonObject report, JsonObject build, Stopwatch clock, CancellationToken token)
    {
        foreach (JsonNode? node in report["implementationInputs"]!.AsArray())
        {
            Budget(clock, token); JsonObject input = node!.AsObject(); string name = Str(input, "name");
            JsonObject output = build["implementationAssemblies"]!.AsArray().Single(node => Path.GetFileName(Str(node!.AsObject(), "path")) == name)!.AsObject();
            Require(ArtifactHash(Obj(input, "retainedAssembly")) == ArtifactHash(output) && FileHash(Child(root, Str(output, "path"))) == ArtifactHash(output), "Retained implementation differs from the verified Release build.");
        }
    }

    private static void ValidateCaseContent(JsonObject actual, JsonObject fixture, JsonObject report,
        Dictionary<string, string> referenceHashes, Stopwatch clock, CancellationToken token)
    {
        bool wrapper = fixture["wrapperSource"] is not null;
        string profile = Str(fixture, "compilerProfile");
        SafeCoreDropCleanupProfile drop = profile == "safe-core-mir-p1-v2" ? SafeCoreDropCleanupProfile.NativeV2 : SafeCoreDropCleanupProfile.LegacyV1;
        string[] required = fixture["requiredFunctions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        JsonArray builds = actual["independentBuilds"]!.AsArray();
        foreach (JsonNode? node in builds)
        {
            Budget(clock, token); JsonObject build = node!.AsObject();
            string producer = Str(Obj(build, "retainedOriginalProducer"), "path"), consumer = Str(Obj(build, "retainedOriginalConsumer"), "path");
            string? middle = wrapper ? Str(Obj(build, "retainedOriginalWrapper"), "path") : null;
            RustSharpMetadataImportResult producerMetadata = RustSharpMetadataConsumer.ReadAssembly(producer, drop, profile, required, cancellationToken: token);
            Metadata(producerMetadata, fixture, "producer", build);
            string panic = fixture["producerPanicStrategy"]?.GetValue<string>() ?? "unwind";
            Require(producerMetadata.Document!.Ownership.All(item => item.PanicStrategy == panic) && producerMetadata.Document.CallContracts.All(item => item.PanicStrategy == panic), "Original producer panic metadata changed.");
            RustSharpMetadataImportResult? middleMetadata = null;
            string[] middleRequired = wrapper ? fixture["requiredWrapperFunctions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray() : [];
            if (middle is not null)
            {
                middleMetadata = RustSharpMetadataConsumer.ReadAssembly(middle, drop, profile, middleRequired, [producer], token);
                Metadata(middleMetadata, fixture, "wrapper", build);
                Proof(P1SourcePackagePlatformRunner.VerifyMemberRefs(middle, Str(fixture, "producerAssembly"), producerMetadata, required, token), build["wrapperImportedMemberRefs"], "Original wrapper MemberRefs differ from reported evidence.");
                Proof(P1SourcePackagePlatformRunner.VerifyRequiredOwners(fixture, "requiredWrapperOwnerAssemblies", middleMetadata, producerMetadata, producer, token), build["wrapperResolvedOwners"], "Original wrapper owner graph differs from reported evidence.");
            }
            string[] dependencies = middle is null ? [producer] : [producer, middle];
            RustSharpMetadataImportResult consumerMetadata = RustSharpMetadataConsumer.ReadAssembly(consumer, drop, profile, dependencyPaths: dependencies, cancellationToken: token);
            Metadata(consumerMetadata, fixture, "consumer", build);
            Proof(P1SourcePackagePlatformRunner.VerifyRequiredOwners(fixture, "requiredConsumerOwnerAssemblies", consumerMetadata, producerMetadata, producer, token), build["consumerResolvedOwners"], "Original consumer owner graph differs from reported evidence.");
            string[] consumerRequired = wrapper ? middleRequired : required;
            if (fixture["requiredConsumerDropTypes"] is not null) consumerRequired = [.. required, .. P1SourcePackagePlatformRunner.RequiredDropFunctions(fixture, producerMetadata, token)];
            Proof(P1SourcePackagePlatformRunner.VerifyMemberRefs(consumer, Str(fixture, wrapper ? "wrapperAssembly" : "producerAssembly"), middleMetadata ?? producerMetadata, consumerRequired, token), build["importedMemberRefs"], "Original consumer MemberRefs differ from reported evidence.");
            foreach (string role in wrapper ? new[] { "producer", "consumer", "wrapper" } : new[] { "producer", "consumer" })
            {
                string path = Path.Combine(Path.GetDirectoryName(consumer)!, role + ".rs");
                Require(FileHash(path) == Str(fixture, role + "Sha256"), "Retained source bytes differ from the frozen fixture.");
                Require(FileHash(Path.ChangeExtension(Str(Obj(build, "retainedOriginal" + Capital(role)), "path"), ".runtimeconfig.json")) ==
                    FileHash(Path.ChangeExtension(Str(Obj(actual, "original" + Capital(role)), "path"), ".runtimeconfig.json")), "Original package runtime configuration changed.");
            }
            Require(FileHash(Path.Combine(Path.GetDirectoryName(consumer)!, "RustSharp.Runtime.dll")) == ArtifactHash(Obj(report, "runtime")), "Original package runtime differs from the verified compiler runtime.");
        }
        JsonObject firstBuild = builds[0]!.AsObject();
        string[] roles = wrapper ? ["producer", "consumer", "wrapper"] : ["producer", "consumer"];
        var remap = roles.ToDictionary(role => Str(Obj(Obj(firstBuild, role), "originalAssembly"), "path"),
            role => Str(Obj(firstBuild, "retainedOriginal" + Capital(role)), "path"), StringComparer.Ordinal);
        string originalRuntime = Path.Combine(Path.GetDirectoryName(Str(Obj(Obj(firstBuild, "consumer"), "originalAssembly"), "path"))!, "RustSharp.Runtime.dll");
        remap.Add(originalRuntime, Path.Combine(Path.GetDirectoryName(Str(Obj(firstBuild, "retainedOriginalConsumer"), "path"))!, "RustSharp.Runtime.dll"));
        foreach (string role in roles)
        {
            Budget(clock, token); JsonObject verifier = Obj(actual, role + "IlVerify");
            JsonObject raw = Parse(Read(Str(Obj(verifier, "report"), "path"), 1048576), clock, token);
            Require(JsonNode.DeepEquals(raw, verifier["rawEvidence"]), "Retained ILVerify bytes differ from the embedded raw evidence.");
            foreach (JsonNode? node in verifier["verificationReferenceArtifacts"]!.AsArray())
            {
                Budget(clock, token); JsonObject reference = node!.AsObject(); string path = Str(reference, "path");
                string physical = remap.GetValueOrDefault(path, path);
                if (!referenceHashes.TryGetValue(physical, out string? observed))
                {
                    Require(referenceHashes.Count < 1024, "ILVerify reference hashing exceeds its item bound.");
                    observed = FileHash(physical); referenceHashes.Add(physical, observed);
                }
                Require(observed == ArtifactHash(reference), "ILVerify actual framework/package reference bytes changed.");
            }
        }
        Require(File.ReadAllText(Str(Obj(actual, "nativeHostSource"), "path")) == P1SourcePackagePlatformRunner.NativeHostSource, "Native host source simulates package behavior.");
        JsonArray inputs = actual["nativeInputs"]!.AsArray();
        string Input(string name) => Str(Obj(inputs.Single(node => Str(node!.AsObject(), "name") == name)!.AsObject(), "retained"), "path");
        Require(File.ReadAllText(Input("Program.cs")) == P1SourcePackagePlatformRunner.NativeHostSource, "Published native host source simulates package behavior.");
        Require(new FileInfo(Input("RustSharp.NativeAotHost.csproj")).Length <= 65536, "Native host project exceeds its byte bound.");
        XDocument project = XDocument.Parse(File.ReadAllText(Input("RustSharp.NativeAotHost.csproj")));
        string[] refs = project.Descendants("Reference").Select(item => item.Element("HintPath")?.Value ?? "").ToArray();
        string[] expectedRefs = [Str(fixture, "consumerAssembly") + ".dll", Str(fixture, "producerAssembly") + ".dll", "RustSharp.Runtime.dll", .. wrapper ? new[] { Str(fixture, "wrapperAssembly") + ".dll" } : []];
        Require(refs.Length == expectedRefs.Length && refs.ToHashSet(StringComparer.Ordinal).SetEquals(expectedRefs) && project.Descendants("PublishAot").Single().Value == "true", "Native project does not reference the original source-package graph.");
        foreach (string name in expectedRefs)
        {
            Budget(clock, token);
            string hash = name == "RustSharp.Runtime.dll" ? ArtifactHash(Obj(report, "runtime")) : ArtifactHash(Obj(actual,
                "original" + Capital(name == Str(fixture, "consumerAssembly") + ".dll" ? "consumer" : name == Str(fixture, "producerAssembly") + ".dll" ? "producer" : "wrapper")));
            Require(FileHash(Input(name)) == hash, "Native publish input differs from the original generated PE/runtime.");
        }
        Require(!Directory.Exists(Str(Obj(actual, "hostCleanup"), "directory")), "A source-package native host directory still exists.");
        string executable = Str(Obj(actual, "nativeExecutable"), "path");
        using FileStream stream = File.OpenRead(executable);
        byte[] header = new byte[20]; stream.ReadExactly(header);
        stream.Position = 0;
        if (Str(report, "targetRuntimeIdentifier") == "win-x64")
        {
            using var pe = new PEReader(stream);
            Require(pe.PEHeaders.CorHeader is null && pe.PEHeaders.CoffHeader.Machine == System.Reflection.PortableExecutable.Machine.Amd64, "Published native executable is a managed or wrong-architecture host.");
        }
        else Require(header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' && header[4] == 2 && header[5] == 1 && header[18] == 62 && header[19] == 0,
            "Published native executable is not a native Linux x64 ELF.");
    }

    private static void Metadata(RustSharpMetadataImportResult result, JsonObject fixture, string role, JsonObject build)
    {
        Require(result.IsSuccessful && result.AssemblyName == Str(fixture, role + "Assembly") && result.Document!.SourceSha256 == Str(fixture, role + "Sha256"), "Original source-package PE metadata/source identity changed.");
        Require(File.ReadAllText(Str(Obj(build, role + "Metadata"), "path")) == result.Document!.Json, "Retained metadata bytes differ from the original PE document.");
    }

    private static void Proof(JsonNode actual, JsonNode? recorded, string error)
    {
        // Historic temporary paths disappear after cleanup. Assembly hashes, MVIDs, source identities and layouts must still match.
        JsonNode copy = actual.DeepClone(), other = recorded?.DeepClone() ?? throw new ArgumentException(error);
        Require(copy.AsArray().Count <= 512 && other.AsArray().Count == copy.AsArray().Count, error);
        foreach (JsonNode? node in copy.AsArray()) if (node is JsonObject item && item["originalProducer"] is JsonObject artifact) artifact.Remove("path");
        foreach (JsonNode? node in other.AsArray()) if (node is JsonObject item && item["originalProducer"] is JsonObject artifact) artifact.Remove("path");
        Require(JsonNode.DeepEquals(copy, other), error);
    }

    private static void ValidateVerifier(JsonObject verifier, JsonObject[] additional, JsonObject original, JsonObject report,
        Dictionary<string, JsonObject> registry, Dictionary<string, string> index)
    {
        Require(Str(verifier, "status") == "passed" && Bool(verifier, "originalPeAndReferenceBindingMatches") && ArtifactHash(Obj(verifier, "originalAssembly")) == ArtifactHash(original), "ILVerify is not bound to the original generated PE.");
        JsonObject process = Obj(verifier, "process"); Registered(process, registry, 180000);
        Require(Int(process, "exitCode") == 0, "Original PE ILVerify process failed.");
        JsonArray references = Array(verifier, "additionalReferenceArtifacts", additional.Length + 1, additional.Length + 1);
        string[] expected = [.. additional.Select(ArtifactHash), ArtifactHash(Obj(report, "runtime"))];
        Require(references.Select(node => ArtifactHash(node!.AsObject())).ToHashSet(StringComparer.Ordinal).SetEquals(expected), "Original PE ILVerify reference graph changed.");
        JsonObject raw = Obj(verifier, "rawEvidence"), assembly = Obj(raw, "Assembly"), verification = Obj(raw, "Verification"), tool = Obj(raw, "Tool"), result = Obj(raw, "VerifyProcess");
        Require(Bool(raw, "Succeeded") && Str(assembly, "Sha256") == ArtifactHash(original) && Str(assembly, "Path") == Str(original, "path") && Str(tool, "PackageId") == "dotnet-ilverify" && Str(tool, "Version") == "10.0.11" &&
            Str(verification, "SystemModule") == "System.Private.CoreLib" && Str(verification, "RuntimeVersion") == Str(report, "runtimeVersion"), "Original PE ILVerify raw binding is stale.");
        Require(Int(result, "ExitCode") == 0 && Str(result, "Termination") == "Exited" && !Bool(result, "StandardOutputTruncated") && !Bool(result, "StandardErrorTruncated") &&
            !Bool(result, "OutputDrainTimedOut") && !Bool(result, "ProcessTreeCleanupIncomplete"), "Original PE ILVerify nested process is incomplete.");
        JsonArray paths = Array(verification, "AdditionalReferencePath", expected.Length, expected.Length);
        Require(paths.Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(references.Select(node => Str(node!.AsObject(), "path"))), "Original PE ILVerify additional reference paths changed.");
        JsonArray hashes = Array(verifier, "verificationReferenceArtifacts", expected.Length, 512);
        Require(hashes.Select(node => Str(node!.AsObject(), "path")).ToHashSet(StringComparer.Ordinal).IsSupersetOf(paths.Select(node => node!.GetValue<string>())), "Original PE ILVerify actual reference hashes are missing.");
        foreach (JsonNode? reference in references) Require(hashes.Any(node => Str(node!.AsObject(), "path") == Str(reference!.AsObject(), "path") && ArtifactHash(node!.AsObject()) == ArtifactHash(reference.AsObject())), "Original PE ILVerify actual reference hash differs.");
        if (Str(report, "targetRuntimeIdentifier") == "win-x64")
        {
            JsonArray rawReferences = Array(verification, "ReferenceFiles", expected.Length, 512);
            Require(rawReferences.Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(hashes.Select(node => Str(node!.AsObject(), "path"))), "Original PE ILVerify actual reference inventory differs from raw evidence.");
            Require(Str(result, "StandardOutput").Contains("All Classes and Methods in", StringComparison.Ordinal) && Str(result, "StandardOutput").Contains("Verified.", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(Str(result, "StandardError")), "Original PE ILVerify nested output does not confirm verification.");
            JsonObject runtime = Obj(verification, "RuntimeReference");
            Require(Bool(runtime, "Validated") && Str(runtime, "Sha256") == ArtifactHash(Obj(report, "runtime")) && Str(runtime, "Sha256AfterVerification") == Str(runtime, "Sha256"), "Original PE ILVerify runtime proof is stale.");
            JsonArray records = Array(raw, "ProcessRecords", 1, 4);
            Require(Int(result, "ProcessId") > 0 && Int(result, "ParentProcessId") == Int(process, "pid") && Date(result, "StartedAt") >= Date(process, "startedAtUtc") &&
                Number(result, "ElapsedMilliseconds") is >= 0 and <= 125000 && records.Any(node => Int(node!.AsObject(), "ProcessId") == Int(result, "ProcessId") && Int(node.AsObject(), "ParentProcessId") == Int(process, "pid") &&
                Str(node.AsObject(), "StartedAt") == Str(result, "StartedAt") && Str(node.AsObject(), "CommandLine") == Str(result, "CommandLine") &&
                Str(node.AsObject(), "CommandLine").Contains(Str(original, "path"), StringComparison.Ordinal) && Str(node.AsObject(), "CommandLine").Contains("--statistics", StringComparison.Ordinal) &&
                Str(node.AsObject(), "CommandLine").Contains("System.Private.CoreLib", StringComparison.Ordinal)), "Original PE ILVerify nested command does not bind the original PE.");
        }
        else Require(JsonNode.DeepEquals(verification["ReferenceArtifacts"], hashes) && JsonNode.DeepEquals(result["NativeProcess"], process) &&
            Str(process, "stdout").Contains("All Classes and Methods in", StringComparison.Ordinal) && Str(process, "stdout").Contains("Verified.", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(Str(process, "stderr")) && Str(process, "commandLine").Contains(Str(original, "path"), StringComparison.Ordinal) &&
            Str(process, "commandLine").Contains("--statistics", StringComparison.Ordinal) && Str(process, "commandLine").Contains("System.Private.CoreLib", StringComparison.Ordinal), "Native ILVerify command does not bind the original PE.");
        Indexed(Obj(verifier, "report"), index);
    }

    private static Dictionary<string, JsonObject> Registry(JsonObject report)
    {
        JsonArray starts = Array(report, "processStarts", 99, 156), results = Array(report, "processResults", starts.Count, starts.Count);
        var registry = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        int? owner = null;
        foreach (JsonNode? node in results)
        {
            JsonObject process = node!.AsObject(); Process(process, 600000);
            owner ??= Int(process, "parentPid");
            Require(Int(process, "parentPid") == owner && Date(process, "startedAtUtc") >= Date(Obj(report, "execution"), "startedAtUtc") &&
                Date(process, "startedAtUtc") <= Date(Obj(report, "execution"), "finishedAtUtc"), "Source-package process ownership or execution interval changed.");
            Require(registry.TryAdd(Key(process), process), "Source-package process result identity is duplicated.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in starts)
        {
            JsonObject start = node!.AsObject(); string key = Key(start);
            Require(seen.Add(key) && registry.TryGetValue(key, out JsonObject? result) && Int(start, "parentPid") == Int(result, "parentPid") && Str(start, "commandLine") == Str(result, "commandLine"), "Source-package process start/result lifecycle is incomplete or duplicated.");
        }
        return registry;
    }
    private static void Registered(JsonObject process, Dictionary<string, JsonObject> registry, int maximumMilliseconds)
    {
        Process(process, maximumMilliseconds);
        Require(registry.TryGetValue(Key(process), out JsonObject? observed) && JsonNode.DeepEquals(observed, process), "A source-package execution is absent from its owned process registry.");
    }
    private static void Process(JsonObject process, int maximumMilliseconds)
    {
        Require(Int(process, "pid") > 0 && Int(process, "parentPid") > 0 && !string.IsNullOrWhiteSpace(Str(process, "commandLine")) &&
            !string.IsNullOrWhiteSpace(Str(process, "workingDirectory")) && Date(process, "startedAtUtc") > DateTimeOffset.UnixEpoch && Str(process, "termination") == "Exited" &&
            Number(process, "elapsedMilliseconds") >= 0 && Number(process, "elapsedMilliseconds") <= maximumMilliseconds + 5000, "Source-package owned process identity/deadline is incomplete.");
        foreach (string flag in new[] { "outputTruncated", "outputReadTimedOut", "outputDrainTimedOut", "outputReadLimitReached", "cleanupIncomplete" }) Require(!Bool(process, flag), "Source-package owned process output or cleanup is incomplete.");
        Require(Empty(process["cleanupDiagnostic"]), "Source-package process cleanup has a diagnostic.");
        _ = Int(process, "exitCode"); _ = Str(process, "stdout"); _ = Str(process, "stderr");
    }
    private static bool Matches(JsonObject process, JsonObject fixture)
    {
        if (Str(process, "stdout").Replace("\r\n", "\n", StringComparison.Ordinal) != Str(fixture, "expectedOutput")) return false;
        string error = Str(process, "stderr"), outcome = P1DropDifferentialRunner.ClassifyGeneratedFailure(Int(process, "exitCode"), error);
        return Str(fixture, "expectedOutcome") switch
        {
            "success" => outcome == "success" && string.IsNullOrWhiteSpace(error),
            "unwound" => outcome == "unwind" && error.Contains("OverflowException", StringComparison.Ordinal) && !error.Contains("RustSharp panic abort:", StringComparison.Ordinal),
            "aborted" => outcome == "panic-abort" && Int(process, "exitCode") == 134 && error.StartsWith("RustSharp panic abort: ", StringComparison.Ordinal),
            "double-panic" => outcome == "double-panic-abort" && Int(process, "exitCode") == 134 && error.StartsWith("RustSharp double panic abort: ", StringComparison.Ordinal),
            _ => false,
        };
    }
    private static Dictionary<string, string> Index(JsonObject report)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonNode? node in Array(report, "retainedArtifacts", 1, 1024))
        {
            JsonObject item = node!.AsObject();
            Require(index.TryAdd(Str(item, "path"), ArtifactHash(item)), "Retained source-package artifact identity is duplicated.");
        }
        return index;
    }
    private static void Indexed(JsonObject artifact, Dictionary<string, string> index) => Require(index.TryGetValue(Str(artifact, "path"), out string? hash) && hash == ArtifactHash(artifact), "A required retained artifact is absent from its hash inventory.");
    private static string ArtifactHash(JsonObject artifact)
    {
        string hash = Str(artifact, "sha256"); Require(hash.Length == 64 && hash.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(Str(artifact, "path")), "Source-package artifact identity/hash is invalid."); return hash;
    }
    private static JsonObject Parse(ReadOnlySpan<byte> bytes, Stopwatch clock, CancellationToken token)
    {
        Require(bytes.Length is > 0 and <= MaximumReportBytes, "Source-package evidence JSON exceeds its byte bound.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 });
        var names = new Stack<HashSet<string>>(); bool finished = false;
        for (int item = 0; item < MaximumTokens; item++)
        {
            Budget(clock, token);
            if (!reader.Read()) { finished = true; break; }
            if (reader.TokenType == JsonTokenType.StartObject) names.Push(new(StringComparer.OrdinalIgnoreCase));
            else if (reader.TokenType == JsonTokenType.EndObject) names.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName) Require(names.Peek().Add(reader.GetString()!), "Duplicate source-package JSON properties are forbidden.");
        }
        Require(finished, "Source-package evidence JSON exceeds its token bound.");
        return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 48 })?.AsObject() ?? throw new ArgumentException("Source-package evidence JSON root must be an object.");
    }
    private static JsonObject Obj(JsonObject node, string key) => node[key] as JsonObject ?? throw new ArgumentException("Source-package object is missing: " + key);
    private static JsonArray Array(JsonObject node, string key, int minimum, int maximum)
    {
        JsonArray values = node[key] as JsonArray ?? throw new ArgumentException("Source-package array is missing: " + key);
        Require(values.Count >= minimum && values.Count <= maximum, "Source-package array denominator changed: " + key); return values;
    }
    private static string Str(JsonObject node, string key) => node[key]?.GetValue<string>() ?? throw new ArgumentException("Source-package string is missing: " + key);
    private static int Int(JsonObject node, string key) => node[key]?.GetValue<int>() ?? throw new ArgumentException("Source-package integer is missing: " + key);
    private static bool Bool(JsonObject node, string key) => node[key]?.GetValue<bool>() ?? throw new ArgumentException("Source-package boolean is missing: " + key);
    private static double Number(JsonObject node, string key)
    {
        double value = node[key]?.GetValue<double>() ?? throw new ArgumentException("Source-package number is missing: " + key);
        Require(double.IsFinite(value), "Source-package numeric evidence must be finite."); return value;
    }
    private static DateTimeOffset Date(JsonObject node, string key) => DateTimeOffset.Parse(Str(node, key), CultureInfo.InvariantCulture);
    private static string Key(JsonObject process) => Int(process, "pid").ToString(CultureInfo.InvariantCulture) + "/" + Date(process, "startedAtUtc").UtcTicks.ToString(CultureInfo.InvariantCulture);
    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];
    private static bool Empty(JsonNode? node) => node is null || node is JsonValue value && value.TryGetValue<string>(out string? text) && string.IsNullOrEmpty(text);
    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool HostMatches(string actual, string rid) => rid == "win-x64" ? actual == rid : rid == "linux-x64" && (actual == rid || Regex.IsMatch(actual, "^ubuntu(?:\\.[0-9]+\\.[0-9]+)?-x64$", RegexOptions.CultureInvariant, PatternTimeout));
    private static void Budget(Stopwatch clock, CancellationToken token) => Require(!token.IsCancellationRequested && clock.Elapsed < ValidationTimeout, "Source-package validation exceeded its deadline or was cancelled.");
    private static bool Complete(BoundedProcessResult process) => process.Termination == BoundedProcessTermination.Exited && !process.OutputTruncated && !process.OutputReadTimedOut && !process.OutputDrainTimedOut && !process.OutputReadLimitReached && !process.ProcessTreeCleanupIncomplete;
    private static bool ValidationFailure(Exception exception) => exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or FormatException or System.ComponentModel.Win32Exception;
    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    private static byte[] Read(string path, int maximum)
    {
        Require(File.Exists(path) && new FileInfo(path).Length is > 0 && new FileInfo(path).Length <= maximum, "Source-package input is missing or exceeds its byte bound."); return File.ReadAllBytes(path);
    }
    private static string FileHash(string path)
    {
        Require(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 134217728, "Source-package physical artifact is missing or oversized.");
        using FileStream file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file));
    }
    private static string Child(string root, string path)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), full = Path.GetFullPath(path, parent);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Require(full.StartsWith(parent + Path.DirectorySeparatorChar, comparison), "Source-package artifact escapes its declared evidence root.");
        string component = full;
        for (int depth = 0; depth < 64 && !component.Equals(parent, comparison); depth++)
        {
            if (File.Exists(component) || Directory.Exists(component))
                Require((File.GetAttributes(component) & FileAttributes.ReparsePoint) == 0, "Source-package artifact paths cannot traverse filesystem links.");
            component = Path.GetDirectoryName(component) ?? throw new ArgumentException("Source-package artifact path depth is invalid.");
        }
        Require(component.Equals(parent, comparison), "Source-package artifact path depth exceeds its bound.");
        return full;
    }
}
