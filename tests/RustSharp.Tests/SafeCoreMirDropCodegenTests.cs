using System.Diagnostics;
using RustSharp.Compiler;
using RustSharp.Runtime;
using RustSharp.Semantics;

namespace RustSharp.Tests;

/// <summary>
/// Real generated-assembly Drop checks.  The semantic cleanup snapshots are
/// covered separately; these tests execute the emitted PE so a backend can not
/// claim Drop support from metadata alone.
/// </summary>
internal static class SafeCoreMirDropCodegenTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generated MIR Drop runs exactly once in reverse order", ReverseOrderAsync),
        new("generated MIR Drop runs on an exception fault path", FaultPathAsync),
        new("generated MIR Drop rejects unsupported destructor failure stably", DestructorFailureBoundaryAsync),
        new("P1 Drop contract freezes flags and failure transitions", DropContractAsync),
        new("P1 Drop flags project bounded ownership paths", DropFlagProjectionAsync),
        new("P1 Drop glue preserves aggregate order and double-panic stop", DropGlueAsync),
        new("P1 Drop glue covers nested aggregates, return and temporaries", NestedAggregateReturnTemporaryAsync),
        new("P1 generated aggregate glue and concrete temporary replacement", AggregateGlueAndConcreteSlotAsync),
        new("P1 panic boundary exposes double panic and abort outcome", DoublePanicBoundaryAsync),
    ];

    private static async Task ReverseOrderAsync()
    {
        const string source = """
            struct First;
            impl Drop for First { fn drop(&mut self) { println!("first"); } }
            struct Second;
            impl Drop for Second { fn drop(&mut self) { println!("second"); } }
            fn emit() { let first = First; let second = Second; println!("body"); }
            fn main() { emit(); }
            """;
        BoundedProcessResult result = await CompileAndRunAsync(source, "body\nsecond\nfirst\n");
        AssertEx.True(result.Succeeded, "Generated Drop program failed: " + result.StandardError);
        AssertEx.Equal("body\nsecond\nfirst\n", Normalize(result.StandardOutput));
    }

    private static async Task FaultPathAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            fn emit() { let marker = Marker; let max: i32 = 2147483647; println!("{}", max + 1); }
            fn main() { emit(); }
            """;
        BoundedProcessResult result = await CompileAndRunAsync(source, expectedOutput: null);
        AssertEx.False(result.Succeeded, "An overflowing body must propagate its panic/exception.");
        AssertEx.Equal("drop\n", Normalize(result.StandardOutput));
        AssertEx.True(result.StandardError.Contains("OverflowException", StringComparison.Ordinal) ||
            result.StandardError.Contains("Unhandled exception", StringComparison.Ordinal),
            "The fault path must retain the original exception boundary: " + result.StandardError);
    }

    private static Task DestructorFailureBoundaryAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker {
                fn drop(&mut self) {
                    let max: i32 = 2147483647;
                    println!("{}", max + 1);
                }
            }
            fn main() { let marker = Marker; }
            """;
        CompilationResult result = CompilerDriver.Check(
            source, "drop-destructor-failure.rs", CompilationProfile.SafeCoreMirV2);
        AssertEx.False(result.Success,
            "A destructor body outside the generated failure contract must not compile silently.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "RSM2002"),
            "Unsupported destructor failure must retain the stable MIR diagnostic: " +
            string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        return Task.CompletedTask;
    }

    private static Task DropContractAsync()
    {
        SafeCoreDropTransition initialized = SafeCoreMirDropContract.Apply(
            SafeCoreDropPlaceState.Uninitialized, SafeCoreDropEvent.Initialize);
        AssertEx.True(initialized.IsValid && initialized.State == SafeCoreDropPlaceState.Live &&
            !initialized.ShouldDrop, "Initialization must publish one live drop flag.");

        SafeCoreDropTransition moved = SafeCoreMirDropContract.Apply(
            SafeCoreDropPlaceState.Live, SafeCoreDropEvent.Move);
        AssertEx.True(moved.IsValid && moved.State == SafeCoreDropPlaceState.Moved &&
            !moved.ShouldDrop && moved.IsTerminal,
            "A whole-value move must consume the drop obligation.");

        SafeCoreDropTransition unwound = SafeCoreMirDropContract.Apply(
            SafeCoreDropPlaceState.Live, SafeCoreDropEvent.PanicUnwind);
        AssertEx.True(unwound.IsValid && unwound.ShouldDrop &&
            unwound.State == SafeCoreDropPlaceState.Dropped,
            "Unwind must consume a live drop obligation exactly once.");

        string snapshot = SafeCoreMirDropContract.Snapshot();
        AssertEx.True(snapshot.StartsWith("safe-core-drop-contract-p1-v1\n", StringComparison.Ordinal) &&
            snapshot.Contains("DROP-ABORT", StringComparison.Ordinal) &&
            snapshot.Contains("failure normal=continue unwind=abort", StringComparison.Ordinal),
            "The frozen transition table must have a stable machine-readable snapshot.");
        return Task.CompletedTask;
    }

    private static Task DropFlagProjectionAsync()
    {
        SafeCoreMirPipelineResult pipeline = SafeCoreMirPipeline.Analyze(
            "fn main() { let value: i32 = 7; }", "drop-flags.rs",
            new SafeCoreMirPipelineOptions
            {
                RequireOwnershipEvidence = true,
                RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(10),
            });
        AssertEx.True(pipeline.IsSuccessful && pipeline.Ownership is not null,
            "The drop-flag fixture must produce ownership evidence: " +
            string.Join(Environment.NewLine, pipeline.Diagnostics));
        SafeCoreMirDropFlagResult flags = SafeCoreMirDropFlagLowering.Lower(pipeline.Ownership!);
        AssertEx.True(flags.IsSuccessful, string.Join(Environment.NewLine, flags.Diagnostics));
        AssertEx.True(flags.Snapshot!.StartsWith("safe-core-mir-drop-flags-p1-v1\n", StringComparison.Ordinal),
            "Drop flags must publish a versioned snapshot.");
        AssertEx.True(flags.Paths.SelectMany(path => path.FinalFlags)
            .Any(flag => flag.LocalName == "value"),
            "Drop-flag evidence must retain source local identity.");
        return Task.CompletedTask;
    }

    private static Task DropGlueAsync()
    {
        var trace = new List<string>();
        RustDropCleanupReport normal = RustDropGlue.Run(
            [
                (Action)(() => trace.Add("field-0")),
                (Action)(() => throw new InvalidOperationException("field failure")),
                (Action)(() => trace.Add("field-2")),
            ],
            () => trace.Add("outer"));
        AssertEx.Equal(RustDropCleanupOutcome.Failed, normal.Outcome);
        AssertEx.Equal("outer,field-0,field-2", string.Join(',', trace));
        AssertEx.Equal("field failure", normal.FirstFailure?.Message ?? string.Empty);

        trace.Clear();
        RustDropCleanupReport unwind = RustDropGlue.Run(
            [
                (Action)(() => trace.Add("field-0")),
                (Action)(() => throw new InvalidOperationException("double panic")),
                (Action)(() => trace.Add("field-2")),
            ],
            () => trace.Add("outer"), duringUnwind: true);
        AssertEx.Equal(RustDropCleanupOutcome.Aborted, unwind.Outcome);
        AssertEx.Equal("outer,field-0", string.Join(',', trace));
        AssertEx.True(unwind.FirstFailure is not null && unwind.SecondFailure is null,
            "Unwind cleanup must stop at the first destructor failure.");

        var slotTrace = new List<string>();
        var slot = new RustDropSlot();
        slot.Initialize(() => slotTrace.Add("old"));
        slot.Replace(() => slotTrace.Add("new"));
        AssertEx.Equal("old", string.Join(',', slotTrace));
        Action moved = slot.MoveOut();
        moved();
        slot.Dispose();
        AssertEx.Equal("old,new", string.Join(',', slotTrace));
        AssertEx.True(slot.IsMoved && !slot.IsLive, "Moving a slot must suppress later cleanup.");
        return Task.CompletedTask;
    }

    private static Task DoublePanicBoundaryAsync()
    {
        var scope = new DropScope();
        scope.Track(new ThrowingDisposable("cleanup failure"));
        RustPanicReport report = RustPanicBoundary.Run(
            () => RustPanicBoundary.Panic("original panic"), scope);
        AssertEx.Equal(RustPanicOutcome.Aborted, report.Outcome);
        AssertEx.True(report.IsDoublePanic && report.CleanupAttempted && !report.CleanupCompleted,
            "A destructor failure during unwind must expose an abort/double-panic report.");
        AssertEx.Equal("original panic", report.Panic?.Message ?? string.Empty);
        AssertEx.Equal("cleanup failure", report.CleanupException?.Message ?? string.Empty);
        AssertEx.True(scope.IsDisposed && scope.TrackedCount == 0,
            "Double-panic cleanup must consume the scope once.");
        return Task.CompletedTask;
    }

    private static Task AggregateGlueAndConcreteSlotAsync()
    {
        var trace = new List<string>();
        using var nested = new RustDropAggregate(
            [
                new RecordingDisposable(() => trace.Add("nested-field-0")),
                new RecordingDisposable(() => trace.Add("nested-field-1")),
            ],
            new RecordingDisposable(() => trace.Add("nested-outer")));
        using var aggregate = new RustDropAggregate(
            [nested, new RecordingDisposable(() => trace.Add("sibling"))],
            new RecordingDisposable(() => trace.Add("outer")));

        aggregate.Dispose();
        AssertEx.Equal("outer,nested-outer,nested-field-0,nested-field-1,sibling", string.Join(',', trace));
        AssertEx.True(aggregate.IsDisposed && nested.IsDisposed,
            "A non-unit aggregate must consume nested cleanup obligations exactly once.");

        var temporaryTrace = new List<string>();
        var slot = new RustDropSlot();
        slot.Initialize(new RecordingDisposable(() => temporaryTrace.Add("temporary")));
        slot.Replace(new RecordingDisposable(() => temporaryTrace.Add("replacement")));
        AssertEx.Equal("temporary", string.Join(',', temporaryTrace));
        slot.Dispose();
        slot.Dispose();
        AssertEx.Equal("temporary,replacement", string.Join(',', temporaryTrace));
        AssertEx.True(slot.IsDropped && !slot.IsLive,
            "Concrete temporary replacement must leave only the replacement live value.");
        return Task.CompletedTask;
    }

    private static Task NestedAggregateReturnTemporaryAsync()
    {
        var trace = new List<string>();
        RustDropCleanupReport nested = RustDropGlue.Run(
            [
                (Action)(() => trace.Add("nested-field-0")),
                (Action)(() => trace.Add("nested-field-1")),
            ],
            () => trace.Add("nested-outer"));
        AssertEx.Equal(RustDropCleanupOutcome.Completed, nested.Outcome);
        AssertEx.Equal("nested-outer,nested-field-0,nested-field-1", string.Join(',', trace));

        trace.Clear();
        var scope = new DropScope();
        scope.Track(new RecordingDisposable(() => trace.Add("owner")));
        RustPanicReport returned = RustPanicBoundary.Run(
            () => trace.Add("return"), scope);
        AssertEx.Equal(RustPanicOutcome.Returned, returned.Outcome);
        AssertEx.Equal("return,owner", string.Join(',', trace));
        AssertEx.True(returned.CleanupAttempted && returned.CleanupCompleted,
            "An explicit return must clean remaining live owners.");

        var temporaryTrace = new List<string>();
        var temporary = new RustDropSlot();
        temporary.Initialize(() => temporaryTrace.Add("temporary"));
        Action moved = temporary.MoveOut();
        moved();
        temporary.Dispose();
        AssertEx.Equal("temporary", string.Join(',', temporaryTrace));
        AssertEx.True(temporary.IsMoved && !temporary.IsLive,
            "Moving an expression temporary out must suppress scope cleanup.");
        return Task.CompletedTask;
    }

    private static async Task<BoundedProcessResult> CompileAndRunAsync(string source, string? expectedOutput)
    {
        string ownedRoot = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(ownedRoot, "mir-drop-codegen-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("Drop test directory already exists: " + directory);
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source, sourcePath, outputPath, "MirDropCodegen", CompilationProfile.SafeCoreMirV2,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "Generated Drop compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult result = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10), started =>
                    Console.WriteLine($"generated MIR Drop process: pid={started.ProcessId} parent={started.ParentProcessId} started={started.StartedAt:O} cwd={started.WorkingDirectory} command={started.CommandLine}")), deadline.Token)
                .ConfigureAwait(false);
            AssertEx.False(result.ProcessTreeCleanupIncomplete,
                "Generated Drop process tree cleanup is incomplete: " + result.ProcessTreeCleanupDiagnostic);
            if (expectedOutput is not null)
                AssertEx.Equal(expectedOutput, Normalize(result.StandardOutput));
            return result;
        }
        finally
        {
            await DeleteOwnedDirectoryAsync(directory, ownedRoot).ConfigureAwait(false);
        }
    }

    private static async Task DeleteOwnedDirectoryAsync(string directory, string ownedRoot)
    {
        const string prefix = "mir-drop-codegen-";
        string fullPath = Path.GetFullPath(directory);
        string fullRoot = Path.GetFullPath(ownedRoot);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string name = Path.GetFileName(fullPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullRoot, comparison) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _))
            throw new InvalidOperationException("Refusing to clean an unowned Drop test directory: " + fullPath);
        if (!Directory.Exists(fullPath)) return;
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to clean a redirected Drop test directory: " + fullPath);

        const int maximumAttempts = 40;
        TimeSpan timeout = TimeSpan.FromSeconds(5);
        var clock = Stopwatch.StartNew();
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < maximumAttempts && clock.Elapsed < timeout; attempt++)
        {
            try { Directory.Delete(fullPath, recursive: true); }
            catch (IOException exception) { lastFailure = exception; }
            catch (UnauthorizedAccessException exception) { lastFailure = exception; }
            if (!Directory.Exists(fullPath)) return;
            TimeSpan remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            double delay = Math.Min(50 + attempt * 25, Math.Min(150, remaining.TotalMilliseconds));
            await Task.Delay(TimeSpan.FromMilliseconds(delay)).ConfigureAwait(false);
        }
        throw new IOException("Drop test directory cleanup exceeded 40 attempts or five seconds: " + fullPath, lastFailure);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class ThrowingDisposable(string message) : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException(message);
    }

    private sealed class RecordingDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
