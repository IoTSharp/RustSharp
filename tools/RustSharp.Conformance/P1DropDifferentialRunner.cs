using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Runtime;
using RustSharp.Semantics;

namespace RustSharp.Conformance;

/// <summary>Fresh source-to-PE Drop evidence, with explicit frozen-contract differences.</summary>
internal static class P1DropDifferentialRunner
{
    internal const string OracleVersion = "rustc 1.98.0 (88d9e12ae 2026-08-18)";
    internal const string ClosureProfile = "p1-drop-closure-v4";
    internal const string NativeClosureProfile = "p1-drop-closure-v5";
    internal const int MaximumCases = 28;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SuiteTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan NativeSuiteTimeout = TimeSpan.FromSeconds(240);
    private const string Prelude = """
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        fn make(value: i32) -> Marker { Marker { value: value } }
        fn overflow(value: i32) -> i32 { value + 1 }

        """;

    internal sealed record Result(string ReportPath, int Passed, int ContractDifferences, int Failed,
        int Blocked, int Skipped, bool CleanupComplete)
    {
        public bool ExpectedContractSatisfied => Passed + ContractDifferences == MaximumCases &&
            Failed == 0 && Blocked == 0 && Skipped == 0 && CleanupComplete;
    }

    private sealed record Fixture(string Id, string Source, string Output, string Outcome = "success",
        SafeCorePanicStrategy Strategy = SafeCorePanicStrategy.Unwind, string? OracleOutcome = null,
        string? OracleOutput = null);

    private static Fixture PlatformFixture(int index, string platform, bool nativeV2 = false) =>
        platform == "linux-x64" && Fixtures[index].Id == "unwind-own-drop-body-failure"
            ? nativeV2 ? Fixtures[index] with { Output = "body\nowner\nbad\n" }
                : Fixtures[index] with { OracleOutcome = "double-panic-abort", OracleOutput = "body\nowner\nbad\n" }
            : Fixtures[index];

    private static int ContractDifferences(string platform, bool nativeV2 = false) => !nativeV2 && platform == "linux-x64" ? 3 : 2;

