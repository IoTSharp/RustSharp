using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Conformance;

/// <summary>Replays frozen, independently emitted source packages on a native x64 host.</summary>
internal static class P1SourcePackagePlatformRunner
{
    internal const int MaximumCases = 19;
    private const int MaximumSourceBytes = 65_536;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan SuiteTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private sealed record SourceSnapshot(string Path, byte[] Bytes, string Text, string Sha256);

    internal sealed record Result(string ReportPath, int Passed, int Failed, int Blocked,
        int NotExecuted, int Denominator, bool CleanupComplete)
    {
        public bool DeadlineMet { get; init; }
        public bool Succeeded => Passed == Denominator && Failed == 0 && Blocked == 0 &&
            NotExecuted == 0 && CleanupComplete && DeadlineMet;
    }

    internal static async Task<Result> RunAsync(string repositoryRoot, string manifestPath,
        string reportPath, int maximumCasesToExecute, CancellationToken cancellationToken = default)
    {
        if (maximumCasesToExecute is < 1 or > MaximumCases)
            throw new ArgumentOutOfRangeException(nameof(maximumCasesToExecute));
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new ArgumentException("Source package platforms require a native Windows or Linux x64 host.");
        string root = Path.GetFullPath(repositoryRoot);
        string manifestFile = ChildPath(root, manifestPath);
        string report = ChildPath(Path.Combine(root, "artifacts", "p1-source-package"),
            Path.GetFullPath(reportPath, root));
        if (new FileInfo(manifestFile).Length > 262_144)
            throw new ArgumentException("The source package manifest exceeds its 256 KiB limit.");
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestFile, cancellationToken).ConfigureAwait(false);
        JsonObject manifest = JsonNode.Parse(manifestBytes, documentOptions: new JsonDocumentOptions { MaxDepth = 16 })?.AsObject()
            ?? throw new ArgumentException("The source package manifest must be an object.");
        if (manifest["schemaVersion"]?.GetValue<int>() != 1 || Text(manifest, "profile") != "p1-source-packages-v1" ||
            manifest["cases"] is not JsonArray fixtures || fixtures.Count is < 1 or > MaximumCases ||
            manifest["denominator"]?.GetValue<int>() != fixtures.Count)
            throw new ArgumentException("The source package manifest has an unsupported schema or denominator.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var manifestClock = Stopwatch.StartNew();
        foreach (JsonNode? fixture in fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manifestClock.Elapsed > TimeSpan.FromSeconds(5))
                throw new ArgumentException("Source package manifest validation exceeded its five second budget.");
            if (fixture is not JsonObject item || !ids.Add(Text(item, "id")) ||
                !Regex.IsMatch(Text(item, "id"), "^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant, PatternTimeout))
                throw new ArgumentException("Source package cases require unique bounded identifiers.");
        }
        string evidenceDirectory = Path.Combine(Path.GetDirectoryName(report)!, "evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDirectory);
        string retainedManifest = Path.Combine(evidenceDirectory, "manifest.json");
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "rsc-p1-source-package-" + Guid.NewGuid().ToString("N"));
        string toolingDirectory = Path.Combine(temporaryDirectory, "tooling");
        var runner = new BoundedProcessRunner();
        var starts = new List<BoundedProcessStarted>(MaximumCases * 8 + 4);
        var processes = new List<BoundedProcessResult>(MaximumCases * 8 + 4);
        void OnStarted(BoundedProcessStarted start)
        {
            if (starts.Count >= MaximumCases * 8 + 4)
                throw new InvalidOperationException("The source package process denominator exceeded its fixed limit.");
            starts.Add(start);
            Console.WriteLine($"Source package process: pid={start.ProcessId} parent={start.ParentProcessId} started={start.StartedAt:O} command={start.CommandLine}");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SuiteTimeout);
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        var clock = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var outcomes = new JsonArray();
        var tools = new JsonObject();
        bool hostsClean = true;
        string? harnessError = null, temporaryCleanupError = null;
        string dotnet = Environment.GetEnvironmentVariable("RUSTSHARP_P1_DOTNET_PATH") ?? "dotnet";
        string pwsh = Environment.GetEnvironmentVariable("RUSTSHARP_P1_PWSH_PATH") ??
            (OperatingSystem.IsWindows() ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "pwsh");
        string runtimeIdentifier = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        string[] verifierPrefix = ["tool", "run", "ilverify", "--"];
        BoundedProcessResult? verifierProbe = null;
        try
        {
            await File.WriteAllBytesAsync(retainedManifest, manifestBytes, deadline.Token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(toolingDirectory, ".config"));
            File.Copy(Path.Combine(root, ".config", "dotnet-tools.json"), Path.Combine(toolingDirectory, ".config", "dotnet-tools.json"));
            string? sdk = Environment.GetEnvironmentVariable("RUSTSHARP_NATIVE_AOT_SDK_VERSION");
            if (sdk is not null)
            {
                if (!Regex.IsMatch(sdk, "^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant, PatternTimeout))
                    throw new ArgumentException("The native SDK selection requires a numeric version.");
                await File.WriteAllTextAsync(Path.Combine(toolingDirectory, "global.json"),
                    new JsonObject { ["sdk"] = new JsonObject { ["version"] = sdk, ["rollForward"] = "disable", ["allowPrerelease"] = false } }.ToJsonString(),
                    deadline.Token).ConfigureAwait(false);
            }
            BoundedProcessResult dotnetProbe = await runner.RunAsync(new(dotnet, ["--info"], toolingDirectory,
                TimeSpan.FromSeconds(30), OnStarted), deadline.Token).ConfigureAwait(false);
            processes.Add(dotnetProbe);
            tools["dotnet"] = Evidence(dotnetProbe);
            if (!dotnetProbe.Succeeded || !Complete(dotnetProbe))
                throw new InvalidOperationException("The native SDK preflight failed.");
            if (OperatingSystem.IsWindows())
            {
                if (!string.Equals(Path.GetFileName(pwsh), "pwsh.exe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Windows source package verification requires an explicitly selected PowerShell 7 pwsh.exe.");
                BoundedProcessResult pwshProbe = await runner.RunAsync(new(pwsh,
                    ["-NoLogo", "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString(); if ($PSVersionTable.PSVersion.Major -lt 7) { exit 1 }"],
                    toolingDirectory, TimeSpan.FromSeconds(30), OnStarted), deadline.Token).ConfigureAwait(false);
                processes.Add(pwshProbe);
                tools["powershell"] = Evidence(pwshProbe);
                if (!pwshProbe.Succeeded || !Complete(pwshProbe))
                    throw new InvalidOperationException("The Windows PowerShell 7 preflight failed.");
            }
            else if (Environment.GetEnvironmentVariable("RUSTSHARP_P1_ILVERIFY_DLL_PATH") is { } verifierDll)
            {
                string fullVerifier = Path.GetFullPath(verifierDll);
                if (!Path.IsPathFullyQualified(verifierDll) || !File.Exists(fullVerifier) ||
                    new FileInfo(fullVerifier).Length is < 1 or > 16_777_216 || Path.GetExtension(fullVerifier) != ".dll")
                    throw new ArgumentException("The configured native ILVerify DLL requires an existing absolute DLL path below 16 MiB.");
                verifierPrefix = [fullVerifier];
                tools["ilVerifyAssembly"] = Artifact(fullVerifier);
            }
            verifierProbe = await runner.RunAsync(new(dotnet, [.. verifierPrefix, "--version"], toolingDirectory,
                TimeSpan.FromSeconds(30), OnStarted), deadline.Token).ConfigureAwait(false);
            processes.Add(verifierProbe);
            tools["ilVerifyVersion"] = Evidence(verifierProbe);
            if (!verifierProbe.Succeeded || !Complete(verifierProbe) ||
                !Regex.IsMatch(verifierProbe.StandardOutput.Trim(), "^10\\.0\\.11(?:[-+\\s]|$)", RegexOptions.CultureInvariant, PatternTimeout))
                throw new InvalidOperationException("Native ILVerify 10.0.11 is unavailable or its bounded version probe failed; no tool is installed by this runner.");
            int selectedCount = Math.Min(maximumCasesToExecute, fixtures.Count);
            for (int index = 0; index < selectedCount && clock.Elapsed < SuiteTimeout; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                JsonObject fixture = fixtures[index]!.AsObject();
                string id = Text(fixture, "id");
                Console.WriteLine($"Source package platform {index + 1}/{fixtures.Count}: {id}");
                var outcome = new JsonObject { ["id"] = id, ["status"] = "blocked" };
                outcomes.Add(outcome);
                try
                {
                    SourceSnapshot producerSource = await ReadSourceAsync(root, fixture, "producer", deadline.Token).ConfigureAwait(false);
                    SourceSnapshot consumerSource = await ReadSourceAsync(root, fixture, "consumer", deadline.Token).ConfigureAwait(false);
                    bool hasWrapper = fixture["wrapperSource"] is not null;
                    SourceSnapshot? wrapperSource = hasWrapper
                        ? await ReadSourceAsync(root, fixture, "wrapper", deadline.Token).ConfigureAwait(false) : null;
                    string producerName = AssemblyName(fixture, "producerAssembly");
                    string consumerName = AssemblyName(fixture, "consumerAssembly");
                    string? wrapperName = hasWrapper ? AssemblyName(fixture, "wrapperAssembly") : null;
                    if (producerName == consumerName || wrapperName == producerName || wrapperName == consumerName)
                        throw new ArgumentException("All source package assembly identities must differ.");
                    CompilationProfile profile = Text(fixture, "compilerProfile") switch
                    {
                        "safe-core-primitives-v1" => CompilationProfile.SafeCorePrimitives,
                        "safe-core-mir-p1-v2" => CompilationProfile.SafeCoreMirV2,
                        _ => throw new ArgumentException("Unsupported source package compiler profile."),
                    };
                    SafeCorePanicStrategy producerPanicStrategy = (fixture["producerPanicStrategy"]?.GetValue<string>() ?? "unwind") switch
                    {
                        "unwind" => SafeCorePanicStrategy.Unwind,
                        "abort" when profile == CompilationProfile.SafeCoreMirV2 => SafeCorePanicStrategy.Abort,
                        _ => throw new ArgumentException("The producer panic strategy must be unwind or a MIR v2 abort strategy."),
                    };
                    string expectedOutput = Text(fixture, "expectedOutput");
                    if (StrictUtf8.GetByteCount(expectedOutput) > MaximumSourceBytes)
                        throw new ArgumentException("The expected source package trace exceeds 64 KiB.");
                    string expectedOutcome = Text(fixture, "expectedOutcome");
                    if (expectedOutcome is not ("success" or "unwound" or "aborted" or "double-panic"))
                        throw new ArgumentException("Unsupported source package execution outcome.");
                    string[] required = RequiredFunctions(fixture, "requiredFunctions");
                    string[] wrapperRequired = hasWrapper ? RequiredFunctions(fixture, "requiredWrapperFunctions") : [];
                    string caseDirectory = Path.Combine(temporaryDirectory, id);
                    string retained = Path.Combine(evidenceDirectory, id);
                    Directory.CreateDirectory(retained);
                    outcome["sourceProducer"] = new JsonObject { ["path"] = producerSource.Path, ["sha256"] = producerSource.Sha256 };
                    outcome["sourceConsumer"] = new JsonObject { ["path"] = consumerSource.Path, ["sha256"] = consumerSource.Sha256 };
                    if (wrapperSource is not null)
                        outcome["sourceWrapper"] = new JsonObject { ["path"] = wrapperSource.Path, ["sha256"] = wrapperSource.Sha256 };
                    outcome["compilerProfile"] = Text(fixture, "compilerProfile");
                    outcome["producerPanicStrategy"] = producerPanicStrategy == SafeCorePanicStrategy.Abort ? "abort" : "unwind";
                    outcome["expectedOutput"] = expectedOutput;
                    outcome["expectedOutcome"] = expectedOutcome;
                    var builds = new JsonArray();
                    outcome["independentBuilds"] = builds;
                    string firstProducer = "", firstConsumer = "";
                    string? firstWrapper = null;
                    using var compileDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    compileDeadline.CancelAfter(ProcessTimeout);
                    for (int build = 0; build < 2; build++)
                    {
                        compileDeadline.Token.ThrowIfCancellationRequested();
                        string directory = Path.Combine(caseDirectory, "build-" + build);
                        Directory.CreateDirectory(directory);
                        string producer = Path.Combine(directory, producerName + ".dll");
                        string consumer = Path.Combine(directory, consumerName + ".dll");
                        string? wrapper = hasWrapper ? Path.Combine(directory, wrapperName + ".dll") : null;
                        CompilationResult producerResult = CompilerDriver.CompileWithPanicStrategy(producerSource.Text,
                            producerSource.Path, producer, producerPanicStrategy, producerName, profile, compileDeadline.Token);
                        var compilation = new JsonObject { ["producer"] = CompileEvidence(producerResult, producer) };
                        builds.Add(compilation);
                        if (!producerResult.Success) { outcome["status"] = "failed"; break; }
                        RustSharpMetadataImportResult producerMetadata = RustSharpMetadataConsumer.ReadAssembly(producer,
                            Text(fixture, "compilerProfile"), required, cancellationToken: compileDeadline.Token);
                        if (!producerMetadata.IsSuccessful || producerMetadata.Document!.SourceSha256 != producerSource.Sha256)
                            throw new InvalidOperationException("Fresh producer metadata does not reconcile with the frozen source: " +
                                string.Join("; ", producerMetadata.Diagnostics));
                        string producerMetadataPath = Path.Combine(retained, "producer-metadata-build-" + build + ".json");
                        await File.WriteAllTextAsync(producerMetadataPath, producerMetadata.Document!.Json,
                            compileDeadline.Token).ConfigureAwait(false);
                        compilation["producerMetadata"] = Artifact(producerMetadataPath);
                        RustSharpMetadataImportResult? wrapperMetadata = null;
                        if (wrapper is not null)
                        {
                            CompilationResult wrapperResult = CompilerDriver.CompileWithMetadataReferences(
                                wrapperSource!.Text, wrapperSource.Path, wrapper, wrapperName!, profile,
                                [producer], required, compileDeadline.Token);
                            compilation["wrapper"] = CompileEvidence(wrapperResult, wrapper);
                            if (!wrapperResult.Success) { outcome["status"] = "failed"; break; }
                            wrapperMetadata = RustSharpMetadataConsumer.ReadAssembly(wrapper,
                                Text(fixture, "compilerProfile"), wrapperRequired, [producer], compileDeadline.Token);
                            if (!wrapperMetadata.IsSuccessful || wrapperMetadata.Document!.SourceSha256 != wrapperSource.Sha256)
                                throw new InvalidOperationException("Fresh wrapper metadata does not reconcile with the frozen source: " +
                                    string.Join("; ", wrapperMetadata.Diagnostics));
                            string wrapperMetadataPath = Path.Combine(retained, "wrapper-metadata-build-" + build + ".json");
                            await File.WriteAllTextAsync(wrapperMetadataPath, wrapperMetadata.Document!.Json,
                                compileDeadline.Token).ConfigureAwait(false);
                            compilation["wrapperMetadata"] = Artifact(wrapperMetadataPath);
                            compilation["wrapperResolvedOwners"] = VerifyRequiredOwners(fixture, "requiredWrapperOwnerAssemblies",
                                wrapperMetadata, producerMetadata, producer, compileDeadline.Token);
                            compilation["wrapperImportedMemberRefs"] = VerifyMemberRefs(wrapper, producerName,
                                producerMetadata, required, compileDeadline.Token);
                        }
                        string[] dependencyPaths = wrapper is null ? [producer] : [producer, wrapper];
                        CompilationResult consumerResult = CompilerDriver.CompileWithMetadataReferences(
                            consumerSource.Text, consumerSource.Path,
                            consumer, consumerName, profile, dependencyPaths, hasWrapper ? null : required, compileDeadline.Token);
                        compilation["consumer"] = CompileEvidence(consumerResult, consumer);
                        if (!consumerResult.Success) { outcome["status"] = "failed"; break; }
                        RustSharpMetadataImportResult consumerMetadata = RustSharpMetadataConsumer.ReadAssembly(consumer,
                            Text(fixture, "compilerProfile"), dependencyPaths: dependencyPaths, cancellationToken: compileDeadline.Token);
                        if (!consumerMetadata.IsSuccessful || consumerMetadata.Document!.SourceSha256 != consumerSource.Sha256)
                            throw new InvalidOperationException("Fresh consumer metadata does not reconcile with the frozen source: " +
                                string.Join("; ", consumerMetadata.Diagnostics));
                        string consumerMetadataPath = Path.Combine(retained, "consumer-metadata-build-" + build + ".json");
                        await File.WriteAllTextAsync(consumerMetadataPath, consumerMetadata.Document!.Json,
                            compileDeadline.Token).ConfigureAwait(false);
                        compilation["consumerMetadata"] = Artifact(consumerMetadataPath);
                        compilation["consumerResolvedOwners"] = VerifyRequiredOwners(fixture, "requiredConsumerOwnerAssemblies", consumerMetadata,
                            producerMetadata, producer, compileDeadline.Token);
                        compilation["sourceHashesReconciled"] = true;
                        string[] consumerRequired = hasWrapper ? wrapperRequired : required;
                        if (fixture["requiredConsumerDropTypes"] is not null)
                        {
                            if (hasWrapper) throw new ArgumentException("Required direct consumer Drop helpers currently target the original producer.");
                            consumerRequired = [.. required, .. RequiredDropFunctions(fixture, producerMetadata, compileDeadline.Token)];
                        }
                        compilation["importedMemberRefs"] = VerifyMemberRefs(consumer, wrapperName ?? producerName,
                            wrapperMetadata ?? producerMetadata, consumerRequired, compileDeadline.Token);
                        string retainedBuild = Path.Combine(retained, "build-" + build);
                        Directory.CreateDirectory(retainedBuild);
                        RetainPackage(producer, consumer, producerSource, consumerSource, wrapper, wrapperSource,
                            retainedBuild, compileDeadline.Token);
                        compilation["retainedOriginalProducer"] = Artifact(Path.Combine(retainedBuild, Path.GetFileName(producer)));
                        compilation["retainedOriginalConsumer"] = Artifact(Path.Combine(retainedBuild, Path.GetFileName(consumer)));
                        compilation["retainedOriginalProducerPdb"] = Artifact(Path.Combine(retainedBuild, Path.GetFileName(Path.ChangeExtension(producer, ".pdb"))));
                        compilation["retainedOriginalConsumerPdb"] = Artifact(Path.Combine(retainedBuild, Path.GetFileName(Path.ChangeExtension(consumer, ".pdb"))));
                        if (wrapper is not null)
                        {
                            compilation["retainedOriginalWrapper"] = Artifact(Path.Combine(retainedBuild, Path.GetFileName(wrapper)));
                            compilation["retainedOriginalWrapperPdb"] = Artifact(Path.Combine(retainedBuild, Path.GetFileName(Path.ChangeExtension(wrapper, ".pdb"))));
                        }
                        if (build == 0) { firstProducer = producer; firstConsumer = consumer; firstWrapper = wrapper; }
                        else
                        {
                            bool identical = EqualArtifacts(firstProducer, producer) && EqualArtifacts(firstConsumer, consumer) &&
                                (wrapper is null || EqualArtifacts(firstWrapper!, wrapper));
                            outcome["independentArtifactsIdentical"] = identical;
                            if (!identical) { outcome["status"] = "failed"; break; }
                        }
                    }
                    if (outcome["status"]!.GetValue<string>() == "failed") continue;
                    RetainPackage(firstProducer, firstConsumer, producerSource, consumerSource, firstWrapper, wrapperSource, retained, deadline.Token);
                    outcome["originalProducer"] = Artifact(Path.Combine(retained, Path.GetFileName(firstProducer)));
                    outcome["originalConsumer"] = Artifact(Path.Combine(retained, Path.GetFileName(firstConsumer)));
                    if (firstWrapper is not null)
                        outcome["originalWrapper"] = Artifact(Path.Combine(retained, Path.GetFileName(firstWrapper)));
                    BoundedProcessResult core = await runner.RunAsync(new(dotnet, [firstConsumer], Path.GetDirectoryName(firstConsumer)!,
                        ProcessTimeout, OnStarted), deadline.Token).ConfigureAwait(false);
                    processes.Add(core);
                    outcome["coreClr"] = Evidence(core);
                    JsonObject producerVerify = await VerifyAsync(runner, root, dotnet, pwsh, verifierPrefix, verifierProbe,
                        toolingDirectory, firstProducer, firstWrapper is null ? [firstConsumer] : [firstWrapper, firstConsumer], Path.Combine(retained, "producer-ilverify.json"),
                        processes, OnStarted, deadline.Token).ConfigureAwait(false);
                    JsonObject consumerVerify = await VerifyAsync(runner, root, dotnet, pwsh, verifierPrefix, verifierProbe,
                        toolingDirectory, firstConsumer, firstWrapper is null ? [firstProducer] : [firstProducer, firstWrapper], Path.Combine(retained, "consumer-ilverify.json"),
                        processes, OnStarted, deadline.Token).ConfigureAwait(false);
                    outcome["producerIlVerify"] = producerVerify;
                    outcome["consumerIlVerify"] = consumerVerify;
                    JsonObject? wrapperVerify = null;
                    if (firstWrapper is not null)
                    {
                        wrapperVerify = await VerifyAsync(runner, root, dotnet, pwsh, verifierPrefix, verifierProbe,
                            toolingDirectory, firstWrapper, [firstProducer, firstConsumer], Path.Combine(retained, "wrapper-ilverify.json"),
                            processes, OnStarted, deadline.Token).ConfigureAwait(false);
                        outcome["wrapperIlVerify"] = wrapperVerify;
                    }
                    const string nativeHost = "namespace RustSharp.NativeAotHost;\n" +
                        "internal static class EntryPoint { private static void Main() => global::RustSharp.Generated.Program.Main(); }\n";
                    string hostSource = Path.Combine(retained, "native-host.cs");
                    await File.WriteAllTextAsync(hostSource, nativeHost, deadline.Token).ConfigureAwait(false);
                    outcome["nativeHostSource"] = Artifact(hostSource);
                    outcome["nativeAdditionalAssembly"] = Artifact(firstProducer);
                    string[] nativeReferences = firstWrapper is null ? [firstProducer] : [firstProducer, firstWrapper];
                    outcome["nativeAdditionalAssemblies"] = new JsonArray(nativeReferences.Select(Artifact).ToArray());
                    NativeAotPublishResult native = await new NativeAotPublisher(runner).PublishAsync(
                        new(firstConsumer, consumerName, runtimeIdentifier, Path.Combine(caseDirectory, "native"), PublishTimeout,
                            OnStarted, AdditionalAssemblyPaths: nativeReferences) { HostSourceOverride = nativeHost }, deadline.Token).ConfigureAwait(false);
                    processes.Add(native.ProcessResult);
                    hostsClean &= native.HostCleanupAttempted && !native.HostCleanupIncomplete && !Directory.Exists(native.HostDirectory);
                    outcome["nativePublish"] = Evidence(native.ProcessResult);
                    int nativeWarnings = Regex.Count(native.ProcessResult.StandardOutput + native.ProcessResult.StandardError,
                        "\\bwarning [A-Z]+[0-9]+:", RegexOptions.CultureInvariant, PatternTimeout);
                    outcome["nativeWarningCount"] = nativeWarnings;
                    outcome["hostCleanup"] = new JsonObject { ["directory"] = native.HostDirectory, ["attempted"] = native.HostCleanupAttempted,
                        ["incomplete"] = native.HostCleanupIncomplete, ["existsAfterCleanup"] = Directory.Exists(native.HostDirectory),
                        ["diagnostic"] = native.HostCleanupDiagnostic };
                    BoundedProcessResult? nativeRun = null;
                    if (native.Succeeded && Complete(native.ProcessResult) && native.ExecutablePath is { } executable)
                    {
                        nativeRun = await runner.RunAsync(new(executable, [], Path.GetDirectoryName(executable)!, ProcessTimeout, OnStarted),
                            deadline.Token).ConfigureAwait(false);
                        processes.Add(nativeRun);
                        string keptExecutable = Path.Combine(retained, Path.GetFileName(executable));
                        File.Copy(executable, keptExecutable);
                        outcome["nativeExecutable"] = Artifact(keptExecutable);
                    }
                    outcome["nativeRun"] = Evidence(nativeRun);
                    bool complete = Complete(core) && Complete(native.ProcessResult) && nativeRun is not null && Complete(nativeRun) &&
                        !native.HostCleanupIncomplete;
                    bool match = Match(core, expectedOutput, expectedOutcome) && nativeRun is not null && Match(nativeRun, expectedOutput, expectedOutcome);
                    bool verified = Text(producerVerify, "status") == "passed" && Text(consumerVerify, "status") == "passed" &&
                        (wrapperVerify is null || Text(wrapperVerify, "status") == "passed");
                    outcome["status"] = !complete || Text(producerVerify, "status") == "blocked" || Text(consumerVerify, "status") == "blocked" ||
                        wrapperVerify is not null && Text(wrapperVerify, "status") == "blocked"
                        ? "blocked" : native.Succeeded && nativeWarnings == 0 && match && verified ? "passed" : "failed";
                }
                catch (Exception exception) when (ExpectedFailure(exception))
                {
                    outcome["status"] = "blocked";
                    outcome["difference"] = exception.Message;
                }
            }
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { harnessError = exception.Message; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            temporaryCleanupError = await CleanupAsync(temporaryDirectory).ConfigureAwait(false);
        }
        var finalizationClock = Stopwatch.StartNew();
        for (int index = outcomes.Count; index < fixtures.Count && finalizationClock.Elapsed < TimeSpan.FromSeconds(5); index++)
            outcomes.Add(new JsonObject { ["id"] = Text(fixtures[index]!.AsObject(), "id"),
                ["status"] = harnessError is null && !deadline.IsCancellationRequested ? "not-executed" : "blocked",
                ["difference"] = harnessError ?? "Outside the selected maximum or suite deadline; no platform pass is claimed." });
        if (outcomes.Count != fixtures.Count)
            throw new InvalidOperationException("Finalizing source package case outcomes exceeded its five second budget.");
        int passed = outcomes.Count(static value => value?["status"]?.GetValue<string>() == "passed");
        int failed = outcomes.Count(static value => value?["status"]?.GetValue<string>() == "failed");
        int blocked = outcomes.Count(static value => value?["status"]?.GetValue<string>() == "blocked");
        int notExecuted = fixtures.Count - passed - failed - blocked;
        bool cleanup = temporaryCleanupError is null && hostsClean && processes.All(static process => !process.ProcessTreeCleanupIncomplete) &&
            starts.All(start => processes.Any(process => process.StartedProcess.ProcessId == start.ProcessId && process.StartedProcess.StartedAt == start.StartedAt));
        bool withinDeadline = !deadline.IsCancellationRequested && clock.Elapsed <= SuiteTimeout;
        var result = new Result(report, passed, failed, blocked, notExecuted, fixtures.Count, cleanup) { DeadlineMet = withinDeadline };
        var document = new JsonObject
        {
            ["schemaVersion"] = 1, ["evidenceKind"] = "p1-source-package-original-pe-coreclr-ilverify-native-aot",
            ["profile"] = Text(manifest, "profile"), ["manifestPath"] = manifestFile,
            ["manifestSha256"] = Convert.ToHexString(SHA256.HashData(manifestBytes)),
            ["retainedManifest"] = File.Exists(retainedManifest) ? Artifact(retainedManifest) : null,
            ["runner"] = Artifact(typeof(P1SourcePackagePlatformRunner).Assembly.Location),
            ["compiler"] = Artifact(typeof(CompilerDriver).Assembly.Location),
            ["runtime"] = Artifact(typeof(RustSharp.Runtime.RustPanicBoundary).Assembly.Location),
            ["implementationAssemblies"] = new JsonArray(new JsonObject[]
            {
                Artifact(typeof(RustSharp.Semantics.SafeCoreMirPipeline).Assembly.Location),
                Artifact(typeof(ClrLirAssemblyEmitter).Assembly.Location),
                Artifact(typeof(RustSharp.Syntax.SafeCoreSyntax).Assembly.Location),
            }),
            ["targetRuntimeIdentifier"] = runtimeIdentifier, ["hostRuntimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(), ["runtimeVersion"] = Environment.Version.ToString(),
            ["tools"] = tools, ["cases"] = outcomes,
            ["summary"] = new JsonObject { ["status"] = result.Succeeded ? "passed" : blocked > 0 || !cleanup || !withinDeadline ? "blocked" : "failed",
                ["denominator"] = fixtures.Count, ["passed"] = passed, ["failed"] = failed, ["blocked"] = blocked,
                ["notExecuted"] = notExecuted, ["maximumSelectedCases"] = maximumCasesToExecute, ["succeeded"] = result.Succeeded },
            ["execution"] = new JsonObject { ["startedAtUtc"] = startedAt, ["finishedAtUtc"] = DateTimeOffset.UtcNow,
                ["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds, ["suiteTimeoutSeconds"] = SuiteTimeout.TotalSeconds,
                ["processTimeoutSeconds"] = ProcessTimeout.TotalSeconds, ["publishTimeoutSeconds"] = PublishTimeout.TotalSeconds,
                ["deadlineExpired"] = deadline.IsCancellationRequested, ["deadlineMet"] = withinDeadline },
            ["cleanup"] = new JsonObject { ["completed"] = cleanup, ["temporaryDirectory"] = temporaryDirectory,
                ["temporaryDirectoryExists"] = Directory.Exists(temporaryDirectory), ["diagnostic"] = temporaryCleanupError,
                ["retainedEvidenceDirectory"] = evidenceDirectory },
            ["processStarts"] = new JsonArray(starts.Select(static start => (JsonNode)new JsonObject { ["pid"] = start.ProcessId,
                ["parentPid"] = start.ParentProcessId, ["startedAtUtc"] = start.StartedAt, ["commandLine"] = start.CommandLine }).ToArray()),
            ["processResults"] = new JsonArray(processes.Select(static process => (JsonNode)Evidence(process)!).ToArray()),
            ["harnessError"] = harnessError,
        };
        string staging = report + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(staging, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None).ConfigureAwait(false);
            File.Move(staging, report, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        Console.WriteLine($"Source package platform report: {report}; passed={passed}/{fixtures.Count}, failed={failed}, blocked={blocked}, cleanup={cleanup}");
        return result;
    }

    private static async Task<JsonObject> VerifyAsync(BoundedProcessRunner runner, string root, string dotnet, string pwsh,
        string[] verifierPrefix, BoundedProcessResult versionProbe, string tooling, string assembly, string[] otherAssemblies,
        string report, List<BoundedProcessResult> processes, Action<BoundedProcessStarted> onStarted,
        CancellationToken cancellationToken)
    {
        string runtime = Path.Combine(Path.GetDirectoryName(assembly)!, "RustSharp.Runtime.dll");
        if (!File.Exists(runtime)) throw new InvalidOperationException("Fresh source package runtime reference is missing.");
        string[] references = [.. otherAssemblies, runtime];
        if (references.Any(static reference => reference.Contains(';', StringComparison.Ordinal)))
            throw new ArgumentException("ILVerify reference paths cannot contain semicolons.");
        BoundedProcessResult process;
        if (OperatingSystem.IsWindows())
        {
            process = await runner.RunAsync(new(pwsh,
                ["-NoLogo", "-NoProfile", "-File", Path.Combine(root, "eng", "Invoke-ILVerify.ps1"),
                    "-AssemblyPath", assembly, "-RuntimeVersion", Environment.Version.ToString(), "-EvidencePath", report,
                    "-TimeoutSeconds", "120", "-ToolWorkingDirectory", tooling,
                    "-AdditionalReferencePath", string.Join(';', references)], root, ProcessTimeout, onStarted), cancellationToken).ConfigureAwait(false);
            processes.Add(process);
        }
        else
        {
            var referenceClock = Stopwatch.StartNew();
            string framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            string[] frameworkReferences = Directory.EnumerateFiles(framework, "*.dll", SearchOption.TopDirectoryOnly)
                .Take(513).Order(StringComparer.Ordinal).ToArray();
            if (frameworkReferences.Length is < 1 or > 512 ||
                !frameworkReferences.Contains(typeof(object).Assembly.Location, StringComparer.Ordinal))
                throw new InvalidOperationException("The native host framework reference denominator is invalid.");
            string[] allReferences = [.. frameworkReferences, .. references];
            var referenceArtifacts = new JsonArray();
            foreach (string reference in allReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (referenceClock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException("Native verifier reference hashing exceeded its five second budget.");
                referenceArtifacts.Add(Artifact(reference));
            }
            var arguments = new List<string>(verifierPrefix.Length + 3 + allReferences.Length * 2);
            arguments.AddRange(verifierPrefix);
            arguments.AddRange(["-s", "System.Private.CoreLib"]);
            foreach (string reference in allReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (referenceClock.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException("Native verifier reference setup exceeded its five second budget.");
                arguments.AddRange(["-r", reference]);
            }
            arguments.AddRange(["--statistics", assembly]);
            process = await runner.RunAsync(new(dotnet, arguments, tooling, ProcessTimeout, onStarted), cancellationToken).ConfigureAwait(false);
            processes.Add(process);
            var nativeEvidence = new JsonObject
            {
                ["SchemaVersion"] = 1, ["Succeeded"] = process.Succeeded && Complete(process) &&
                    process.StandardOutput.Contains("All Classes and Methods in", StringComparison.Ordinal) &&
                    process.StandardOutput.Contains("Verified.", StringComparison.Ordinal),
                ["Assembly"] = new JsonObject { ["Path"] = assembly, ["Sha256"] = Hash(assembly) },
                ["Tool"] = new JsonObject { ["PackageId"] = "dotnet-ilverify", ["Version"] = "10.0.11",
                    ["InvocationPrefix"] = new JsonArray(verifierPrefix.Select(static value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                    ["VersionProbe"] = Evidence(versionProbe) },
                ["Environment"] = new JsonObject { ["DotnetPath"] = dotnet, ["RuntimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
                    ["ProcessArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(), ["PowerShellInvoked"] = false },
                ["Verification"] = new JsonObject { ["SystemModule"] = "System.Private.CoreLib",
                    ["SystemModulePath"] = typeof(object).Assembly.Location, ["ReferenceDirectory"] = framework,
                    ["ReferenceArtifacts"] = referenceArtifacts,
                    ["AdditionalReferencePath"] = new JsonArray(references.Select(static value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                    ["RuntimeVersion"] = Environment.Version.ToString(), ["TimeoutSeconds"] = ProcessTimeout.TotalSeconds },
                ["VerifyProcess"] = new JsonObject { ["ExitCode"] = process.ExitCode, ["Termination"] = process.Termination.ToString(),
                    ["StandardOutputTruncated"] = process.StandardOutputTruncated,
                    ["StandardErrorTruncated"] = process.StandardErrorTruncated, ["OutputDrainTimedOut"] = process.OutputDrainTimedOut,
                    ["ProcessTreeCleanupIncomplete"] = process.ProcessTreeCleanupIncomplete,
                    ["NativeProcess"] = Evidence(process) },
            };
            await File.WriteAllTextAsync(report, nativeEvidence.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
        }
        JsonObject? raw = File.Exists(report) && new FileInfo(report).Length <= 1_048_576
            ? JsonNode.Parse(await File.ReadAllTextAsync(report, cancellationToken).ConfigureAwait(false),
                documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject() : null;
        JsonNode? verifier = raw?["VerifyProcess"];
        bool clean = verifier?["ProcessTreeCleanupIncomplete"]?.GetValue<bool>() == false &&
            verifier?["StandardOutputTruncated"]?.GetValue<bool>() == false &&
            verifier?["StandardErrorTruncated"]?.GetValue<bool>() == false &&
            verifier?["OutputDrainTimedOut"]?.GetValue<bool>() == false &&
            verifier?["ExitCode"]?.GetValue<int>() == 0 && verifier?["Termination"]?.GetValue<string>() == "Exited";
        bool bound = raw?["Assembly"]?["Sha256"]?.GetValue<string>() == Hash(assembly) &&
            raw?["Tool"]?["Version"]?.GetValue<string>() == "10.0.11" &&
            raw?["Verification"]?["SystemModule"]?.GetValue<string>() == "System.Private.CoreLib" &&
            raw?["Verification"]?["RuntimeVersion"]?.GetValue<string>() == Environment.Version.ToString() &&
            raw?["Verification"]?["AdditionalReferencePath"] is JsonArray additional &&
            references.All(reference => additional.Any(value => value?.GetValue<string>() == reference));
        bool verified = process.Succeeded && Complete(process) && raw?["Succeeded"]?.GetValue<bool>() == true && clean && bound;
        return new JsonObject { ["status"] = verified ? "passed" : raw is null || !Complete(process) ? "blocked" : "failed",
            ["originalAssembly"] = Artifact(assembly), ["additionalReferenceArtifacts"] = new JsonArray(references.Select(Artifact).ToArray()),
            ["process"] = Evidence(process), ["rawEvidence"] = raw, ["originalPeAndReferenceBindingMatches"] = bound,
            ["report"] = File.Exists(report) ? Artifact(report) : null };
    }

    private static JsonObject CompileEvidence(CompilationResult result, string output) => new()
    {
        ["success"] = result.Success, ["originalAssembly"] = File.Exists(output) ? Artifact(output) : null,
        ["originalPdb"] = File.Exists(Path.ChangeExtension(output, ".pdb")) ? Artifact(Path.ChangeExtension(output, ".pdb")) : null,
        ["diagnostics"] = new JsonArray(result.Diagnostics.Select(static value => (JsonNode)new JsonObject
            { ["code"] = value.Code, ["message"] = value.Message, ["sourcePath"] = value.SourcePath,
                ["spanStart"] = value.Span.Start, ["spanLength"] = value.Span.Length }).ToArray()),
    };

    private static JsonArray VerifyMemberRefs(string consumer, string producerName, RustSharpMetadataImportResult producer,
        string[] requiredFunctions, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(consumer);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        if (reader.MemberReferences.Count > 512)
            throw new InvalidOperationException("The consumer MemberRef table exceeds 512 rows.");
        var expected = new Dictionary<string, RustSharpMetadataFunction>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        foreach (string required in requiredFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Required source function resolution exceeded its five second budget.");
            RustSharpMetadataFunction function = RustSharpMetadataConsumer.FindFunction(producer.Document!, required)
                ?? throw new InvalidOperationException("The validated producer has no required function " + required + ".");
            if (!function.IsPublic || !expected.TryAdd(function.Name, function))
                throw new InvalidOperationException("Required function exports must be public and have distinct CLR names.");
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new JsonArray();
        var provider = new MemberRefSignatureProvider(producerName, producer.ResolvedOwnerPaths);
        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Consumer MemberRef validation exceeded its five second budget.");
            MemberReference member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            TypeReference declaringType = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (declaringType.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            AssemblyReference assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)declaringType.ResolutionScope);
            if (reader.GetString(assembly.Name) != producerName) continue;
            string name = reader.GetString(member.Name);
            if (!expected.TryGetValue(name, out RustSharpMetadataFunction? function)) continue;
            if (reader.GetString(declaringType.Namespace) != "RustSharp.Generated" || reader.GetString(declaringType.Name) != "Program" ||
                member.GetKind() != MemberReferenceKind.Method)
                throw new InvalidOperationException("An imported function MemberRef has the wrong producer declaring type.");
            MethodSignature<string> signature = member.DecodeMethodSignature(provider, genericContext: null);
            string actual = string.Join(",", signature.ParameterTypes) + "->" + signature.ReturnType;
            if (signature.Header.IsInstance || signature.Header.CallingConvention != SignatureCallingConvention.Default ||
                signature.GenericParameterCount != 0 || actual != function.Signature)
                throw new InvalidOperationException("Imported function MemberRef disagrees with the actual producer MethodDef: " + name + ".");
            if (found.Add(name)) evidence.Add(new JsonObject { ["sourceFunction"] = function.SourceQualifiedName,
                ["assembly"] = producerName, ["declaringType"] = "RustSharp.Generated.Program", ["name"] = name,
                ["signature"] = actual, ["producerMethodDefValidated"] = true });
        }
        if (found.Count != expected.Count)
            throw new InvalidOperationException("Consumer PE omits a required imported MemberRef: " + string.Join(", ", expected.Keys.Except(found)));
        return evidence;
    }

    private static JsonArray VerifyRequiredOwners(JsonObject fixture, string requirementMember, RustSharpMetadataImportResult consumer,
        RustSharpMetadataImportResult producer, string producerPath, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        if (fixture[requirementMember] is not { } requirement) return result;
        if (requirement is not JsonArray required || required.Count is < 1 or > 16)
            throw new ArgumentException("Required consumer owners must be a bounded array of one to sixteen identities.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        string producerSha256 = Hash(producerPath);
        foreach (JsonNode? value in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Consumer source owner proof reconciliation exceeded its five second budget.");
            string name = value?.GetValue<string>() ?? throw new ArgumentException("A required consumer owner is missing.");
            if (name != producer.AssemblyName || !seen.Add(name) ||
                !consumer.ResolvedOwnerPaths.TryGetValue(name, out string? actualPath) || actualPath != producerPath)
                throw new InvalidOperationException("Package metadata does not bind its required source owner to the actual fresh producer.");
            RustSharpMetadataSourceValueType[] layouts = consumer.Document!.SourceValueTypes.Where(layout =>
                layout.Owner?.AssemblyName == name).ToArray();
            RustSharpMetadataSourceStructuralType[] structural = consumer.Document.SourceStructuralTypes.Where(layout =>
                layout.Owner.AssemblyName == name).ToArray();
            if (layouts.Length + structural.Length is < 1 or > 32 ||
                layouts.Any(layout => layout.Owner!.ModuleVersionId != producer.ModuleVersionId ||
                    layout.Owner.SourceSha256 != producer.Document!.SourceSha256 || layout.Owner.AssemblySha256 != producerSha256) ||
                structural.Any(layout => layout.Owner.ModuleVersionId != producer.ModuleVersionId ||
                    layout.Owner.SourceSha256 != producer.Document!.SourceSha256 || layout.Owner.AssemblySha256 != producerSha256))
                throw new InvalidOperationException("Consumer source owner proofs disagree with fresh producer MVID/source/assembly hashes.");
            result.Add(new JsonObject { ["assembly"] = name, ["originalProducer"] = Artifact(producerPath),
                ["moduleVersionId"] = producer.ModuleVersionId, ["sourceSha256"] = producer.Document!.SourceSha256,
                ["sourceLayouts"] = new JsonArray(layouts.Select(layout => (JsonNode)new JsonObject { ["sourceName"] = layout.Name,
                    ["clrName"] = layout.ClrName, ["owningSourceName"] = layout.Owner!.SourceName,
                    ["owningClrName"] = layout.Owner.ClrName }).ToArray()),
                ["structuralLayouts"] = new JsonArray(structural.Select(layout => (JsonNode)new JsonObject
                    { ["sourceType"] = layout.Type, ["clrName"] = layout.ClrName,
                        ["owningSourceName"] = layout.Owner.SourceName, ["owningClrName"] = layout.Owner.ClrName }).ToArray()),
                ["freshProducerProofReconciled"] = true });
        }
        return result;
    }

    private sealed class MemberRefSignatureProvider(string producerName, IReadOnlyDictionary<string, string> resolvedOwners)
        : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "Void", PrimitiveTypeCode.Boolean => "Bool", PrimitiveTypeCode.Int32 => "I32",
            PrimitiveTypeCode.Object => "Any", PrimitiveTypeCode.String => "Text", _ => throw Unsupported(),
        };
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            if (rawTypeKind != (byte)SignatureTypeKind.ValueType || reader.GetString(type.Namespace) != "RustSharp.Generated.Values" ||
                type.ResolutionScope.Kind != HandleKind.AssemblyReference)
                throw Unsupported();
            string owner = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
            if (owner != producerName && !resolvedOwners.ContainsKey(owner)) throw Unsupported();
            return "Value(" + (owner == producerName ? "" : owner + "::") + reader.GetString(type.Name) + ")";
        }
        public string GetByReferenceType(string elementType) => "&" + elementType;
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => throw Unsupported();
        public string GetSZArrayType(string elementType) => throw Unsupported();
        public string GetArrayType(string elementType, ArrayShape shape) => throw Unsupported();
        public string GetFunctionPointerType(MethodSignature<string> signature) => throw Unsupported();
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => throw Unsupported();
        public string GetGenericMethodParameter(object? genericContext, int index) => throw Unsupported();
        public string GetGenericTypeParameter(object? genericContext, int index) => throw Unsupported();
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => throw Unsupported();
        public string GetPinnedType(string elementType) => throw Unsupported();
        public string GetPointerType(string elementType) => throw Unsupported();
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle,
            byte rawTypeKind) => throw Unsupported();
        private static InvalidOperationException Unsupported() => new("The source package MemberRef uses an unsupported CLR signature shape.");
    }

    private static bool EqualArtifacts(string first, string second) => Hash(first) == Hash(second) &&
        Hash(Path.ChangeExtension(first, ".pdb")) == Hash(Path.ChangeExtension(second, ".pdb"));

    private static void RetainPackage(string producer, string consumer, SourceSnapshot producerSource,
        SourceSnapshot consumerSource, string? wrapper, SourceSnapshot? wrapperSource,
        string destination, CancellationToken cancellationToken)
    {
        File.WriteAllBytes(Path.Combine(destination, "producer.rs"), producerSource.Bytes);
        File.WriteAllBytes(Path.Combine(destination, "consumer.rs"), consumerSource.Bytes);
        if (wrapperSource is not null) File.WriteAllBytes(Path.Combine(destination, "wrapper.rs"), wrapperSource.Bytes);
        string[] files = [producer, consumer, Path.ChangeExtension(producer, ".pdb"),
            Path.ChangeExtension(consumer, ".pdb"), Path.ChangeExtension(producer, ".runtimeconfig.json"),
            Path.ChangeExtension(consumer, ".runtimeconfig.json"), Path.Combine(Path.GetDirectoryName(consumer)!, "RustSharp.Runtime.dll"),
            .. wrapper is null ? Array.Empty<string>() : [wrapper, Path.ChangeExtension(wrapper, ".pdb"), Path.ChangeExtension(wrapper, ".runtimeconfig.json")]];
        var clock = Stopwatch.StartNew();
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("Retaining source package artifacts exceeded its thirty second budget.");
            if (File.Exists(file)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static async Task<SourceSnapshot> ReadSourceAsync(string root, JsonObject fixture, string role,
        CancellationToken cancellationToken)
    {
        string source = ChildPath(root, Text(fixture, role + "Source"));
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        if (stream.Length is < 1 or > MaximumSourceBytes)
            throw new ArgumentException("The frozen " + role + " source exceeds its 64 KiB bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, readDeadline.Token).ConfigureAwait(false);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (stream.ReadByte() != -1 || sha256 != Text(fixture, role + "Sha256"))
            throw new ArgumentException("The frozen " + role + " source is missing, oversized or has a stale hash.");
        return new(source, bytes, StrictUtf8.GetString(bytes), sha256);
    }

    private static string AssemblyName(JsonObject fixture, string member)
    {
        string name = Text(fixture, member);
        if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant, PatternTimeout) || name == "RustSharpRuntime")
            throw new ArgumentException("Invalid package assembly identity.");
        return name;
    }

    private static string[] RequiredFunctions(JsonObject fixture, string member)
    {
        if (fixture[member] is not JsonArray requiredJson || requiredJson.Count is < 1 or > 16)
            throw new ArgumentException("Source packages require one to sixteen exported function identities.");
        string[] required = requiredJson.Select(static value => value!.GetValue<string>()).ToArray();
        if (required.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 4096) ||
            required.Distinct(StringComparer.Ordinal).Count() != required.Length)
            throw new ArgumentException("Required source package functions must be unique bounded identities.");
        return required;
    }

    private static string[] RequiredDropFunctions(JsonObject fixture, RustSharpMetadataImportResult producer,
        CancellationToken cancellationToken)
    {
        string[] requiredTypes = RequiredFunctions(fixture, "requiredConsumerDropTypes");
        var functions = new List<string>(requiredTypes.Length);
        var clock = Stopwatch.StartNew();
        foreach (string name in requiredTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Required source Drop helper resolution exceeded its five second budget.");
            RustSharpMetadataSourceValueType? layout = producer.Document!.SourceValueTypes.FirstOrDefault(value => value.Name == name);
            if (layout?.DropFunctionId is not { } drop || layout.Owner is not null ||
                RustSharpMetadataConsumer.FindFunction(producer.Document, drop) is not { IsPublic: true })
                throw new InvalidOperationException("A required direct consumer Drop helper has no validated public original producer export: " + name + ".");
            functions.Add(drop);
        }
        return functions.ToArray();
    }

    private static string ChildPath(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), fullRoot);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("The requested file must remain inside its declared root.");
        return full;
    }

    private static string Text(JsonObject node, string member) => node[member]?.GetValue<string>()
        ?? throw new ArgumentException("Missing source package string member: " + member);
    private static string Hash(string path)
    {
        if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new ArgumentException("A platform artifact exceeds 512 MiB.");
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static JsonObject Artifact(string path) => new() { ["path"] = path, ["sha256"] = Hash(path) };
    private static bool Complete(BoundedProcessResult result) => result.Termination == BoundedProcessTermination.Exited &&
        !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut && !result.OutputReadLimitReached &&
        !result.ProcessTreeCleanupIncomplete;
    private static bool Match(BoundedProcessResult result, string output, string expectedOutcome)
    {
        if (!Complete(result) || result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal) != output) return false;
        string classified = P1DropDifferentialRunner.ClassifyGeneratedFailure(result.ExitCode, result.StandardError);
        return expectedOutcome switch
        {
            "success" => classified == "success" && string.IsNullOrWhiteSpace(result.StandardError),
            "unwound" => classified == "unwind" && result.StandardError.Contains("OverflowException", StringComparison.Ordinal) &&
                !result.StandardError.Contains("RustSharp panic abort:", StringComparison.Ordinal),
            "aborted" => classified == "panic-abort" && result.ExitCode == 134 &&
                result.StandardError.StartsWith("RustSharp panic abort: ", StringComparison.Ordinal),
            "double-panic" => classified == "double-panic-abort" && result.ExitCode == 134 &&
                result.StandardError.StartsWith("RustSharp double panic abort: ", StringComparison.Ordinal),
            _ => false,
        };
    }
    private static JsonObject? Evidence(BoundedProcessResult? result) => result is null ? null : new()
    {
        ["pid"] = result.StartedProcess.ProcessId, ["parentPid"] = result.StartedProcess.ParentProcessId,
        ["startedAtUtc"] = result.StartedProcess.StartedAt, ["commandLine"] = result.StartedProcess.CommandLine,
        ["workingDirectory"] = result.StartedProcess.WorkingDirectory, ["exitCode"] = result.ExitCode,
        ["termination"] = result.Termination.ToString(), ["elapsedMilliseconds"] = result.Elapsed.TotalMilliseconds,
        ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError,
        ["outputTruncated"] = result.OutputTruncated, ["outputReadTimedOut"] = result.OutputReadTimedOut,
        ["outputDrainTimedOut"] = result.OutputDrainTimedOut, ["outputReadLimitReached"] = result.OutputReadLimitReached,
        ["cleanupAttempted"] = result.ProcessTreeCleanupAttempted, ["cleanupIncomplete"] = result.ProcessTreeCleanupIncomplete,
        ["cleanupDiagnostic"] = result.ProcessTreeCleanupDiagnostic,
    };
    private static bool ExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception;

    private static async Task<string?> CleanupAsync(string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(full) != temp || !Path.GetFileName(full).StartsWith("rsc-p1-source-package-", StringComparison.Ordinal))
            return "Refused cleanup outside the runner's exclusive temporary directory.";
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 8 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { if (Directory.Exists(full)) Directory.Delete(full, recursive: true); return null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 7) return exception.Message;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), CancellationToken.None).ConfigureAwait(false);
            }
        }
        return "The runner's temporary cleanup exceeded its bounded retry/time budget.";
    }
}
