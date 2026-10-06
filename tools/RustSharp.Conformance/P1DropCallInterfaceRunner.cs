using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Runtime;
using RustSharp.Semantics;

namespace RustSharp.Conformance;

/// <summary>One direct-call report host runs against each original generated PE on both backends.</summary>
internal static class P1DropCallInterfaceRunner
{
    internal const int MaximumArtifacts = 2;
    internal const int MaximumCases = 7;
    private const int MaximumProcessCount = 16;
    private const long MaximumArtifactBytes = 512 * 1024 * 1024;
    private const int MaximumReportBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan SuiteTimeout = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250));
    private static readonly Regex SdkVersion = new("^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250));

    internal sealed record Result(string ReportPath, int Passed, int Failed, int Blocked, int Skipped,
        int RequestedArtifacts, bool CleanupComplete, bool IsNativeHost, bool DeadlineExpired)
    {
        public bool Succeeded => Passed == (RequestedArtifacts == 1 ? 6 : MaximumCases) &&
            Failed == 0 && Blocked == 0 && CleanupComplete && !DeadlineExpired;
        public bool ExpectedContractSatisfied => Succeeded && RequestedArtifacts == MaximumArtifacts &&
            Skipped == 0 && IsNativeHost;
    }

    private sealed record Fixture(string Id, string AssemblyName, string Source, SafeCorePanicStrategy Strategy,
        string[] ProbeNames, string[] CaseIds, string ExpectedOutput);

    private const string ReturnedLine = "returned|Returned|none|none|False|True|False\n";
    private const string SingleLine = "single-panic|Unwound|OverflowException|none|False|True|False\n";
    private const string LocalMultipleLine = "normal-multiple|Unwound|cleanup(OverflowException,DivideByZeroException)|none|False|True|False\n";
    private const string DoubleLine = "double-panic|Aborted|OverflowException|DivideByZeroException|True|False|True\n";
    private const string AggregateMultipleLine = "aggregate-normal-multiple|Unwound|cleanup(DivideByZeroException,OverflowException)|none|False|True|False\n";
    private const string AggregateDoubleLine = "aggregate-double-panic|Aborted|OverflowException|DivideByZeroException|True|False|True\n";
    private const string AbortLine = "explicit-abort|Aborted|OverflowException|none|False|False|False|host-preserved\n";

    private static readonly Fixture[] Fixtures =
    [
        new("unwind", "P1DropCallableUnwind", """
            fn overflow(value: i32) -> i32 { value + 1 }
            fn divide(value: i32) -> i32 { 10 / value }
            struct Good;
            impl Drop for Good { fn drop(&mut self) {} }
            struct Receiver { value: i32 }
            impl Drop for Receiver { fn drop(&mut self) { self.value += 1; println!("{}", self.value); } }
            struct BadDivide;
            impl Drop for BadDivide { fn drop(&mut self) { let ignored = divide(0); } }
            struct BadOverflow;
            impl Drop for BadOverflow { fn drop(&mut self) { let ignored = overflow(2147483647); } }
            struct Owner { bad: BadOverflow, good: Good }
            impl Drop for Owner { fn drop(&mut self) { let ignored = divide(0); } }
            pub fn returned() { let receiver = Receiver { value: 1 }; }
            pub fn single_panic() { let good = Good; let ignored = overflow(2147483647); }
            pub fn normal_multiple() { let first = BadDivide; let second = BadOverflow; }
            pub fn double_panic() { let bad = BadDivide; let ignored = overflow(2147483647); }
            pub fn aggregate_multiple() { let owner = Owner { bad: BadOverflow, good: Good }; }
            pub fn aggregate_double() {
                let owner = Owner { bad: BadOverflow, good: Good };
                let ignored = overflow(2147483647);
            }
            fn main() {}
            """, SafeCorePanicStrategy.Unwind,
            ["returned", "single_panic", "normal_multiple", "double_panic", "aggregate_multiple", "aggregate_double"],
            ["returned", "single-panic", "normal-multiple", "double-panic", "aggregate-normal-multiple", "aggregate-double-panic"],
            "2\n2\n" + ReturnedLine + SingleLine + LocalMultipleLine + DoubleLine + AggregateMultipleLine + AggregateDoubleLine),
        new("abort", "P1DropCallableAbort", """
            fn overflow(value: i32) -> i32 { value + 1 }
            fn divide(value: i32) -> i32 { 10 / value }
            struct Bad;
            impl Drop for Bad { fn drop(&mut self) { let ignored = divide(0); } }
            pub fn explicit_abort() { let bad = Bad; let ignored = overflow(2147483647); }
            fn main() {}
            """, SafeCorePanicStrategy.Abort, ["explicit_abort"], ["explicit-abort"], AbortLine),
    ];

    internal static async Task<Result> RunAsync(string repositoryRoot, string? inputReportPath, string reportPath,
        int maximumArtifactsToExecute = MaximumArtifacts, CancellationToken cancellationToken = default)
    {
        if (maximumArtifactsToExecute is not (1 or MaximumArtifacts))
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactsToExecute));
        string hostRuntimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new ArgumentException("Callable evidence requires a current Windows or Linux x64 process.");
        string runtimeIdentifier = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        string root = Path.GetFullPath(repositoryRoot);
        string report = Path.GetFullPath(reportPath, root);
        string evidenceRoot = Path.Combine(root, "artifacts", "p1-drop");
        RequireChild(report, evidenceRoot);
        string evidenceDirectory = Path.Combine(Path.GetDirectoryName(report)!, "call-interface-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDirectory);
        string probeDirectory = NewTemporaryHost();
        Directory.CreateDirectory(probeDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SuiteTimeout);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var clock = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var runner = new BoundedProcessRunner();
        var processes = new List<BoundedProcessResult>(MaximumProcessCount);
        var starts = new List<BoundedProcessStarted>(MaximumProcessCount);
        var artifacts = new JsonArray();
        var cases = new JsonArray();
        var tools = new JsonObject();
        int passed = 0, failed = 0, blocked = 0;
        bool temporaryCleanupComplete = true;
        string? harnessError = null;
        bool wsl = false;
        string? inputPath = null, inputHash = null;
        JsonObject? input = null;
        string? requestedSdk = Environment.GetEnvironmentVariable("RUSTSHARP_NATIVE_AOT_SDK_VERSION");
        void OnStarted(BoundedProcessStarted start)
        {
            if (starts.Count >= MaximumProcessCount) throw new InvalidOperationException("Callable process count exceeded its fixed bound.");
            starts.Add(start);
            Console.WriteLine($"P1 callable process: pid={start.ProcessId} parent={start.ParentProcessId} started={start.StartedAt:O} command={start.CommandLine}");
        }
        async Task<BoundedProcessResult> Execute(string executable, IReadOnlyList<string> arguments, string directory, TimeSpan timeout)
        {
            if (processes.Count >= MaximumProcessCount) throw new InvalidOperationException("Callable process count exceeded its fixed bound.");
            BoundedProcessResult result = await runner.RunAsync(new(executable, arguments, directory, timeout, OnStarted), deadline.Token).ConfigureAwait(false);
            processes.Add(result);
            return result;
        }
        try
        {
            wsl = OperatingSystem.IsLinux() && File.Exists("/proc/version") &&
                (await File.ReadAllTextAsync("/proc/version", deadline.Token).ConfigureAwait(false)).Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            if (inputReportPath is not null)
            {
                inputPath = ResolveInputPath(inputReportPath, root, evidenceRoot);
                if (string.Equals(inputPath, report, PathComparison)) throw new ArgumentException("Callable output must not overwrite its original input report.");
                if (new FileInfo(inputPath).Length > MaximumReportBytes) throw new InvalidOperationException("Callable input report exceeds 64 MiB.");
                inputHash = HashFile(inputPath);
                input = JsonNode.Parse(await File.ReadAllTextAsync(inputPath, deadline.Token).ConfigureAwait(false),
                    documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject()
                    ?? throw new InvalidOperationException("Callable input report is not an object.");
                ValidateInputReport(input, deadline.Token);
            }
            await WriteSdkSelectionAsync(probeDirectory, requestedSdk, deadline.Token).ConfigureAwait(false);
            string resolver = OperatingSystem.IsWindows() ? @"C:\Windows\System32\where.exe" : "/bin/sh";
            string[] resolverArguments = OperatingSystem.IsWindows() ? ["dotnet"] : ["-c", "command -v dotnet"];
            BoundedProcessResult resolution = await Execute(resolver, resolverArguments, probeDirectory, RunTimeout).ConfigureAwait(false);
            string dotnet = resolution.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (!Complete(resolution) || !resolution.Succeeded || !Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
                throw new InvalidOperationException("Registered dotnet discovery failed; no directory scan was performed.");
            tools["dotnetPath"] = dotnet;
            tools["dotnetSha256"] = HashFile(dotnet);
            BoundedProcessResult sdk = await Execute(dotnet, ["--info"], probeDirectory, RunTimeout).ConfigureAwait(false);
            tools["dotnetInfo"] = Evidence(sdk);
            if (!Complete(sdk) || !sdk.Succeeded) throw new InvalidOperationException("The selected host SDK probe failed.");
            string[] nativeTools = OperatingSystem.IsWindows()
                ? [@"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64\cl.exe",
                    @"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64\link.exe"]
                : ["/usr/bin/clang", "/usr/bin/gcc", "/usr/bin/ld"];
            for (int toolIndex = 0; toolIndex < nativeTools.Length && toolIndex < 3 && clock.Elapsed < SuiteTimeout; toolIndex++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string path = nativeTools[toolIndex];
                if (!File.Exists(path)) { tools[path] = null; continue; }
                var tool = new JsonObject { ["path"] = path, ["sha256"] = HashFile(path) };
                if (OperatingSystem.IsWindows()) tool["fileVersion"] = FileVersionInfo.GetVersionInfo(path).FileVersion;
                else tool["versionProbe"] = Evidence(await Execute(path, ["--version"], probeDirectory, RunTimeout).ConfigureAwait(false));
                tools[path] = tool;
            }
            for (int index = 0; index < maximumArtifactsToExecute && index < MaximumArtifacts && clock.Elapsed < SuiteTimeout; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Fixture fixture = Fixtures[index];
                Console.WriteLine($"P1 callable artifact: {index + 1}/{MaximumArtifacts} {fixture.Id}");
                string directory = Path.Combine(evidenceDirectory, fixture.Id);
                Directory.CreateDirectory(directory);
                var artifact = new JsonObject { ["id"] = fixture.Id, ["caseIds"] = Strings(fixture.CaseIds), ["expectedOutput"] = fixture.ExpectedOutput,
                    ["assemblyName"] = fixture.AssemblyName, ["panicStrategy"] = StrategyName(fixture.Strategy),
                    ["origin"] = input is null ? "fresh-source-compile" : "verified-original-input" };
                string status = "blocked";
                try
                {
                    JsonObject? original = input?["artifacts"]?[index]?.AsObject();
                    string sourcePath = original is null ? Path.Combine(directory, "program.rs") : OriginalArtifact(original, "sourcePath", "sourceSha256", root, evidenceRoot);
                    string generatedPath = original is null ? Path.Combine(directory, fixture.AssemblyName + ".dll") : OriginalArtifact(original, "generatedAssemblyPath", "generatedAssemblySha256", root, evidenceRoot);
                    string runtimePath = original is null ? Path.Combine(directory, "RustSharp.Runtime.dll") : OriginalArtifact(original, "runtimeAssemblyPath", "runtimeAssemblySha256", root, evidenceRoot);
                    RequireRuntimeCompanion(generatedPath, runtimePath, deadline.Token);
                    if (original is null) await File.WriteAllTextAsync(sourcePath, fixture.Source, Utf8, deadline.Token).ConfigureAwait(false);
                    else
                    {
                        if (new FileInfo(sourcePath).Length > 65536) throw new InvalidOperationException("Original callable source exceeds 64 KiB.");
                        if (Normalize(await File.ReadAllTextAsync(sourcePath, deadline.Token).ConfigureAwait(false)) != Normalize(fixture.Source) ||
                            Text(original, "panicStrategy") != StrategyName(fixture.Strategy) || Text(original, "expectedOutput") != fixture.ExpectedOutput)
                            throw new InvalidOperationException("Original callable fixture source/strategy/expectation does not match the fixed artifact contract.");
                    }
                    artifact["sourcePath"] = sourcePath;
                    artifact["sourceSha256"] = HashFile(sourcePath);
                    if (original is null)
                    {
                        using var sourceDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        sourceDeadline.CancelAfter(SourceTimeout);
                        DateTimeOffset compileStarted = DateTimeOffset.UtcNow;
                        CompilationResult compilation = CompilerDriver.CompileWithPanicStrategy(fixture.Source, sourcePath, generatedPath,
                            fixture.Strategy, fixture.AssemblyName, CompilationProfile.SafeCoreMirV2, sourceDeadline.Token);
                        artifact["sourceCompile"] = new JsonObject { ["success"] = compilation.Success, ["mode"] = "fresh-source-compile", ["startedAtUtc"] = compileStarted,
                            ["finishedAtUtc"] = DateTimeOffset.UtcNow, ["timeoutSeconds"] = SourceTimeout.TotalSeconds,
                            ["diagnostics"] = new JsonArray(compilation.Diagnostics.Select(static diagnostic =>
                                (JsonNode)new JsonObject { ["code"] = diagnostic.Code, ["message"] = diagnostic.Message }).ToArray()) };
                        if (!compilation.Success) throw new InvalidOperationException("Callable source compilation rejected its required fixture.");
                    }
                    else artifact["sourceCompile"] = new JsonObject { ["success"] = true, ["mode"] = "verified-original-input",
                        ["originalCompileEvidence"] = original["sourceCompile"]?.DeepClone() };
                    artifact["generatedAssemblyPath"] = generatedPath;
                    artifact["generatedAssemblySha256"] = HashFile(generatedPath);
                    artifact["runtimeAssemblyPath"] = runtimePath;
                    artifact["runtimeAssemblySha256"] = HashFile(runtimePath);
                    RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(generatedPath, cancellationToken: cancellationToken);
                    if (!imported.IsSuccessful || imported.Document is null) throw new InvalidOperationException("Callable PE metadata is invalid.");
                    if (!string.Equals(imported.Document.SourceSha256, HashFile(sourcePath), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Callable PE source hash does not bind its retained original source.");
                    var names = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int probeIndex = 0; probeIndex < fixture.ProbeNames.Length && probeIndex < 6 && clock.Elapsed < SuiteTimeout; probeIndex++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        string probe = fixture.ProbeNames[probeIndex];
                        RustSharpMetadataFunction[] matches = imported.Document.Functions.Where(function =>
                            function.SourceQualifiedName == "crate::" + probe + "#value").Take(2).ToArray();
                        if (matches.Length != 1 || !matches[0].IsPublic || matches[0].Signature != "->Void" || !Identifier.IsMatch(matches[0].Name))
                            throw new InvalidOperationException("Callable metadata must bind one public void(), bounded C# identifier: " + probe);
                        names.Add(probe, "global::RustSharp.Generated.Program.@" + matches[0].Name + "()");
                    }
                    if (names.Count != fixture.ProbeNames.Length) throw new InvalidOperationException("Callable metadata binding exceeded its six-probe/suite deadline.");
                    if (original is not null)
                    {
                        if (original["boundMethods"] is not JsonObject originalMethods || originalMethods.Count != names.Count)
                            throw new InvalidOperationException("Original callable direct-call metadata binding is missing or excessive.");
                        for (int probeIndex = 0; probeIndex < fixture.ProbeNames.Length && probeIndex < 6 && clock.Elapsed < SuiteTimeout; probeIndex++)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            string probe = fixture.ProbeNames[probeIndex];
                            if (Text(originalMethods, probe) != names[probe]) throw new InvalidOperationException("Original callable emitted method binding drifted.");
                        }
                    }
                    string expectedHost = CreateHost(fixture, names, deadline.Token);
                    string host = expectedHost;
                    if (original is not null)
                    {
                        string originalHost = OriginalArtifact(original, "hostSourcePath", "hostSourceSha256", root, evidenceRoot);
                        if (new FileInfo(originalHost).Length > 65536) throw new InvalidOperationException("Original callable host exceeds 64 KiB.");
                        host = await File.ReadAllTextAsync(originalHost, deadline.Token).ConfigureAwait(false);
                        if (Normalize(host) != Normalize(expectedHost) ||
                            !string.Equals(HashText(host), Text(original, "hostSourceSha256"), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Original host bytes or exact metadata-bound direct-call schema drifted.");
                    }
                    if (Utf8.GetByteCount(host) > 65536) throw new InvalidOperationException("Callable host exceeded 64 KiB.");
                    string retainedHost = Path.Combine(directory, "host.cs");
                    await File.WriteAllTextAsync(retainedHost, host, Utf8, deadline.Token).ConfigureAwait(false);
                    artifact["hostSourcePath"] = retainedHost;
                    artifact["hostSourceSha256"] = HashFile(retainedHost);
                    artifact["boundMethods"] = new JsonObject(names.Select(static item =>
                        new KeyValuePair<string, JsonNode?>(item.Key, JsonValue.Create(item.Value))));
                    string coreDirectory = Path.Combine(directory, "coreclr");
                    Directory.CreateDirectory(coreDirectory);
                    string coreHostDirectory = NewTemporaryHost();
                    Directory.CreateDirectory(coreHostDirectory);
                    BoundedProcessResult coreBuild;
                    BoundedProcessResult? coreRun = null;
                    try
                    {
                        await WriteSdkSelectionAsync(coreHostDirectory, requestedSdk, deadline.Token).ConfigureAwait(false);
                        string projectPath = Path.Combine(coreHostDirectory, "Host.csproj");
                        string coreAssembly = fixture.AssemblyName + ".CoreClrHost";
                        await File.WriteAllTextAsync(Path.Combine(coreHostDirectory, "Program.cs"), host, Utf8, deadline.Token).ConfigureAwait(false);
                        await File.WriteAllTextAsync(projectPath, CreateProject(coreAssembly, generatedPath, runtimePath), Utf8, deadline.Token).ConfigureAwait(false);
                        coreBuild = await Execute(dotnet, ["build", projectPath, "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false",
                            "-c", "Release", "-warnaserror", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-o", coreDirectory],
                            coreHostDirectory, BuildTimeout).ConfigureAwait(false);
                        artifact["coreClrBuild"] = Evidence(coreBuild);
                        if (Complete(coreBuild) && coreBuild.Succeeded)
                            coreRun = await Execute(dotnet, [Path.Combine(coreDirectory, coreAssembly + ".dll")], coreDirectory, RunTimeout).ConfigureAwait(false);
                        artifact["coreClrRun"] = Evidence(coreRun);
                    }
                    finally
                    {
                        bool incompleteTree = processes.Any(process => process.ProcessTreeCleanupIncomplete &&
                            string.Equals(process.StartedProcess.WorkingDirectory, coreHostDirectory, PathComparison)) ||
                            starts.Any(start => string.Equals(start.WorkingDirectory, coreHostDirectory, PathComparison) &&
                                !processes.Any(process => process.StartedProcess.ProcessId == start.ProcessId && process.StartedProcess.StartedAt == start.StartedAt));
                        string? cleanup = incompleteTree ? "Retained the CoreCLR host because a task process tree is not confirmed reclaimed." :
                            await CleanupTemporaryHostAsync(coreHostDirectory).ConfigureAwait(false);
                        temporaryCleanupComplete &= cleanup is null;
                        artifact["coreClrHostCleanup"] = new JsonObject { ["directory"] = coreHostDirectory, ["completed"] = cleanup is null, ["diagnostic"] = cleanup };
                    }
                    // Publish the same original Rust PE; this second host never recompiles Rust source.
                    string nativeDirectory = Path.Combine(directory, "native-aot");
                    RequireRuntimeCompanion(generatedPath, runtimePath, deadline.Token);
                    if (HashFile(runtimePath) != Text(artifact, "runtimeAssemblySha256"))
                        throw new InvalidOperationException("The PE companion runtime changed before Native AOT publication.");
                    NativeAotPublishResult publication = await new NativeAotPublisher(runner).PublishAsync(
                        new(generatedPath, fixture.AssemblyName, runtimeIdentifier, nativeDirectory, PublishTimeout, OnStarted)
                        { HostSourceOverride = host }, deadline.Token).ConfigureAwait(false);
                    processes.Add(publication.ProcessResult);
                    bool nativeHostClean = publication.HostCleanupAttempted && !publication.HostCleanupIncomplete && !Directory.Exists(publication.HostDirectory);
                    temporaryCleanupComplete &= nativeHostClean;
                    artifact["nativePublish"] = Evidence(publication.ProcessResult);
                    artifact["nativeHostCleanup"] = new JsonObject { ["directory"] = publication.HostDirectory,
                        ["completed"] = nativeHostClean, ["diagnostic"] = publication.HostCleanupDiagnostic };
                    RequireRuntimeCompanion(generatedPath, runtimePath, deadline.Token);
                    if (HashFile(runtimePath) != Text(artifact, "runtimeAssemblySha256"))
                        throw new InvalidOperationException("The PE companion runtime changed during Native AOT publication.");
                    BoundedProcessResult? nativeRun = publication.Succeeded && Complete(publication.ProcessResult)
                        ? await Execute(publication.ExecutablePath!, [], nativeDirectory, RunTimeout).ConfigureAwait(false) : null;
                    artifact["nativeRun"] = Evidence(nativeRun);
                    artifact["nativeExecutablePath"] = publication.ExecutablePath;
                    artifact["nativeExecutableSha256"] = publication.ExecutablePath is null ? null : HashFile(publication.ExecutablePath);
                    if (HashFile(generatedPath) != Text(artifact, "generatedAssemblySha256") ||
                        HashFile(runtimePath) != Text(artifact, "runtimeAssemblySha256") || HashFile(retainedHost) != Text(artifact, "hostSourceSha256"))
                        throw new InvalidOperationException("Original callable PE/runtime/host changed during backend execution.");
                    bool complete = Complete(coreBuild) && coreRun is not null && Complete(coreRun) &&
                        Complete(publication.ProcessResult) && nativeHostClean && nativeRun is not null && Complete(nativeRun);
                    bool match = complete && coreBuild.Succeeded && publication.Succeeded && coreRun!.Succeeded && nativeRun!.Succeeded &&
                        Normalize(coreRun.StandardOutput) == fixture.ExpectedOutput && Normalize(nativeRun.StandardOutput) == fixture.ExpectedOutput;
                    status = !complete ? "blocked" : match ? "passed" : "failed";
                    artifact["difference"] = match ? null : "The direct-call report host did not produce its exact asserted trace on both CoreCLR and original-PE Native AOT.";
                }
                catch (Exception exception) when (ExpectedFailure(exception)) { artifact["difference"] = exception.Message; }
                artifact["status"] = status;
                artifacts.Add(artifact);
                var caseClock = Stopwatch.StartNew();
                for (int caseIndex = 0; caseIndex < fixture.CaseIds.Length && caseIndex < 6 && caseClock.Elapsed < TimeSpan.FromSeconds(5); caseIndex++)
                {
                    cases.Add(new JsonObject { ["id"] = fixture.CaseIds[caseIndex], ["artifactId"] = fixture.Id, ["status"] = status });
                    if (status == "passed") passed++; else if (status == "failed") failed++; else blocked++;
                }
            }
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { harnessError = exception.Message; }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            string? cleanup = await CleanupTemporaryHostAsync(probeDirectory).ConfigureAwait(false);
            temporaryCleanupComplete &= cleanup is null;
            tools["sdkProbeCleanup"] = new JsonObject { ["directory"] = probeDirectory, ["completed"] = cleanup is null, ["diagnostic"] = cleanup };
        }
        int skipped = 0;
        var finalReportClock = Stopwatch.StartNew();
        for (int artifactIndex = artifacts.Count; artifactIndex < MaximumArtifacts && finalReportClock.Elapsed < TimeSpan.FromSeconds(5); artifactIndex++)
        {
            Fixture fixture = Fixtures[artifactIndex];
            bool smoke = artifactIndex >= maximumArtifactsToExecute && harnessError is null && !deadline.IsCancellationRequested;
            string status = smoke ? "not-executed" : "blocked";
            for (int caseIndex = 0; caseIndex < fixture.CaseIds.Length && caseIndex < 6 && finalReportClock.Elapsed < TimeSpan.FromSeconds(5); caseIndex++)
            {
                cases.Add(new JsonObject { ["id"] = fixture.CaseIds[caseIndex], ["artifactId"] = fixture.Id,
                    ["status"] = status, ["difference"] = smoke ? "Outside explicit one-artifact smoke; no seven-case closure is claimed." : harnessError ?? "Suite deadline or cancellation." });
                if (smoke) skipped++; else blocked++;
            }
        }
        bool cleanupComplete = temporaryCleanupComplete && processes.All(static process => !process.ProcessTreeCleanupIncomplete) &&
            starts.All(start => processes.Any(process => process.StartedProcess.ProcessId == start.ProcessId && process.StartedProcess.StartedAt == start.StartedAt));
        if (cases.Count != MaximumCases)
        {
            harnessError ??= "Callable report fill exceeded its seven-case/five-second budget.";
            blocked += MaximumCases - cases.Count;
        }
        var result = new Result(report, passed, failed, blocked, skipped, maximumArtifactsToExecute, cleanupComplete,
            IsNativeHost: true, DeadlineExpired: deadline.IsCancellationRequested);
        var document = new JsonObject
        {
            ["schemaVersion"] = 1, ["evidenceKind"] = "p1-drop-direct-call-report-coreclr-original-pe-native-aot",
            ["profile"] = "p1-drop-call-interface-v1", ["runPurpose"] = maximumArtifactsToExecute == 1 ? "bounded-smoke-only" : "fixed-suite-closure",
            ["runtimeIdentifier"] = runtimeIdentifier, ["targetRuntimeIdentifier"] = runtimeIdentifier,
            ["hostRuntimeIdentifier"] = hostRuntimeIdentifier, ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtimeVersion"] = Environment.Version.ToString(), ["osDescription"] = RuntimeInformation.OSDescription,
            ["isWsl"] = wsl, ["requestedNativeHostSdkVersion"] = requestedSdk,
            ["inputReportPath"] = inputPath, ["inputReportSha256"] = inputHash,
            ["inputCompilerAssemblySha256"] = input?["compilerAssemblySha256"]?.DeepClone(),
            ["inputSemanticsAssemblySha256"] = input?["semanticsAssemblySha256"]?.DeepClone(),
            ["inputEmitterAssemblySha256"] = input?["emitterAssemblySha256"]?.DeepClone(),
            ["inputRuntimeAssemblySha256"] = input?["runtimeAssemblySha256"]?.DeepClone(),
            ["runnerAssemblyPath"] = typeof(P1DropCallInterfaceRunner).Assembly.Location,
            ["runnerAssemblySha256"] = HashFile(typeof(P1DropCallInterfaceRunner).Assembly.Location),
            ["compilerAssemblySha256"] = HashFile(typeof(CompilerDriver).Assembly.Location),
            ["semanticsAssemblySha256"] = HashFile(typeof(SafeCoreMirLowering).Assembly.Location),
            ["emitterAssemblySha256"] = HashFile(typeof(ClrLirEmitter).Assembly.Location),
            ["runtimeAssemblySha256"] = HashFile(typeof(RustGeneratedPanic).Assembly.Location),
            ["tools"] = tools,
            ["summary"] = new JsonObject { ["status"] = !cleanupComplete || blocked > 0 || result.DeadlineExpired ? "blocked" : failed > 0 ? "failed" : skipped > 0 ? "smoke-complete" : "passed",
                ["denominator"] = MaximumCases, ["artifactDenominator"] = MaximumArtifacts, ["requestedArtifacts"] = maximumArtifactsToExecute,
                ["passed"] = passed, ["failed"] = failed, ["blocked"] = blocked, ["skipped"] = skipped,
                ["succeeded"] = result.Succeeded, ["expectedContractSatisfied"] = result.ExpectedContractSatisfied },
            ["execution"] = new JsonObject { ["startedAtUtc"] = startedAt, ["finishedAtUtc"] = DateTimeOffset.UtcNow,
                ["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds, ["suiteTimeoutSeconds"] = SuiteTimeout.TotalSeconds,
                ["sourceCompileTimeoutSeconds"] = SourceTimeout.TotalSeconds, ["coreClrBuildTimeoutSeconds"] = BuildTimeout.TotalSeconds,
                ["nativePublishTimeoutSeconds"] = PublishTimeout.TotalSeconds, ["runTimeoutSeconds"] = RunTimeout.TotalSeconds,
                ["maximumProcessCount"] = MaximumProcessCount, ["deadlineExpired"] = result.DeadlineExpired },
            ["cleanup"] = new JsonObject { ["completed"] = cleanupComplete, ["retainedEvidenceDirectory"] = evidenceDirectory,
                ["reason"] = "Original source, Rust PE/runtime, exact host.cs, CoreCLR binaries, native executables and raw process outcomes are retained for review/ILVerify; only verified GUID-owned temporary host directories are removed." },
            ["processStarts"] = new JsonArray(starts.Select(static start => (JsonNode)new JsonObject { ["pid"] = start.ProcessId,
                ["parentPid"] = start.ParentProcessId, ["startedAtUtc"] = start.StartedAt, ["commandLine"] = start.CommandLine,
                ["workingDirectory"] = start.WorkingDirectory }).ToArray()),
            ["processResults"] = new JsonArray(processes.Select(static process => (JsonNode)Evidence(process)!).ToArray()),
            ["harnessError"] = harnessError, ["artifacts"] = artifacts, ["cases"] = cases,
        };
        string json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (Utf8.GetByteCount(json) > MaximumReportBytes) throw new InvalidOperationException("Callable evidence report exceeded its 64 MiB bound.");
        string temporaryReport = report + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryReport, json, Utf8, CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryReport, report, overwrite: true);
        }
        finally { if (File.Exists(temporaryReport)) File.Delete(temporaryReport); }
        Console.WriteLine($"P1 callable evidence: {report}; passed={passed}, failed={failed}, blocked={blocked}, skipped={skipped}, cleanup={cleanupComplete}");
        return result;
    }

    private static string CreateHost(Fixture fixture, Dictionary<string, string> calls, CancellationToken cancellationToken)
    {
        string body = fixture.Strategy == SafeCorePanicStrategy.Unwind ? """
            RustPanicReport returned = RustPanicBoundary.Run(() => @returned@);
            Verify(returned, RustPanicOutcome.Returned, "none", "none", false, true, false);
            bool prior = RustGeneratedPanic.SwapUnwinding(true);
            try {
                Verify(RustPanicBoundary.Run(() => @returned@), RustPanicOutcome.Returned, "none", "none", false, true, false, expectedUnwinding: true);
            } finally { RustGeneratedPanic.SwapUnwinding(prior); }
            Require(!RustGeneratedPanic.IsUnwinding(), "Returned call did not restore its caller mode.");
            Trace("returned", returned);
            Check("single-panic", () => @single_panic@, RustPanicOutcome.Unwound, "OverflowException", "none", false, true, false);
            Check("normal-multiple", () => @normal_multiple@, RustPanicOutcome.Unwound, "cleanup(OverflowException,DivideByZeroException)", "none", false, true, false);
            Check("double-panic", () => @double_panic@, RustPanicOutcome.Aborted, "OverflowException", "DivideByZeroException", true, false, true);
            Check("aggregate-normal-multiple", () => @aggregate_multiple@, RustPanicOutcome.Unwound, "cleanup(DivideByZeroException,OverflowException)", "none", false, true, false);
            Check("aggregate-double-panic", () => @aggregate_double@, RustPanicOutcome.Aborted, "OverflowException", "DivideByZeroException", true, false, true);
            """ : """
            var owner = new TrackedOwner();
            using var scope = new DropScope();
            scope.Track(owner);
            RustPanicReport report = RustPanicBoundary.Run(() => @explicit_abort@, scope);
            Verify(report, RustPanicOutcome.Aborted, "OverflowException", "none", false, false, false);
            Require(owner.DropCount == 0 && !scope.IsDisposed && scope.TrackedCount == 1, "Generated abort disposed the host scope.");
            scope.Dispose();
            Require(owner.DropCount == 1 && scope.IsDisposed && scope.TrackedCount == 0, "The preserved host scope cannot clean its owner exactly once.");
            Trace("explicit-abort", report, "|host-preserved");
            """;
        if (calls.Count != fixture.ProbeNames.Length || calls.Count > 6) throw new InvalidOperationException("Callable host has missing or extra probe bindings.");
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < fixture.ProbeNames.Length && index < 6 && clock.Elapsed < TimeSpan.FromSeconds(5); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string probe = fixture.ProbeNames[index];
            body = body.Replace("@" + probe + "@", calls[probe], StringComparison.Ordinal);
        }
        if (Regex.IsMatch(body, "@[a-z_]+@", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250)))
            throw new InvalidOperationException("Callable host contains an unbound probe token.");
        return """
            using System;
            using RustSharp.Runtime;
            namespace RustSharp.CallableHost;
            internal static class EntryPoint {
                private static void Main() {
                    Require(!RustGeneratedPanic.IsUnwinding(), "Host entered with an unexpected unwind mode.");
            """ + body + """
                }
                private static void Check(string id, Action body, RustPanicOutcome outcome, string panic, string cleanup, bool attempted, bool completed, bool doubled) {
                    Require(!RustGeneratedPanic.IsUnwinding(), "Prior callable left unwind mode active.");
                    RustPanicReport report = RustPanicBoundary.Run(body);
                    Verify(report, outcome, panic, cleanup, attempted, completed, doubled);
                    Trace(id, report);
                }
                private static void Verify(RustPanicReport report, RustPanicOutcome outcome, string panic, string cleanup, bool attempted, bool completed, bool doubled, bool expectedUnwinding = false) {
                    Require(report.Outcome == outcome && Tag(report.Panic) == panic && Tag(report.CleanupException) == cleanup, "Callable reported a different outcome or panic order.");
                    Require(report.CleanupAttempted == attempted && report.CleanupCompleted == completed && report.IsDoublePanic == doubled, "Callable cleanup/double-panic flags drifted.");
                    Require(report.IsSuccessful == (outcome == RustPanicOutcome.Returned), "Callable success flag drifted.");
                    Require(RustGeneratedPanic.IsUnwinding() == expectedUnwinding, "Callable did not restore its caller unwind mode.");
                }
                private static string Tag(Exception? failure, int depth = 0) {
                    Require(depth < 4, "Callable panic report exceeded its bounded nesting schema.");
                    if (failure is null) return "none";
                    if (failure is OverflowException) return "OverflowException";
                    if (failure is DivideByZeroException) return "DivideByZeroException";
                    if (failure is RustGeneratedCleanupException collected) {
                        Require(collected.SubsequentFailures.Count == 1, "Callable did not preserve exactly two normal cleanup failures.");
                        return "cleanup(" + Tag(collected.FirstFailure, depth + 1) + "," + Tag(collected.SubsequentFailures[0], depth + 1) + ")";
                    }
                    throw new InvalidOperationException("Callable reported an unexpected panic type.");
                }
                private static void Trace(string id, RustPanicReport report, string suffix = "") => Console.WriteLine(id + "|" + report.Outcome + "|" + Tag(report.Panic) + "|" + Tag(report.CleanupException) + "|" + report.CleanupAttempted + "|" + report.CleanupCompleted + "|" + report.IsDoublePanic + suffix);
                private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
                private sealed class TrackedOwner : IDisposable { internal int DropCount; public void Dispose() { DropCount++; } }
            }
            """;
    }

    private static string CreateProject(string assemblyName, string generatedPath, string runtimePath) =>
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"),
                new XElement("AssemblyName", assemblyName), new XElement("ImplicitUsings", "disable"), new XElement("Nullable", "enable"),
                new XElement("EnableDefaultCompileItems", "false"), new XElement("IsAotCompatible", "true"),
                new XElement("TreatWarningsAsErrors", "true"), new XElement("ImportDirectoryBuildProps", "false"), new XElement("ImportDirectoryBuildTargets", "false")),
            new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", "Program.cs")),
                new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(generatedPath)), new XElement("HintPath", generatedPath), new XElement("Private", "true")),
                new XElement("Reference", new XAttribute("Include", "RustSharp.Runtime"), new XElement("HintPath", runtimePath), new XElement("Private", "true"))))).ToString(SaveOptions.DisableFormatting);

    private static async Task WriteSdkSelectionAsync(string directory, string? requestedSdk, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedSdk)) return;
        if (requestedSdk.Length > 32 || !SdkVersion.IsMatch(requestedSdk)) throw new ArgumentException("Host SDK selection must be a bounded numeric version.");
        string json = new JsonObject { ["sdk"] = new JsonObject { ["version"] = requestedSdk, ["rollForward"] = "disable", ["allowPrerelease"] = false } }.ToJsonString();
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), json, Utf8, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateInputReport(JsonObject input, CancellationToken cancellationToken)
    {
        if (Text(input, "evidenceKind") != "p1-drop-direct-call-report-coreclr-original-pe-native-aot" ||
            Text(input, "profile") != "p1-drop-call-interface-v1" || Text(input, "runtimeIdentifier") != "win-x64" ||
            input["schemaVersion"]?.GetValue<int>() != 1 || Text(input, "runPurpose") != "fixed-suite-closure" ||
            input["isWsl"]?.GetValue<bool>() != false || input["summary"]?["expectedContractSatisfied"]?.GetValue<bool>() != true ||
            input["summary"]?["denominator"]?.GetValue<int>() != MaximumCases ||
            input["summary"]?["artifactDenominator"]?.GetValue<int>() != MaximumArtifacts ||
            input["summary"]?["requestedArtifacts"]?.GetValue<int>() != MaximumArtifacts ||
            input["summary"]?["passed"]?.GetValue<int>() != MaximumCases ||
            input["summary"]?["failed"]?.GetValue<int>() != 0 || input["summary"]?["blocked"]?.GetValue<int>() != 0 ||
            input["summary"]?["skipped"]?.GetValue<int>() != 0 || input["cleanup"]?["completed"]?.GetValue<bool>() != true ||
            input["execution"]?["deadlineExpired"]?.GetValue<bool>() != false ||
            input["artifacts"] is not JsonArray artifacts || artifacts.Count != MaximumArtifacts ||
            input["cases"] is not JsonArray cases || cases.Count != MaximumCases)
            throw new InvalidOperationException("Callable reuse requires a complete native Windows two-artifact/seven-case report, not smoke evidence.");
        ValidateCurrentImplementation(input, cancellationToken);
        var clock = Stopwatch.StartNew();
        int caseOrdinal = 0;
        for (int artifactIndex = 0; artifactIndex < MaximumArtifacts && clock.Elapsed < TimeSpan.FromSeconds(5); artifactIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fixture expected = Fixtures[artifactIndex];
            if (artifacts[artifactIndex] is not JsonObject artifact || Text(artifact, "id") != expected.Id ||
                Text(artifact, "assemblyName") != expected.AssemblyName || Text(artifact, "status") != "passed" ||
                Text(artifact, "origin") != "fresh-source-compile" || artifact["sourceCompile"]?["success"]?.GetValue<bool>() != true ||
                artifact["caseIds"] is not JsonArray ids || ids.Count != expected.CaseIds.Length)
                throw new InvalidOperationException("Callable original artifact identity, source compilation or case mapping is invalid.");
            if (!SuccessfulEvidence(artifact["coreClrBuild"]) || !SuccessfulEvidence(artifact["coreClrRun"]) ||
                !SuccessfulEvidence(artifact["nativePublish"]) || !SuccessfulEvidence(artifact["nativeRun"]) ||
                artifact["coreClrHostCleanup"]?["completed"]?.GetValue<bool>() != true || artifact["nativeHostCleanup"]?["completed"]?.GetValue<bool>() != true ||
                Normalize(Text(artifact["coreClrRun"]!.AsObject(), "stdout")) != expected.ExpectedOutput ||
                Normalize(Text(artifact["nativeRun"]!.AsObject(), "stdout")) != expected.ExpectedOutput)
                throw new InvalidOperationException("Original callable report lacks complete successful build/run traces and temporary cleanup.");
            for (int index = 0; index < expected.CaseIds.Length && index < 6 && clock.Elapsed < TimeSpan.FromSeconds(5); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = expected.CaseIds[index];
                if (ids[index]?.GetValue<string>() != id || cases[caseOrdinal] is not JsonObject entry ||
                    Text(entry, "id") != id || Text(entry, "artifactId") != expected.Id || Text(entry, "status") != "passed")
                    throw new InvalidOperationException("Callable input has missing, extra, reordered or unexecuted case identities.");
                caseOrdinal++;
            }
        }
        if (caseOrdinal != MaximumCases) throw new InvalidOperationException("Callable input validation exceeded its seven-item/five-second budget.");
    }

    /// <summary>Binds original callable evidence to the implementation loaded by this runner.</summary>
    internal static void ValidateCurrentImplementation(JsonObject input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var clock = Stopwatch.StartNew();
        CheckBudget();
        (string Key, string Path)[] implementations =
        [
            ("compilerAssemblySha256", typeof(CompilerDriver).Assembly.Location),
            ("semanticsAssemblySha256", typeof(SafeCoreMirLowering).Assembly.Location),
            ("emitterAssemblySha256", typeof(ClrLirEmitter).Assembly.Location),
            ("runtimeAssemblySha256", typeof(RustGeneratedPanic).Assembly.Location),
        ];
        string? runtimeHash = null;
        for (int index = 0; index < implementations.Length && index < 4; index++)
        {
            CheckBudget();
            (string key, string path) = implementations[index];
            string current = HashCurrent(path);
            if (!current.Equals(Text(input, key), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Original callable implementation is stale relative to this build: " + key);
            if (key == "runtimeAssemblySha256") runtimeHash = current;
        }
        if (input["artifacts"] is not JsonArray { Count: MaximumArtifacts } artifacts || runtimeHash is null)
            throw new InvalidOperationException("Callable implementation binding requires exactly two original artifacts.");
        for (int index = 0; index < MaximumArtifacts; index++)
        {
            CheckBudget();
            if (artifacts[index] is not JsonObject artifact ||
                !runtimeHash.Equals(Text(artifact, "runtimeAssemblySha256"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An original callable artifact uses a runtime outside the current producer binding.");
        }
        CheckBudget();

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Callable implementation validation exceeded five seconds.");
        }
        string HashCurrent(string path)
        {
            const int blockBytes = 65_536;
            const int maximumBlocks = 8_192;
            CheckBudget();
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > MaximumArtifactBytes) throw new InvalidOperationException("Callable producer exceeded its 512 MiB bound.");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[blockBytes];
            long total = 0;
            for (int block = 0; block <= maximumBlocks; block++)
            {
                CheckBudget();
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) return Convert.ToHexString(hash.GetHashAndReset());
                total += count;
                if (total > MaximumArtifactBytes) throw new InvalidOperationException("Callable producer grew beyond its 512 MiB bound.");
                hash.AppendData(buffer, 0, count);
            }
            throw new InvalidOperationException("Callable producer exceeded its bounded hash read count.");
        }
    }

    private static bool SuccessfulEvidence(JsonNode? node) => node is JsonObject process &&
        process["termination"]?.GetValue<string>() == "exited" && process["rawExitCode"]?.GetValue<int>() == 0 &&
        process["outputTruncated"]?.GetValue<bool>() == false && process["outputReadTimedOut"]?.GetValue<bool>() == false &&
        process["outputDrainTimedOut"]?.GetValue<bool>() == false && process["outputReadLimitReached"]?.GetValue<bool>() == false &&
        process["cleanupIncomplete"]?.GetValue<bool>() == false && process["pid"]?.GetValue<int>() > 0 && process["parentPid"]?.GetValue<int>() > 0;

    private static string OriginalArtifact(JsonObject original, string pathKey, string hashKey, string root, string evidenceRoot)
    {
        string path = ResolveInputPath(Text(original, pathKey), root, evidenceRoot);
        if (!string.Equals(HashFile(path), Text(original, hashKey), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Original callable artifact hash changed: " + pathKey);
        return path;
    }

    /// <summary>Checks resolved platform paths against the companion copied by NativeAotPublisher.</summary>
    internal static void RequireRuntimeCompanion(string generatedPath, string runtimePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string expected = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(generatedPath))!, "RustSharp.Runtime.dll");
        if (!string.Equals(Path.GetFullPath(runtimePath), expected, PathComparison))
            throw new InvalidOperationException("Callable runtime evidence must name the PE-adjacent RustSharp.Runtime.dll actually consumed by Native AOT.");
    }

    private static string ResolveInputPath(string path, string root, string evidenceRoot)
    {
        string normalized = path.Replace('\\', '/');
        string full;
        if (Path.IsPathFullyQualified(path) && File.Exists(path)) full = Path.GetFullPath(path);
        else
        {
            const string anchor = "/artifacts/p1-drop/";
            int index = normalized.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            full = index >= 0 ? Path.GetFullPath(normalized[(index + 1)..], root) : Path.GetFullPath(path, root);
        }
        RequireChild(full, evidenceRoot);
        return full;
    }

    private static string Text(JsonObject value, string key) => value[key]?.GetValue<string>()
        ?? throw new InvalidOperationException("Required callable evidence string is missing: " + key);
    private static string StrategyName(SafeCorePanicStrategy strategy) => strategy == SafeCorePanicStrategy.Unwind ? "unwind" : "abort";
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value)));

    private static bool Complete(BoundedProcessResult result) => result.Termination == BoundedProcessTermination.Exited &&
        !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut && !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;
    private static JsonObject? Evidence(BoundedProcessResult? result) => result is null ? null : new JsonObject {
        ["pid"] = result.StartedProcess.ProcessId, ["parentPid"] = result.StartedProcess.ParentProcessId, ["startedAtUtc"] = result.StartedProcess.StartedAt,
        ["commandLine"] = result.StartedProcess.CommandLine, ["workingDirectory"] = result.StartedProcess.WorkingDirectory,
        ["rawExitCode"] = result.ExitCode, ["termination"] = result.Termination.ToString().ToLowerInvariant(), ["elapsedMilliseconds"] = result.Elapsed.TotalMilliseconds,
        ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError, ["outputTruncated"] = result.OutputTruncated,
        ["outputReadTimedOut"] = result.OutputReadTimedOut, ["outputDrainTimedOut"] = result.OutputDrainTimedOut, ["outputReadLimitReached"] = result.OutputReadLimitReached,
        ["cleanupAttempted"] = result.ProcessTreeCleanupAttempted, ["cleanupIncomplete"] = result.ProcessTreeCleanupIncomplete, ["cleanupDiagnostic"] = result.ProcessTreeCleanupDiagnostic };
    private static JsonArray Strings(string[] values) => new(values.Select(static value => (JsonNode)value).ToArray());
    private static string HashFile(string path) {
        if (new FileInfo(path).Length > MaximumArtifactBytes) throw new InvalidOperationException("Callable artifact exceeded its 512 MiB bound.");
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static void RequireChild(string path, string root) {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, PathComparison))
            throw new ArgumentException("Callable evidence must remain below artifacts/p1-drop.");
    }
    private static string Normalize(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static bool ExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidOperationException or
        ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException;
    private static string NewTemporaryHost() => Path.Combine(Path.GetTempPath(), $"rsc-p1-call-{Environment.ProcessId}-{Guid.NewGuid():N}");
    private static async Task<string?> CleanupTemporaryHostAsync(string path) {
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) return null;
        string prefix = "rsc-p1-call-" + Environment.ProcessId + "-";
        string name = Path.GetFileName(fullPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), PathComparison) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            return "Refused cleanup outside a verified current-task GUID-owned temporary host.";
        var clock = Stopwatch.StartNew();
        Exception? last = null;
        for (int attempt = 0; attempt < 8 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++) {
            try { Directory.Delete(fullPath, recursive: true); return null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { last = exception; }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        return "Callable temporary host cleanup exceeded eight attempts/five seconds: " + last?.Message;
    }
}