    private static string HostPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()))
            throw new InvalidOperationException("Drop differential evidence requires Windows or Linux x64.");
        return OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    }

    private static string Difference(Fixture fixture) => fixture.Id == "unwind-own-drop-body-failure"
        ? "Linux rustc visits the bad field after a destructor panic during body unwind; Rust# aborts after the owner panic. The exact stdout differs (body/owner/bad versus body/owner), while both outcomes are double-panic-abort. This platform oracle difference is not approved by the full P1 language exit gate."
        : fixture.OracleOutput is null
            ? "Observable stdout agrees; rustc aborts the second destructor panic while Rust# preserves both failures in RustGeneratedCleanupException."
            : "Rust# continues to the good field after owner and bad-field failures; rustc aborts after bad and never visits good. Both the stdout difference and exit-category difference are explicit frozen-contract expectations.";

    private static readonly Fixture[] Fixtures =
    [
        new("array-order", Prelude + "fn main() { let values: [Marker; 3] = [make(1), make(2), make(3)]; println!(\"body\"); }", "body\n1\n2\n3\n"),
        new("aggregate-order", Prelude + "struct Fields { pair: (Marker, Marker), tail: Marker } fn main() { let value = Fields { pair: (make(1), make(2)), tail: make(3) }; println!(\"body\"); }", "body\n1\n2\n3\n"),
        new("enum-active", Prelude + "enum Choice { One(Marker), Two(Marker, Marker), Empty } fn main() { { let choice = Choice::Two(make(2), make(1)); println!(\"body\"); } { let empty = Choice::Empty; } }", "body\n2\n1\n"),
        new("partial-move", Prelude + "struct Fields { first: Marker, second: Marker } fn main() { let value = Fields { first: make(1), second: make(2) }; let moved = value.first; println!(\"body\"); }", "body\n1\n2\n"),
        new("aggregate-replacement", Prelude + "struct Fields { first: Marker, second: Marker } fn main() { let mut value = Fields { first: make(1), second: make(2) }; value = Fields { first: make(3), second: make(4) }; println!(\"body\"); }", "1\n2\nbody\n3\n4\n"),
        new("field-replacement", Prelude + "struct Fields { first: Marker, second: Marker } fn main() { let mut value = Fields { first: make(1), second: make(2) }; value.first = make(3); println!(\"body\"); }", "1\nbody\n3\n2\n"),
        new("replacement-rhs-panic", Prelude + "fn main() { let mut value = make(1); value = make(overflow(2147483647)); println!(\"unreachable\"); }", "1\n", "unwind"),
        new("statement-temporary", Prelude + "fn main() { make(1); println!(\"after\"); }", "1\nafter\n"),
        new("wildcard-temporary", Prelude + "fn main() { let _ = make(1); println!(\"after\"); }", "1\nafter\n"),
        new("extended-temporary-borrow", Prelude + "fn main() { let view = &make(1); println!(\"{}\", view.value); println!(\"body\"); }", "1\nbody\n1\n"),
        new("partial-construction-panic", Prelude + "struct Fields { first: Marker, second: Marker } fn main() { let value = Fields { first: make(1), second: make(overflow(2147483647)) }; println!(\"unreachable\"); }", "1\n", "unwind"),
        new("body-unwind", Prelude + "fn main() { let value = make(1); println!(\"body\"); println!(\"{}\", overflow(2147483647)); }", "body\n1\n", "unwind"),
        new("normal-single-drop-panic", Prelude + "struct Bad; impl Drop for Bad { fn drop(&mut self) { println!(\"bad\"); println!(\"{}\", overflow(2147483647)); } } fn main() { let first = make(1); let bad = Bad; println!(\"body\"); }", "body\nbad\n1\n", "unwind"),
        new("body-double-panic", Prelude + "struct Bad; impl Drop for Bad { fn drop(&mut self) { println!(\"bad\"); println!(\"{}\", overflow(2147483647)); } } fn main() { let outer = make(1); let bad = Bad; println!(\"body\"); println!(\"{}\", overflow(2147483647)); }", "body\nbad\n", "double-panic-abort"),
        new("explicit-abort", Prelude + "fn main() { let value = make(1); println!(\"body\"); println!(\"{}\", overflow(2147483647)); }", "body\n", "panic-abort", SafeCorePanicStrategy.Abort),
        new("normal-multiple-drop-panic-contract", Prelude + "struct First; struct Second; impl Drop for First { fn drop(&mut self) { println!(\"first\"); println!(\"{}\", overflow(2147483647)); } } impl Drop for Second { fn drop(&mut self) { println!(\"second\"); println!(\"{}\", overflow(2147483647)); } } fn main() { let first = First; let second = Second; println!(\"body\"); }", "body\nsecond\nfirst\n", "multiple-normal-cleanup-failures", OracleOutcome: "double-panic-abort"),
        new("owned-return", Prelude + "fn value() -> Marker { let other = make(1); let returned = make(2); return returned; } fn tail() -> Marker { let other = make(3); let returned = make(4); returned } fn main() { let first = value(); let second = tail(); println!(\"body\"); }", "1\n3\nbody\n4\n2\n"),
        new("loop-continue", Prelude + "fn main() { let mut count: i32 = 0; while count < 2 { let marker = make(1); println!(\"iteration\"); count += 1; continue; } println!(\"after\"); }", "iteration\n1\niteration\n1\nafter\n"),
        new("labelled-break", Prelude + "fn main() { let outer = make(1); 'target: loop { let first = make(2); loop { let second = make(3); println!(\"body\"); break 'target; } } println!(\"after\"); }", "body\n3\n2\nafter\n1\n"),
        new("moved-unwind", Prelude + "fn consume(value: Marker) { println!(\"callee\"); println!(\"{}\", overflow(2147483647)); } fn main() { let retained = make(1); let moved = make(2); consume(moved); println!(\"unreachable\"); }", "callee\n2\n1\n", "unwind"),
        new("active-enum-unwind", Prelude + "enum Choice { One(Marker), Two(Marker, Marker), Empty } fn main() { let choice = Choice::Two(make(2), make(1)); println!(\"body\"); println!(\"{}\", overflow(2147483647)); }", "body\n2\n1\n", "unwind"),
        new("expired-array-unwind", Prelude + "fn main() { { let values: [Marker; 2] = [make(1), make(2)]; println!(\"scope\"); } println!(\"after\"); println!(\"{}\", overflow(2147483647)); }", "scope\n1\n2\nafter\n", "unwind"),
        new("zero-sized-construction-panic", Prelude + "struct First; impl Drop for First { fn drop(&mut self) { println!(\"first\"); } } struct Fields { first: First, second: First } fn fail(value: i32) -> First { println!(\"{}\", overflow(value)); First } fn main() { let fields = Fields { first: First, second: fail(2147483647) }; println!(\"unreachable\"); }", "first\n", "unwind"),
        new("owned-closure-capture", Prelude + "fn observe(value: &Marker) { println!(\"{}\", value.value); } fn main() { let owner = make(1); let callback = move || observe(&owner); callback(); println!(\"after\"); }", "1\nafter\n1\n"),
        new("normal-own-drop-body-and-field-failure-contract", """
            fn overflow(value: i32) -> i32 { value + 1 }
            fn divide(value: i32) -> i32 { 10 / value }
            struct Bad { divisor: i32 }
            impl Drop for Bad {
                fn drop(&mut self) { println!("bad"); println!("{}", divide(self.divisor)); }
            }
            struct Good;
            impl Drop for Good { fn drop(&mut self) { println!("good"); } }
            struct Owner { bad: Bad, good: Good }
            impl Drop for Owner {
                fn drop(&mut self) { println!("owner"); println!("{}", overflow(2147483647)); }
            }
            fn main() { let owner = Owner { bad: Bad { divisor: 0 }, good: Good }; println!("body"); }
            """, "body\nowner\nbad\ngood\n", "multiple-normal-cleanup-failures",
            OracleOutcome: "double-panic-abort", OracleOutput: "body\nowner\nbad\n"),
        new("unwind-own-drop-body-failure", """
            fn overflow(value: i32) -> i32 { value + 1 }
            fn divide(value: i32) -> i32 { 10 / value }
            struct Bad;
            impl Drop for Bad {
                fn drop(&mut self) { println!("bad"); println!("{}", overflow(2147483647)); }
            }
            struct Good;
            impl Drop for Good { fn drop(&mut self) { println!("good"); } }
            struct Owner { divisor: i32, bad: Bad, good: Good }
            impl Drop for Owner {
                fn drop(&mut self) { println!("owner"); println!("{}", divide(self.divisor)); }
            }
            fn main() {
                let owner = Owner { divisor: 0, bad: Bad, good: Good };
                println!("body");
                println!("{}", overflow(2147483647));
                println!("unreachable");
            }
            """, "body\nowner\n", "double-panic-abort"),
        new("enum-mutable-call-replacement", Prelude + """
            enum Choice { One(Marker), Two(Marker, Marker), Empty }
            fn replace(value: &mut Choice) { *value = Choice::Two(make(2), make(3)); }
            fn main() {
                let retained = make(9);
                let mut choice = Choice::One(make(1));
                replace(&mut choice);
                println!("body");
            }
            """, "1\nbody\n2\n3\n9\n"),
        new("enum-mutable-call-replacement-unwind", Prelude + """
            enum Choice { One(Marker), Two(Marker, Marker), Empty }
            fn replace(value: &mut Choice) {
                *value = Choice::Two(make(2), make(3));
                println!("callee");
                println!("{}", overflow(2147483647));
            }
            fn main() {
                let retained = make(9);
                let mut choice = Choice::One(make(1));
                println!("body");
                replace(&mut choice);
                println!("unreachable");
            }
            """, "body\n1\ncallee\n2\n3\n9\n", "unwind"),
    ];

    internal static async Task<Result> RunAsync(string repositoryRoot, string reportPath,
        string rustcPath, string dotnetPath, int maximumCasesToExecute = MaximumCases,
        CancellationToken cancellationToken = default) => await RunCoreAsync(repositoryRoot, reportPath, rustcPath, dotnetPath,
            maximumCasesToExecute, nativeV2: false, cancellationToken).ConfigureAwait(false);

    internal static async Task<Result> RunNativeV5Async(string repositoryRoot, string reportPath,
        string rustcPath, string dotnetPath, int maximumCasesToExecute = MaximumCases,
        CancellationToken cancellationToken = default) => await RunCoreAsync(repositoryRoot, reportPath, rustcPath, dotnetPath,
            maximumCasesToExecute, nativeV2: true, cancellationToken).ConfigureAwait(false);

    private static async Task<Result> RunCoreAsync(string repositoryRoot, string reportPath, string rustcPath,
        string dotnetPath, int maximumCasesToExecute, bool nativeV2, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string report = Path.GetFullPath(reportPath, root);
        string evidenceRoot = Path.GetFullPath(Path.Combine(root, "artifacts", "p1-drop"));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!report.StartsWith(evidenceRoot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Drop evidence must be saved below the dedicated artifacts/p1-drop directory.");
        if (Fixtures.Length != MaximumCases) throw new InvalidOperationException("The fixed Drop denominator changed.");
        string platform = HostPlatform();
        string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        if (maximumCasesToExecute is < 1 or > MaximumCases) throw new ArgumentOutOfRangeException(nameof(maximumCasesToExecute));
        string evidenceDirectory = Path.Combine(Path.GetDirectoryName(report)!, "evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan suiteTimeout = nativeV2 ? NativeSuiteTimeout : SuiteTimeout;
        deadline.CancelAfter(suiteTimeout);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var clock = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var cases = new JsonArray();
        var processes = new List<BoundedProcessResult>(1 + MaximumCases * 3);
        var runner = new BoundedProcessRunner();
        BoundedProcessResult? probe = null;
        int passed = 0, differences = 0, failed = 0, blocked = 0, skipped = 0;
        string? harnessError = null;
        try
        {
            probe = await RunProcessAsync(runner, rustcPath, ["+1.98.0", "--version"], root, processes, deadline.Token).ConfigureAwait(false);
            bool oracleAvailable = Complete(probe) && probe.Succeeded && probe.StandardOutput.Trim() == OracleVersion;
            for (int index = 0; index < maximumCasesToExecute && clock.Elapsed < suiteTimeout; index++)
            {
                Fixture fixture = PlatformFixture(index, platform, nativeV2);
                if (fixture.OracleOutput is not null && fixture.OracleOutcome is null)
                    throw new InvalidOperationException("An expected oracle stdout difference must have an explicit contract-difference outcome.");
                Console.WriteLine($"P1 Drop differential: {index + 1}/{MaximumCases} {fixture.Id}");
                if (!oracleAvailable || deadline.IsCancellationRequested)
                {
                    cases.Add(Blocked(fixture, !oracleAvailable ? "Pinned rustc 1.98.0 oracle unavailable." : "Suite deadline or cancellation."));
                    blocked++;
                    continue;
                }
                JsonObject result = await RunFixtureAsync(runner, fixture, evidenceDirectory, root,
                    rustcPath, dotnetPath, processes, nativeV2, deadline.Token).ConfigureAwait(false);
                cases.Add(result);
                switch (result["status"]!.GetValue<string>())
                {
                    case "passed": passed++; break;
                    case "contract-difference": differences++; break;
                    case "failed": failed++; break;
                    default: blocked++; break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            harnessError = exception.Message;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
        for (int index = cases.Count; index < MaximumCases; index++)
        {
            if (index >= maximumCasesToExecute && harnessError is null && !deadline.IsCancellationRequested)
            {
                JsonObject unexecuted = Blocked(PlatformFixture(index, platform, nativeV2), "Outside the deliberate smoke execution limit; this report cannot close the fixed suite.");
                unexecuted["status"] = "not-executed";
                cases.Add(unexecuted);
                skipped++;
                continue;
            }
            cases.Add(Blocked(PlatformFixture(index, platform, nativeV2), harnessError ?? "Suite deadline prevented execution."));
            blocked++;
        }
        bool cleanupComplete = processes.All(static process => !process.ProcessTreeCleanupIncomplete);
        string compilerPath = typeof(CompilerDriver).Assembly.Location;
        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["evidenceKind"] = "p1-drop-source-generated-pe-rustc-differential",
            ["profile"] = nativeV2 ? NativeClosureProfile : ClosureProfile,
            ["runPurpose"] = maximumCasesToExecute < MaximumCases ? "bounded-smoke-only" : "fixed-suite-closure",
            ["compilerSha256"] = HashFile(compilerPath),
            ["compilerPath"] = compilerPath,
            ["implementationAssemblies"] = new JsonArray(
                AssemblyFingerprint(typeof(CompilerDriver)),
                AssemblyFingerprint(typeof(SafeCoreMirLowering)),
                AssemblyFingerprint(typeof(ClrLirEmitter)),
                AssemblyFingerprint(typeof(RustGeneratedPanic))),
            ["runtimeIdentifier"] = runtimeIdentifier,
            ["hostContract"] = new JsonObject
            {
                ["contractVersion"] = nativeV2 ? 2 : 1, ["platform"] = platform, ["nativeRuntimeIdentifier"] = runtimeIdentifier,
                ["exactMatches"] = MaximumCases - ContractDifferences(platform, nativeV2),
                ["contractDifferences"] = ContractDifferences(platform, nativeV2),
                ["fullP1Closure"] = false, ["fullP1LanguageGateApproved"] = false,
                ["platformBinding"] = "retained-rustc-native-executable-sha256-and-machine-header",
            },
            ["oracleVersion"] = probe?.StandardOutput.Trim(),
            ["oracleProbe"] = Evidence(probe),
            ["summary"] = new JsonObject
            {
                ["status"] = blocked > 0 || !cleanupComplete ? "blocked" : failed > 0 ? "failed" : skipped > 0 ? "smoke-complete" : differences > 0 ? "passed-with-contract-differences" : "passed",
                ["denominator"] = MaximumCases, ["executed"] = passed + differences + failed,
                ["passed"] = passed, ["contractDifferences"] = differences,
                ["failed"] = failed, ["blocked"] = blocked, ["skipped"] = skipped,
                ["matchesRustcInAllCases"] = passed == MaximumCases,
                ["expectedContractSatisfied"] = passed + differences == MaximumCases && failed == 0 && blocked == 0 && skipped == 0 && cleanupComplete,
            },
            ["contractDifference"] = "Rust# normal cleanup retains multiple destructor failures and continues remaining cleanup; rustc turns the second destructor panic during unwind into abort. The nested owner/field case has a different stdout trace because rustc never visits the remaining good field. On Linux, unwind-own-drop-body-failure has the additional explicit rustc body/owner/bad versus Rust# body/owner trace difference; neither this oracle contract nor its additional difference establishes full P1 language closure.",
            ["execution"] = new JsonObject
            {
                ["startedAtUtc"] = started, ["finishedAtUtc"] = DateTimeOffset.UtcNow,
                ["elapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds, ["deadlineSeconds"] = suiteTimeout.TotalSeconds,
                ["maximumCases"] = MaximumCases, ["processTimeoutSeconds"] = ProcessTimeout.TotalSeconds,
                ["deadlineExpired"] = deadline.IsCancellationRequested,
            },
            ["cleanup"] = new JsonObject { ["completed"] = cleanupComplete, ["retainedEvidenceDirectory"] = evidenceDirectory,
                ["reason"] = cleanupComplete
                    ? "Source, generated PE, oracle executable and raw outcomes are retained deliberately for review and ILVerify; all launched process trees were reclaimed."
                    : "Review artifacts are retained; one or more launched process trees reported incomplete cleanup." },
            ["harnessError"] = harnessError,
            ["cases"] = cases,
        };
        if (nativeV2)
        {
            document["dropCleanupProfile"] = SafeCoreDropCleanupProfiles.NativeV2MetadataValue;
            document["contractDifference"] = "Native v2 retains the two previously frozen normal-cleanup continuation differences. Nested destructor body unwind now follows the native field-cleanup algorithm; no new semantic difference is approved and the full P1 gate remains independent.";
        }
        string temporaryReport = report + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // Even a cancelled run must retain its bounded failure report.
            await File.WriteAllTextAsync(temporaryReport, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryReport, report, overwrite: true);
        }
        finally { if (File.Exists(temporaryReport)) File.Delete(temporaryReport); }
        Console.WriteLine($"P1 Drop evidence: {report}; matched={passed}, contract differences={differences}, failed={failed}, blocked={blocked}");
        return new(report, passed, differences, failed, blocked, skipped, cleanupComplete);
    }

    private static async Task<JsonObject> RunFixtureAsync(BoundedProcessRunner runner, Fixture fixture,
        string evidenceDirectory, string repositoryRoot, string rustcPath, string dotnetPath,
        List<BoundedProcessResult> processes, bool nativeV2, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(evidenceDirectory, fixture.Id);
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string managedOutput = Path.Combine(directory, "program.dll");
        string oracleOutput = Path.Combine(directory, OperatingSystem.IsWindows() ? "oracle.exe" : "oracle");
        await File.WriteAllTextAsync(sourcePath, fixture.Source, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        string sourceHash = HashFile(sourcePath);
        BoundedProcessResult oracleCompile = await RunProcessAsync(runner, rustcPath,
            ["+1.98.0", sourcePath, "--edition", "2024", "-C", "overflow-checks=yes", "-C",
                fixture.Strategy == SafeCorePanicStrategy.Abort ? "panic=abort" : "panic=unwind", "-o", oracleOutput],
            repositoryRoot, processes, cancellationToken).ConfigureAwait(false);
        BoundedProcessResult? oracleRun = Complete(oracleCompile) && oracleCompile.Succeeded
            ? await RunProcessAsync(runner, oracleOutput, [], directory, processes, cancellationToken).ConfigureAwait(false) : null;
        DateTimeOffset compileStarted = DateTimeOffset.UtcNow;
        using var compileDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        compileDeadline.CancelAfter(ProcessTimeout);
        CompilationResult compilation = nativeV2
            ? CompilerDriver.CompileWithDropProfile(fixture.Source, sourcePath, managedOutput, SafeCoreDropCleanupProfile.NativeV2,
                fixture.Strategy, "P1DropDifferential", CompilationProfile.SafeCoreMirV2, compileDeadline.Token)
            : CompilerDriver.CompileWithPanicStrategy(fixture.Source, sourcePath, managedOutput,
                fixture.Strategy, "P1DropDifferential", CompilationProfile.SafeCoreMirV2, compileDeadline.Token);
        BoundedProcessResult? managedRun = compilation.Success
            ? await RunProcessAsync(runner, dotnetPath, [managedOutput], directory, processes, cancellationToken).ConfigureAwait(false) : null;
        string expectedOracleOutcome = fixture.OracleOutcome ?? fixture.Outcome;
        string expectedOracleOutput = fixture.OracleOutput ?? fixture.Output;
        string oracleOutcome = Classify(oracleRun, oracle: true, fixture.Strategy);
        string managedOutcome = Classify(managedRun, oracle: false, fixture.Strategy);
        bool incomplete = !Complete(oracleCompile) || oracleRun is not null && !Complete(oracleRun) ||
            managedRun is not null && !Complete(managedRun);
        bool match = oracleCompile.Succeeded && compilation.Success && oracleRun is not null && managedRun is not null &&
            Normalize(oracleRun.StandardOutput) == expectedOracleOutput && Normalize(managedRun.StandardOutput) == fixture.Output &&
            oracleOutcome == expectedOracleOutcome && managedOutcome == fixture.Outcome;
        string status = incomplete ? "blocked" : !match ? "failed" : fixture.OracleOutcome is null ? "passed" : "contract-difference";
        return new JsonObject
        {
            ["id"] = fixture.Id, ["status"] = status, ["sourcePath"] = sourcePath, ["sourceSha256"] = sourceHash,
            ["panicStrategy"] = fixture.Strategy.ToString().ToLowerInvariant(), ["expectedOutput"] = fixture.Output,
            ["expectedRustcOutput"] = expectedOracleOutput,
            ["expectedRustSharpOutput"] = fixture.Output,
            ["expectedOutputsAgree"] = expectedOracleOutput == fixture.Output,
            ["expectedRustcOutcome"] = expectedOracleOutcome, ["expectedRustSharpOutcome"] = fixture.Outcome,
            ["rustcOutcome"] = oracleOutcome, ["rustSharpOutcome"] = managedOutcome,
            ["rustcArtifact"] = File.Exists(oracleOutput) ? new JsonObject
            {
                ["path"] = oracleOutput, ["sha256"] = HashFile(oracleOutput),
                ["platform"] = NativeExecutablePlatform(oracleOutput),
            } : null,
            ["rustcCompile"] = Evidence(oracleCompile), ["rustcRun"] = Evidence(oracleRun),
            ["rustSharpCompile"] = new JsonObject
            {
                ["success"] = compilation.Success, ["startedAtUtc"] = compileStarted, ["finishedAtUtc"] = DateTimeOffset.UtcNow,
                ["diagnostics"] = new JsonArray(compilation.Diagnostics.Select(static diagnostic =>
                    (JsonNode)new JsonObject { ["code"] = diagnostic.Code, ["message"] = diagnostic.Message }).ToArray()),
                ["outputPath"] = managedOutput, ["outputSha256"] = File.Exists(managedOutput) ? HashFile(managedOutput) : null,
            },
            ["rustSharpRun"] = Evidence(managedRun),
            ["difference"] = status == "contract-difference" ? Difference(fixture) :
                status == "failed" ? "Compilation, exact stdout or semantic exit classification did not match the declared fixture contract." : null,
        };
    }

    internal static string? ReadDropCleanupMetadata(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidOperationException("Drop profile metadata input exceeds 16 MiB.");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        CustomAttributeHandleCollection attributes = metadata.GetAssemblyDefinition().GetCustomAttributes();
        if (attributes.Count > 256) throw new InvalidOperationException("Drop profile metadata exceeds 256 assembly attributes.");
        var clock = Stopwatch.StartNew();
        string? result = null;
        foreach (CustomAttributeHandle handle in attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Drop metadata validation exceeded two seconds.");
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection" || metadata.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            BlobReader blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1 || blob.ReadSerializedString() != SafeCoreDropCleanupProfiles.MetadataKey) continue;
            if (result is not null) throw new InvalidOperationException("Drop profile metadata is duplicated.");
            result = blob.ReadSerializedString();
            if (result != SafeCoreDropCleanupProfiles.NativeV2MetadataValue || blob.ReadUInt16() != 0 || blob.RemainingBytes != 0)
                throw new InvalidOperationException("Drop profile metadata is malformed or unknown.");
        }
        return result;
    }

    private static async Task<BoundedProcessResult> RunProcessAsync(BoundedProcessRunner runner, string executable,
        IReadOnlyList<string> arguments, string directory, List<BoundedProcessResult> processes, CancellationToken cancellationToken)
    {
        BoundedProcessResult result = await runner.RunAsync(new(executable, arguments, directory, ProcessTimeout,
            started => Console.WriteLine($"P1 Drop process: pid={started.ProcessId} parent={started.ParentProcessId} started={started.StartedAt:O} command={started.CommandLine}")), cancellationToken).ConfigureAwait(false);
        processes.Add(result);
        return result;
    }

    private static bool Complete(BoundedProcessResult result) => result.Termination == BoundedProcessTermination.Exited &&
        !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut &&
        !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;

    private static string Classify(BoundedProcessResult? result, bool oracle, SafeCorePanicStrategy strategy)
    {
        if (result is null) return "not-run";
        if (!Complete(result)) return "incomplete";
        if (result.ExitCode == 0) return "success";
        string error = result.StandardError;
        if (oracle)
            return ClassifyOracleFailure(result.ExitCode, error, strategy);
        else
        {
            return ClassifyGeneratedFailure(result.ExitCode, error);
        }
    }

    internal static string ClassifyGeneratedFailure(int? rawExitCode, string error)
    {
        if (rawExitCode == 0) return "success";
        if (rawExitCode == RustGeneratedPanic.AbortExitCode && error.Contains("RustSharp double panic abort:", StringComparison.Ordinal)) return "double-panic-abort";
        if (rawExitCode == RustGeneratedPanic.AbortExitCode && error.Contains("RustSharp panic abort:", StringComparison.Ordinal)) return "panic-abort";
        if (error.Contains("RustSharp double panic abort:", StringComparison.Ordinal) ||
            error.Contains("RustSharp panic abort:", StringComparison.Ordinal)) return "unexpected-runtime-failure";
        if (rawExitCode is null) return "unexpected-runtime-failure";
        if (error.Contains("RustGeneratedCleanupException", StringComparison.Ordinal)) return "multiple-normal-cleanup-failures";
        if (error.Contains("OverflowException", StringComparison.Ordinal)) return "unwind";
        return "unexpected-runtime-failure";
    }

    /// <summary>Rejects partial, stale or self-declared closure before native publication uses its PE artifacts.</summary>
    internal static void ValidateClosedReport(JsonObject document, string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string root = Path.GetFullPath(repositoryRoot);
        string artifactRoot = Path.Combine(root, "artifacts", "p1-drop");
        var clock = Stopwatch.StartNew();
        var hashes = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        CheckBudget();
        bool nativeV2 = Text(document, "profile") == NativeClosureProfile;
        Require(nativeV2 || document["dropCleanupProfile"] is null, "Legacy v4 evidence cannot relabel a native v2 profile.");
        Require(document["schemaVersion"]?.GetValue<int>() == 1 &&
            Text(document, "evidenceKind") == "p1-drop-source-generated-pe-rustc-differential" &&
            (Text(document, "profile") == ClosureProfile || nativeV2) && Text(document, "runPurpose") == "fixed-suite-closure" &&
            Text(document, "oracleVersion") == OracleVersion, "The input is not the current fixed-suite differential closure.");
        Require(document["cases"] is JsonArray { Count: MaximumCases }, "The input has a missing or extra fixed case.");
        JsonArray cases = (JsonArray)document["cases"]!;
        JsonObject firstArtifact = Object(cases[0]!.AsObject(), "rustcArtifact");
        string firstOraclePath = FullPath(Text(firstArtifact, "path"));
        RequireUnder(firstOraclePath, artifactRoot);
        // The retained native executable binds the original execution host. A Windows
        // report can therefore be replayed for Linux Native AOT without trusting its
        // platform declaration or confusing the replay host with the oracle host.
        string platform = NativeExecutablePlatform(firstOraclePath);
        CheckBudget();
        JsonObject hostContract = Object(document, "hostContract");
        string runtimeIdentifier = Text(document, "runtimeIdentifier");
        int expectedDifferences = ContractDifferences(platform, nativeV2);
        if (nativeV2) Require(Text(document, "dropCleanupProfile") == SafeCoreDropCleanupProfiles.NativeV2MetadataValue,
            "Native v5 evidence must name the explicitly selected v2 compiler contract.");
        Require(hostContract["contractVersion"]?.GetValue<int>() == (nativeV2 ? 2 : 1) && Text(hostContract, "platform") == platform &&
            Text(hostContract, "nativeRuntimeIdentifier") == runtimeIdentifier &&
            (platform == "windows-x64" ? runtimeIdentifier == "win-x64" : runtimeIdentifier is "linux-x64" or "ubuntu.24.04-x64") &&
            hostContract["exactMatches"]?.GetValue<int>() == MaximumCases - expectedDifferences &&
            hostContract["contractDifferences"]?.GetValue<int>() == expectedDifferences &&
            hostContract["fullP1Closure"]?.GetValue<bool>() == false && hostContract["fullP1LanguageGateApproved"]?.GetValue<bool>() == false &&
            Text(hostContract, "platformBinding") == "retained-rustc-native-executable-sha256-and-machine-header",
            "The Drop host contract does not match the retained native oracle platform or its fixed expectations.");
        JsonObject summary = Object(document, "summary");
        Require(summary["expectedContractSatisfied"]?.GetValue<bool>() == true &&
            summary["denominator"]?.GetValue<int>() == MaximumCases && summary["executed"]?.GetValue<int>() == MaximumCases &&
            summary["passed"]?.GetValue<int>() == MaximumCases - expectedDifferences &&
            summary["contractDifferences"]?.GetValue<int>() == expectedDifferences &&
            summary["failed"]?.GetValue<int>() == 0 && summary["blocked"]?.GetValue<int>() == 0 && summary["skipped"]?.GetValue<int>() == 0 &&
            Text(summary, "status") == (expectedDifferences == 0 ? "passed" : "passed-with-contract-differences") &&
            summary["matchesRustcInAllCases"]?.GetValue<bool>() == (expectedDifferences == 0), "The differential summary does not close every fixed case.");
        Require(Object(document, "cleanup")["completed"]?.GetValue<bool>() == true && document["harnessError"] is null &&
            Object(document, "execution")["deadlineExpired"]?.GetValue<bool>() == false, "The input closure has incomplete cleanup, a harness error or an expired deadline.");
        JsonObject oracleProbe = Object(document, "oracleProbe");
        Require(CompleteEvidence(oracleProbe) && oracleProbe["rawExitCode"]?.GetValue<int>() == 0 &&
            Text(oracleProbe, "stdout").Trim() == OracleVersion &&
            Arguments(oracleProbe).SequenceEqual(["+1.98.0", "--version"]), "The pinned oracle probe has no complete successful outcome.");

        (string Name, string Path)[] producers =
        [
            ("RustSharp.Compiler.dll", typeof(CompilerDriver).Assembly.Location),
            ("RustSharp.Semantics.dll", typeof(SafeCoreMirLowering).Assembly.Location),
            ("RustSharp.CodeGen.IL.dll", typeof(ClrLirEmitter).Assembly.Location),
            ("RustSharp.Runtime.dll", typeof(RustGeneratedPanic).Assembly.Location),
        ];
        Require(document["implementationAssemblies"] is JsonArray { Count: 4 }, "Exactly four implementation assembly fingerprints are required.");
        var producerHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonNode? node in (JsonArray)document["implementationAssemblies"]!)
        {
            CheckBudget();
            Require(node is JsonObject, "An implementation fingerprint is not an object.");
            JsonObject fingerprint = (JsonObject)node!;
            string path = FullPath(Text(fingerprint, "path"));
            string name = Path.GetFileName(path);
            string hash = Text(fingerprint, "sha256");
            Require(IsHash(hash) && producers.Any(producer => producer.Name == name) && producerHashes.TryAdd(name, hash),
                "Implementation fingerprints must have the four unique current assembly names and SHA-256 values.");
            RequireUnder(path, root);
            Require(Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase), "An input producer assembly artifact has changed.");
            string current = producers.Single(producer => producer.Name == name).Path;
            Require(Hash(Path.GetFullPath(current)).Equals(hash, StringComparison.OrdinalIgnoreCase), "The differential producer fingerprint is stale relative to this build.");
        }
        string compilerPath = FullPath(Text(document, "compilerPath"));
        Require(Path.GetFileName(compilerPath) == "RustSharp.Compiler.dll" &&
            Text(document, "compilerSha256").Equals(producerHashes["RustSharp.Compiler.dll"], StringComparison.OrdinalIgnoreCase) &&
            Hash(compilerPath).Equals(producerHashes["RustSharp.Compiler.dll"], StringComparison.OrdinalIgnoreCase), "The compiler identity disagrees with the implementation fingerprints.");
        for (int index = 0; index < MaximumCases; index++)
        {
            CheckBudget();
            Fixture expected = PlatformFixture(index, platform, nativeV2);
            Require(cases[index] is JsonObject, "A fixed case is not an object.");
            JsonObject item = (JsonObject)cases[index]!;
            string expectedSourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected.Source)));
            Require(Text(item, "id") == expected.Id && Text(item, "sourceSha256").Equals(expectedSourceHash, StringComparison.OrdinalIgnoreCase) &&
                Text(item, "panicStrategy") == (expected.Strategy == SafeCorePanicStrategy.Unwind ? "unwind" : "abort") &&
                Text(item, "expectedOutput") == expected.Output && Text(item, "expectedRustSharpOutput") == expected.Output &&
                Text(item, "expectedRustcOutput") == (expected.OracleOutput ?? expected.Output) &&
                Text(item, "expectedRustSharpOutcome") == expected.Outcome &&
                Text(item, "expectedRustcOutcome") == (expected.OracleOutcome ?? expected.Outcome) &&
                item["expectedOutputsAgree"]?.GetValue<bool>() == ((expected.OracleOutput ?? expected.Output) == expected.Output) &&
                Text(item, "status") == (expected.OracleOutcome is null ? "passed" : "contract-difference") &&
                (expected.OracleOutcome is null ? item["difference"] is null : Text(item, "difference") == Difference(expected)),
                "A fixed case ID, source, strategy or expectation differs from the current immutable inventory.");
            string sourcePath = FullPath(Text(item, "sourcePath"));
            RequireUnder(sourcePath, artifactRoot);
            Require(Hash(sourcePath) == expectedSourceHash, "A fixed source artifact has changed.");
            JsonObject oracleArtifact = Object(item, "rustcArtifact");
            string oraclePath = FullPath(Text(oracleArtifact, "path"));
            RequireUnder(oraclePath, artifactRoot);
            Require(oraclePath == Path.Combine(Path.GetDirectoryName(sourcePath)!, platform == "windows-x64" ? "oracle.exe" : "oracle") &&
                IsHash(Text(oracleArtifact, "sha256")) && Hash(oraclePath).Equals(Text(oracleArtifact, "sha256"), StringComparison.OrdinalIgnoreCase) &&
                Text(oracleArtifact, "platform") == platform && NativeExecutablePlatform(oraclePath) == platform,
                "A retained oracle executable does not match its hash, case directory or original host platform.");
            CheckBudget();
            JsonObject compilation = Object(item, "rustSharpCompile");
            Require(compilation["success"]?.GetValue<bool>() == true && compilation["diagnostics"] is JsonArray { Count: 0 },
                "The generated PE has no clean successful compilation evidence.");
            string managedPath = FullPath(Text(compilation, "outputPath"));
            RequireUnder(managedPath, artifactRoot);
            string managedHash = Text(compilation, "outputSha256");
            Require(IsHash(managedHash) && Hash(managedPath).Equals(managedHash, StringComparison.OrdinalIgnoreCase), "The original generated PE artifact has changed.");
            Require(ReadDropCleanupMetadata(managedPath, cancellationToken) ==
                (nativeV2 ? SafeCoreDropCleanupProfiles.NativeV2MetadataValue : null),
                "The actual generated PE cleanup metadata disagrees with the selected differential profile.");
            string runtimePath = Path.Combine(Path.GetDirectoryName(managedPath)!, "RustSharp.Runtime.dll");
            Require(Hash(runtimePath).Equals(producerHashes["RustSharp.Runtime.dll"], StringComparison.OrdinalIgnoreCase), "The generated PE's runtime dependency differs from the producer.");
            JsonObject oracleCompile = Object(item, "rustcCompile"), oracleRun = Object(item, "rustcRun"), managedRun = Object(item, "rustSharpRun");
            Require(CompleteEvidence(oracleCompile) && oracleCompile["rawExitCode"]?.GetValue<int>() == 0 &&
                CompleteEvidence(oracleRun) && CompleteEvidence(managedRun), "A required process outcome is incomplete or has incomplete cleanup.");
            Require(Text(oracleCompile, "executable") == Text(oracleProbe, "executable") &&
                Arguments(oracleCompile).SequenceEqual(["+1.98.0", Text(item, "sourcePath"), "--edition", "2024", "-C", "overflow-checks=yes", "-C",
                    expected.Strategy == SafeCorePanicStrategy.Abort ? "panic=abort" : "panic=unwind", "-o", Text(oracleArtifact, "path")]) &&
                Text(oracleRun, "executable") == Text(oracleArtifact, "path") && Arguments(oracleRun).Length == 0 &&
                FullPath(Text(oracleRun, "workingDirectory")) == Path.GetDirectoryName(oraclePath) &&
                Path.GetFileName(PlatformPath(Text(managedRun, "executable"))) == (platform == "windows-x64" ? "dotnet.exe" : "dotnet") &&
                Arguments(managedRun).SequenceEqual([Text(compilation, "outputPath")]) &&
                FullPath(Text(managedRun, "workingDirectory")) == Path.GetDirectoryName(managedPath),
                "The recorded compilation or execution is not bound to its retained source, oracle and generated PE.");
            string oracleOutcome = oracleRun["rawExitCode"]?.GetValue<int>() == 0 ? "success" :
                ClassifyOracleFailure(oracleRun["rawExitCode"]?.GetValue<int>(), Text(oracleRun, "stderr"), expected.Strategy);
            string managedOutcome = ClassifyGeneratedFailure(managedRun["rawExitCode"]?.GetValue<int>(), Text(managedRun, "stderr"));
            Require(Normalize(Text(oracleRun, "stdout")) == (expected.OracleOutput ?? expected.Output) &&
                Normalize(Text(managedRun, "stdout")) == expected.Output && oracleOutcome == (expected.OracleOutcome ?? expected.Outcome) &&
                managedOutcome == expected.Outcome && Text(item, "rustcOutcome") == oracleOutcome && Text(item, "rustSharpOutcome") == managedOutcome,
                "A raw trace or exit classification does not satisfy its fixed case expectation.");
        }
        CheckBudget();

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Closed Drop report validation exceeded ten seconds.");
        }
        string FullPath(string path) => Path.GetFullPath(PlatformPath(path), root);
        string Hash(string path)
        {
            CheckBudget();
            if (hashes.TryGetValue(path, out string? existing)) return existing;
            if (hashes.Count >= MaximumCases * 4 + 12) throw new InvalidOperationException("Drop report artifact count exceeded its bound.");
            const int blockBytes = 65536;
            const int maximumBlocks = 8192;
            using FileStream stream = File.OpenRead(path);
            Require(stream.Length <= (long)blockBytes * maximumBlocks, "A Drop evidence artifact exceeds 512 MiB.");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[blockBytes];
            bool complete = false;
            long total = 0;
            for (int block = 0; block <= maximumBlocks; block++)
            {
                CheckBudget();
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) { complete = true; break; }
                total += count;
                Require(total <= (long)blockBytes * maximumBlocks, "A Drop evidence artifact grew beyond its 512 MiB bound.");
                hash.AppendData(buffer, 0, count);
            }
            Require(complete, "A Drop evidence artifact exceeded its bounded read count.");
            string result = Convert.ToHexString(hash.GetHashAndReset());
            hashes.Add(path, result);
            return result;
        }
    }

    private static string NativeExecutablePlatform(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[64];
        Require(stream.Length is >= 64 and <= 64 * 1024 * 1024, "A native oracle executable is outside its fixed 64 MiB size bounds.");
        stream.ReadExactly(header);
        if (header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        {
            Require(header[4] == 2 && header[5] == 1 && header[6] == 1 &&
                BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) == 62,
                "The retained ELF oracle is not Linux x64.");
            return "linux-x64";
        }
        Require(header[0] == (byte)'M' && header[1] == (byte)'Z', "The retained oracle has no supported native executable header.");
        stream.Position = 0;
        using var pe = new PEReader(stream);
        Require(pe.PEHeaders.CoffHeader.Machine == Machine.Amd64 && pe.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus &&
            pe.PEHeaders.CorHeader is null, "The retained PE oracle is not native Windows x64.");
        return "windows-x64";
    }

    private static bool CompleteEvidence(JsonObject process) => Text(process, "termination") == "exited" &&
        process["pid"]?.GetValue<int>() > 0 && process["parentPid"]?.GetValue<int>() > 0 &&
        DateTimeOffset.TryParse(Text(process, "startedAtUtc"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _) && Text(process, "commandLine") ==
            new BoundedProcessStarted(process["pid"]!.GetValue<int>(), process["parentPid"]!.GetValue<int>(), default,
                Text(process, "executable"), Arguments(process), Text(process, "workingDirectory")).CommandLine &&
        Path.IsPathFullyQualified(PlatformPath(Text(process, "workingDirectory"))) && process["rawExitCode"] is not null &&
        process["outputTruncated"]?.GetValue<bool>() == false && process["outputReadTimedOut"]?.GetValue<bool>() == false &&
        process["outputDrainTimedOut"]?.GetValue<bool>() == false && process["outputReadLimitReached"]?.GetValue<bool>() == false &&
        process["cleanupIncomplete"]?.GetValue<bool>() == false && process["cleanupDiagnostic"] is null;
    private static string[] Arguments(JsonObject process)
    {
        Require(process["arguments"] is JsonArray { Count: <= 16 }, "Process arguments are missing or exceed the fixed bound.");
        return ((JsonArray)process["arguments"]!).Select(static argument => argument?.GetValue<string>()
            ?? throw new InvalidOperationException("A process argument is missing.")).ToArray();
    }
    private static JsonObject Object(JsonObject parent, string name) => parent[name] as JsonObject
        ?? throw new InvalidOperationException("Required Drop evidence object is missing: " + name);
    private static string Text(JsonObject parent, string name) => parent[name]?.GetValue<string>()
        ?? throw new InvalidOperationException("Required Drop evidence string is missing: " + name);
    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static string PlatformPath(string path) => OperatingSystem.IsLinux() && path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            ? "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/') : path;
    private static void RequireUnder(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        Require(!string.Equals(Path.TrimEndingDirectorySeparator(fullPath), fullRoot, comparison) &&
            fullPath.StartsWith(prefix, comparison), "A Drop artifact is outside its declared owned directory.");
    }

    internal static string ClassifyOracleFailure(int? rawExitCode, string error, SafeCorePanicStrategy strategy)
    {
        if (error.Contains("panic in a destructor during cleanup", StringComparison.Ordinal))
            return "double-panic-abort";
        const string panicMarker = "panicked at";
        int firstPanic = error.IndexOf(panicMarker, StringComparison.Ordinal);
        // rustc 1.98 on Windows terminates a second panic with STATUS_STACK_BUFFER_OVERRUN
        // instead of the Linux destructor-cleanup diagnostic. Both the actual
        // fail-fast exit and two independently emitted panic headers are required.
        if (strategy == SafeCorePanicStrategy.Unwind && rawExitCode == unchecked((int)0xC0000409) &&
            firstPanic >= 0 && error.IndexOf(panicMarker, firstPanic + panicMarker.Length, StringComparison.Ordinal) >= 0)
            return "double-panic-abort";
        if (firstPanic >= 0)
            return strategy == SafeCorePanicStrategy.Abort ? "panic-abort" : "unwind";
        return "unexpected-runtime-failure";
    }

    private static JsonObject Blocked(Fixture fixture, string reason) => new()
    {
        ["id"] = fixture.Id, ["status"] = "blocked", ["difference"] = reason,
        ["sourceSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Source))),
    };

    private static JsonObject? Evidence(BoundedProcessResult? result) => result is null ? null : new JsonObject
    {
        ["pid"] = result.StartedProcess.ProcessId, ["parentPid"] = result.StartedProcess.ParentProcessId,
        ["startedAtUtc"] = result.StartedProcess.StartedAt, ["commandLine"] = result.StartedProcess.CommandLine,
        ["executable"] = result.StartedProcess.FileName,
        ["arguments"] = new JsonArray(result.StartedProcess.Arguments.Select(static argument => (JsonNode)JsonValue.Create(argument)!).ToArray()),
        ["workingDirectory"] = result.StartedProcess.WorkingDirectory, ["rawExitCode"] = result.ExitCode,
        ["termination"] = result.Termination.ToString().ToLowerInvariant(), ["elapsedMilliseconds"] = result.Elapsed.TotalMilliseconds,
        ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError, ["outputTruncated"] = result.OutputTruncated,
        ["outputReadTimedOut"] = result.OutputReadTimedOut, ["outputDrainTimedOut"] = result.OutputDrainTimedOut,
        ["outputReadLimitReached"] = result.OutputReadLimitReached, ["cleanupIncomplete"] = result.ProcessTreeCleanupIncomplete,
        ["cleanupDiagnostic"] = result.ProcessTreeCleanupDiagnostic,
    };

    private static string Normalize(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static JsonObject AssemblyFingerprint(Type type) => new()
    {
        ["path"] = type.Assembly.Location, ["sha256"] = HashFile(type.Assembly.Location),
    };
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
