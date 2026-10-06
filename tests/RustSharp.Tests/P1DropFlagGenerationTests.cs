using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P1DropFlagGenerationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 Drop flags retain distinct replacement generations", ReplacementGenerationsAsync),
        new("P1 Drop flags reject excess and missing generation facts", RejectsGenerationDriftAsync),
        new("P1 typed Drop flags retain moved field and live sibling separately", PartialMovePlacesAsync),
        new("P1 typed Drop flags reinitialize a field without resetting its sibling", ReinitializedFieldAsync),
        new("P1 typed Drop flags reject unknown and non-droppable projected facts", RejectsProjectedDriftAsync),
        new("P1 typed Drop flags reject stale projected generations", RejectsStaleProjectedGenerationAsync),
        new("P1 typed enum Drop flags guard only the actual payload", ActiveEnumPlacesAsync),
        new("P1 typed mutable enum borrow invalidates tag facts without reviving moved owners", MutableEnumBorrowAsync),
        new("P1 typed borrowed replacement drops the old value without scope-owning the new value", OrdinaryBorrowReplacementAsync),
        new("P1 typed borrowed enum replacement rejects inactive extra drops", BorrowedEnumInactiveDropAsync),
    ];

    private static Task ReplacementGenerationsAsync()
    {
        SafeCoreMirOwnershipResult ownership = Evidence();
        SafeCoreMirDropFlagResult result = SafeCoreMirDropFlagLowering.Lower(ownership);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
        SafeCoreMirDropFlagPath path = result.Paths.Single();
        AssertEx.Equal(SafeCoreDropPlaceState.Dropped, path.FinalFlags.Single().State);
        AssertEx.False(path.FinalFlags.Single().Eligible, "The last generation must be consumed.");
        SafeCoreOwnershipPath source = ownership.Ownership!.Paths.Single();
        AssertEx.Equal(source.Trace.Length, path.Events.Count);
        int[] assignments = [.. source.Trace.Select((trace, index) => (trace, index))
            .Where(item => item.trace == "assign value").Select(static item => item.index)];
        int[] drops = [.. source.Trace.Select((trace, index) => (trace, index))
            .Where(item => item.trace == "drop value").Select(static item => item.index)];
        AssertEx.Equal(3, assignments.Length);
        AssertEx.Equal(3, drops.Length);
        foreach (int index in assignments)
        {
            AssertEx.Equal(SafeCoreDropPlaceState.Live, path.Events[index].Single().State);
            AssertEx.True(path.Events[index].Single().Eligible, "Every explicit initializer must activate its new generation.");
        }
        foreach (int index in drops)
        {
            AssertEx.Equal(SafeCoreDropPlaceState.Dropped, path.Events[index].Single().State);
            AssertEx.False(path.Events[index].Single().Eligible, "Every corresponding Drop must consume its generation.");
        }
        AssertEx.Equal(3, source.DropOrder.Length);
        return Task.CompletedTask;
    }

    private static Task RejectsGenerationDriftAsync()
    {
        SafeCoreMirOwnershipResult ownership = Evidence();
        SafeCoreOwnershipAnalysisResult analysis = ownership.Ownership!;
        SafeCoreOwnershipPath path = analysis.Paths.Single();
        SafeCoreMirDropFlagResult missing = SafeCoreMirDropFlagLowering.Lower(ownership with
        {
            Ownership = analysis with { Paths = [path with { DropOrder = ["value", "value"] }] },
        });
        AssertEx.False(missing.IsSuccessful, "A missing replacement generation cannot publish flags.");
        AssertEx.Equal(SafeCoreMirDropFlagLowering.InvalidEvidence, missing.Diagnostics.Single().Code);

        SafeCoreMirDropFlagResult excess = SafeCoreMirDropFlagLowering.Lower(ownership with
        {
            Ownership = analysis with { Paths = [path with { DropOrder = ["value", "value", "value", "value"] }] },
        });
        AssertEx.False(excess.IsSuccessful, "An extra drop without initialization must be rejected.");
        AssertEx.Equal(SafeCoreMirDropFlagLowering.InvalidEvidence, excess.Diagnostics.Single().Code);
        AssertEx.True(missing.Snapshot is null && excess.Snapshot is null,
            "Rejected generation evidence must not publish a partial snapshot.");
        return Task.CompletedTask;
    }

    private static SafeCoreMirOwnershipResult Evidence()
    {
        SafeCoreMirSource source = new("drop-generation.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreType type = SafeCoreType.Adt("crate::Marker");
        var function = new SafeCoreOwnershipFunction("crate::main",
            [new SafeCoreOwnershipLocal(0, "value", type, SafeCoreOwnershipKind.Move,
                true, 0, false, false, source)],
            [new SafeCoreOwnershipScope(0, -1, source)],
            [new SafeCoreOwnershipBlock(0, 0,
                [SafeCoreOwnershipInstruction.Assign(0, source),
                    SafeCoreOwnershipInstruction.Drop(0, source),
                    SafeCoreOwnershipInstruction.Assign(0, source),
                    SafeCoreOwnershipInstruction.Drop(0, source),
                    SafeCoreOwnershipInstruction.Assign(0, source)],
                SafeCoreOwnershipTerminator.ReturnUnit(source), source)],
            0, SafeCorePanicStrategy.Unwind, source);
        var program = new SafeCoreOwnershipProgram([function]);
        SafeCoreOwnershipAnalysisResult analysis = SafeCoreOwnershipAnalysis.Analyze(program,
            new SafeCoreOwnershipOptions { Timeout = TimeSpan.FromSeconds(5) });
        AssertEx.True(analysis.IsSuccessful, string.Join("; ", analysis.Diagnostics));
        SafeCoreMirValidationResult validation = SafeCoreMirPipeline.Analyze("fn main() {}", "drop-generation.rs").Mir!.Validation!;
        return new(program, analysis, validation, [], false);
    }

    private const string FieldsPrelude = """
        struct Marker { value: i32 }
        impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
        struct Fields { first: Marker, second: Marker }

        """;

    private static Task PartialMovePlacesAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence(FieldsPrelude + """
            fn main() {
                let value = Fields { first: Marker { value: 1 }, second: Marker { value: 2 } };
                let moved = value.first;
                println!("body");
            }
            """);
        SafeCoreMirDropFlagResult result = LowerTypedEvidence(evidence);
        SafeCoreOwnershipPath source = TypedMainPaths(evidence).Single();
        SafeCoreMirDropFlagPath flags = result.Paths.Single(path => path.PathId == source.PathId);
        int movedIndex = Array.FindIndex(source.Trace.ToArray(), trace => trace.StartsWith("move value.first -> ", StringComparison.Ordinal));
        AssertEx.True(movedIndex >= 0, "The ownership producer must retain the full moved field identity.");
        IReadOnlyList<SafeCoreMirDropFlag> snapshot = flags.Events[movedIndex];
        AssertEx.Equal(SafeCoreDropPlaceState.PartiallyMoved, Flag(snapshot, "value").State);
        AssertEx.Equal(SafeCoreDropPlaceState.Moved, Flag(snapshot, "value.first").State);
        AssertEx.False(Flag(snapshot, "value.first").Eligible, "A transferred field cannot remain eligible.");
        AssertEx.Equal(SafeCoreDropPlaceState.Live, Flag(snapshot, "value.second").State);
        AssertEx.True(Flag(snapshot, "value.second").Eligible, "The untouched sibling retains its actual Drop obligation.");
        return Task.CompletedTask;
    }

    private static Task ReinitializedFieldAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence(FieldsPrelude + """
            fn main() {
                let mut value = Fields { first: Marker { value: 1 }, second: Marker { value: 2 } };
                let moved = value.first;
                value.first = Marker { value: 3 };
                println!("body");
            }
            """);
        SafeCoreMirDropFlagResult result = LowerTypedEvidence(evidence);
        SafeCoreOwnershipPath source = TypedMainPaths(evidence).Single();
        SafeCoreMirDropFlagPath flags = result.Paths.Single(path => path.PathId == source.PathId);
        int assignedIndex = Array.FindIndex(source.Trace.ToArray(), trace => trace.StartsWith("move ", StringComparison.Ordinal) &&
            trace.EndsWith(" -> value.first", StringComparison.Ordinal));
        AssertEx.True(assignedIndex >= 0, "Field reinitialization must name its projected destination.");
        IReadOnlyList<SafeCoreMirDropFlag> snapshot = flags.Events[assignedIndex];
        AssertEx.Equal(SafeCoreDropPlaceState.Live, Flag(snapshot, "value").State);
        AssertEx.True(Flag(snapshot, "value.first").Eligible && Flag(snapshot, "value.second").Eligible,
            "The new field generation and untouched sibling must both remain eligible.");
        AssertEx.Equal(SafeCoreDropPlaceState.Live, Flag(snapshot, "value.second").State);
        return Task.CompletedTask;
    }

    private static Task RejectsProjectedDriftAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence(FieldsPrelude +
            "fn main() { let value = Fields { first: Marker { value: 1 }, second: Marker { value: 2 } }; }");
        SafeCoreOwnershipAnalysisResult analysis = evidence.Ownership!;
        SafeCoreOwnershipPath path = TypedMainPaths(evidence).Single();
        foreach (string invalid in new[] { "value.absent", "value.first.value", "value" })
        {
            SafeCoreOwnershipPath changed = path with
            {
                Trace = [.. path.Trace.Select(trace => trace == "drop value.first" ? "drop " + invalid : trace)],
                DropOrder = [.. path.DropOrder.Select(drop => drop == "value.first" ? invalid : drop)],
            };
            AssertRejected(evidence with { Ownership = analysis with { Paths = [.. analysis.Paths.Select(candidate => candidate.PathId == path.PathId ? changed : candidate)] } });
        }
        SafeCoreOwnershipPath missing = path with { DropOrder = [.. path.DropOrder.Where(drop => drop != "value.first")] };
        AssertRejected(evidence with { Ownership = analysis with { Paths = [.. analysis.Paths.Select(candidate => candidate.PathId == path.PathId ? missing : candidate)] } });
        SafeCoreOwnershipPath extra = path with { DropOrder = [.. path.DropOrder, "value.first"] };
        AssertRejected(evidence with { Ownership = analysis with { Paths = [.. analysis.Paths.Select(candidate => candidate.PathId == path.PathId ? extra : candidate)] } });
        return Task.CompletedTask;
    }

    private static Task RejectsStaleProjectedGenerationAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence(FieldsPrelude +
            "fn main() { let value = Fields { first: Marker { value: 1 }, second: Marker { value: 2 } }; }");
        SafeCoreOwnershipAnalysisResult analysis = evidence.Ownership!;
        SafeCoreOwnershipPath path = TypedMainPaths(evidence).Single();
        SafeCoreOwnershipPath changed = path with
        {
            Trace = [.. path.Trace.SelectMany(trace => trace == "drop value.first" ? new[] { trace, trace } : new[] { trace })],
            DropOrder = [.. path.DropOrder.SelectMany(drop => drop == "value.first" ? new[] { drop, drop } : new[] { drop })],
        };
        AssertRejected(evidence with { Ownership = analysis with { Paths = [.. analysis.Paths.Select(candidate => candidate.PathId == path.PathId ? changed : candidate)] } });
        return Task.CompletedTask;
    }

    private static Task ActiveEnumPlacesAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence("""
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            enum Choice { One(Marker), Two(Marker), Empty }
            fn main() { let value = Choice::Two(Marker); println!("body"); }
            """);
        SafeCoreMirDropFlagResult result = LowerTypedEvidence(evidence);
        foreach (SafeCoreOwnershipPath source in TypedMainPaths(evidence))
        {
            SafeCoreMirDropFlagPath flags = result.Paths.Single(path => path.PathId == source.PathId);
            int initializedIndex = Array.FindIndex(source.Trace.ToArray(), trace => trace.StartsWith("move ", StringComparison.Ordinal) && trace.EndsWith(" -> value", StringComparison.Ordinal));
            AssertEx.True(initializedIndex >= 0, "Enum ownership must transfer from its typed constructor.");
            IReadOnlyList<SafeCoreMirDropFlag> snapshot = flags.Events[initializedIndex];
            SafeCoreMirDropFlag[] payloads = [.. snapshot.Where(flag => flag.LocalName.StartsWith("value.", StringComparison.Ordinal) && flag.GuardCondition is not null)];
            AssertEx.Equal(2, payloads.Length);
            AssertEx.Equal(1, payloads.Count(flag => flag.Eligible));
            AssertEx.Equal(1, payloads.Count(flag => flag.State == SafeCoreDropPlaceState.Uninitialized));
        }
        return Task.CompletedTask;
    }

    private static Task MutableEnumBorrowAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence("""
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            enum Choice { One(Marker), Two(Marker), Empty }
            fn noop(value: &mut Choice) {}
            fn main() {
                let mut choice = Choice::One(Marker);
                let view = &mut choice;
                noop(view);
                println!("body");
            }
            """);
        SafeCoreMirDropFlagResult result = LowerTypedEvidence(evidence);
        foreach (SafeCoreOwnershipPath source in TypedMainPaths(evidence))
        {
            SafeCoreMirDropFlagPath flags = result.Paths.Single(path => path.PathId == source.PathId);
            int borrowIndex = Array.FindIndex(source.Trace.ToArray(), trace => trace.StartsWith("borrow_mut choice as ", StringComparison.Ordinal));
            AssertEx.True(borrowIndex > 0, "The checked producer must retain the mutable enum owner place.");
            IReadOnlyList<SafeCoreMirDropFlag> before = flags.Events[borrowIndex - 1];
            IReadOnlyList<SafeCoreMirDropFlag> after = flags.Events[borrowIndex];
            SafeCoreMirDropFlag[] payloads = [.. after.Where(flag => flag.LocalName.StartsWith("choice.", StringComparison.Ordinal) && flag.GuardCondition is not null)];
            AssertEx.Equal(2, payloads.Length);
            AssertEx.True(payloads.All(flag => flag.State == SafeCoreDropPlaceState.Live && flag.Eligible),
                "Unknown enum tag facts must preserve both potential obligations with their explicit guards.");
            SafeCoreMirDropFlag[] moved = [.. before.Where(flag => flag.State == SafeCoreDropPlaceState.Moved)];
            AssertEx.True(moved.Length > 0, "The constructor transfer must provide a real consumed source generation.");
            foreach (SafeCoreMirDropFlag consumed in moved)
            {
                AssertEx.Equal(SafeCoreDropPlaceState.Moved, Flag(after, consumed.LocalName).State);
                AssertEx.False(Flag(after, consumed.LocalName).Eligible, "Invalidating tag facts cannot revive a moved owner.");
            }
        }
        return Task.CompletedTask;
    }

    private static Task OrdinaryBorrowReplacementAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence("""
            struct Marker { value: i32 }
            impl Drop for Marker { fn drop(&mut self) { println!("{}", self.value); } }
            fn replace(value: &mut Marker) { *value = Marker { value: 2 }; }
            fn main() { let mut owner = Marker { value: 1 }; replace(&mut owner); println!("body"); }
            """);
        SafeCoreMirDropFlagResult result = LowerTypedEvidence(evidence);
        string function = evidence.MirProgram!.Functions.Single(candidate => candidate.Name == "crate::replace#value").Name;
        SafeCoreOwnershipPath source = evidence.Ownership!.Paths.Single(path => path.FunctionName == function);
        SafeCoreMirDropFlagPath flags = result.Paths.Single(path => path.PathId == source.PathId);
        AssertEx.Equal(1, source.DropOrder.Length);
        AssertEx.Equal("value.*", source.DropOrder.Single());
        int droppedIndex = Array.FindIndex(source.Trace.ToArray(), static trace => trace == "drop value.*");
        int assignedIndex = Array.FindIndex(source.Trace.ToArray(), trace => trace.StartsWith("move ", StringComparison.Ordinal) &&
            trace.EndsWith(" -> value.*", StringComparison.Ordinal));
        AssertEx.True(droppedIndex >= 0 && assignedIndex > droppedIndex,
            "The checked replacement must consume the old borrowed value before installing the new generation.");
        AssertEx.Equal(SafeCoreDropPlaceState.Dropped, Flag(flags.Events[droppedIndex], "value.*").State);
        AssertEx.Equal(SafeCoreDropPlaceState.Live, Flag(flags.Events[assignedIndex], "value.*").State);
        AssertEx.False(Flag(flags.Events[assignedIndex], "value.*").Eligible,
            "Installing a caller-owned new value does not grant the callee automatic scope cleanup ownership.");
        return Task.CompletedTask;
    }

    private static Task BorrowedEnumInactiveDropAsync()
    {
        SafeCoreMirOwnershipResult evidence = TypedEvidence("""
            struct Marker;
            impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
            enum Choice { One(Marker), Two(Marker), Empty }
            fn replace(value: &mut Choice) { *value = Choice::Two(Marker); }
            fn main() { let mut owner = Choice::One(Marker); replace(&mut owner); println!("body"); }
            """);
        SafeCoreMirDropFlagResult result = LowerTypedEvidence(evidence);
        SafeCoreOwnershipAnalysisResult analysis = evidence.Ownership!;
        string function = evidence.MirProgram!.Functions.Single(candidate => candidate.Name == "crate::replace#value").Name;
        SafeCoreOwnershipPath[] paths = [.. analysis.Paths.Where(path => path.FunctionName == function && path.DropOrder.Length == 1)];
        string[] payloads = [.. paths.Select(static path => path.DropOrder.Single()).Distinct(StringComparer.Ordinal)];
        AssertEx.Equal(2, payloads.Length);
        SafeCoreOwnershipPath source = paths.First(path => path.DropOrder.Single() == payloads[0]);
        int droppedIndex = Array.FindIndex(source.Trace.ToArray(), trace => trace == "drop " + payloads[0]);
        AssertEx.True(droppedIndex > 0, "The checked branch must precede its borrowed payload replacement Drop.");
        SafeCoreMirDropFlagPath flags = result.Paths.Single(path => path.PathId == source.PathId);
        AssertEx.Equal(SafeCoreDropPlaceState.Uninitialized, Flag(flags.Events[droppedIndex - 1], payloads[1]).State);
        AssertEx.False(Flag(flags.Events[droppedIndex - 1], payloads[0]).Eligible,
            "Borrowed replacement permissions do not imply automatic scope ownership.");
        SafeCoreOwnershipPath forged = source with
        {
            Trace = [.. source.Trace.Take(droppedIndex), "drop " + payloads[1], .. source.Trace.Skip(droppedIndex)],
            DropOrder = [payloads[1], .. source.DropOrder],
        };
        AssertRejected(evidence with { Ownership = analysis with { Paths = [.. analysis.Paths.Select(path => path.PathId == source.PathId ? forged : path)] } });
        return Task.CompletedTask;
    }

    private static SafeCoreMirOwnershipResult TypedEvidence(string source)
    {
        SafeCoreMirPipelineResult pipeline = SafeCoreMirPipeline.Analyze(source, "typed-drop-flags.rs", new()
        { RequireOwnershipEvidence = true, RequireCleanupEvidence = true, EnableP1Extensions = true, Timeout = TimeSpan.FromSeconds(10) });
        AssertEx.True(pipeline.IsSuccessful && pipeline.Ownership is not null, string.Join("; ", pipeline.Diagnostics));
        AssertEx.True(pipeline.Ownership!.MirProgram is not null, "Typed flag evidence must keep its actual validated MIR.");
        return pipeline.Ownership;
    }

    private static SafeCoreMirDropFlagResult LowerTypedEvidence(SafeCoreMirOwnershipResult evidence)
    {
        SafeCoreMirDropFlagResult result = SafeCoreMirDropFlagLowering.Lower(evidence);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
        return result;
    }

    private static SafeCoreOwnershipPath[] TypedMainPaths(SafeCoreMirOwnershipResult evidence)
    {
        SafeCoreMirFunction main = evidence.MirProgram!.Functions.Single(function => function.Name == "crate::main#value");
        SafeCoreOwnershipPath[] paths = [.. evidence.Ownership!.Paths.Where(path => path.FunctionName == main.Name)];
        AssertEx.True(paths.Length > 0, "Typed main evidence must contain an actual checked ownership path.");
        return paths;
    }
    private static SafeCoreMirDropFlag Flag(IReadOnlyList<SafeCoreMirDropFlag> flags, string place) => flags.Single(flag => flag.LocalName == place);
    private static void AssertRejected(SafeCoreMirOwnershipResult evidence)
    {
        SafeCoreMirDropFlagResult rejected = SafeCoreMirDropFlagLowering.Lower(evidence);
        AssertEx.False(rejected.IsSuccessful, "Forged projected Drop evidence must not publish flags.");
        AssertEx.Equal(SafeCoreMirDropFlagLowering.InvalidEvidence, rejected.Diagnostics.Single().Code);
        AssertEx.True(rejected.Snapshot is null && rejected.Paths.Count == 0, "Rejected places must not leak a partial snapshot.");
    }
}
