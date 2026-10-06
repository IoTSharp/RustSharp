using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>Publishes the exact PE retained by fresh Drop differential evidence.</summary>
internal static class P1DropNativeAotRunner
{
    internal const int MaximumCases = P1DropDifferentialRunner.MaximumCases;
    private const long MaximumReportBytes = 4 * 1024 * 1024;
    private const long MaximumArtifactBytes = 512 * 1024 * 1024;
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SuiteTimeout = TimeSpan.FromSeconds(900);

    internal sealed record Result(string ReportPath, int Passed, int Failed, int Blocked, int Skipped,
        int RequestedCases, bool CleanupComplete)
    {
        public bool Succeeded => Passed == RequestedCases && Failed == 0 && Blocked == 0 && CleanupComplete;
        public bool ExpectedContractSatisfied => Succeeded && RequestedCases == MaximumCases && Skipped == 0;
    }

    internal static async Task<Result> RunAsync(string repositoryRoot, string differentialReportPath,
        string reportPath, int maximumCasesToExecute = MaximumCases, CancellationToken cancellationToken = default)
    {
        if (maximumCasesToExecute is not (1 or MaximumCases))
            throw new ArgumentOutOfRangeException(nameof(maximumCasesToExecute), "Choose one smoke case or the fixed source suite.");
        string hostRuntimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new ArgumentException("Drop Native AOT evidence requires a current Windows or Linux x64 process.");
        string runtimeIdentifier = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        string root = Path.GetFullPath(repositoryRoot);
        string inputReport = Path.GetFullPath(PlatformPath(differentialReportPath), root);
        string report = Path.GetFullPath(PlatformPath(reportPath), root);
        string evidenceRoot = Path.Combine(root, "artifacts", "p1-drop");
        RequireChild(inputReport, evidenceRoot);
        RequireChild(report, evidenceRoot);
        if (string.Equals(inputReport, report, PathComparison))
            throw new ArgumentException("Native AOT evidence must not overwrite its differential input report.");
        if (new FileInfo(inputReport).Length > MaximumReportBytes)
            throw new ArgumentException("The Drop differential report exceeds its four MiB bound.");
        string inputReportHash = HashFile(inputReport);
        JsonObject input = JsonNode.Parse(await File.ReadAllTextAsync(inputReport, cancellationToken).ConfigureAwait(false),
            documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject()
            ?? throw new InvalidOperationException("The Drop differential report is not an object.");
        P1DropDifferentialRunner.ValidateClosedReport(input, root, cancellationToken);
        if (Text(input, "evidenceKind") != "p1-drop-source-generated-pe-rustc-differential" ||
            input["summary"]?["denominator"]?.GetValue<int>() != MaximumCases ||
            input["cases"] is not JsonArray fixtures || fixtures.Count != MaximumCases)
            throw new InvalidOperationException("Native AOT requires the real fixed Drop differential report.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? fixture in fixtures)
            if (fixture is not JsonObject item || !identities.Add(Text(item, "id")))
                throw new InvalidOperationException("Drop differential case identities must be unique objects.");
        string runtimeHash = RuntimeHash(input);
        string evidenceDirectory = Path.Combine(Path.GetDirectoryName(report)!, "native-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDirectory);
        string probeDirectory = Path.Combine(Path.GetTempPath(), $"rsc-p1-drop-aot-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SuiteTimeout);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var clock = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var runner = new BoundedProcessRunner();
        var processes = new List<BoundedProcessResult>(MaximumCases * 2 + 8);
        var starts = new List<BoundedProcessStarted>(MaximumCases * 2 + 8);
        var cases = new JsonArray();
        var tools = new JsonObject();
        int passed = 0, failed = 0, blocked = 0;
        bool hostCleanupComplete = true;
        bool wsl = false;
        string? harnessError = null, probeCleanupDiagnostic = null;
        string? requestedSdk = Environment.GetEnvironmentVariable("RUSTSHARP_NATIVE_AOT_SDK_VERSION");
        void OnStarted(BoundedProcessStarted process)
        {
            starts.Add(process);
            Console.WriteLine($"P1 Drop AOT process: pid={process.ProcessId} parent={process.ParentProcessId} started={process.StartedAt:O} command={process.CommandLine}");
        }
        try
        {
            wsl = OperatingSystem.IsLinux() && File.Exists("/proc/version") &&
                (await File.ReadAllTextAsync("/proc/version", deadline.Token).ConfigureAwait(false)).Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(requestedSdk))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(requestedSdk, "^[0-9]+\\.[0-9]+\\.[0-9]+$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    throw new ArgumentException("Native AOT SDK selection must be a numeric version.");
                await File.WriteAllTextAsync(Path.Combine(probeDirectory, "global.json"),
                    new JsonObject { ["sdk"] = new JsonObject { ["version"] = requestedSdk, ["rollForward"] = "disable", ["allowPrerelease"] = false } }.ToJsonString(), deadline.Token).ConfigureAwait(false);
            }
            BoundedProcessResult sdk = await runner.RunAsync(new("dotnet", ["--info"], probeDirectory, RunTimeout, OnStarted), deadline.Token).ConfigureAwait(false);
            processes.Add(sdk);
            tools["dotnetInfo"] = Evidence(sdk);
            if (!Complete(sdk) || !sdk.Succeeded)
                throw new InvalidOperationException("The exact Native AOT host SDK probe failed.");
            await RecordNativeToolsAsync(runner, probeDirectory, tools, processes, OnStarted, deadline.Token).ConfigureAwait(false);
            for (int index = 0; index < maximumCasesToExecute && clock.Elapsed < SuiteTimeout; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                JsonObject fixture = fixtures[index]!.AsObject();
                string id = Text(fixture, "id");
                Console.WriteLine($"P1 Drop Native AOT: {index + 1}/{MaximumCases} {id}");
                JsonObject outcome;
                try
                {
                    if (Text(fixture, "status") is not ("passed" or "contract-difference") ||
                        fixture["rustSharpCompile"]?["success"]?.GetValue<bool>() != true)
                        throw new InvalidOperationException("The input case has no successful fresh source-to-PE evidence.");
                    string sourcePath = InputArtifact(fixture, "sourcePath", evidenceRoot);
                    string managedPath = Path.GetFullPath(PlatformPath(Text(fixture["rustSharpCompile"]!.AsObject(), "outputPath")), root);
                    RequireChild(managedPath, evidenceRoot);
                    RequireHash(sourcePath, Text(fixture, "sourceSha256"));
                    RequireHash(managedPath, Text(fixture["rustSharpCompile"]!.AsObject(), "outputSha256"));
                    string runtimePath = Path.Combine(Path.GetDirectoryName(managedPath)!, "RustSharp.Runtime.dll");
                    RequireHash(runtimePath, runtimeHash);
                    string outputDirectory = Path.Combine(evidenceDirectory, "case-" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
                    NativeAotPublishResult publication = await new NativeAotPublisher(runner).PublishAsync(
                        new(managedPath, "P1DropDifferential", runtimeIdentifier, outputDirectory,
                            PublishTimeout, OnStarted), deadline.Token).ConfigureAwait(false);
                    processes.Add(publication.ProcessResult);
                    hostCleanupComplete &= publication.HostCleanupAttempted && !publication.HostCleanupIncomplete;
                    BoundedProcessResult? nativeRun = null;
                    if (publication.Succeeded && Complete(publication.ProcessResult))
                    {
                        nativeRun = await runner.RunAsync(new(publication.ExecutablePath!, [], outputDirectory,
                            RunTimeout, OnStarted), deadline.Token).ConfigureAwait(false);
                        processes.Add(nativeRun);
                    }
                    string actualOutcome = Classify(nativeRun);
                    string expectedOutcome = Text(fixture, "expectedRustSharpOutcome");
                    bool complete = Complete(publication.ProcessResult) && !publication.HostCleanupIncomplete &&
                        nativeRun is not null && Complete(nativeRun);
                    bool match = complete && publication.Succeeded && Normalize(nativeRun!.StandardOutput) == Text(fixture, "expectedOutput") &&
                        actualOutcome == expectedOutcome;
                    string status = !complete ? "blocked" : match ? "passed" : "failed";
                    outcome = new JsonObject
                    {
                        ["id"] = id, ["status"] = status, ["sourcePath"] = sourcePath, ["sourceSha256"] = HashFile(sourcePath),
                        ["managedAssemblyPath"] = managedPath, ["managedAssemblySha256"] = HashFile(managedPath),
                        ["runtimeAssemblyPath"] = runtimePath, ["runtimeAssemblySha256"] = HashFile(runtimePath),
                        ["panicStrategy"] = fixture["panicStrategy"]?.DeepClone(),
                        ["expectedOutput"] = Text(fixture, "expectedOutput"), ["expectedOutcome"] = expectedOutcome,
                        ["actualOutcome"] = actualOutcome, ["nativeExecutablePath"] = publication.ExecutablePath,
                        ["nativeExecutableSha256"] = publication.ExecutablePath is null ? null : HashFile(publication.ExecutablePath),
                        ["publish"] = Evidence(publication.ProcessResult), ["nativeRun"] = Evidence(nativeRun),
                        ["hostCleanup"] = new JsonObject { ["directory"] = publication.HostDirectory,
                            ["attempted"] = publication.HostCleanupAttempted, ["incomplete"] = publication.HostCleanupIncomplete,
                            ["diagnostic"] = publication.HostCleanupDiagnostic, ["directoryExistsAfterCleanup"] = Directory.Exists(publication.HostDirectory) },
                        ["difference"] = match ? null : "Native publish, exact stdout or semantic exit classification differed from the original generated PE contract.",
                    };
                    hostCleanupComplete &= !Directory.Exists(publication.HostDirectory);
                }
                catch (Exception exception) when (ExpectedFailure(exception))
                {
                    outcome = new JsonObject { ["id"] = id, ["status"] = "blocked", ["difference"] = exception.Message };
                }
                cases.Add(outcome);
                switch (Text(outcome, "status"))
                {
                    case "passed": passed++; break;
                    case "failed": failed++; break;
                    default: blocked++; break;
                }
            }
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { harnessError = exception.Message; }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            probeCleanupDiagnostic = await CleanupProbeAsync(probeDirectory).ConfigureAwait(false);
        }
        for (int index = cases.Count; index < MaximumCases; index++)
        {
            bool deliberateSmoke = index >= maximumCasesToExecute && harnessError is null && !deadline.IsCancellationRequested;
            cases.Add(new JsonObject { ["id"] = Text(fixtures[index]!.AsObject(), "id"),
                ["status"] = deliberateSmoke ? "not-executed" : "blocked",
                ["difference"] = deliberateSmoke ? "Outside the explicit one-case smoke limit; no fixed-suite closure is claimed." : harnessError ?? "Suite deadline or cancellation prevented execution." });
            if (!deliberateSmoke) blocked++;
        }
        int skipped = MaximumCases - maximumCasesToExecute;
        bool cleanupComplete = hostCleanupComplete && probeCleanupDiagnostic is null &&
            processes.All(static process => !process.ProcessTreeCleanupIncomplete) &&
            starts.All(start => processes.Any(process => process.StartedProcess.ProcessId == start.ProcessId && process.StartedProcess.StartedAt == start.StartedAt));
        var result = new Result(report, passed, failed, blocked, skipped, maximumCasesToExecute, cleanupComplete);
        var document = new JsonObject
        {
            ["schemaVersion"] = 1, ["evidenceKind"] = "p1-drop-original-generated-pe-native-aot",
            ["profile"] = "p1-drop-native-aot-v1", ["runPurpose"] = maximumCasesToExecute == 1 ? "bounded-smoke-only" : "fixed-suite-closure",
            ["inputDifferentialReport"] = inputReport, ["inputDifferentialReportSha256"] = inputReportHash,
            ["inputImplementationAssemblies"] = input["implementationAssemblies"]?.DeepClone(),
            ["runnerAssemblyPath"] = typeof(P1DropNativeAotRunner).Assembly.Location,
            ["runnerAssemblySha256"] = HashFile(typeof(P1DropNativeAotRunner).Assembly.Location),
            ["runtimeIdentifier"] = runtimeIdentifier, ["targetRuntimeIdentifier"] = runtimeIdentifier,
            ["hostRuntimeIdentifier"] = hostRuntimeIdentifier, ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtimeVersion"] = Environment.Version.ToString(), ["osDescription"] = RuntimeInformation.OSDescription,
            ["isWsl"] = wsl,
            ["requestedNativeHostSdkVersion"] = requestedSdk, ["tools"] = tools,
            ["summary"] = new JsonObject { ["status"] = !cleanupComplete || blocked > 0 ? "blocked" : failed > 0 ? "failed" : maximumCasesToExecute == 1 ? "smoke-complete" : "passed",
                ["denominator"] = MaximumCases, ["executed"] = passed + failed, ["requestedCases"] = maximumCasesToExecute,
                ["passed"] = passed, ["failed"] = failed, ["blocked"] = blocked, ["skipped"] = skipped,
                ["succeeded"] = result.Succeeded, ["expectedContractSatisfied"] = result.ExpectedContractSatisfied },
            ["execution"] = new JsonObject { ["startedAtUtc"] = startedAt, ["finishedAtUtc"] = DateTimeOffset.UtcNow,
                ["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds, ["deadlineSeconds"] = SuiteTimeout.TotalSeconds,
                ["maximumCases"] = MaximumCases, ["publishTimeoutSeconds"] = PublishTimeout.TotalSeconds,
                ["runTimeoutSeconds"] = RunTimeout.TotalSeconds, ["deadlineExpired"] = deadline.IsCancellationRequested },
            ["cleanup"] = new JsonObject { ["completed"] = cleanupComplete, ["probeDirectory"] = probeDirectory,
                ["probeDiagnostic"] = probeCleanupDiagnostic, ["retainedEvidenceDirectory"] = evidenceDirectory,
                ["reason"] = "Native executables and raw process evidence are retained as review artifacts; only disposable SDK probes and publisher host directories are reclaimed." },
            ["processStarts"] = new JsonArray(starts.Select(static start => (JsonNode)new JsonObject { ["pid"] = start.ProcessId,
                ["parentPid"] = start.ParentProcessId, ["startedAtUtc"] = start.StartedAt, ["commandLine"] = start.CommandLine,
                ["workingDirectory"] = start.WorkingDirectory }).ToArray()),
            ["processResults"] = new JsonArray(processes.Select(static process => (JsonNode)Evidence(process)!).ToArray()),
            ["harnessError"] = harnessError, ["cases"] = cases,
        };
        string temporaryReport = report + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryReport, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryReport, report, overwrite: true);
        }
        finally { if (File.Exists(temporaryReport)) File.Delete(temporaryReport); }
        Console.WriteLine($"P1 Drop Native AOT evidence: {report}; passed={passed}, failed={failed}, blocked={blocked}, cleanup={cleanupComplete}");
        return result;
    }

    private static async Task RecordNativeToolsAsync(BoundedProcessRunner runner, string directory, JsonObject tools,
        List<BoundedProcessResult> processes, Action<BoundedProcessStarted> onStarted, CancellationToken cancellationToken)
    {
        string resolver = OperatingSystem.IsWindows() ? @"C:\Windows\System32\where.exe" : "/bin/sh";
        string[] resolverArguments = OperatingSystem.IsWindows() ? ["dotnet"] : ["-c", "command -v dotnet"];
        BoundedProcessResult resolution = await runner.RunAsync(new(resolver, resolverArguments, directory,
            RunTimeout, onStarted), cancellationToken).ConfigureAwait(false);
        processes.Add(resolution);
        tools["dotnetResolution"] = Evidence(resolution);
        string dotnetPath = resolution.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        if (!Complete(resolution) || !resolution.Succeeded || !Path.IsPathFullyQualified(dotnetPath) || !File.Exists(dotnetPath))
            throw new InvalidOperationException("Native AOT dotnet PATH resolution failed without scanning any directories.");
        tools["dotnetExecutable"] = new JsonObject { ["path"] = dotnetPath, ["sha256"] = HashFile(dotnetPath) };
        if (OperatingSystem.IsWindows())
        {
            string compilerDirectory = @"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64";
            foreach (string tool in new[] { "cl.exe", "link.exe" })
            {
                string path = Path.Combine(compilerDirectory, tool);
                tools[tool] = File.Exists(path) ? new JsonObject { ["path"] = path,
                    ["fileVersion"] = FileVersionInfo.GetVersionInfo(path).FileVersion, ["sha256"] = HashFile(path) } : null;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (string path in new[] { "/usr/bin/clang", "/usr/bin/gcc", "/usr/bin/ld" })
            {
                if (!File.Exists(path)) { tools[path] = null; continue; }
                BoundedProcessResult probe = await runner.RunAsync(new(path, ["--version"], directory, RunTimeout, onStarted), cancellationToken).ConfigureAwait(false);
                processes.Add(probe);
                tools[path] = new JsonObject { ["path"] = path, ["sha256"] = HashFile(path), ["versionProbe"] = Evidence(probe) };
            }
        }
    }

    private static string Classify(BoundedProcessResult? run)
    {
        if (run is null) return "not-run";
        if (!Complete(run)) return "incomplete";
        if (run.Succeeded) return "success";
        return P1DropDifferentialRunner.ClassifyGeneratedFailure(run.ExitCode, run.StandardError);
    }

    private static bool Complete(BoundedProcessResult result) => result.Termination == BoundedProcessTermination.Exited &&
        !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut &&
        !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;

    private static JsonObject? Evidence(BoundedProcessResult? result) => result is null ? null : new JsonObject
    {
        ["pid"] = result.StartedProcess.ProcessId, ["parentPid"] = result.StartedProcess.ParentProcessId,
        ["startedAtUtc"] = result.StartedProcess.StartedAt, ["fileName"] = result.StartedProcess.FileName,
        ["arguments"] = new JsonArray(result.StartedProcess.Arguments.Select(static argument => (JsonNode)argument).ToArray()),
        ["commandLine"] = result.StartedProcess.CommandLine, ["workingDirectory"] = result.StartedProcess.WorkingDirectory,
        ["rawExitCode"] = result.ExitCode, ["termination"] = result.Termination.ToString().ToLowerInvariant(),
        ["elapsedMilliseconds"] = result.Elapsed.TotalMilliseconds, ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError,
        ["outputTruncated"] = result.OutputTruncated, ["outputReadTimedOut"] = result.OutputReadTimedOut,
        ["outputDrainTimedOut"] = result.OutputDrainTimedOut, ["outputReadLimitReached"] = result.OutputReadLimitReached,
        ["cleanupAttempted"] = result.ProcessTreeCleanupAttempted, ["cleanupIncomplete"] = result.ProcessTreeCleanupIncomplete,
        ["cleanupDiagnostic"] = result.ProcessTreeCleanupDiagnostic, ["outputDiagnostic"] = result.OutputDiagnostic,
    };

    private static string RuntimeHash(JsonObject input)
    {
        if (input["implementationAssemblies"] is not JsonArray assemblies || assemblies.Count > 16)
            throw new InvalidOperationException("The differential report has no bounded implementation fingerprints.");
        foreach (JsonNode? node in assemblies)
            if (node is JsonObject assembly && Path.GetFileName(PlatformPath(Text(assembly, "path"))) == "RustSharp.Runtime.dll")
                return Text(assembly, "sha256");
        throw new InvalidOperationException("The differential report has no runtime assembly fingerprint.");
    }

    private static string InputArtifact(JsonObject fixture, string key, string evidenceRoot)
    {
        string path = Path.GetFullPath(PlatformPath(Text(fixture, key)));
        RequireChild(path, evidenceRoot);
        return path;
    }

    private static void RequireHash(string path, string expected)
    {
        if (!string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The original differential artifact hash changed: " + path);
    }

    private static string HashFile(string path)
    {
        if (new FileInfo(path).Length > MaximumArtifactBytes)
            throw new InvalidOperationException("Evidence artifact exceeds its 512 MiB bound: " + path);
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string PlatformPath(string path) => OperatingSystem.IsLinux() && path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            ? "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/') : path;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static void RequireChild(string path, string root)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, PathComparison))
            throw new ArgumentException("Drop evidence must remain below artifacts/p1-drop: " + path);
    }
    private static string Text(JsonObject value, string key) => value[key]?.GetValue<string>()
        ?? throw new InvalidOperationException("Required Drop evidence string is missing: " + key);
    private static string Normalize(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static bool ExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException;

    private static async Task<string?> CleanupProbeAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) return null;
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), PathComparison) ||
            !Path.GetFileName(fullPath).StartsWith("rsc-p1-drop-aot-" + Environment.ProcessId + "-", StringComparison.Ordinal) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            return "Refused to remove a probe outside the task-owned temporary prefix.";
        var clock = Stopwatch.StartNew();
        Exception? last = null;
        for (int attempt = 0; attempt < 8 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try
            {
                string global = Path.Combine(fullPath, "global.json");
                if (File.Exists(global)) File.Delete(global);
                Directory.Delete(fullPath, recursive: false);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { last = exception; }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        return "Task-owned SDK probe cleanup failed: " + last?.Message;
    }
}
