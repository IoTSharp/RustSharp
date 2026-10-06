using System.Collections.Immutable;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirCleanupTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("typed MIR cleanup lowers source return scope exit", SourceReturnAsync),
        new("typed MIR cleanup preserves unwind and abort boundaries", PanicStrategiesAsync),
        new("typed MIR cleanup keeps branch and bounded-loop paths distinct", BranchLoopCleanupAsync),
        new("typed MIR cleanup rejects incomplete ownership evidence", RejectsIncompleteEvidenceAsync),
        new("typed MIR cleanup rejects drop trace absent from DropOrder", RejectsExtraDropEvidenceAsync),
        new("typed MIR cleanup rejects duplicate legacy DropOrder", RejectsRepeatedLegacyDropOrderAsync),
        new("typed MIR cleanup preserves reassignment generations and missing legacy trace", ReplacementGenerationsAsync),
        new("typed MIR cleanup validates complete projected Drop types", ProjectedPlaceTypesAsync),
        new("typed MIR cleanup rejects missing drop trace without legacy fallback", RejectsMissingTypedTraceAsync),
        new("typed MIR cleanup rejects reordered drop trace", RejectsReorderedTypedTraceAsync),
        new("typed MIR cleanup bounds cyclic synthetic storage-place evidence", RejectsCyclicStoragePlaceAsync),
        new("typed MIR cleanup permits borrowed replacement without owning the new referent scope", BorrowedReplacementScopeAsync),
        new("typed MIR cleanup rejects borrowed Drop without a canonical replacement store", RejectsNoncanonicalBorrowedDropAsync),
        new("typed MIR cleanup rejects borrowed replacement with a terminal branch bypass", RejectsBorrowedReplacementBranchBypassAsync),
        new("typed MIR cleanup rejects borrowed replacement with a cyclic branch bypass", RejectsBorrowedReplacementCycleBypassAsync),
        new("typed MIR cleanup rejects forged new borrowed generation scope Drop", RejectsBorrowedScopeDropAsync),
        new("typed MIR cleanup rejects ownership bound to a different immutable program", RejectsMismatchedTypedProgramAsync),
        new("typed MIR cleanup snapshot is deterministic and bounded", DeterministicSnapshotAsync),
        new("typed MIR cleanup snapshot round-trips through metadata", MetadataSnapshotAsync),
        new("compiler source wiring persists MIR ownership and cleanup evidence", CompilerSourceWiringAsync),
        new("compiler MIR backend executes fixed-array indexing", MirBackendArrayAsync),
        new("compiler MIR backend executes direct calls and inclusive comparisons", MirBackendCallAsync),
        new("compiler MIR check shares backend capability diagnostics", BackendCapabilityGateAsync),
    ];

    private static Task BranchLoopCleanupAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildBranchLoopEvidence();
        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(mir, ownership);
        AssertEx.True(cleanup.IsSuccessful, string.Join(Environment.NewLine, cleanup.Diagnostics));
        AssertEx.Equal(3, cleanup.Functions.Single().Paths.Count,
            "The bounded branch/loop fixture must retain all three terminal paths.");
        foreach (SafeCoreMirCleanupPath path in cleanup.Functions.Single().Paths)
        {
            AssertEx.Equal(1, path.Actions.Count(action =>
                action.Kind == SafeCoreMirCleanupActionKind.Drop && action.LocalName == "resource"));
            AssertEx.Equal(1, path.Actions.Count(action =>
                action.Kind == SafeCoreMirCleanupActionKind.ScopeExit));
            AssertEx.Equal(SafeCoreMirCleanupExitKind.Returned, path.ExitKind);
        }

        SafeCoreMirDropFlagResult flags = SafeCoreMirDropFlagLowering.Lower(ownership);
        AssertEx.True(flags.IsSuccessful, string.Join(Environment.NewLine, flags.Diagnostics));
        AssertEx.Equal(3, flags.Paths.Count,
            "Drop flags must preserve the same branch/loop path denominator.");
        AssertEx.True(flags.Paths.All(path => path.FinalFlags.Any(flag =>
                flag.LocalName == "resource" && flag.State == SafeCoreDropPlaceState.Dropped)),
            "Every terminal path must consume the resource drop flag exactly once.");
        return Task.CompletedTask;
    }

    private static Task SourceReturnAsync()
    {
        SafeCoreMirPipelineResult pipeline = SafeCoreMirPipeline.Analyze(
            "fn main() {}",
            "cleanup-source.rs",
            new SafeCoreMirPipelineOptions
            {
                RequireOwnershipEvidence = true,
                RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(10),
            });
        AssertEx.True(pipeline.IsSuccessful,
            "The source-to-ownership pipeline must succeed: " +
            string.Join(Environment.NewLine, pipeline.Diagnostics));
        SafeCoreMirProgram mir = pipeline.Mir!.Program ??
            throw new InvalidOperationException("The pipeline did not publish typed MIR.");
        SafeCoreMirOwnershipResult ownership = pipeline.Ownership ??
            throw new InvalidOperationException("The pipeline did not publish ownership evidence.");

        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(mir, ownership);
        AssertEx.True(cleanup.IsSuccessful,
            string.Join(Environment.NewLine, cleanup.Diagnostics));
        string snapshot = AssertEx.NotNull(cleanup.Snapshot,
            "Successful cleanup lowering must retain a deterministic snapshot.");
        AssertEx.True(snapshot.StartsWith("safe-core-mir-cleanup-p1-v1\n", StringComparison.Ordinal),
            "Cleanup snapshot must carry its version header.");
        AssertEx.True(snapshot.Contains("scope_exit", StringComparison.Ordinal),
            "Return cleanup must retain explicit scope-exit evidence.");
        AssertEx.True(snapshot.Contains("return_boundary", StringComparison.Ordinal),
            "Return cleanup must retain the function boundary event.");
        AssertEx.Equal(SafeCoreMirCleanupExitKind.Returned,
            cleanup.Functions.Single().Paths.Single().ExitKind);
        return Task.CompletedTask;
    }

    private static Task PanicStrategiesAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult unwind) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirCleanupResult unwound = SafeCoreMirCleanupLowering.Lower(mir, unwind);
        AssertEx.True(unwound.IsSuccessful, string.Join(Environment.NewLine, unwound.Diagnostics));
        SafeCoreMirCleanupPath unwindPath = unwound.Functions.Single().Paths.Single();
        AssertEx.Equal(SafeCoreMirCleanupExitKind.Unwound, unwindPath.ExitKind);
        AssertEx.True(unwindPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.Drop && action.LocalName == "resource"),
            "Unwind must lower the live Drop value.");
        AssertEx.True(unwindPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.PanicBoundary),
            "Unwind must retain an explicit panic boundary.");
        AssertEx.True(unwindPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.ScopeExit),
            "Unwind must retain scope-exit cleanup evidence.");

        (mir, SafeCoreMirOwnershipResult abort) = BuildPanicEvidence(SafeCorePanicStrategy.Abort);
        SafeCoreMirCleanupResult aborted = SafeCoreMirCleanupLowering.Lower(mir, abort);
        AssertEx.True(aborted.IsSuccessful, string.Join(Environment.NewLine, aborted.Diagnostics));
        SafeCoreMirCleanupPath abortPath = aborted.Functions.Single().Paths.Single();
        AssertEx.Equal(SafeCoreMirCleanupExitKind.Aborted, abortPath.ExitKind);
        AssertEx.False(abortPath.Actions.Any(action => action.Kind == SafeCoreMirCleanupActionKind.Drop),
            "Abort must not lower unwind Drop actions.");
        AssertEx.False(abortPath.Actions.Any(action => action.Kind == SafeCoreMirCleanupActionKind.ScopeExit),
            "Abort must leave the cleanup scope untouched for the host policy.");
        AssertEx.True(abortPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.PanicBoundary),
            "Abort must retain an explicit panic boundary.");
        return Task.CompletedTask;
    }

    private static Task RejectsIncompleteEvidenceAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirOwnershipResult incomplete = ownership with
        {
            Program = null,
            Ownership = null,
            Diagnostics = [new Diagnostic("RSM3001", "missing ownership", Source(0).Span)],
            IsTruncated = false,
        };
        SafeCoreMirCleanupResult result = SafeCoreMirCleanupLowering.Lower(mir, incomplete);
        AssertEx.False(result.IsSuccessful, "Incomplete ownership must not publish cleanup evidence.");
        AssertEx.True(result.Functions.Count == 0 && result.Snapshot is null,
            "Failed cleanup lowering must not publish a partial plan.");
        AssertEx.Equal(SafeCoreMirCleanupLowering.InvalidEvidence, result.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task RejectsExtraDropEvidenceAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreOwnershipAnalysisResult analysis = ownership.Ownership ??
            throw new InvalidOperationException("The panic fixture did not publish ownership analysis.");
        SafeCoreOwnershipPath path = analysis.Paths.Single();
        string[] originalTrace = path.Trace.ToArray();
        int dropIndex = Array.FindIndex(originalTrace,
            static trace => string.Equals(trace, "drop resource", StringComparison.Ordinal));
        AssertEx.True(dropIndex >= 0, "The panic fixture must contain one resource drop trace.");

        // Add a second generation to the producer trace without adding it to
        // DropOrder. A consumer that silently trusts Trace would emit two
        // cleanup actions for one ownership obligation.
        ImmutableArray<string> trace = [
            .. originalTrace[..(dropIndex + 1)],
            "assign resource",
            "drop resource",
            .. originalTrace[(dropIndex + 1)..],
        ];
        SafeCoreOwnershipAnalysisResult malformed = analysis with
        {
            Paths = [path with { Trace = trace }],
        };
        SafeCoreMirCleanupResult result = SafeCoreMirCleanupLowering.Lower(
            mir, ownership with { Ownership = malformed });
        AssertEx.False(result.IsSuccessful,
            "Cleanup lowering must reject a trace drop that is absent from DropOrder.");
        AssertEx.Equal(SafeCoreMirCleanupLowering.InvalidEvidence,
            result.Diagnostics.Single().Code);
        AssertEx.True(result.Diagnostics.Single().Message.Contains(
                "absent from DropOrder", StringComparison.Ordinal),
            "The mismatch diagnostic must identify the missing DropOrder fact.");
        return Task.CompletedTask;
    }

    private static Task RejectsRepeatedLegacyDropOrderAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreOwnershipAnalysisResult analysis = ownership.Ownership!;
        SafeCoreOwnershipPath path = analysis.Paths.Single();
        SafeCoreOwnershipPath legacy = path with
        {
            Trace = [.. path.Trace.Where(static trace => !trace.StartsWith("drop ", StringComparison.Ordinal))],
        };
        SafeCoreMirCleanupResult validLegacy = SafeCoreMirCleanupLowering.Lower(
            mir, ownership with { Ownership = analysis with { Paths = [legacy] } });
        AssertEx.True(validLegacy.IsSuccessful,
            "The versioned compact DropOrder fallback must still materialize one missing legacy drop trace.");
        AssertEx.Equal(1, validLegacy.Functions.Single().Paths.Single().Actions.Count(
            static action => action.Kind == SafeCoreMirCleanupActionKind.Drop));

        SafeCoreMirCleanupResult repeated = SafeCoreMirCleanupLowering.Lower(
            mir, ownership with
            {
                Ownership = analysis with
                {
                    Paths = [legacy with { DropOrder = ["resource", "resource"] }],
                },
            });
        AssertEx.False(repeated.IsSuccessful,
            "A duplicated compact drop fact cannot invent a second initialization generation.");
        AssertEx.Equal(SafeCoreMirCleanupLowering.InvalidEvidence, repeated.Diagnostics.Single().Code);
        AssertEx.True(repeated.Functions.Count == 0 && repeated.Snapshot is null,
            "Rejected duplicate facts must not publish a partial cleanup plan.");
        return Task.CompletedTask;
    }

    private static Task ReplacementGenerationsAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreOwnershipFunction initial = ownership.Program!.Functions.Single();
        SafeCoreOwnershipProgram program = new([new SafeCoreOwnershipFunction(
            initial.Name, initial.Locals, initial.Scopes,
            [new SafeCoreOwnershipBlock(0, 0,
                [SafeCoreOwnershipInstruction.Drop(0, initial.Source),
                 SafeCoreOwnershipInstruction.Assign(0, initial.Source)],
                SafeCoreOwnershipTerminator.Panic(initial.Source), initial.Source)],
            0, initial.PanicStrategy, initial.Source)]);
        SafeCoreOwnershipAnalysisResult analysis = SafeCoreOwnershipAnalysis.Analyze(program);
        AssertEx.True(analysis.IsSuccessful, string.Join(Environment.NewLine, analysis.Diagnostics));
        SafeCoreOwnershipPath path = analysis.Paths.Single();
        AssertEx.Equal("resource,resource", string.Join(',', path.DropOrder));
        SafeCoreMirOwnershipResult replacement = ownership with { Program = program, Ownership = analysis };
        SafeCoreMirCleanupResult lowered = SafeCoreMirCleanupLowering.Lower(mir, replacement);
        AssertEx.True(lowered.IsSuccessful, string.Join(Environment.NewLine, lowered.Diagnostics));
        AssertEx.Equal(2, lowered.Functions.Single().Paths.Single().Actions.Count(
            static action => action.Kind == SafeCoreMirCleanupActionKind.Drop));

        // Omit the later drop trace while retaining the assignment event. The
        // legacy fallback must select the unconsumed replacement generation,
        // rather than reusing the already observed first-generation key.
        int laterDrop = path.Trace.LastIndexOf("drop resource");
        SafeCoreOwnershipPath legacy = path with { Trace = path.Trace.RemoveAt(laterDrop) };
        SafeCoreMirCleanupResult missingTrace = SafeCoreMirCleanupLowering.Lower(
            mir, replacement with { Ownership = analysis with { Paths = [legacy] } });
        AssertEx.True(missingTrace.IsSuccessful, string.Join(Environment.NewLine, missingTrace.Diagnostics));
        AssertEx.Equal(2, missingTrace.Functions.Single().Paths.Single().Actions.Count(
            static action => action.Kind == SafeCoreMirCleanupActionKind.Drop));
        return Task.CompletedTask;
    }

    private static Task ProjectedPlaceTypesAsync()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) {} }
            struct Fields { marker: Marker, count: i32 }
            fn main() { let fields = Fields { marker: Marker, count: 1 }; }
            """;
        SafeCoreMirPipelineResult proof = SafeCoreMirPipeline.Analyze(source, "cleanup-projected-types.rs",
            new() { EnableP1Extensions = true, RequireOwnershipEvidence = true, Timeout = TimeSpan.FromSeconds(10) });
        AssertEx.True(proof.IsSuccessful, string.Join(Environment.NewLine, proof.Diagnostics));
        SafeCoreMirProgram mir = proof.Mir!.Program!;
        string mainName = mir.Functions.Single(function => !function.IsDestructor).Name;
        SafeCoreMirOwnershipResult ownership = proof.Ownership!;
        SafeCoreMirCleanupResult valid = SafeCoreMirCleanupLowering.Lower(mir, ownership);
        AssertEx.True(valid.IsSuccessful, string.Join(Environment.NewLine, valid.Diagnostics));
        AssertEx.True(valid.Functions.Single(function => function.Name == mainName).Paths.Single().Actions.Any(action =>
            action.Kind == SafeCoreMirCleanupActionKind.Drop && action.LocalName == "fields.marker"),
            "Projected cleanup must retain its complete field identity even when the containing type has no own Drop.");

        AssertInvalid("fields.missing");
        AssertInvalid("fields.count");

        void AssertInvalid(string changedPlace)
        {
            SafeCoreOwnershipPath original = ownership.Ownership!.Paths.Single(path => path.FunctionName == mainName);
            SafeCoreOwnershipPath changed = original with
            {
                Trace = [.. original.Trace.Select(trace => trace.Replace("drop fields.marker", "drop " + changedPlace, StringComparison.Ordinal))],
                DropOrder = [.. original.DropOrder.Select(place => place == "fields.marker" ? changedPlace : place)],
            };
            SafeCoreMirOwnershipResult forged = ownership with
            {
                Ownership = ownership.Ownership with
                {
                    Paths = [.. ownership.Ownership.Paths.Select(path => path.PathId == original.PathId ? changed : path)],
                },
            };
            SafeCoreMirCleanupResult rejected = SafeCoreMirCleanupLowering.Lower(mir, forged);
            AssertEx.False(rejected.IsSuccessful, "An unknown or non-droppable projected type must not inherit root cleanup eligibility.");
            AssertEx.True(rejected.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirCleanupLowering.InvalidEvidence),
                "Malformed complete place evidence must produce RSM4001.");
        }
        return Task.CompletedTask;
    }

    private static Task RejectsMissingTypedTraceAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership, SafeCoreOwnershipPath path) = TypedDropEvidence();
        SafeCoreOwnershipPath changed = path with
        {
            Trace = [.. path.Trace.Where(static trace => trace != "drop fields.first")],
        };
        AssertInvalidTypedTrace(mir, ownership, path, changed);
        return Task.CompletedTask;
    }

    private static Task RejectsReorderedTypedTraceAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership, SafeCoreOwnershipPath path) = TypedDropEvidence();
        string[] trace = path.Trace.ToArray();
        int first = Array.IndexOf(trace, "drop fields.first");
        int second = Array.IndexOf(trace, "drop fields.second");
        AssertEx.True(first >= 0 && second > first, "The typed fixture must retain two ordered field drops.");
        (trace[first], trace[second]) = (trace[second], trace[first]);
        AssertInvalidTypedTrace(mir, ownership, path, path with { Trace = [.. trace] });
        return Task.CompletedTask;
    }

    private static Task RejectsCyclicStoragePlaceAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership, SafeCoreOwnershipPath path) = TypedDropEvidence();
        SafeCoreMirFunction typed = mir.Functions.Single(function => function.Name == path.FunctionName);
        SafeCoreOwnershipFunction original = ownership.Program!.Functions.Single(function => function.Name == path.FunctionName);
        SafeCoreOwnershipLocal synthetic = new(typed.Locals.Count, "0", typed.Locals[0].Type,
            SafeCoreOwnershipKind.Move, true, 0, false, true, typed.Source)
        {
            // A malicious complete name shadows the numeric backing alias.
            // Resolving its StoragePlace would revisit this same synthetic slot.
            StoragePlace = new SafeCoreOwnershipPlace(0),
        };
        SafeCoreOwnershipFunction changedFunction = new(original.Name, [synthetic, .. original.Locals],
            original.Scopes, original.Blocks, original.EntryBlockId, original.PanicStrategy, original.Source);
        SafeCoreOwnershipPath changedPath = path with { Trace = ["drop 0", "scope_exit 0", "return"], DropOrder = ["0"] };
        SafeCoreMirOwnershipResult malformed = ownership with
        {
            Program = new SafeCoreOwnershipProgram([.. ownership.Program.Functions.Select(function =>
                function.Name == original.Name ? changedFunction : function)]),
            Ownership = ownership.Ownership! with
            {
                Paths = [.. ownership.Ownership.Paths.Select(candidate => candidate.PathId == path.PathId ? changedPath : candidate)],
            },
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SafeCoreMirCleanupResult rejected = SafeCoreMirCleanupLowering.Lower(mir, malformed, new()
        {
            Timeout = TimeSpan.FromSeconds(2), MaximumOperations = 16_384, CancellationToken = cancellation.Token,
        });
        AssertEx.False(rejected.IsSuccessful, "Cyclic synthetic storage must terminate without a stack overflow.");
        AssertEx.True(rejected.IsTruncated && rejected.Snapshot is null && rejected.Functions.Count == 0,
            "Storage-place depth exhaustion must not publish partial cleanup evidence.");
        AssertEx.Equal(SafeCoreMirCleanupLowering.LimitReached, rejected.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static (SafeCoreMirProgram Mir, SafeCoreMirOwnershipResult Ownership, SafeCoreOwnershipPath Path) TypedDropEvidence()
    {
        const string source = """
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) {} }
            struct Fields { first: Marker, second: Marker }
            fn main() { let fields = Fields { first: Marker, second: Marker }; }
            """;
        SafeCoreMirPipelineResult proof = SafeCoreMirPipeline.Analyze(source, "cleanup-typed-integrity.rs", new()
        { EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true, Timeout = TimeSpan.FromSeconds(10) });
        AssertEx.True(proof.IsSuccessful, string.Join(Environment.NewLine, proof.Diagnostics));
        SafeCoreMirProgram mir = proof.Mir!.Program!;
        SafeCoreMirOwnershipResult ownership = proof.Ownership!;
        AssertEx.True(ownership.MirProgram is not null, "Current ownership evidence must be bound to its actual typed MIR.");
        string mainName = mir.Functions.Single(function => !function.IsDestructor).Name;
        SafeCoreOwnershipPath path = ownership.Ownership!.Paths.Single(candidate => candidate.FunctionName == mainName);
        AssertEx.True(path.Trace.Contains("drop fields.first") && path.Trace.Contains("drop fields.second"),
            "The typed producer must include complete field drop identities.");
        return (mir, ownership, path);
    }

    private static void AssertInvalidTypedTrace(SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership,
        SafeCoreOwnershipPath original, SafeCoreOwnershipPath changed)
    {
        SafeCoreMirOwnershipResult malformed = ownership with
        {
            Ownership = ownership.Ownership! with
            {
                Paths = [.. ownership.Ownership.Paths.Select(path => path.PathId == original.PathId ? changed : path)],
            },
        };
        SafeCoreMirCleanupResult rejected = SafeCoreMirCleanupLowering.Lower(mir, malformed,
            new() { Timeout = TimeSpan.FromSeconds(3), MaximumOperations = 16_384 });
        AssertEx.False(rejected.IsSuccessful, "Missing or reordered typed trace must not be repaired from compact facts.");
        AssertEx.Equal(SafeCoreMirCleanupLowering.InvalidEvidence, rejected.Diagnostics.Single().Code);
        AssertEx.True(rejected.Snapshot is null && rejected.Functions.Count == 0,
            "Rejected typed trace must not publish a partial cleanup plan.");
    }

    private static Task BorrowedReplacementScopeAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership, SafeCoreOwnershipPath path) = BorrowedReplacementEvidence();
        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(mir, ownership);
        AssertEx.True(cleanup.IsSuccessful, string.Join(Environment.NewLine, cleanup.Diagnostics));
        AssertEx.Equal("value.*", string.Join(',', path.DropOrder), "Only the replaced old borrowed generation belongs to the callee's explicit Drop trace.");
        int replacement = Array.FindIndex(path.Trace.ToArray(), static trace =>
            trace.StartsWith("move ", StringComparison.Ordinal) && trace.EndsWith(" -> value.*", StringComparison.Ordinal));
        AssertEx.True(replacement >= 0, "The canonical producer must retain the borrowed replacement store.");
        AssertEx.False(path.Trace.Skip(replacement + 1).Any(static trace => trace.StartsWith("drop value.*", StringComparison.Ordinal)),
            "The callee scope must not destroy its caller's newly stored owner.");
        SafeCoreMirDropFlagResult flags = SafeCoreMirDropFlagLowering.Lower(ownership);
        AssertEx.True(flags.IsSuccessful, string.Join(Environment.NewLine, flags.Diagnostics));
        SafeCoreMirDropFlag referent = flags.Paths.Single(candidate => candidate.PathId == path.PathId)
            .Events[replacement].Single(flag => flag.LocalName == "value.*");
        AssertEx.Equal(SafeCoreDropPlaceState.Live, referent.State);
        AssertEx.False(referent.Eligible, "A live replacement through a borrow remains owned by the caller.");
        return Task.CompletedTask;
    }

    private static Task RejectsNoncanonicalBorrowedDropAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership, SafeCoreOwnershipPath path) = BorrowedReplacementEvidence();
        SafeCoreMirFunction original = mir.Functions.Single(function => function.Name == path.FunctionName);
        int parameter = original.Locals.Single(local => local.Kind == SafeCoreMirLocalKind.Parameter).Id;
        bool IsStore(SafeCoreMirStatement statement) => statement.DestinationPlace is { } place &&
            place.LocalId == parameter && place.Projections.Any(static projection => projection.Kind == SafeCoreMirProjectionKind.Dereference);
        AssertEx.True(original.Blocks.SelectMany(static block => block.Statements).Any(IsStore),
            "The positive fixture must contain its actual borrowed replacement store.");
        SafeCoreMirFunction changed = new(original.Id, original.Name, original.ReturnType, original.Locals,
            [.. original.Blocks.Select(block => new SafeCoreMirBlock(block.Id,
                [.. block.Statements.Where(statement => !IsStore(statement))], block.Terminator, block.Source))],
            original.EntryBlockId, original.Source, original.IsPublic)
        { PanicStrategy = original.PanicStrategy };
        SafeCoreMirProgram noStore = new([.. mir.Functions.Select(function => function.Id == original.Id ? changed : function)], mir.AdtLayouts);
        SafeCoreMirOwnershipResult forged = ownership with { MirProgram = noStore };
        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(noStore, forged);
        AssertEx.False(cleanup.IsSuccessful, "A mutable receiver and destructor call alone cannot grant borrowed destruction permission.");
        AssertEx.True(cleanup.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == SafeCoreMirDiagnosticCodes.InvalidControlFlow &&
                diagnostic.Message.Contains("exact owned place", StringComparison.Ordinal)),
            "MIR validation must reject a borrowed Drop without its canonical following replacement before cleanup evidence is accepted.");
        SafeCoreMirDropFlagResult flags = SafeCoreMirDropFlagLowering.Lower(forged);
        AssertEx.False(flags.IsSuccessful, "Flags must reject the same noncanonical borrowed cleanup permission.");
        AssertEx.Equal(SafeCoreMirDropFlagLowering.InvalidEvidence, flags.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task RejectsBorrowedReplacementBranchBypassAsync() => RejectsBorrowedReplacementBypassAsync(false);

    private static Task RejectsBorrowedReplacementCycleBypassAsync() => RejectsBorrowedReplacementBypassAsync(true);

    private static Task RejectsBorrowedReplacementBypassAsync(bool cycle)
    {
        (SafeCoreMirProgram mir, _, SafeCoreOwnershipPath path) = BorrowedReplacementEvidence();
        SafeCoreMirFunction original = mir.Functions.Single(function => function.Name == path.FunctionName);
        SafeCoreMirBlock drop = original.Blocks.Single(block => block.Terminator is
            { Kind: SafeCoreMirTerminatorKind.Call, DropLocalId: null });
        SafeCoreMirSource source = drop.Terminator.Source;
        SafeCoreType boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
        int conditionId = original.Locals.Count;
        int branchId = original.Blocks.Count;
        int bypassId = branchId + 1;
        SafeCoreMirOperand condition = SafeCoreMirOperand.Local(conditionId, boolean, source);
        SafeCoreMirStatement initialize = new(conditionId,
            SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(boolean, "false", source), source), source);
        SafeCoreMirTerminator bypass = cycle ? SafeCoreMirTerminator.Goto(branchId, source) :
            original.Blocks.Single(block => block.Terminator.Kind == SafeCoreMirTerminatorKind.Return).Terminator;
        // The real store remains on the other branch. An existential search
        // would grant permission even though the selected normal path bypasses
        // it, either by returning or by staying in a store-free cycle.
        SafeCoreMirFunction changed = new(original.Id, original.Name, original.ReturnType,
            [.. original.Locals, new SafeCoreMirLocal(conditionId, "replacement_bypass", boolean,
                SafeCoreMirLocalKind.Temporary, false, source)],
            [.. original.Blocks.Select(block => block.Id == drop.Id
                ? new SafeCoreMirBlock(block.Id, [initialize, .. block.Statements],
                    SafeCoreMirTerminator.Call(block.Terminator.Operand!, block.Terminator.Arguments,
                        block.Terminator.DestinationLocalId, branchId, block.Terminator.Source), block.Source) : block),
                new SafeCoreMirBlock(branchId, [], SafeCoreMirTerminator.Branch(condition,
                    drop.Terminator.TargetBlockId, bypassId, source), source),
                new SafeCoreMirBlock(bypassId, [], bypass, original.Source)],
            original.EntryBlockId, original.Source, original.IsPublic)
        { PanicStrategy = original.PanicStrategy };
        SafeCoreMirProgram bypassProgram = new(
            [.. mir.Functions.Select(function => function.Id == original.Id ? changed : function)], mir.AdtLayouts);
        SafeCoreMirValidationResult rejected = SafeCoreMirValidation.Validate(bypassProgram, new()
        { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 50_000 });
        AssertEx.False(rejected.IsSuccessful, "Every normal continuation of a borrowed replacement Drop must reach its matching store.");
        AssertEx.True(rejected.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == SafeCoreMirDiagnosticCodes.InvalidControlFlow &&
            diagnostic.Message.Contains("exact owned place", StringComparison.Ordinal)),
            "A store on only one branch cannot authorize borrowed destruction on the bypass path.");
        AssertEx.False(rejected.IsTruncated, "The finite bypass graph must be rejected semantically within the proof budget.");
        return Task.CompletedTask;
    }

    private static Task RejectsBorrowedScopeDropAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership, SafeCoreOwnershipPath path) = BorrowedReplacementEvidence();
        string[] trace = path.Trace.ToArray();
        int scope = Array.FindLastIndex(trace, static item => item.StartsWith("scope_exit ", StringComparison.Ordinal));
        AssertEx.True(scope >= 0, "The callee's normal exit must retain its explicit scope boundary.");
        SafeCoreOwnershipPath changed = path with
        {
            // Forging a fresh generation avoids a duplicate-drop-only rejection.
            // It still cannot invent another actual replacement instruction.
            Trace = [.. trace[..scope], "assign value.*", "drop value.*", .. trace[scope..]],
            DropOrder = [.. path.DropOrder, "value.*"],
        };
        SafeCoreMirOwnershipResult forged = ownership with
        {
            Ownership = ownership.Ownership! with
            { Paths = [.. ownership.Ownership.Paths.Select(candidate => candidate.PathId == path.PathId ? changed : candidate)] },
        };
        AssertInvalidTypedTrace(mir, ownership, path, changed);
        SafeCoreMirDropFlagResult flags = SafeCoreMirDropFlagLowering.Lower(forged);
        AssertEx.False(flags.IsSuccessful, "A new borrowed generation cannot become an invented callee scope Drop.");
        AssertEx.Equal(SafeCoreMirDropFlagLowering.InvalidEvidence, flags.Diagnostics.Single().Code);
        AssertEx.True(flags.Paths.Count == 0 && flags.Snapshot is null, "Rejected borrowed scope facts cannot publish partial flags.");
        return Task.CompletedTask;
    }

    private static Task RejectsMismatchedTypedProgramAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership, _) = BorrowedReplacementEvidence();
        SafeCoreMirProgram copy = new(mir.Functions, mir.AdtLayouts);
        SafeCoreMirCleanupResult rejected = SafeCoreMirCleanupLowering.Lower(copy, ownership);
        AssertEx.False(rejected.IsSuccessful, "Cleanup and flag proofs cannot read different immutable typed programs.");
        AssertEx.Equal(SafeCoreMirCleanupLowering.InvalidEvidence, rejected.Diagnostics.Single().Code);
        AssertEx.True(rejected.Diagnostics.Single().Message.Contains("same immutable program", StringComparison.Ordinal),
            "The typed-program binding mismatch must have a stable diagnostic explanation.");
        return Task.CompletedTask;
    }

    private static (SafeCoreMirProgram Mir, SafeCoreMirOwnershipResult Ownership, SafeCoreOwnershipPath Path) BorrowedReplacementEvidence()
    {
        const string source = """
            struct Marker { value: i32 }
            impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
            fn replace(value: &mut Marker) { *value = Marker { value: 2 }; }
            fn main() { let mut value = Marker { value: 1 }; replace(&mut value); println!("body"); }
            """;
        SafeCoreMirPipelineResult proof = SafeCoreMirPipeline.Analyze(source, "cleanup-borrowed-replacement.rs", new()
        { EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true, Timeout = TimeSpan.FromSeconds(10) });
        AssertEx.True(proof.IsSuccessful, string.Join(Environment.NewLine, proof.Diagnostics));
        SafeCoreMirProgram mir = proof.Mir!.Program!;
        SafeCoreMirOwnershipResult ownership = proof.Ownership!;
        string name = mir.Functions.Single(function => function.Name.StartsWith("crate::replace#", StringComparison.Ordinal)).Name;
        return (mir, ownership, ownership.Ownership!.Paths.Single(path => path.FunctionName == name));
    }

    private static Task DeterministicSnapshotAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirCleanupLoweringOptions options = new()
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOperations = 4096,
            MaximumCharacters = 32_768,
        };
        SafeCoreMirCleanupResult first = SafeCoreMirCleanupLowering.Lower(mir, ownership, options);
        SafeCoreMirCleanupResult second = SafeCoreMirCleanupLowering.Lower(mir, ownership, options);
        AssertEx.True(first.IsSuccessful && second.IsSuccessful,
            "Bounded cleanup lowering must succeed under the declared limits.");
        AssertEx.Equal(first.Snapshot!, second.Snapshot!,
            "Cleanup snapshots must be deterministic across repeated lowering.");

        SafeCoreMirCleanupResult limited = SafeCoreMirCleanupLowering.Lower(
            mir, ownership, options with { MaximumCharacters = 32 });
        AssertEx.True(limited.IsTruncated && !limited.IsSuccessful,
            "The cleanup snapshot character limit must be observable and bounded.");
        AssertEx.True(limited.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreMirCleanupLowering.LimitReached),
            "The cleanup limit diagnostic must be machine readable.");

        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(mir);
        int sharedBudget = checked(validation.OperationsUsed + 1);
        SafeCoreMirCleanupResult budgetLimited = SafeCoreMirCleanupLowering.Lower(
            mir,
            ownership,
            options with { MaximumOperations = sharedBudget, MaximumCharacters = 32_768 });
        AssertEx.True(budgetLimited.IsTruncated && !budgetLimited.IsSuccessful,
            "Cleanup validation and projection must consume one shared operation budget.");
        return Task.CompletedTask;
    }

    private static Task MetadataSnapshotAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(mir, ownership);
        AssertEx.True(cleanup.IsSuccessful, string.Join(Environment.NewLine, cleanup.Diagnostics));
        string snapshot = AssertEx.NotNull(cleanup.Snapshot,
            "The metadata fixture requires a cleanup snapshot.");
        var document = new RustSharpMetadataDocument(
            SafeCoreMirPipeline.Profile,
            new string('A', 64),
            [new RustSharpMetadataFunction("Main", "()->()")],
            cleanupSnapshot: snapshot);
        AssertEx.True(document.Json.Contains("cleanupSnapshot", StringComparison.Ordinal),
            "The metadata document must persist the cleanup snapshot field.");
        RustSharpMetadataDocument parsed = RustSharpMetadataDocument.Parse(document.Json);
        AssertEx.Equal(snapshot, parsed.CleanupSnapshot!,
            "Cleanup evidence must survive metadata canonicalization.");
        return Task.CompletedTask;
    }

    private static Task CompilerSourceWiringAsync()
    {
        const string source = "fn main() { let value: i32 = 7; println!(\"{}\", value); }";
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "mir-source-evidence-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            CompilationResult compiled = CompilerDriver.Compile(
                source,
                sourcePath,
                outputPath,
                "MirSourceEvidence",
                CompilationProfile.SafeCoreMir);
            AssertEx.True(compiled.Success,
                "Compiler-integrated MIR evidence failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                outputPath,
                SafeCoreMirPipeline.Profile,
                ["Main"]);
            AssertEx.True(imported.IsSuccessful,
                "Compiler-integrated metadata import failed: " +
                string.Join("; ", imported.Diagnostics));
            RustSharpMetadataDocument document = imported.Document ??
                throw new InvalidOperationException("Compiler-integrated metadata document is missing.");
            AssertEx.True(document.MirSnapshot?.StartsWith("safe-core-mir-v1\n", StringComparison.Ordinal) == true,
                "Source compilation must persist the typed-MIR snapshot.");
            RustSharpMetadataOwnershipFunction ownership = document.Ownership.Single(fact =>
                fact.Outcomes?.Contains("Returned", StringComparer.Ordinal) == true);
            AssertEx.True(ownership.Outcomes?.Contains("Returned", StringComparer.Ordinal) == true,
                "Source compilation must persist returned ownership evidence.");
            AssertEx.True(document.CleanupSnapshot?.StartsWith(
                    "safe-core-mir-cleanup-p1-v1\n", StringComparison.Ordinal) == true,
                "Source compilation must persist cleanup evidence.");
            AssertEx.True(document.CleanupSnapshot!.Contains("scope_exit", StringComparison.Ordinal) &&
                document.CleanupSnapshot.Contains("return_boundary", StringComparison.Ordinal),
                "Cleanup evidence must include the source function return boundary.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task MirBackendArrayAsync()
    {
        const string source =
            "fn main() { let values: [i32; 3] = [4, 5, 6]; println!(\"{}\", values[1]); }";
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "mir-array-runtime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source,
                sourcePath,
                outputPath,
                "MirArrayRuntime",
                CompilationProfile.SafeCoreMir,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "MIR array backend compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10)),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, "MIR array runtime failed: " + run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete,
                "MIR array runtime process cleanup must complete.");
            AssertEx.Equal("5\n",
                run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task MirBackendCallAsync()
    {
        const string source =
            "fn add(left: i32, right: i32) -> i32 { left + right } " +
            "fn le(left: i32, right: i32) -> bool { left <= right } " +
            "fn ge(left: i32, right: i32) -> bool { left >= right } " +
            "fn main() { println!(\"{}\", add(2, 3)); " +
            "println!(\"{}\", le(1, 2)); println!(\"{}\", le(2, 1)); " +
            "println!(\"{}\", ge(2, 1)); println!(\"{}\", ge(1, 2)); }";
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "mir-call-runtime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source,
                sourcePath,
                outputPath,
                "MirCallRuntime",
                CompilationProfile.SafeCoreMir,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "MIR direct-call compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10)),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, "MIR direct-call runtime failed: " + run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete,
                "MIR direct-call runtime process cleanup must complete.");
            AssertEx.Equal("5\ntrue\nfalse\ntrue\nfalse\n",
                run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task BackendCapabilityGateAsync()
    {
        // SafeCoreMirValidation intentionally accepts the language-level MIR
        // contract, while the executable CLR value model is narrower. `check`
        // must nevertheless run the same backend capability gate as `compile`.
        (string Source, string Code)[] unsupported =
        [
            ("fn main() { println!(\"{}\", 'A'); }", SafeCoreMirClrLowering.Unsupported),
            ("fn identity(value: char) -> char { value } fn main() { identity('A'); }", SafeCoreMirClrLowering.Unsupported),
        ];

        foreach ((string source, string code) in unsupported)
        {
            CompilationResult result = CompilerDriver.Check(
                source, "mir-backend-capability.rs", CompilationProfile.SafeCoreMir);
            AssertEx.False(result.Success,
                "A source outside the executable MIR backend must not pass check.");
            AssertEx.Equal(code, result.Diagnostics.Single().Code,
                $"The check diagnostic must come from the same backend capability boundary ({source}): " +
                string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        }

        CompilationResult supported = CompilerDriver.Check(
            "fn main() { let value: i32 = 6 / 2 + true as i32; println!(\"{}\", value); }",
            "mir-backend-capability-ok.rs", CompilationProfile.SafeCoreMir);
        AssertEx.True(supported.Success,
            "A supported MIR scalar program must remain checkable after the backend gate.");
        return Task.CompletedTask;
    }

    private static (SafeCoreMirProgram Mir, SafeCoreMirOwnershipResult Ownership) BuildPanicEvidence(
        SafeCorePanicStrategy strategy)
    {
        SafeCoreType integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreMirSource source = Source(0);
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::panic_probe",
                integer,
                [new SafeCoreMirLocal(0, "resource", integer, SafeCoreMirLocalKind.Parameter, false, source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, integer, source), source),
                    source)],
                0,
                source),
        ]);
        SafeCoreOwnershipFunction ownershipFunction = new(
            "crate::panic_probe",
            [new SafeCoreOwnershipLocal(
                0,
                "resource",
                integer,
                SafeCoreOwnershipKind.Move,
                HasDrop: true,
                ScopeId: 0,
                IsReference: false,
                InitiallyInitialized: true,
                source)],
            [new SafeCoreOwnershipScope(0, -1, source)],
            [new SafeCoreOwnershipBlock(
                0,
                0,
                [],
                SafeCoreOwnershipTerminator.Panic(source),
                source)],
            0,
            strategy,
            source);
        SafeCoreOwnershipProgram ownershipProgram = new([ownershipFunction]);
        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(mir);
        SafeCoreOwnershipAnalysisResult analysis = SafeCoreOwnershipAnalysis.Analyze(ownershipProgram);
        AssertEx.True(validation.IsSuccessful && analysis.IsSuccessful,
            "Panic evidence fixture must be valid before cleanup lowering.");
        return (mir, new SafeCoreMirOwnershipResult(
            ownershipProgram,
            analysis,
            validation,
            [],
            false));
    }

    private static (SafeCoreMirProgram Mir, SafeCoreMirOwnershipResult Ownership) BuildBranchLoopEvidence()
    {
        SafeCoreType boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
        SafeCoreType integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreMirSource source = Source(0);
        SafeCoreMirOperand condition = SafeCoreMirOperand.Local(0, boolean, source);
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::branch_loop_probe",
                boolean,
                [
                    new SafeCoreMirLocal(0, "condition", boolean, SafeCoreMirLocalKind.Parameter, false, source),
                    new SafeCoreMirLocal(1, "resource", integer, SafeCoreMirLocalKind.User, false, source),
                ],
                [
                    new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Branch(condition, 1, 3, source), source),
                    new SafeCoreMirBlock(1, [], SafeCoreMirTerminator.Branch(condition, 2, 3, source), source),
                    new SafeCoreMirBlock(2, [], SafeCoreMirTerminator.Goto(3, source), source),
                    new SafeCoreMirBlock(3, [], SafeCoreMirTerminator.Return(condition, source), source),
                ],
                0,
                source),
        ]);

        SafeCoreOwnershipFunction ownershipFunction = new(
            "crate::branch_loop_probe",
            [
                new SafeCoreOwnershipLocal(
                    0, "condition", boolean, SafeCoreOwnershipKind.Copy,
                    HasDrop: false, ScopeId: 0, IsReference: false,
                    InitiallyInitialized: true, source),
                new SafeCoreOwnershipLocal(
                    1, "resource", integer, SafeCoreOwnershipKind.Move,
                    HasDrop: true, ScopeId: 0, IsReference: false,
                    InitiallyInitialized: true, source),
            ],
            [new SafeCoreOwnershipScope(0, -1, source)],
            [
                new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.Branch(0, 1, 3, source), source),
                new SafeCoreOwnershipBlock(1, 0, [], SafeCoreOwnershipTerminator.Branch(0, 2, 3, source), source),
                new SafeCoreOwnershipBlock(2, 0, [], SafeCoreOwnershipTerminator.Goto(3, source), source),
                new SafeCoreOwnershipBlock(3, 0, [], SafeCoreOwnershipTerminator.Return(0, source), source),
            ],
            0,
            SafeCorePanicStrategy.Unwind,
            source);
        SafeCoreOwnershipProgram ownershipProgram = new([ownershipFunction]);
        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(mir);
        SafeCoreOwnershipAnalysisResult analysis = SafeCoreOwnershipAnalysis.Analyze(ownershipProgram);
        AssertEx.True(validation.IsSuccessful && analysis.IsSuccessful,
            "Branch/loop evidence must validate before cleanup lowering: " +
            string.Join(Environment.NewLine, analysis.Diagnostics));
        return (mir, new SafeCoreMirOwnershipResult(
            ownershipProgram, analysis, validation, [], false));
    }

    private static SafeCoreMirSource Source(int start) =>
        new("cleanup.rs", new TextSpan(start, 1), start, 64);
}
