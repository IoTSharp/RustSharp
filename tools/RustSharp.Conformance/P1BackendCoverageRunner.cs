using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;
using static RustSharp.Conformance.P1BackendCoverageEvidence;

namespace RustSharp.Conformance;

/// <summary>Serial, bounded original-PE witnesses for the six exact P1 backend gaps.</summary>
internal static class P1BackendCoverageRunner
{
    internal sealed record Options(string CandidateSha, string SourceSnapshotPath, string ReleaseBuildPath);
    internal static readonly TimeSpan SuiteTimeout = TimeSpan.FromSeconds(1200);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(600);
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static async Task<int> RunAsync(string repositoryRoot, string reportPath, string runtimeIdentifier,
        int maximumFixturesToExecute, Options options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); Require(maximumFixturesToExecute is >= 1 and <= 6, "Backend selection must be between one and six fixtures.");
        Require(Rids.Contains(runtimeIdentifier, StringComparer.Ordinal), "Only the frozen win-x64 and linux-x64 matrix is supported.");
        string root = Path.GetFullPath(repositoryRoot), baseDirectory = Path.Combine(root, "artifacts", "p1-backend"), reportFile = Child(baseDirectory, Path.GetFullPath(reportPath, root));
        string snapshotInput = Child(root, options.SourceSnapshotPath), releaseInput = Child(root, options.ReleaseBuildPath);
        Require(!File.Exists(reportFile), "Backend reports cannot overwrite previously retained evidence.");
        string runDirectory = Path.Combine(Path.GetDirectoryName(reportFile)!, "backend-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        string temporary = Path.Combine(Path.GetTempPath(), "rsc-p1-backend-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory); Directory.CreateDirectory(temporary);
        DateTimeOffset started = DateTimeOffset.UtcNow; var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(SuiteTimeout);
        var starts = new JsonArray(); var results = new JsonArray(); var artifacts = new JsonArray(); var witnesses = new JsonArray(); var inputs = new JsonArray(); var buildArtifacts = new JsonArray();
        var recordedPaths = new HashSet<string>(StringComparer.Ordinal); string? failure = null; string? cleanupDiagnostic = null;
        JsonObject? initial = null, final = null, snapshotRaw = null, buildRaw = null, snapshotArtifact = null, buildArtifact = null, manifestArtifact = null;
        string? tree = null, manifestHash = null;
        void Started(BoundedProcessStarted process) { starts.Add(Start(process)); Console.WriteLine("P1 backend process: " + process.ProcessId + " " + process.CommandLine); }
        JsonObject Keep(string path)
        {
            string full = Child(runDirectory, path); var result = new JsonObject { ["path"] = full, ["sha256"] = FileHash(full), ["length"] = new FileInfo(full).Length };
            if (recordedPaths.Add(full)) artifacts.Add(result.DeepClone()); return result;
        }
        JsonObject Retain(string source, string destination) { string target = Child(runDirectory, destination); File.Copy(source, target, false); return Keep(target); }
        async Task<BoundedProcessResult> Run(BoundedProcessRequest request)
        { BoundedProcessResult result = await new BoundedProcessRunner().RunAsync(request, deadline.Token).ConfigureAwait(false); results.Add(ProcessEvidence(result)); return result; }
        var runner = new BoundedProcessRunner();
        try
        {
            Require(IsNativeHost(runtimeIdentifier), "Requires actual native " + runtimeIdentifier + " execution; cross-compilation/WSL does not supply another host's report.");
            string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", P1GateCoverageContract.ManifestFileName);
            string manifestText = File.ReadAllText(manifestPath); var manifest = P1GateCoverageContract.ParseManifest(manifestText, root, deadline.Token); ValidateMapping(manifest); manifestHash = manifest.Sha256;
            manifestArtifact = Retain(manifestPath, Path.Combine(runDirectory, P1GateCoverageContract.ManifestFileName));
            var provenance = await P1SourcePackageEvidenceValidator.ValidateCandidateInputsAsync(root, options.CandidateSha,
                snapshotInput, releaseInput, runtimeIdentifier, Started, deadline.Token).ConfigureAwait(false);
            results.Add(ProcessEvidence(provenance.Process)); initial = ProcessEvidence(provenance.Process); snapshotRaw = provenance.Snapshot; buildRaw = provenance.ReleaseBuild; tree = Str(snapshotRaw, "treeSha");
            snapshotArtifact = Retain(snapshotInput, Path.Combine(runDirectory, "source-snapshot.json"));
            buildArtifact = Retain(releaseInput, Path.Combine(runDirectory, "release-build.json"));
            string retainedBuildDirectory = Path.Combine(runDirectory, "build-artifacts"); Directory.CreateDirectory(retainedBuildDirectory);
            void RetainBuild(string original, string hash, string name)
            {
                string path = Child(root, original); Require(FileHash(path) == hash, "Verified Release artifact changed before retention.");
                buildArtifacts.Add(new JsonObject { ["originalPath"] = original, ["sha256"] = hash, ["retained"] = Retain(path, Path.Combine(retainedBuildDirectory, name)) });
            }
            foreach (string kind in new[] { "compiler", "testsAssembly" }) RetainBuild(Str(buildRaw, kind + "ArtifactPath"), Str(buildRaw, kind + "Sha256"), kind + ".dll");
            RetainBuild(Str(buildRaw, "registrationInventoryPath"), Str(buildRaw, "registrationInventorySha256"), "registration-inventory.json");
            int snapshotIndex = 0;
            foreach (JsonNode? artifact in Arr(buildRaw, "sourceSnapshots", 2, 2))
            { JsonObject item = artifact!.AsObject(); RetainBuild(Str(item, "path"), Str(item, "sha256"), "source-snapshot-" + snapshotIndex++ + ".json"); }
            string[] loaded = [typeof(CompilerDriver).Assembly.Location, typeof(P1BackendCoverageRunner).Assembly.Location, typeof(RustSharp.Runtime.MirReference).Assembly.Location,
                typeof(SafeCoreMirProgram).Assembly.Location, typeof(ClrLirAssemblyEmitter).Assembly.Location, typeof(TextSpan).Assembly.Location];
            foreach (string assembly in loaded)
            {
                deadline.Token.ThrowIfCancellationRequested(); string name = Path.GetFileName(assembly);
                JsonObject fresh = Arr(buildRaw, "implementationAssemblies", 6, 6).Select(n => n!.AsObject()).Single(n => Path.GetFileName(Str(n, "path").Replace('\\', '/')) == name);
                Require(FileHash(assembly) == Str(fresh, "sha256"), "Loaded module differs from the verified fresh Release output: " + name);
                JsonObject retained = Retain(assembly, Path.Combine(runDirectory, name));
                inputs.Add(new JsonObject { ["name"] = name, ["loadedAssembly"] = new JsonObject { ["path"] = assembly, ["sha256"] = FileHash(assembly) }, ["releaseOutput"] = fresh.DeepClone(), ["retainedAssembly"] = retained });
                RetainBuild(Str(fresh, "artifactPath"), Str(fresh, "sha256"), name);
            }
            Directory.CreateDirectory(Path.Combine(temporary, ".config")); File.Copy(Path.Combine(root, ".config", "dotnet-tools.json"), Path.Combine(temporary, ".config", "dotnet-tools.json"));
            string? sdk = Environment.GetEnvironmentVariable("RUSTSHARP_NATIVE_AOT_SDK_VERSION");
            if (!string.IsNullOrWhiteSpace(sdk))
            {
                Require(Version.TryParse(sdk, out _) && sdk.Length <= 32 && sdk.All(c => char.IsAsciiDigit(c) || c == '.'), "Native SDK selection must be a numeric version.");
                await File.WriteAllTextAsync(Path.Combine(temporary, "global.json"), new JsonObject { ["sdk"] = new JsonObject { ["version"] = sdk, ["rollForward"] = "disable", ["allowPrerelease"] = false } }.ToJsonString(), deadline.Token).ConfigureAwait(false);
            }
            string dotnet = Environment.GetEnvironmentVariable("RUSTSHARP_P1_DOTNET_PATH") ?? (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            string pwsh = Environment.GetEnvironmentVariable("RUSTSHARP_P1_PWSH_PATH") ?? (OperatingSystem.IsWindows() ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "pwsh");
            Require(!OperatingSystem.IsWindows() || Path.GetFileName(pwsh).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase), "PowerShell 7 pwsh.exe is required.");
            for (int index = 0; index < maximumFixturesToExecute && index < FixtureDenominator; index++)
            {
                deadline.Token.ThrowIfCancellationRequested(); Fixture fixture = Fixtures[index]; Console.WriteLine($"P1 backend fixture: {index + 1}/{FixtureDenominator} {fixture.Id}");
                foreach (string witnessId in fixture.WitnessIds)
                {
                    deadline.Token.ThrowIfCancellationRequested(); bool hand = witnessId.EndsWith(":frozen-hand-mir", StringComparison.Ordinal);
                    string directory = Path.Combine(runDirectory, fixture.Id + (hand ? "-hand-mir" : fixture.HandMir ? "-rust-source" : "")); Directory.CreateDirectory(directory);
                    string assemblyName = "Backend_" + fixture.Id.Replace('-', '_') + (hand ? "_hand" : "_source"), pe = Path.Combine(directory, assemblyName + ".dll"), source = Path.Combine(directory, "program.rs");
                    var witness = new JsonObject { ["id"] = witnessId, ["fixtureId"] = fixture.Id, ["requirementId"] = fixture.RequirementId, ["ownerLeaf"] = fixture.OwnerLeaf,
                        ["originalCaseId"] = fixture.CaseId, ["registrationFile"] = fixture.RegistrationFile, ["registrationSha256"] = fixture.RegistrationSha256, ["assemblyName"] = assemblyName,
                        ["witnessKind"] = hand ? "frozen-hand-authored-mir" : fixture.HandMir ? "supplementary-rust-source" : "original-rust-source", ["status"] = "blocked",
                        ["expectedManagedOutput"] = hand ? "" : fixture.ExpectedOutput, ["expectedManagedExitCode"] = hand ? 41 : 0, ["expectedNativeOutput"] = fixture.ExpectedOutput };
                    witnesses.Add(witness);
                    try
                    {
                        witness["registrationSource"] = Retain(Path.Combine(root, fixture.RegistrationFile), Path.Combine(directory, Path.GetFileName(fixture.RegistrationFile)));
                        if (hand)
                        {
                            GeneratedAssembly generated = EmitFrozenHandMir(assemblyName, deadline.Token); Require(generated.PdbImage is not null, "Frozen MIR did not emit PDB bytes.");
                            await File.WriteAllBytesAsync(pe, generated.PeImage, deadline.Token).ConfigureAwait(false);
                            await File.WriteAllBytesAsync(Path.ChangeExtension(pe, ".pdb"), generated.PdbImage!, deadline.Token).ConfigureAwait(false);
                            await File.WriteAllTextAsync(Path.ChangeExtension(pe, ".runtimeconfig.json"), generated.RuntimeConfigJson, Utf8, deadline.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await File.WriteAllTextAsync(source, fixture.Source, Utf8, deadline.Token).ConfigureAwait(false); witness["source"] = Keep(source);
                            CompilationResult compiled = CompilerDriver.CompileWithDropProfile(fixture.Source, source, pe, SafeCoreDropCleanupProfile.LegacyV1,
                                assemblyName: assemblyName, profile: CompilationProfile.SafeCoreMirV2, cancellationToken: deadline.Token);
                            Require(compiled.Success, "Exact backend Rust witness failed compilation: " + string.Join("; ", compiled.Diagnostics.Select(n => n.Code + ": " + n.Message)));
                        }
                        string runtime = Path.Combine(directory, "RustSharp.Runtime.dll");
                        if (!File.Exists(runtime)) File.Copy(typeof(RustSharp.Runtime.MirReference).Assembly.Location, runtime, false);
                        witness["originalPe"] = Keep(pe); witness["originalPdb"] = Keep(Path.ChangeExtension(pe, ".pdb")); witness["runtimeConfig"] = Keep(Path.ChangeExtension(pe, ".runtimeconfig.json")); witness["runtime"] = Keep(runtime);
                        string peHash = FileHash(pe), runtimeHash = FileHash(runtime);
                        BoundedProcessResult managed = await Run(new(dotnet, [pe], directory, ProcessTimeout, Started)).ConfigureAwait(false); witness["managedRun"] = ProcessEvidence(managed);
                        Require(Complete(managed) && managed.ExitCode == (hand ? 41 : 0) && Normalize(managed.StandardOutput) == (hand ? "" : fixture.ExpectedOutput) && string.IsNullOrWhiteSpace(managed.StandardError), "Original PE CoreCLR behavior differs.");
                        string ilReport = Path.Combine(directory, "ilverify.json");
                        BoundedProcessResult il = await Run(new(pwsh, ["-NoLogo", "-NoProfile", "-File", Path.Combine(root, "eng", "Invoke-ILVerify.ps1"),
                            "-AssemblyPath", pe, "-RuntimeVersion", Environment.Version.ToString(), "-EvidencePath", ilReport, "-TimeoutSeconds", "175", "-ToolWorkingDirectory", temporary,
                            "-AdditionalReferencePath", runtime, "-RuntimeReferenceSha256", runtimeHash], root, VerifyTimeout, Started)).ConfigureAwait(false);
                        JsonObject ilEvidence = new() { ["status"] = "blocked", ["process"] = ProcessEvidence(il) }; witness["ilVerify"] = ilEvidence;
                        if (File.Exists(ilReport)) ilEvidence["report"] = Keep(ilReport);
                        Require(Complete(il) && il.Succeeded && File.Exists(ilReport), "Original PE ILVerify did not complete: " + il.StandardError.Trim());
                        JsonObject raw = Parse(Read(ilReport, 4194304), Stopwatch.StartNew(), deadline.Token); Require(Bool(raw, "Succeeded"), "ILVerify raw evidence did not succeed.");
                        Require(FileHash(pe) == peHash && FileHash(runtime) == runtimeHash, "ILVerify changed the original PE/runtime."); ilEvidence["status"] = "passed";
                        var hostInputs = new JsonArray(); string hostCapture = Path.Combine(directory, "native-inputs"); Directory.CreateDirectory(hostCapture);
                        void PublishStarted(BoundedProcessStarted process)
                        {
                            Started(process);
                            foreach (string name in new[] { "Program.cs", "RustSharp.NativeAotHost.csproj", assemblyName + ".dll", "RustSharp.Runtime.dll" })
                                hostInputs.Add(Retain(Path.Combine(process.WorkingDirectory, name), Path.Combine(hostCapture, name)));
                        }
                        NativeAotPublishResult publish = await new NativeAotPublisher(runner).PublishAsync(new NativeAotPublishRequest(pe, assemblyName, runtimeIdentifier,
                            Path.Combine(directory, "native"), PublishTimeout, PublishStarted) { HostSourceOverride = NativeHostSource(hand) }, deadline.Token).ConfigureAwait(false);
                        results.Add(ProcessEvidence(publish.ProcessResult)); JsonObject aot = new() { ["status"] = "blocked", ["publish"] = ProcessEvidence(publish.ProcessResult),
                            ["retainedHostInputs"] = hostInputs, ["hostDirectory"] = publish.HostDirectory, ["hostCleanupAttempted"] = publish.HostCleanupAttempted,
                            ["hostCleanupIncomplete"] = publish.HostCleanupIncomplete, ["hostDirectoryExists"] = Directory.Exists(publish.HostDirectory), ["hostCleanupDiagnostic"] = publish.HostCleanupDiagnostic };
                        witness["nativeAot"] = aot;
                        Require(publish.Succeeded && Complete(publish.ProcessResult) && publish.HostCleanupAttempted && !publish.HostCleanupIncomplete &&
                            !HasWarnings(publish.ProcessResult.StandardOutput + publish.ProcessResult.StandardError), "Native AOT publication/cleanup failed or produced warnings.");
                        Require(FileHash(pe) == peHash && FileHash(runtime) == runtimeHash, "Native AOT changed its original PE/runtime.");
                        NativeHeader(publish.ExecutablePath!, runtimeIdentifier); aot["executable"] = Keep(publish.ExecutablePath!);
                        BoundedProcessResult native = await Run(new(publish.ExecutablePath!, [], directory, ProcessTimeout, Started)).ConfigureAwait(false); aot["run"] = ProcessEvidence(native);
                        Require(Complete(native) && native.Succeeded && Normalize(native.StandardOutput) == fixture.ExpectedOutput && string.IsNullOrWhiteSpace(native.StandardError), "Native binary behavior differs from the original source/MIR.");
                        aot["status"] = "passed"; witness["status"] = "passed";
                    }
                    catch (Exception exception) when (Failure(exception)) { witness["reason"] = exception.Message; witness["status"] = exception is OperationCanceledException ? "blocked" : "failed"; }
                }
            }
            var ending = await P1SourcePackageEvidenceValidator.ValidateCandidateInputsAsync(root, options.CandidateSha,
                snapshotInput, releaseInput, runtimeIdentifier, Started, deadline.Token).ConfigureAwait(false);
            results.Add(ProcessEvidence(ending.Process)); final = ProcessEvidence(ending.Process);
            Require(Str(ending.Snapshot, "treeSha") == tree && JsonNode.DeepEquals(ending.Snapshot, snapshotRaw) && JsonNode.DeepEquals(ending.ReleaseBuild, buildRaw), "Candidate/build evidence changed during execution.");
        }
        catch (Exception exception) when (Failure(exception)) { failure = exception.Message; }
        finally
        {
            try
            {
                string full = Path.GetFullPath(temporary), tempParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                Require(Path.GetDirectoryName(full) == tempParent && Path.GetFileName(full).StartsWith("rsc-p1-backend-" + Environment.ProcessId + "-", StringComparison.Ordinal) &&
                    (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "Backend temporary cleanup ownership failed.");
                // Only the explicitly created tool manifest/global are removed; unexpected contents are retained and reported.
                string config = Path.Combine(full, ".config"), manifest = Path.Combine(config, "dotnet-tools.json"), global = Path.Combine(full, "global.json");
                if (File.Exists(manifest)) File.Delete(manifest); if (Directory.Exists(config)) Directory.Delete(config, false); if (File.Exists(global)) File.Delete(global); Directory.Delete(full, false);
            }
            catch (Exception exception) when (Failure(exception)) { cleanupDiagnostic = exception.Message; }
        }
        var cells = new JsonArray(); int passed = 0, failed = 0, blocked = 0;
        foreach (Fixture fixture in Fixtures) foreach (string backend in Backends) foreach (string rid in Rids)
        {
            bool foreign = rid != runtimeIdentifier; JsonObject[] matching = witnesses.Where(n => Str(n!.AsObject(), "fixtureId") == fixture.Id).Select(n => n!.AsObject()).ToArray();
            string status = foreign ? "blocked" : matching.Length != fixture.WitnessIds.Count ? "blocked" : matching.All(n => n[backend == "ilverify" ? "ilVerify" : "nativeAot"] is JsonObject value && value["status"]?.GetValue<string>() == "passed") ? "passed" : matching.Any(n => n["status"]?.GetValue<string>() == "failed") ? "failed" : "blocked";
            if (status == "passed") passed++; else if (status == "failed") failed++; else blocked++;
            cells.Add(new JsonObject { ["id"] = CellId(fixture, backend, rid), ["fixtureId"] = fixture.Id, ["requirementId"] = fixture.RequirementId, ["ownerLeaf"] = fixture.OwnerLeaf,
                ["originalCaseId"] = fixture.CaseId, ["backend"] = backend, ["runtimeIdentifier"] = rid, ["status"] = status,
                ["witnessIds"] = new JsonArray(fixture.WitnessIds.Select(n => (JsonNode)n).ToArray()), ["reason"] = foreign ? "Requires independent native " + rid + " execution." : status == "passed" ? null : "Both original behavior and all required backend witnesses must complete." });
        }
        bool cleanupComplete = cleanupDiagnostic is null && results.All(n => !Bool(n!.AsObject(), "cleanupIncomplete")) && results.Count == starts.Count;
        bool deadlineMet = clock.Elapsed < SuiteTimeout && !deadline.IsCancellationRequested, closed = passed == 12 && failed == 0 && blocked == 12 && maximumFixturesToExecute == 6 && failure is null && cleanupComplete && deadlineMet;
        var report = new JsonObject { ["schemaVersion"] = 1, ["profile"] = Profile, ["evidenceKind"] = "p1-exact-backend-gap-native-witnesses", ["candidateSha"] = options.CandidateSha, ["candidateTreeSha"] = tree,
            ["targetRuntimeIdentifier"] = runtimeIdentifier, ["hostRuntimeIdentifier"] = RuntimeInformation.RuntimeIdentifier, ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(), ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["nativeExecution"] = IsNativeHost(runtimeIdentifier), ["runtimeVersion"] = Environment.Version.ToString(), ["compilerProfile"] = "safe-core-mir-p1-v2", ["dropCleanupProfile"] = "legacy-v1", ["manifestSha256"] = manifestHash,
            ["retainedManifest"] = manifestArtifact, ["sourceSnapshot"] = snapshotArtifact, ["releaseBuild"] = buildArtifact, ["sourceSnapshotRaw"] = snapshotRaw?.DeepClone(), ["releaseBuildRaw"] = buildRaw?.DeepClone(),
            ["initialCandidateValidation"] = initial, ["finalCandidateValidation"] = final, ["implementationInputs"] = inputs, ["retainedBuildArtifacts"] = buildArtifacts, ["witnesses"] = witnesses, ["cells"] = cells, ["processStarts"] = starts, ["processResults"] = results, ["retainedArtifacts"] = artifacts,
            ["summary"] = new JsonObject { ["fixtureDenominator"] = 6, ["cellDenominator"] = 24, ["localCellDenominator"] = 12, ["maximumSelectedFixtures"] = maximumFixturesToExecute, ["passed"] = passed, ["failed"] = failed, ["blocked"] = blocked, ["localClosure"] = closed },
            ["execution"] = new JsonObject { ["startedAtUtc"] = started.ToString("O"), ["finishedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds, ["suiteTimeoutSeconds"] = 1200, ["deadlineMet"] = deadlineMet, ["cancelled"] = cancellationToken.IsCancellationRequested },
            ["cleanup"] = new JsonObject { ["completed"] = cleanupComplete, ["retainedEvidenceDirectory"] = runDirectory, ["temporaryDirectory"] = temporary, ["temporaryDirectoryExists"] = Directory.Exists(temporary), ["diagnostic"] = cleanupDiagnostic }, ["harnessError"] = failure };
        string writing = reportFile + "." + Environment.ProcessId + ".tmp";
        try { await File.WriteAllTextAsync(writing, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8, CancellationToken.None).ConfigureAwait(false); File.Move(writing, reportFile, false); }
        finally { if (File.Exists(writing)) File.Delete(writing); }
        Console.WriteLine($"P1 backend evidence: {reportFile}; passed={passed}, failed={failed}, blocked={blocked}; localClosure={closed}");
        return closed ? 0 : failed > 0 ? 1 : 2;
    }

    private static JsonObject Start(BoundedProcessStarted process) => new() { ["pid"] = process.ProcessId, ["parentPid"] = process.ParentProcessId, ["startedAtUtc"] = process.StartedAt.ToString("O"),
        ["fileName"] = process.FileName, ["arguments"] = new JsonArray(process.Arguments.Select(n => (JsonNode)n).ToArray()), ["commandLine"] = process.CommandLine, ["workingDirectory"] = process.WorkingDirectory };
    internal static JsonObject ProcessEvidence(BoundedProcessResult result)
    {
        JsonObject node = Start(result.StartedProcess); node["exitCode"] = result.ExitCode; node["termination"] = result.Termination.ToString(); node["elapsedMilliseconds"] = result.Elapsed.TotalMilliseconds;
        node["stdout"] = result.StandardOutput; node["stderr"] = result.StandardError; node["outputTruncated"] = result.OutputTruncated; node["outputReadTimedOut"] = result.OutputReadTimedOut;
        node["outputDrainTimedOut"] = result.OutputDrainTimedOut; node["outputReadLimitReached"] = result.OutputReadLimitReached; node["cleanupAttempted"] = result.ProcessTreeCleanupAttempted;
        node["cleanupIncomplete"] = result.ProcessTreeCleanupIncomplete; node["cleanupDiagnostic"] = result.ProcessTreeCleanupDiagnostic; return node;
    }
}
