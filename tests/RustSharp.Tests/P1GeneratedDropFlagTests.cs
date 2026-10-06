using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Runtime;
using RustSharp.Semantics;

namespace RustSharp.Tests;

/// <summary>Emitted PE fault paths, runtime collection, and the source owner's existing CLR budget.</summary>
internal static class P1GeneratedDropFlagTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 generated fault skips a normally expired Drop scope", ExpiredScopeAsync),
        new("P1 generated fault skips a branch that never initialized its owner", UninitializedBranchAsync),
        new("P1 generated fault does not reuse a previous loop iteration Drop flag", CompletedLoopAsync),
        new("P1 generated fault skips a later owner before its initializer", LaterInitializerAsync),
        new("P1 generated normal destructor failure continues remaining cleanup", NormalFailureContinuesAsync),
        new("P1 generated double panic stops outer cleanup and exits 134", DoublePanicProcessAsync),
        new("P1 generated reusable call retains both normal cleanup failures", NormalFailuresReportAsync),
        new("P1 runtime normal failure collector preserves 260 identities and rejects excess source owners", ManyNormalFailuresReportAsync),
        new("P1 generated reusable call exposes original and destructor double panic", DoublePanicReportAsync),
        new("P1 generated reusable aggregate call retains owner and field normal cleanup failures", AggregateNormalFailuresReportAsync),
        new("P1 generated reusable aggregate call retains outer panic and first owner failure", AggregateDoublePanicReportAsync),
        new("P1 generated reusable explicit abort preserves host cleanup scope", ExplicitAbortReportAsync),
        new("P1 generated panic rejects unsafe fault receiver instructions", RejectsUnsafeReceiverAsync),
    ];

    private static Task ExpiredScopeAsync() => RunAsync("expired-scope", """
        struct Outer;
        struct Inner;
        impl Drop for Outer { fn drop(&mut self) { println!("outer"); } }
        impl Drop for Inner { fn drop(&mut self) { println!("inner"); } }
        fn main() {
            let outer = Outer;
            { let inner = Inner; println!("nested"); }
            println!("after");
            let max: i32 = 2147483647;
            println!("{}", max + 1);
        }
        """, "nested\ninner\nafter\nouter\n");

    private static Task UninitializedBranchAsync() => RunAsync("uninitialized-branch", """
        struct Marker;
        impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
        fn main() {
            if false { let marker = Marker; println!("unreachable"); }
            let max: i32 = 2147483647;
            println!("{}", max + 1);
        }
        """, string.Empty);

    private static Task CompletedLoopAsync() => RunAsync("completed-loop", """
        struct Marker;
        impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
        fn main() {
            let mut count: i32 = 0;
            while count < 2 {
                let marker = Marker;
                println!("iteration");
                count += 1;
            }
            let max: i32 = 2147483647;
            println!("{}", max + 1);
        }
        """, "iteration\ndrop\niteration\ndrop\n");

    private static Task LaterInitializerAsync() => RunAsync("later-initializer", """
        struct Marker;
        impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
        fn main() {
            let max: i32 = 2147483647;
            println!("{}", max + 1);
            let marker = Marker;
        }
        """, string.Empty);

    private static Task NormalFailureContinuesAsync() => RunAsync("normal-failure", """
        struct First;
        struct Bad;
        impl Drop for First { fn drop(&mut self) { println!("first"); } }
        impl Drop for Bad { fn drop(&mut self) { println!("bad"); let max: i32 = 2147483647; println!("{}", max + 1); } }
        fn main() { let first = First; let bad = Bad; println!("body"); }
        """, "body\nbad\nfirst\n");

    private static Task DoublePanicProcessAsync() => RunAsync("double-panic", """
        struct Outer;
        struct Bad;
        impl Drop for Outer { fn drop(&mut self) { println!("outer-must-not-drop"); } }
        impl Drop for Bad { fn drop(&mut self) { println!("bad"); let zero: i32 = 0; println!("{}", 1 / zero); } }
        fn inner() { let bad = Bad; println!("body"); let max: i32 = 2147483647; println!("{}", max + 1); }
        fn main() { let outer = Outer; inner(); println!("after-must-not-run"); }
        """, "body\nbad\n", RustGeneratedPanic.AbortExitCode, "RustSharp double panic abort");

    private static Task NormalFailuresReportAsync() => InvokeGeneratedAsync("normal-report", """
        struct First;
        struct Second;
        impl Drop for First { fn drop(&mut self) { let zero: i32 = 0; println!("{}", 1 / zero); } }
        impl Drop for Second { fn drop(&mut self) { let max: i32 = 2147483647; println!("{}", max + 1); } }
        pub fn probe() { let first = First; let second = Second; }
        fn main() {}
        """, report =>
        {
            AssertEx.Equal(RustPanicOutcome.Unwound, report.Outcome);
            AssertEx.True(report.Panic is RustGeneratedCleanupException,
                "Normal cleanup must retain multiple failures without turning them into a double-panic abort.");
            var failures = (RustGeneratedCleanupException)report.Panic!;
            AssertEx.True(failures.FirstFailure is OverflowException &&
                failures.SubsequentFailures.Single() is DivideByZeroException,
                "The generated reusable call must preserve both destructor failures in reverse local order.");
        });

    private static async Task ManyNormalFailuresReportAsync()
    {
        const int ownerCount = 260;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        var expectedFailures = new Exception[ownerCount];
        int created = 0;
        for (int index = 0; index < ownerCount && clock.Elapsed < TimeSpan.FromSeconds(5); index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            expectedFailures[index] = new InvalidOperationException("failure-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            created++;
        }
        AssertEx.Equal(ownerCount, created, "Runtime failure construction must stay within 260 items and five seconds.");
        object collected = expectedFailures[0];
        int appended = 0;
        for (int index = 1; index < ownerCount && clock.Elapsed < TimeSpan.FromSeconds(5); index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            collected = RustGeneratedPanic.ContinueNormalCleanup(collected, expectedFailures[index]);
            appended++;
        }
        AssertEx.Equal(ownerCount - 1, appended, "The runtime collector must retain all 259 subsequent failures within its test budget.");
        AssertEx.True(collected is RustGeneratedCleanupException, "Multiple runtime failures require their bounded immutable collector.");
        var failures = (RustGeneratedCleanupException)collected;
        AssertEx.True(ReferenceEquals(expectedFailures[0], failures.FirstFailure), "The first runtime exception identity must be preserved.");
        AssertEx.Equal(ownerCount - 1, failures.SubsequentFailures.Count);
        int verified = 0;
        for (int index = 1; index < ownerCount && clock.Elapsed < TimeSpan.FromSeconds(5); index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(ReferenceEquals(expectedFailures[index], failures.SubsequentFailures[index - 1]),
                "The runtime collector must preserve every distinct subsequent exception identity and order.");
            verified++;
        }
        AssertEx.Equal(ownerCount - 1, verified, "Runtime identity validation must stay within 260 items and five seconds.");

        // This source is a diagnostic boundary, not a claim that 260 owners execute.
        using var sourceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sourceClock = Stopwatch.StartNew();
        var source = new System.Text.StringBuilder("struct Bad; impl Drop for Bad { fn drop(&mut self) { println!(\"drop\"); let zero: i32 = 0; println!(\"{}\", 1 / zero); } } pub fn probe() {");
        int generated = 0;
        for (int index = 0; index < ownerCount && sourceClock.Elapsed < TimeSpan.FromSeconds(5); index++)
        {
            sourceDeadline.Token.ThrowIfCancellationRequested();
            source.Append("let owner").Append(index).Append(" = Bad; ");
            generated++;
        }
        AssertEx.Equal(ownerCount, generated, "The source boundary fixture must stay within 260 declarations and five seconds.");
        source.Append("} fn main() {}");
        string ownedRoot = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(ownedRoot, "p1-generated-drop-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string outputPath = Path.Combine(directory, "program.dll");
            CompilationResult rejected = CompilerDriver.Compile(source.ToString(), Path.Combine(directory, "source-owner-bound.rs"),
                outputPath, "SourceOwnerBound", CompilationProfile.SafeCoreMirV2, compileDeadline.Token);
            AssertEx.False(rejected.Success, "A 260-owner source body exceeds the existing bounded CLR local arena.");
            AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirClrLowering.LimitReached),
                "The source scale boundary must diagnose RSM2103, not claim generated execution or a semantic RSO0003 failure.");
            AssertEx.False(File.Exists(outputPath), "A rejected source-owner boundary must not publish a partial PE.");
        }
        finally { await DeleteOwnedDirectoryAsync(directory, ownedRoot).ConfigureAwait(false); }
    }

    private static Task DoublePanicReportAsync() => InvokeGeneratedAsync("double-report", """
        struct Bad;
        impl Drop for Bad { fn drop(&mut self) { let zero: i32 = 0; println!("{}", 1 / zero); } }
        pub fn probe() { let bad = Bad; let max: i32 = 2147483647; println!("{}", max + 1); }
        fn main() {}
        """, report =>
        {
            AssertEx.Equal(RustPanicOutcome.Aborted, report.Outcome);
            AssertEx.True(report.IsDoublePanic && report.Panic is OverflowException &&
                report.CleanupException is DivideByZeroException,
                "The generated reusable call must expose the actual body panic and destructor failure.");
            AssertEx.False(RustGeneratedPanic.IsUnwinding(), "A reusable generated call must restore its caller's unwind mode.");
        });

    private static Task AggregateNormalFailuresReportAsync() => InvokeGeneratedAsync("aggregate-normal-report", """
        struct Bad;
        struct Good;
        struct Owner { bad: Bad, good: Good }
        impl Drop for Bad { fn drop(&mut self) { let max: i32 = 2147483647; println!("{}", max + 1); } }
        impl Drop for Good { fn drop(&mut self) {} }
        impl Drop for Owner { fn drop(&mut self) { let zero: i32 = 0; println!("{}", 1 / zero); } }
        pub fn probe() { let owner = Owner { bad: Bad, good: Good }; }
        fn main() {}
        """, report =>
        {
            AssertEx.Equal(RustPanicOutcome.Unwound, report.Outcome);
            AssertEx.True(report.Panic is RustGeneratedCleanupException,
                "Normal owner cleanup must retain its own failure and continue automatic field cleanup.");
            var failures = (RustGeneratedCleanupException)report.Panic!;
            AssertEx.True(failures.FirstFailure is DivideByZeroException &&
                failures.SubsequentFailures.Single() is OverflowException,
                "Normal cleanup must preserve the owner failure before the failing automatic field.");
            AssertEx.False(RustGeneratedPanic.IsUnwinding(), "Normal cleanup must restore its caller's unwind mode.");
        });

    private static Task AggregateDoublePanicReportAsync() => InvokeGeneratedAsync("aggregate-double-report", """
        struct Bad;
        struct Good;
        struct Owner { bad: Bad, good: Good }
        impl Drop for Bad { fn drop(&mut self) { let max: i32 = 2147483647; println!("{}", max + 1); } }
        impl Drop for Good { fn drop(&mut self) {} }
        impl Drop for Owner { fn drop(&mut self) { let zero: i32 = 0; println!("{}", 1 / zero); } }
        pub fn probe() {
            let owner = Owner { bad: Bad, good: Good };
            let max: i32 = 2147483647;
            println!("{}", max + 1);
        }
        fn main() {}
        """, report =>
        {
            AssertEx.Equal(RustPanicOutcome.Aborted, report.Outcome);
            AssertEx.True(report.IsDoublePanic && report.Panic is OverflowException &&
                report.CleanupException is DivideByZeroException,
                "An owner failure during unwind must preserve the outer panic and stop before automatic fields.");
            AssertEx.False(RustGeneratedPanic.IsUnwinding(), "A nested generated failure must restore its caller's unwind mode.");
        });

    private static async Task ExplicitAbortReportAsync()
    {
        bool hostOwnerDropped = false;
        using var hostScope = new DropScope();
        hostScope.Track(new RecordingDisposable(() => hostOwnerDropped = true));
        await InvokeGeneratedAsync("explicit-abort-report", """
            struct Bad;
            impl Drop for Bad { fn drop(&mut self) { let zero: i32 = 0; println!("{}", 1 / zero); } }
            pub fn probe() { let bad = Bad; let max: i32 = 2147483647; println!("{}", max + 1); }
            fn main() {}
            """, report =>
            {
                AssertEx.Equal(RustPanicOutcome.Aborted, report.Outcome);
                AssertEx.True(report.Panic is OverflowException && report.CleanupException is null && !report.IsDoublePanic,
                    "An exported abort call must retain the body panic without running its failing destructor.");
                AssertEx.False(report.CleanupAttempted || report.CleanupCompleted,
                    "An exported abort outcome cannot claim cleanup was attempted or completed.");
                AssertEx.True(!hostOwnerDropped && !hostScope.IsDisposed && hostScope.TrackedCount == 1,
                    "The generated abort must leave the caller's tracked DropScope untouched.");
                AssertEx.False(RustGeneratedPanic.IsUnwinding(), "An exported abort call must restore its caller's unwind mode.");
            }, SafeCorePanicStrategy.Abort, hostScope).ConfigureAwait(false);
        hostScope.Dispose();
        AssertEx.True(hostOwnerDropped, "The host must remain able to clean its preserved scope explicitly.");
    }

    private static Task RejectsUnsafeReceiverAsync()
    {
        var cleanup = new ClrLirGuardedCleanup(new("Destructor", ClrLirType.Void, [ClrLirType.Any]), 0,
            receiverInstructions: [new ClrLirLoadLocal(1), new ClrLirStoreLocal(1), new ClrLirLoadLocal(1)]);
        var method = new ClrLirMethod("Main", ClrLirType.Void, [],
            [new("flag", ClrLirType.Bool), new("owner", ClrLirType.Any)],
            [new("entry", [new ClrLirReturn()])], guardedExceptionCleanup: [cleanup]);
        AssertEx.True(method.Validate().Diagnostics.Any(diagnostic => diagnostic.Code == "LIR022"),
            "Fault receiver reconstruction must reject writes even when its final stack shape is valid.");
        return Task.CompletedTask;
    }

    private static async Task RunAsync(string name, string source, string expectedTrace,
        int? expectedExitCode = null, string errorFragment = "OverflowException")
    {
        string ownedRoot = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(ownedRoot, "p1-generated-drop-flags-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("Generated Drop flag test directory already exists.");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, name + ".rs");
            string outputPath = Path.Combine(directory, "program.dll");
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(source, sourcePath, outputPath,
                "P1GeneratedDropFlags", CompilationProfile.SafeCoreMirV2, compileDeadline.Token);
            AssertEx.True(compiled.Success, "Generated Drop flag fixture failed to compile: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));

            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult result = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10), started =>
                    Console.WriteLine($"generated Drop flag process: pid={started.ProcessId} parent={started.ParentProcessId} started={started.StartedAt:O} cwd={started.WorkingDirectory} command={started.CommandLine}")),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.Equal(BoundedProcessTermination.Exited, result.Termination);
            AssertEx.False(result.Succeeded, "The fixture overflow must cross the generated fault boundary.");
            AssertEx.False(result.ProcessTreeCleanupIncomplete, "Generated Drop flag process cleanup is incomplete: " + result.ProcessTreeCleanupDiagnostic);
            AssertEx.Equal(expectedTrace, result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            if (expectedExitCode.HasValue) AssertEx.Equal(expectedExitCode.Value, result.ExitCode!.Value);
            AssertEx.True(result.StandardError.Contains(errorFragment, StringComparison.Ordinal),
                "The original panic must remain observable: " + result.StandardError);
        }
        finally
        {
            await DeleteOwnedDirectoryAsync(directory, ownedRoot).ConfigureAwait(false);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This in-process test intentionally loads the generated PE; Native AOT evidence uses a compiled process host.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "This in-process test intentionally reflects the generated public probe described by its checked metadata.")]
    private static async Task InvokeGeneratedAsync(string name, string source, Action<RustPanicReport> verify,
        SafeCorePanicStrategy strategy = SafeCorePanicStrategy.Unwind, DropScope? hostScope = null)
    {
        string ownedRoot = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string identity = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(ownedRoot, "p1-generated-drop-flags-" + identity);
        Directory.CreateDirectory(directory);
        try
        {
            string outputPath = Path.Combine(directory, "program.dll");
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.CompileWithPanicStrategy(source, Path.Combine(directory, name + ".rs"),
                outputPath, strategy, "GeneratedDropReport" + identity, CompilationProfile.SafeCoreMirV2, compileDeadline.Token);
            AssertEx.True(compiled.Success, "Generated reusable panic fixture failed to compile: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(outputPath);
            AssertEx.True(imported.IsSuccessful, "The generated reusable panic interface must carry valid metadata.");
            string methodName = imported.Document!.Functions.Single(function => function.SourceQualifiedName == "crate::probe#value").Name;
            using var image = new MemoryStream(await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false), writable: false);
            Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(image);
            MethodInfo method = assembly.GetType("RustSharp.Generated.Program")!.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Static)!;
            Exception? generatedFailure = null;
            RustPanicReport report = RustPanicBoundary.Run(() =>
            {
                try { method.Invoke(null, null); }
                catch (TargetInvocationException exception) when (exception.InnerException is not null)
                {
                    generatedFailure = exception.InnerException;
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                }
            }, hostScope);
            if (strategy == SafeCorePanicStrategy.Abort)
            {
                AssertEx.True(generatedFailure is RustGeneratedAbortException,
                    "The emitted exported method must carry its explicit abort strategy across the reusable boundary.");
                var abort = (RustGeneratedAbortException)generatedFailure!;
                AssertEx.True(ReferenceEquals(abort.Panic, report.Panic) && abort.CleanupFailure is null,
                    "The generated abort carrier must preserve the original exception without inventing a cleanup failure.");
            }
            verify(report);
        }
        finally
        {
            await DeleteOwnedDirectoryAsync(directory, ownedRoot).ConfigureAwait(false);
        }
    }

    private static async Task DeleteOwnedDirectoryAsync(string directory, string ownedRoot)
    {
        const string prefix = "p1-generated-drop-flags-";
        string fullPath = Path.GetFullPath(directory);
        string name = Path.GetFileName(fullPath);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(fullPath), ownedRoot, comparison) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to clean an unowned generated Drop flag directory: " + fullPath);
        var clock = Stopwatch.StartNew();
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 40 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { Directory.Delete(fullPath, recursive: true); }
            catch (IOException exception) { lastFailure = exception; }
            catch (UnauthorizedAccessException exception) { lastFailure = exception; }
            if (!Directory.Exists(fullPath)) return;
            TimeSpan remaining = TimeSpan.FromSeconds(5) - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(150, remaining.TotalMilliseconds))).ConfigureAwait(false);
        }
        throw new IOException("Generated Drop flag directory cleanup exceeded 40 attempts or five seconds: " + fullPath, lastFailure);
    }

    private sealed class RecordingDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
