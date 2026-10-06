using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>Executes the frozen 24-case platform suite with bounded managed probes.</summary>
internal static class P1ExpandedPlatformRunner
{
    internal const string ProfileName = "p1-platform-v2";
    private const int MaximumTimeoutSeconds = 300;
    private const int MaximumDeadlineSeconds = 900;
    private const string CompilerProfile = "safe-core-mir-p1-v2";
    private static readonly IReadOnlyDictionary<string, string> ExpectedOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["borrow-shared"] = "7\n",
        ["borrow-mutable-reborrow"] = "9\n",
        ["drop-return-order"] = "body\ndrop\n",
        ["drop-early-return"] = "drop\n",
        ["borrow-write-read"] = "9\n9\n",
        ["borrow-shared-after-update"] = "1\n9\n",
        ["borrow-mutable-reborrow-chain"] = "9\n9\n9\n",
        ["borrow-shared-reborrow"] = "7\n9\n",
        ["drop-reverse-locals"] = "body\nsecond\nfirst\n",
        ["drop-nested-scopes"] = "nested\ninner\nafter\nouter\n",
        ["drop-return-reverse"] = "return\nsecond\nfirst\ncaller\n",
        ["drop-branch-return"] = "early\nsecond\nfirst\nlate\nfirst\n",
        ["slice-unsize"] = "3\n5\n",
        ["pattern-capture"] = "7\n",
        ["generic-import-call"] = "42\ntrue\n9\n",
        ["byref-import-call"] = "7\n",
        ["metadata-contract"] = "42\n",
        ["mir-projection"] = "7\n",
        ["mir-family"] = "7\n",
        ["source-package"] = "42\n",
        ["aggregate-struct-drop"] = "body\naggregate\nfirst\nsecond\n",
        ["aggregate-enum-drop"] = "one\n",
        ["panic-unwind-generated"] = "body\ninner-drop\nouter-drop\n",
        ["panic-abort-generated"] = "drop\nbody\ndrop\n",
    };
    private static readonly HashSet<string> SemanticClosureEligibleIds = new(ExpectedOutputs.Keys, StringComparer.Ordinal);
    private static readonly HashSet<string> SourcePackageIds =
    [
        "generic-import-call", "byref-import-call", "metadata-contract", "source-package",
    ];
    private static readonly string[] PlaceholderSemanticIds = [];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string CompilerProfileFor(string id) => id switch
    {
        "generic-import-call" => "safe-core-generics-v1",
        "byref-import-call" => "safe-core-mir-p1-v2",
        "metadata-contract" or "source-package" => "safe-core-primitives-v1",
        _ => CompilerProfile,
    };

    internal static async Task<int> RunAsync(string root, string reportPath, TimeSpan timeout, TimeSpan deadline, DateTimeOffset startedAtUtc, Stopwatch clock, string runtimeIdentifier)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(MaximumDeadlineSeconds)) throw new ArgumentOutOfRangeException(nameof(deadline));
        string fullReport = Path.GetFullPath(reportPath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReport)!);
        string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", P1ExpandedSuiteValidator.ManifestFileName);
        P1ExpandedSuiteValidator.SuiteSpec? suite = null;
        string? harnessError = null;
        string? manifestSha256 = null;
        try
        {
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
            P1ExpandedSuiteValidator.ExpandedManifest manifest = P1ExpandedSuiteValidator.ParseManifest(System.Text.Encoding.UTF8.GetString(manifestBytes), root);
            suite = manifest.Suites.Single(static item => item.Profile == ProfileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            harnessError = exception.Message;
        }
        using var cancellation = new CancellationTokenSource(deadline);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var cases = new List<JsonObject>();
        var processRunner = new BoundedProcessRunner();
        string dotnetPath = ResolveToolPath("RUSTSHARP_P1_DOTNET_PATH", "dotnet");
        string pwshPath = ResolveToolPath("RUSTSHARP_P1_PWSH_PATH", OperatingSystem.IsWindows() ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "pwsh");
        string cliPath = ResolveCliPath(root);
        string compilerSha256 = ComputeSha256IfPresent(cliPath);
        // Keep SDK/tool probing outside the repository tree. The repository
        // global.json pins an SDK that may be unavailable on a validation
        // host, while `dotnet tool run` discovers its manifest from the
        // current directory. Copy the frozen manifest into this task-owned
        // directory so both concerns remain bounded and reproducible.
        string runDirectory = Path.Combine(Path.GetTempPath(), ".run-p1-platform-v2-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        string toolWorkingDirectory = Path.Combine(runDirectory, "tooling");
        string toolManifestPath = Path.Combine(toolWorkingDirectory, ".config", "dotnet-tools.json");
        string? toolSetupError = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(toolManifestPath)!);
            File.Copy(Path.Combine(root, ".config", "dotnet-tools.json"), toolManifestPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            toolSetupError = "ILVerify tool manifest setup failed: " + exception.Message;
        }
        ToolProbe dotnetProbe = toolSetupError is null
            ? await ProbeToolAsync(processRunner, dotnetPath, ["--version"], toolWorkingDirectory, timeout, cancellation.Token).ConfigureAwait(false)
            : new ToolProbe(null, toolSetupError);
        ToolProbe rustcProbe = await ProbeToolAsync(processRunner, ResolveToolPath("RUSTSHARP_P1_RUSTC_PATH", "rustc"), ["+1.98.0", "--version"], root, timeout, cancellation.Token).ConfigureAwait(false);
        ToolProbe ilVerifyProbe = toolSetupError is null
            ? await ProbeToolAsync(processRunner, dotnetPath, ["tool", "run", "ilverify", "--", "--version"], toolWorkingDirectory, timeout, cancellation.Token).ConfigureAwait(false)
            : new ToolProbe(null, toolSetupError);
        string observedOracle = ProbeVersion(rustcProbe, "unavailable");
        bool oracleAvailable = observedOracle.StartsWith(P1ExpandedSuiteValidator.OraclePrefix, StringComparison.Ordinal);
        // Keep mutable compilation outputs on the native file system. In WSL,
        // a report under /mnt/d can live on DrvFS where the compiler's sidecar
        // byte-range lock is unavailable. Reports remain in the requested path.
        string? cleanupDiagnostic = null;
        try
        {
            if (suite is not null)
            {
                Directory.CreateDirectory(runDirectory);
                foreach (P1ExpandedSuiteValidator.CaseSpec fixture in suite.Cases)
                {
                    if (cancellation.IsCancellationRequested) { cases.Add(Blocked(fixture, "Platform deadline expired before this case started.")); continue; }
                    Console.WriteLine($"P1 platform {cases.Count + 1}/{suite.Denominator}: {fixture.Id}");
                    try
                    {
                        JsonObject result = await RunCaseAsync(processRunner, root, runDirectory, fixture, timeout, runtimeIdentifier, ilVerifyProbe, dotnetPath, pwshPath, cliPath, toolWorkingDirectory, cancellation.Token).ConfigureAwait(false);
                        if (!oracleAvailable) { result["status"] = "blocked"; result["difference"] = "Required native rustc 1.98.0 oracle preflight was unavailable; observed backend execution is retained."; }
                        cases.Add(result);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cases.Add(Blocked(fixture, "Platform case deadline or cancellation prevented complete evidence.")); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { cases.Add(Blocked(fixture, exception.Message)); }
                }
            }
        }
        finally { Console.CancelKeyPress -= cancelHandler; cleanupDiagnostic = DeleteBounded(runDirectory); }
        if (suite is not null) for (int missing = cases.Count; missing < suite.Denominator && missing < P1ExpandedSuiteValidator.PlatformDenominator; missing++) cases.Add(Blocked(suite.Cases[missing], harnessError ?? "Platform case did not start."));
        clock.Stop();
        int denominator = suite?.Denominator ?? 0;
        int passed = cases.Count(static item => item["status"]?.GetValue<string>() == "passed");
        int failed = cases.Count(static item => item["status"]?.GetValue<string>() == "failed");
        int blocked = cases.Count(static item => item["status"]?.GetValue<string>() == "blocked");
        string status = suite is null || harnessError is not null || cleanupDiagnostic is not null || cancellation.IsCancellationRequested || blocked > 0 ? "blocked" : failed > 0 ? "failed" : passed == denominator ? "passed" : "blocked";
        bool semanticClosureEligible = suite is not null && suite.Cases.All(fixture => SemanticClosureEligibleIds.Contains(fixture.Id));
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["evidenceKind"] = "p1-platform-coreclr-ilverify-native-aot",
            ["profile"] = ProfileName,
            ["candidateSha"] = ResolveCandidateSha(),
            ["backend"] = suite?.Backend ?? "",
            ["compilerSha256"] = ComputeSha256IfPresent(cliPath),
            ["manifest"] = new JsonObject { ["version"] = P1ExpandedSuiteValidator.ManifestVersion, ["path"] = Path.GetRelativePath(root, manifestPath).Replace(Path.DirectorySeparatorChar, '/'), ["sha256"] = manifestSha256 ?? "", ["declaredSha256"] = suite?.ManifestSha256 ?? "", ["denominator"] = denominator, ["validated"] = suite is not null },
            ["compiler"] = new JsonObject { ["sha256"] = ComputeSha256IfPresent(cliPath), ["declaredSha256"] = suite?.CompilerSha256 ?? "", ["path"] = cliPath, ["profile"] = CompilerProfile },
            ["platform"] = new JsonObject { ["name"] = runtimeIdentifier == "win-x64" ? "windows-x64" : "linux-x64", ["runtimeIdentifier"] = runtimeIdentifier, ["observedRuntimeIdentifier"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), ["nativeExecution"] = IsNativeHost(runtimeIdentifier), ["backend"] = suite?.Backend ?? "", ["oracle"] = observedOracle, ["runtime"] = Environment.Version.ToString(), ["sdk"] = ProbeVersion(dotnetProbe, "unavailable") },
            ["toolVersions"] = new JsonObject { ["dotnet"] = Environment.Version.ToString(), ["sdkVersion"] = ProbeVersion(dotnetProbe, "unavailable"), ["rustc"] = observedOracle, ["ilverify"] = ProbeVersion(ilVerifyProbe, "unavailable") },
            ["oracle"] = new JsonObject { ["version"] = observedOracle },
            ["limits"] = new JsonObject { ["maximumCases"] = P1ExpandedSuiteValidator.PlatformDenominator, ["caseTimeoutSeconds"] = timeout.TotalSeconds, ["deadlineSeconds"] = deadline.TotalSeconds, ["maximumOutputBytes"] = BoundedProcessRunner.MaximumTotalOutputBytes },
            ["preflight"] = new JsonObject { ["dotnet"] = ProbeEvidence(dotnetProbe), ["rustc"] = ProbeEvidence(rustcProbe), ["ilverify"] = ProbeEvidence(ilVerifyProbe) },
            ["summary"] = new JsonObject { ["status"] = status, ["exitCode"] = status == "passed" ? 0 : status == "failed" ? 1 : 2, ["denominator"] = denominator, ["executed"] = cases.Count, ["passed"] = passed, ["failed"] = failed, ["blocked"] = blocked, ["skipped"] = 0 },
            ["cases"] = new JsonArray(cases.Select(static item => (JsonNode)item).ToArray()),
            ["execution"] = new JsonObject { ["startedAtUtc"] = startedAtUtc, ["finishedAtUtc"] = DateTimeOffset.UtcNow, ["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds, ["deadlineExpired"] = cancellation.IsCancellationRequested },
            ["cleanup"] = new JsonObject { ["completed"] = cleanupDiagnostic is null, ["diagnostic"] = cleanupDiagnostic },
            ["semanticClosureEligible"] = semanticClosureEligible,
            ["coverageLimitations"] = new JsonArray(PlaceholderSemanticIds.Select(static id => (JsonNode)id).ToArray()),
            ["harnessError"] = harnessError,
        };
        P1EvidenceBindingValidator.ValidationResult bindingValidation = suite is null
            ? P1EvidenceBindingValidator.ValidationResult.Fail(["Frozen platform manifest was not validated."])
            : P1EvidenceBindingValidator.Validate(report.ToJsonString(JsonOptions), CreateBindingExpectation(suite, runtimeIdentifier, manifestSha256 ?? "", compilerSha256, ResolveCandidateSha()));
        report["bindingValidation"] = new JsonObject
        {
            ["valid"] = bindingValidation.Valid, ["status"] = bindingValidation.Status,
            ["errors"] = new JsonArray(bindingValidation.Errors.Select(static error => (JsonNode)error).ToArray()),
            ["scope"] = semanticClosureEligible ? "Frozen identity, output, provenance and bounded backend execution; semantic closure is eligible." : "Frozen identity, output, provenance and bounded backend execution; semantic closure remains ineligible.",
        };
        if (status == "passed" && !bindingValidation.Valid)
        {
            status = "failed";
            report["summary"]!["status"] = status; report["summary"]!["exitCode"] = 1;
        }
        string temp = fullReport + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temp, report.ToJsonString(JsonOptions)).ConfigureAwait(false); File.Move(temp, fullReport, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Console.WriteLine(report.ToJsonString(JsonOptions));
        return status == "passed" ? 0 : status == "failed" ? 1 : 2;
    }

    private static async Task<JsonObject> RunCaseAsync(BoundedProcessRunner runner, string root, string runDirectory, P1ExpandedSuiteValidator.CaseSpec fixture, TimeSpan timeout, string runtimeIdentifier, ToolProbe ilVerifyProbe, string dotnetPath, string pwshPath, string cliPath, string toolWorkingDirectory, CancellationToken cancellationToken)
    {
        if (SourcePackageIds.Contains(fixture.Id))
            return await RunSourcePackageCaseAsync(runner, root, runDirectory, fixture, timeout, runtimeIdentifier, ilVerifyProbe, dotnetPath, pwshPath, cliPath, toolWorkingDirectory, cancellationToken).ConfigureAwait(false);

        string source = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", fixture.Source);
        string sourceSha256 = ComputeSha256IfPresent(source);
        string directory = Path.Combine(runDirectory, fixture.Id); Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, fixture.Id + ".dll");
        BoundedProcessResult compile = await runner.RunAsync(new BoundedProcessRequest(dotnetPath, [cliPath, "compile", source, "--output", output, "--profile", CompilerProfile], root, timeout), cancellationToken).ConfigureAwait(false);
        BoundedProcessResult? run = Succeeded(compile) ? await runner.RunAsync(new BoundedProcessRequest(dotnetPath, [output], directory, timeout), cancellationToken).ConfigureAwait(false) : null;
        JsonObject ilVerify = await RunIlVerifyAsync(runner, root, directory, output, timeout, ilVerifyProbe, pwshPath, toolWorkingDirectory, cancellationToken).ConfigureAwait(false);
        // The compiler freezes the PE assembly identity from the source name;
        // an output file name is only a destination and can differ from it.
        string assemblyName = File.Exists(output)
            ? ReadGeneratedAssemblyName(output)
            : Path.GetFileNameWithoutExtension(fixture.Source);
        JsonObject nativeAot = await RunNativeAotAsync(runner, root, directory, output, assemblyName, fixture.Id, timeout, runtimeIdentifier, cancellationToken).ConfigureAwait(false);
        string expected = ExpectedOutputs.TryGetValue(fixture.Id, out string? known) ? known : NormalizeOutput(fixture.Id + "\n");
        bool coreClrOutputMatches = OutputMatches(run, expected);
        bool coreClrPassed = Succeeded(compile) && run is not null && Succeeded(run) && coreClrOutputMatches;
        bool ilVerifyPassed = ilVerify["status"]?.GetValue<string>() == "passed";
        bool nativeAotPassed = nativeAot["status"]?.GetValue<string>() == "passed";
        bool passed = coreClrPassed && ilVerifyPassed && nativeAotPassed;
        bool blocked = ilVerify["status"]?.GetValue<string>() == "blocked" || nativeAot["status"]?.GetValue<string>() == "blocked";
        string status = passed ? "passed" : blocked ? "blocked" : "failed";
        bool semanticEligible = SemanticClosureEligibleIds.Contains(fixture.Id);
        var result = new JsonObject { ["id"] = fixture.Id, ["source"] = fixture.Source, ["sourceSha256"] = sourceSha256 == ComputeSha256IfPresent(source) ? sourceSha256 : "", ["expectationSha256"] = fixture.ExpectationSha256, ["assemblySha256"] = ComputeSha256IfPresent(output), ["status"] = status, ["semanticClosureEligible"] = semanticEligible, ["semanticCoverage"] = semanticEligible ? "ownership-drop-scenario" : "placeholder", ["scope"] = semanticEligible ? "fixed executable output" : "placeholder source emits only its case identifier; semantic package/panic/aggregate behavior is unproven", ["difference"] = passed ? null : "CoreCLR, ILVerify, or Native AOT evidence did not complete." };
        result["coreClrCompile"] = Evidence(compile); result["coreClrRun"] = run is null ? null : Evidence(run);
        if (run is not null) result["coreClrRun"]!["outputMatches"] = coreClrOutputMatches;
        result["ilVerify"] = ilVerify;
        result["nativeAot"] = nativeAot;
        return result;
    }

    private static async Task<JsonObject> RunSourcePackageCaseAsync(BoundedProcessRunner runner, string root, string runDirectory, P1ExpandedSuiteValidator.CaseSpec fixture, TimeSpan timeout, string runtimeIdentifier, ToolProbe ilVerifyProbe, string dotnetPath, string pwshPath, string cliPath, string toolWorkingDirectory, CancellationToken cancellationToken)
    {
        string source = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", fixture.Source);
        string sourceSha256 = ComputeSha256IfPresent(source);
        string directory = Path.Combine(runDirectory, fixture.Id); Directory.CreateDirectory(directory);
        string producerSource = Path.Combine(directory, "producer.rs");
        string producerOutput;
        string output = Path.Combine(directory, fixture.Id + ".dll");
        string producerName;
        string producerProfile;
        string requiredFunction;
        string producerText;
        switch (fixture.Id)
        {
            case "generic-import-call":
                producerName = "GenericProducer";
                producerProfile = "safe-core-generics-v1";
                // Emit one closed specialization so the required source
                // export is unambiguous. The consumer still exercises the
                // generic syntax locally; the metadata reference is checked
                // independently through --require.
                requiredFunction = "crate::identity";
                producerText = "pub fn identity<T>(value: T) -> T { value } fn main() { println!(\"{}\", identity::<i32>(1)); }";
                break;
            case "byref-import-call":
                producerName = "ByrefProducer";
                producerProfile = "safe-core-mir-p1-v2";
                requiredFunction = "crate::read";
                producerText = "pub fn read(value: &i32) -> i32 { *value } fn main() {}";
                break;
            case "metadata-contract":
                producerName = "MetadataProducer";
                producerProfile = "safe-core-primitives-v1";
                requiredFunction = "crate::add";
                producerText = "pub fn add(left: i32, middle: i32, right: i32) -> i32 { left + middle + right } fn main() {}";
                break;
            case "source-package":
                producerName = "SourceProducer";
                producerProfile = "safe-core-primitives-v1";
                requiredFunction = "crate::add";
                producerText = "pub fn add(left: i32, middle: i32, right: i32) -> i32 { left + middle + right } fn main() {}";
                break;
            default: throw new ArgumentException("Unknown source package case.", nameof(fixture));
        }
        producerSource = Path.Combine(directory, producerName + ".rs");
        producerOutput = Path.Combine(directory, producerName + ".dll");
        await File.WriteAllTextAsync(producerSource, producerText, cancellationToken).ConfigureAwait(false);
        BoundedProcessResult producerCompile = await runner.RunAsync(
            new BoundedProcessRequest(dotnetPath,
                [cliPath, "compile", producerSource, "--output", producerOutput, "--profile", producerProfile],
                root, timeout), cancellationToken).ConfigureAwait(false);
        string consumerProfile = CompilerProfileFor(fixture.Id);
        var consumerArguments = new List<string>
        {
            cliPath, "compile", source, "--output", output, "--profile", consumerProfile,
            "--reference", producerOutput,
        };
        if (requiredFunction.Length != 0)
        {
            consumerArguments.Add("--require");
            consumerArguments.Add(requiredFunction);
        }
        BoundedProcessResult compile = await runner.RunAsync(
            new BoundedProcessRequest(dotnetPath, consumerArguments, root, timeout), cancellationToken).ConfigureAwait(false);
        BoundedProcessResult? run = Succeeded(compile)
            ? await runner.RunAsync(new BoundedProcessRequest(dotnetPath, [output], directory, timeout), cancellationToken).ConfigureAwait(false)
            : null;
        JsonObject ilVerify = await RunIlVerifyAsync(runner, root, directory, output, timeout, ilVerifyProbe, pwshPath, toolWorkingDirectory, cancellationToken, [producerOutput]).ConfigureAwait(false);
        string assemblyName = File.Exists(output) ? ReadGeneratedAssemblyName(output) : Path.GetFileNameWithoutExtension(fixture.Source);
        JsonObject nativeAot = await RunNativeAotAsync(runner, root, directory, output, assemblyName, fixture.Id, timeout, runtimeIdentifier, cancellationToken, [producerOutput]).ConfigureAwait(false);
        string expected = ExpectedOutputs[fixture.Id];
        bool coreClrOutputMatches = OutputMatches(run, expected);
        bool coreClrPassed = Succeeded(producerCompile) && Succeeded(compile) && run is not null && Succeeded(run) && coreClrOutputMatches;
        bool ilVerifyPassed = ilVerify["status"]?.GetValue<string>() == "passed";
        bool nativeAotPassed = nativeAot["status"]?.GetValue<string>() == "passed";
        bool passed = coreClrPassed && ilVerifyPassed && nativeAotPassed;
        bool blocked = ilVerify["status"]?.GetValue<string>() == "blocked" || nativeAot["status"]?.GetValue<string>() == "blocked";
        string status = passed ? "passed" : blocked ? "blocked" : "failed";
        var result = new JsonObject
        {
            ["id"] = fixture.Id,
            ["source"] = fixture.Source,
            ["sourceSha256"] = sourceSha256,
            ["expectationSha256"] = fixture.ExpectationSha256,
            ["assemblySha256"] = ComputeSha256IfPresent(output),
            ["status"] = status,
            ["semanticClosureEligible"] = true,
            ["semanticCoverage"] = "ownership-drop-scenario",
            ["scope"] = "checked source-package producer/consumer contract",
            ["difference"] = passed ? null : "Producer, consumer, ILVerify, or Native AOT evidence did not complete.",
            ["producer"] = new JsonObject
            {
                ["assemblyName"] = producerName,
                ["profile"] = producerProfile,
                ["sourceSha256"] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(producerText))),
                ["compile"] = Evidence(producerCompile),
                ["assemblySha256"] = ComputeSha256IfPresent(producerOutput),
            },
        };
        result["coreClrCompile"] = Evidence(compile); result["coreClrRun"] = run is null ? null : Evidence(run);
        if (run is not null) result["coreClrRun"]!["outputMatches"] = coreClrOutputMatches;
        result["ilVerify"] = ilVerify;
        result["nativeAot"] = nativeAot;
        return result;
    }

    private static async Task<JsonObject> RunIlVerifyAsync(BoundedProcessRunner runner, string root, string directory, string assembly, TimeSpan timeout, ToolProbe probe, string pwshPath, string toolWorkingDirectory, CancellationToken cancellationToken, IReadOnlyList<string>? additionalReferences = null)
    {
        if (!Succeeded(probe.Result)) return new JsonObject { ["status"] = "blocked", ["diagnostic"] = probe.Diagnostic ?? "ILVerify tool preflight did not succeed.", ["preflight"] = ProbeEvidence(probe) };
        string evidencePath = Path.Combine(directory, "ilverify.json");
        string script = Path.Combine(root, "eng", "Invoke-ILVerify.ps1");
        BoundedProcessResult result;
        var references = new List<string>();
        string runtimeAssembly = Path.Combine(directory, "RustSharp.Runtime.dll");
        if (File.Exists(runtimeAssembly)) references.Add(runtimeAssembly);
        if (additionalReferences is not null)
        {
            foreach (string reference in additionalReferences)
            {
                if (string.IsNullOrWhiteSpace(reference) || !File.Exists(reference))
                    return new JsonObject { ["status"] = "blocked", ["diagnostic"] = "An imported assembly reference is missing." };
                references.Add(reference);
            }
        }
        var arguments = new List<string> { "-NoLogo", "-NoProfile", "-File", script, "-AssemblyPath", assembly, "-RuntimeVersion", Environment.Version.ToString(), "-EvidencePath", evidencePath, "-TimeoutSeconds", "120", "-ToolWorkingDirectory", toolWorkingDirectory };
        if (references.Any(static reference => reference.Contains(';', StringComparison.Ordinal)))
            return new JsonObject { ["status"] = "blocked", ["diagnostic"] = "ILVerify reference paths must not contain semicolons." };
        if (references.Count > 0)
        {
            arguments.Add("-AdditionalReferencePath");
            arguments.Add(string.Join(';', references));
        }
        try { result = await runner.RunAsync(new BoundedProcessRequest(pwshPath, arguments, root, timeout), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new JsonObject { ["status"] = "blocked", ["diagnostic"] = "PowerShell 7 or ILVerify launcher is unavailable: " + exception.Message }; }
        string? evidenceJson = File.Exists(evidencePath) && new FileInfo(evidencePath).Length <= 1_048_576 ? await File.ReadAllTextAsync(evidencePath, cancellationToken).ConfigureAwait(false) : null;
        bool verified = Succeeded(result) && evidenceJson is not null && IlVerifyEvidenceSucceeded(evidenceJson);
        bool unavailable = result.Termination != BoundedProcessTermination.Exited || result.StandardError.Contains("SDK", StringComparison.OrdinalIgnoreCase) || result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase) || result.StandardError.Contains("tool", StringComparison.OrdinalIgnoreCase) && result.StandardError.Contains("cannot", StringComparison.OrdinalIgnoreCase);
        return new JsonObject { ["status"] = verified ? "passed" : unavailable ? "blocked" : "failed", ["succeeded"] = verified, ["diagnostic"] = verified ? null : unavailable ? "ILVerify launcher or SDK is unavailable." : "ILVerify did not produce successful evidence.", ["process"] = Evidence(result), ["evidencePath"] = Path.GetRelativePath(root, evidencePath).Replace(Path.DirectorySeparatorChar, '/'), ["evidenceJson"] = evidenceJson, ["evidenceSha256"] = evidenceJson is not null ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(evidenceJson))) : null };
    }
    private static async Task<JsonObject> RunNativeAotAsync(BoundedProcessRunner runner, string root, string directory, string assembly, string assemblyName, string expectedKey, TimeSpan timeout, string runtimeIdentifier, CancellationToken cancellationToken, IReadOnlyList<string>? additionalAssemblies = null)
    {
        bool compatible = IsNativeHost(runtimeIdentifier);
        if (!compatible) return new JsonObject { ["status"] = "blocked", ["diagnostic"] = $"Native AOT evidence requires the {runtimeIdentifier} host." };
        try
        {
            var publisher = new NativeAotPublisher(runner);
            string outputDirectory = Path.Combine(directory, "native-aot");
            NativeAotPublishResult publish = await publisher.PublishAsync(new NativeAotPublishRequest(assembly, assemblyName, runtimeIdentifier, outputDirectory, timeout, AdditionalAssemblyPaths: additionalAssemblies), cancellationToken).ConfigureAwait(false);
            if (!publish.Succeeded || !Succeeded(publish.ProcessResult) || publish.ExecutablePath is null)
            {
                string diagnostic = publish.ProcessResult.StandardError + publish.ProcessResult.StandardOutput;
                bool unavailable = publish.ProcessResult.Termination != BoundedProcessTermination.Exited || diagnostic.Contains("SDK", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("not found", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("PublishAot", StringComparison.OrdinalIgnoreCase);
                return new JsonObject { ["status"] = unavailable ? "blocked" : "failed", ["diagnostic"] = string.IsNullOrWhiteSpace(diagnostic) ? "Native AOT publish did not produce an executable." : diagnostic, ["publish"] = Evidence(publish.ProcessResult), ["hostCleanupIncomplete"] = publish.HostCleanupIncomplete };
            }
            BoundedProcessResult run = await runner.RunAsync(new BoundedProcessRequest(publish.ExecutablePath, [], outputDirectory, timeout), cancellationToken).ConfigureAwait(false);
            string expected = ExpectedOutputs.TryGetValue(expectedKey, out string? known) ? known : NormalizeOutput(expectedKey + "\n");
            bool outputMatches = OutputMatches(run, expected);
            JsonObject runEvidence = Evidence(run);
            runEvidence["outputMatches"] = outputMatches;
            return new JsonObject { ["status"] = Succeeded(run) && outputMatches ? "passed" : "failed", ["succeeded"] = Succeeded(run) && outputMatches, ["diagnostic"] = Succeeded(run) && outputMatches ? null : "Native AOT executable failed to run or output differed.", ["publish"] = Evidence(publish.ProcessResult), ["run"] = runEvidence, ["outputMatches"] = outputMatches, ["assemblySha256"] = ComputeSha256IfPresent(assembly), ["executablePath"] = Path.GetRelativePath(root, publish.ExecutablePath).Replace(Path.DirectorySeparatorChar, '/'), ["executableSha256"] = File.Exists(publish.ExecutablePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(publish.ExecutablePath))) : null, ["hostCleanupIncomplete"] = publish.HostCleanupIncomplete };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return new JsonObject { ["status"] = "blocked", ["diagnostic"] = "Native AOT toolchain unavailable: " + exception.Message }; }
    }
    private static bool IlVerifyEvidenceSucceeded(string json)
    {
        try { using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Succeeded", out JsonElement succeeded) && succeeded.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }
    private static async Task<ToolProbe> ProbeToolAsync(BoundedProcessRunner runner, string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { BoundedProcessResult result = await runner.RunAsync(new BoundedProcessRequest(fileName, arguments, workingDirectory, timeout), cancellationToken).ConfigureAwait(false); return new ToolProbe(result, result.Succeeded ? null : $"{fileName} preflight exited with code {result.ExitCode}."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new ToolProbe(null, $"{fileName} preflight was cancelled by the platform deadline."); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException) { return new ToolProbe(null, $"{fileName} preflight unavailable: {exception.Message}"); }
    }
    private static string ProbeVersion(ToolProbe probe, string fallback) => Succeeded(probe.Result) ? probe.Result!.StandardOutput.Trim() : fallback;
    private static JsonObject ProbeEvidence(ToolProbe probe) => probe.Result is null ? new JsonObject { ["status"] = "blocked", ["diagnostic"] = probe.Diagnostic } : Evidence(probe.Result);
    private static string ResolveToolPath(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured ? configured : fallback;
    private static string ResolveCliPath(string root)
    {
        string configured = Environment.GetEnvironmentVariable("RUSTSHARP_P1_CLI_PATH") ?? Path.Combine(root, "src", "RustSharp.Cli", "bin", "Release", "net10.0", "rsc.dll");
        return Path.GetFullPath(configured, root);
    }
    private static string ComputeSha256IfPresent(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
    private static string NormalizeOutput(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    internal static bool OutputMatches(BoundedProcessResult? result, string expected) => Succeeded(result) && NormalizeOutput(result!.StandardOutput) == NormalizeOutput(expected);
    internal static bool IsSemanticClosureEligible(string id) => SemanticClosureEligibleIds.Contains(id);
    internal static P1EvidenceBindingValidator.BindingExpectation CreateBindingExpectation(P1ExpandedSuiteValidator.SuiteSpec suite, string runtimeIdentifier, string manifestSha256, string compilerSha256, string? candidateSha = null)
    {
        ArgumentNullException.ThrowIfNull(suite);
        if (suite.Profile != ProfileName || suite.Cases.Count != P1ExpandedSuiteValidator.PlatformDenominator || suite.Denominator != P1ExpandedSuiteValidator.PlatformDenominator)
            throw new ArgumentException("Binding requires the previously validated frozen platform suite.", nameof(suite));
        return new("p1-platform-coreclr-ilverify-native-aot", ProfileName, runtimeIdentifier, suite.Denominator, manifestSha256, compilerSha256,
            CandidateSha: candidateSha, CaseBindings: suite.Cases.Select(static fixture => new P1EvidenceBindingValidator.CaseBinding(fixture.Id, fixture.Source, fixture.SourceSha256, fixture.ExpectationSha256,
                ExpectedOutputs.TryGetValue(fixture.Id, out string? output) ? output : fixture.Id + "\n")).ToArray(), RequireSemanticClosure: true,
            ObservedRuntimeIdentifier: System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
    }
    internal static string ReadGeneratedAssemblyName(string path) => System.Reflection.AssemblyName.GetAssemblyName(path).Name ?? throw new InvalidOperationException("Generated PE assembly identity is missing.");
    private static bool IsNativeHost(string runtimeIdentifier) => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 && (runtimeIdentifier == "win-x64" ? OperatingSystem.IsWindows() : runtimeIdentifier == "linux-x64" && OperatingSystem.IsLinux());
    private sealed record ToolProbe(BoundedProcessResult? Result, string? Diagnostic);
    private static JsonObject Blocked(P1ExpandedSuiteValidator.CaseSpec fixture, string reason)
    {
        bool semanticEligible = SemanticClosureEligibleIds.Contains(fixture.Id);
        return new() { ["id"] = fixture.Id, ["source"] = fixture.Source, ["sourceSha256"] = fixture.SourceSha256, ["expectationSha256"] = fixture.ExpectationSha256, ["status"] = "blocked", ["semanticClosureEligible"] = semanticEligible, ["semanticCoverage"] = semanticEligible ? "ownership-drop-scenario" : "placeholder", ["difference"] = reason };
    }
    private static bool Succeeded(BoundedProcessResult? result) => result is not null && result.Succeeded && !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut && !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;
    private static JsonObject Evidence(BoundedProcessResult result) => new() { ["commandLine"] = result.StartedProcess.CommandLine, ["fileName"] = result.StartedProcess.FileName, ["arguments"] = new JsonArray(result.StartedProcess.Arguments.Select(static argument => (JsonNode)argument).ToArray()), ["workingDirectory"] = result.StartedProcess.WorkingDirectory, ["processId"] = result.StartedProcess.ProcessId, ["parentProcessId"] = result.StartedProcess.ParentProcessId, ["startedAtUtc"] = result.StartedProcess.StartedAt, ["exitCode"] = result.ExitCode, ["termination"] = result.Termination.ToString().ToLowerInvariant(), ["elapsedMilliseconds"] = result.Elapsed.TotalMilliseconds, ["standardOutput"] = result.StandardOutput, ["standardError"] = result.StandardError, ["outputTruncated"] = result.OutputTruncated, ["outputReadTimedOut"] = result.OutputReadTimedOut, ["outputDrainTimedOut"] = result.OutputDrainTimedOut, ["outputReadLimitReached"] = result.OutputReadLimitReached, ["cleanupIncomplete"] = result.ProcessTreeCleanupIncomplete, ["diagnostic"] = result.OutputDiagnostic ?? result.ProcessTreeCleanupDiagnostic };
    private static string? ResolveCandidateSha() => Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("CI_COMMIT_SHA");
    private static string? DeleteBounded(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string ownedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string prefix = ".run-p1-platform-v2-" + Environment.ProcessId + "-";
        string fileName = Path.GetFileName(fullPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), ownedParent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !fileName.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(fileName[prefix.Length..], "N", out _))
            return "Platform cleanup refused a directory outside the task-owned temporary prefix.";
        if (!Directory.Exists(fullPath)) return null;
        Exception? last = null;
        Stopwatch clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { Directory.Delete(fullPath, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { last = exception; }
            if (!Directory.Exists(fullPath)) return null;
            Thread.Sleep(50);
        }
        return "Platform run directory cleanup failed: " + (last?.Message ?? "directory still exists");
    }
}
