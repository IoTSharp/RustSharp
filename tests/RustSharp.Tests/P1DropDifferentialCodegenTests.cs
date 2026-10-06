using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Conformance;
using RustSharp.Runtime;
using RustSharp.Semantics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RustSharp.Tests;

internal static class P1DropDifferentialCodegenTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 generated Drop rustc 1.98 bounded smoke records one real case", SmokeAsync),
        new("P1 generated Drop rustc 1.98 differential records equality and frozen contract differences", RunAsync),
        new("P1 Drop oracle distinguishes Windows double panic from ordinary unwind", OracleClassificationAsync),
        new("P1 generated abort classification requires the declared raw exit code", GeneratedClassificationAsync),
        new("P1 callable original reports reject stale producers, mismatched runtimes and nonadjacent runtime paths", CallableProducerBindingAsync),
    ];

    private static async Task RunAsync()
    {
        P1DropDifferentialRunner.Result result = await ExecuteAsync(P1DropDifferentialRunner.MaximumCases).ConfigureAwait(false);
        AssertEx.True(result.ExpectedContractSatisfied,
            $"Drop source differential did not close its fixed contract: passed={result.Passed}, differences={result.ContractDifferences}, failed={result.Failed}, blocked={result.Blocked}; evidence={result.ReportPath}");
        int differences = OperatingSystem.IsLinux() ? 3 : 2;
        AssertEx.Equal(differences, result.ContractDifferences, "The fixed Windows/Linux oracle differences must remain explicit.");
        AssertEx.Equal(P1DropDifferentialRunner.MaximumCases - differences, result.Passed,
            "Different stdout must never count as exact rustc equality.");
        await VerifyClosedReportAsync(result.ReportPath).ConfigureAwait(false);
    }

    private static async Task SmokeAsync()
    {
        P1DropDifferentialRunner.Result result = await ExecuteAsync(1).ConfigureAwait(false);
        AssertEx.Equal(1, result.Passed, "The bounded smoke must execute a real source-to-PE oracle case.");
        AssertEx.Equal(P1DropDifferentialRunner.MaximumCases - 1, result.Skipped, "The smoke cannot claim the remaining fixed suite executed.");
        AssertEx.True(result.CleanupComplete && result.Failed == 0 && result.Blocked == 0 && !result.ExpectedContractSatisfied,
            "A valid one-case smoke must remain ineligible as full suite closure evidence: " + result.ReportPath);
        JsonObject report = await ReadReportAsync(result.ReportPath).ConfigureAwait(false);
        AssertEx.Throws<InvalidOperationException>(() => P1DropDifferentialRunner.ValidateClosedReport(report, RepositoryRoot()));
    }

    private static Task OracleClassificationAsync()
    {
        const string first = "thread 'main' (42) panicked at program.rs:1:1:\nattempt to add with overflow\n";
        const string second = "thread 'main' (42) panicked at program.rs:2:1:\nattempt to divide by zero\n";
        int failFast = unchecked((int)0xC0000409);
        AssertEx.Equal("double-panic-abort", P1DropDifferentialRunner.ClassifyOracleFailure(
            failFast, first + second, SafeCorePanicStrategy.Unwind));
        AssertEx.Equal("unwind", P1DropDifferentialRunner.ClassifyOracleFailure(
            failFast, first, SafeCorePanicStrategy.Unwind), "A fail-fast code alone cannot prove a second panic.");
        AssertEx.Equal("unwind", P1DropDifferentialRunner.ClassifyOracleFailure(
            101, first + second, SafeCorePanicStrategy.Unwind), "Two headers without an abort exit cannot prove double-panic abort.");
        AssertEx.Equal("double-panic-abort", P1DropDifferentialRunner.ClassifyOracleFailure(
            134, "panic in a destructor during cleanup", SafeCorePanicStrategy.Unwind));
        AssertEx.Equal("panic-abort", P1DropDifferentialRunner.ClassifyOracleFailure(
            failFast, first, SafeCorePanicStrategy.Abort));
        return Task.CompletedTask;
    }

    private static Task GeneratedClassificationAsync()
    {
        const string single = "RustSharp panic abort: Arithmetic operation resulted in an overflow.";
        const string twice = "RustSharp double panic abort: body | destructor";
        AssertEx.Equal("panic-abort", P1DropDifferentialRunner.ClassifyGeneratedFailure(134, single));
        AssertEx.Equal("double-panic-abort", P1DropDifferentialRunner.ClassifyGeneratedFailure(134, twice));
        AssertEx.Equal("unexpected-runtime-failure", P1DropDifferentialRunner.ClassifyGeneratedFailure(135, single),
            "Printing an abort message does not establish the declared process exit.");
        AssertEx.Equal("unexpected-runtime-failure", P1DropDifferentialRunner.ClassifyGeneratedFailure(unchecked((int)0xC0000409), twice),
            "A different fail-fast outcome cannot count as generated exit 134.");
        AssertEx.Equal("unexpected-runtime-failure", P1DropDifferentialRunner.ClassifyGeneratedFailure(null, twice));
        AssertEx.Equal("success", P1DropDifferentialRunner.ClassifyGeneratedFailure(0, twice));
        return Task.CompletedTask;
    }

    private static Task CallableProducerBindingAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string runtimeHash = Fingerprint(typeof(RustGeneratedPanic));
        var original = new JsonObject
        {
            ["compilerAssemblySha256"] = Fingerprint(typeof(CompilerDriver)),
            ["semanticsAssemblySha256"] = Fingerprint(typeof(SafeCoreMirLowering)),
            ["emitterAssemblySha256"] = Fingerprint(typeof(ClrLirEmitter)),
            ["runtimeAssemblySha256"] = runtimeHash,
            ["artifacts"] = new JsonArray(
                new JsonObject { ["runtimeAssemblySha256"] = runtimeHash },
                new JsonObject { ["runtimeAssemblySha256"] = runtimeHash }),
        };
        P1DropCallInterfaceRunner.ValidateCurrentImplementation(original, deadline.Token);
        string runtimePath = Path.Combine(AppContext.BaseDirectory, "RustSharp.Runtime.dll");
        string generatedPath = Path.Combine(AppContext.BaseDirectory, "P1DropCallableUnwind.dll");
        P1DropCallInterfaceRunner.RequireRuntimeCompanion(generatedPath, runtimePath, deadline.Token);
        // A current runtime at another directory cannot bind the companion copied by Native AOT.
        AssertEx.Throws<InvalidOperationException>(() => P1DropCallInterfaceRunner.RequireRuntimeCompanion(
            Path.Combine(AppContext.BaseDirectory, "other", "P1DropCallableUnwind.dll"), runtimePath, deadline.Token));
        // A different filename cannot substitute for the actual PE-adjacent RustSharp.Runtime.dll.
        AssertEx.Throws<InvalidOperationException>(() => P1DropCallInterfaceRunner.RequireRuntimeCompanion(
            generatedPath, Path.Combine(AppContext.BaseDirectory, "RustSharp.Runtime.other.dll"), deadline.Token));
        Action<JsonObject>[] mutations =
        [
            report => report.Remove("compilerAssemblySha256"),
            report => report["compilerAssemblySha256"] = new string('A', 64),
            report => report["semanticsAssemblySha256"] = new string('A', 64),
            report => report["emitterAssemblySha256"] = new string('A', 64),
            report => report["runtimeAssemblySha256"] = new string('A', 64),
            report => report["artifacts"]![0]!["runtimeAssemblySha256"] = new string('A', 64),
            report => report["artifacts"]![1]!["runtimeAssemblySha256"] = new string('A', 64),
            report => report["artifacts"]!.AsArray().RemoveAt(1),
        ];
        for (int index = 0; index < mutations.Length && index < 8; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Callable producer contamination checks exceeded ten seconds.");
            JsonObject changed = original.DeepClone().AsObject();
            mutations[index](changed);
            AssertEx.Throws<InvalidOperationException>(() => P1DropCallInterfaceRunner.ValidateCurrentImplementation(changed, deadline.Token));
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P1DropCallInterfaceRunner.ValidateCurrentImplementation(original, cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => P1DropCallInterfaceRunner.RequireRuntimeCompanion(generatedPath, runtimePath, cancelled.Token));
        AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Callable producer and companion contamination checks exceeded ten seconds.");
        return Task.CompletedTask;

        string Fingerprint(Type producer)
        {
            deadline.Token.ThrowIfCancellationRequested();
            string path = Path.Combine(AppContext.BaseDirectory, producer.Assembly.GetName().Name + ".dll");
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new InvalidOperationException("Callable test producer exceeded its 16 MiB bound.");
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
    }

    private static async Task VerifyClosedReportAsync(string path)
    {
        JsonObject original = await ReadReportAsync(path).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string root = RepositoryRoot();
        P1DropDifferentialRunner.ValidateClosedReport(original, root, deadline.Token);
        Action<JsonObject>[] mutations =
        [
            report => report["summary"]!["expectedContractSatisfied"] = false,
            report => report["summary"]!["skipped"] = 1,
            report => report["cleanup"]!["completed"] = false,
            report => report["runPurpose"] = "bounded-smoke-only",
            report => report["profile"] = "p1-drop-closure-v3",
            report => report["oracleVersion"] = "rustc unpinned",
            report => report["cases"]![1]!["id"] = report["cases"]![0]!["id"]!.GetValue<string>(),
            report => report["cases"]![0]!["sourceSha256"] = report["cases"]![1]!["sourceSha256"]!.GetValue<string>(),
            report => report["cases"]![0]!["expectedOutput"] = "invented trace\n",
            report => report["implementationAssemblies"]![1] = report["implementationAssemblies"]![0]!.DeepClone(),
            report => report["implementationAssemblies"]![0]!["sha256"] = new string('A', 64),
            report => report["cases"]![0]!["rustSharpCompile"]!["outputSha256"] = new string('A', 64),
            report => report["cases"]![0]!["rustSharpRun"]!["outputDrainTimedOut"] = true,
            report => report["cases"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "explicit-abort")!["rustSharpRun"]!["rawExitCode"] = 135,
            report => report["hostContract"]!["contractVersion"] = 2,
            report => report["hostContract"]!["platform"] = OperatingSystem.IsLinux() ? "windows-x64" : "linux-x64",
            report => report["runtimeIdentifier"] = OperatingSystem.IsLinux() ? "win-x64" : "linux-x64",
            report => report["hostContract"]!["nativeRuntimeIdentifier"] = "forged-x64",
            report => report["hostContract"]!["exactMatches"] = 28,
            report => report["hostContract"]!["contractDifferences"] = 0,
            report => report["hostContract"]!["fullP1Closure"] = true,
            report => report["hostContract"]!["fullP1LanguageGateApproved"] = true,
            report => report["cases"]![0]!["rustcArtifact"]!["sha256"] = new string('A', 64),
            report => report["cases"]![1]!["rustcArtifact"]!["platform"] = OperatingSystem.IsLinux() ? "windows-x64" : "linux-x64",
            report => report["cases"]![0]!["rustcCompile"]!["arguments"]![1] = "unrelated-source.rs",
            report => report["cases"]![0]!["rustcRun"]!["executable"] = "unrelated-oracle",
            report => report["cases"]![0]!["rustSharpRun"]!["arguments"]![0] = "unrelated-program.dll",
            report => Case(report, "normal-multiple-drop-panic-contract")["expectedRustcOutcome"] = "multiple-normal-cleanup-failures",
            report => Case(report, "normal-own-drop-body-and-field-failure-contract")["expectedRustcOutput"] = "body\nowner\nbad\ngood\n",
            report => Case(report, "unwind-own-drop-body-failure")["expectedRustcOutput"] = OperatingSystem.IsLinux() ? "body\nowner\n" : "body\nowner\nbad\n",
            report => Case(report, "unwind-own-drop-body-failure")["expectedRustSharpOutput"] = "body\nowner\nbad\n",
            report => Case(report, "unwind-own-drop-body-failure")["rustcRun"]!["stdout"] = OperatingSystem.IsLinux() ? "body\nowner\n" : "body\nowner\nbad\n",
            report => Case(report, "unwind-own-drop-body-failure")["rustSharpRun"]!["stdout"] = "body\nowner\nbad\n",
            report => Case(report, "unwind-own-drop-body-failure")["status"] = OperatingSystem.IsLinux() ? "passed" : "contract-difference",
            report => Case(report, "unwind-own-drop-body-failure")["expectedOutputsAgree"] = OperatingSystem.IsLinux(),
        ];
        AssertEx.True(mutations.Length <= 36, "Drop contamination controls exceed their fixed count bound.");
        for (int index = 0; index < mutations.Length && index < 36; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(45), "Drop contamination controls exceeded 45 seconds.");
            JsonObject changed = original.DeepClone().AsObject();
            mutations[index](changed);
            AssertEx.Throws<InvalidOperationException>(() => P1DropDifferentialRunner.ValidateClosedReport(changed, root, deadline.Token));
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => P1DropDifferentialRunner.ValidateClosedReport(original, root, cancelled.Token));

        static JsonObject Case(JsonObject report, string id) => report["cases"]!.AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == id)!.AsObject();
    }

    private static async Task<JsonObject> ReadReportAsync(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidOperationException("Drop test report exceeds its four MiB bound.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return JsonNode.Parse(await File.ReadAllTextAsync(path, deadline.Token).ConfigureAwait(false))!.AsObject();
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static async Task<P1DropDifferentialRunner.Result> ExecuteAsync(int maximumCasesToExecute)
    {
        string root = RepositoryRoot();
        string report = Path.Combine(root, "artifacts", "p1-drop", "differential-" +
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier + "-" + Guid.NewGuid().ToString("N") + ".json");
        string rustc = ResolveTool("RUSTSHARP_RUSTC_PATH", "rustc");
        string dotnet = ResolveTool("RUSTSHARP_DOTNET_PATH", "dotnet");
        return await P1DropDifferentialRunner.RunAsync(root, report, rustc, dotnet,
            maximumCasesToExecute: maximumCasesToExecute).ConfigureAwait(false);
    }

    private static string ResolveTool(string configurationName, string command)
    {
        string? configured = Environment.GetEnvironmentVariable(configurationName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new InvalidOperationException(configurationName + " must name an existing absolute executable path.");
            return configured;
        }
        string executable = OperatingSystem.IsWindows() ? command + ".exe" : command;
        string[] searchPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Search only registered PATH entries; never scan directories or recurse.
        for (int index = 0; index < searchPath.Length && index < 64 && clock.Elapsed < TimeSpan.FromSeconds(2); index++)
        {
            string directory = searchPath[index].Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"{command} was not found within 64 PATH entries and two seconds; configure {configurationName} explicitly.");
    }
}
