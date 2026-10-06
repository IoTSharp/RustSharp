using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RustSharp.Semantics;

internal static partial class SafeCoreMirDropEvidencePlaces
{
    internal static bool HasCanonicalBorrowedReplacementDrop(SafeCoreMirProgram mir, SafeCoreMirFunction function,
        string key, SafeCoreMirSource? source, Stopwatch clock, TimeSpan timeout, int maximumOperations,
        ref int operations, CancellationToken cancellationToken)
    {
        foreach (SafeCoreMirBlock block in function.Blocks)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (source is not null && block.Terminator.Source != source) continue;
            if (TryCanonicalBorrowedReplacementDrop(mir, function, block, clock, timeout, maximumOperations,
                    ref operations, cancellationToken) is { } actual && string.Equals(key, actual, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static string? TryCanonicalBorrowedReplacementDrop(SafeCoreMirProgram mir, SafeCoreMirFunction function,
        SafeCoreMirBlock block, Stopwatch clock, TimeSpan timeout, int maximumOperations,
        ref int operations, CancellationToken cancellationToken)
    {
        Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
        SafeCoreMirTerminator call = block.Terminator;
        if (call.Kind != SafeCoreMirTerminatorKind.Call || call.DropLocalId is not null || call.DestinationLocalId is not null ||
            call.Operand is not { Kind: SafeCoreMirOperandKind.Function } target ||
            !mir.IsDestructorFunction(target.Id) ||
            call.Arguments.Count != 1 || call.Arguments[0] is not { Kind: SafeCoreMirOperandKind.Local } receiver ||
            receiver.Id < 0 || receiver.Id >= function.Locals.Count ||
            function.Locals[receiver.Id].Kind != SafeCoreMirLocalKind.Temporary ||
            receiver.Type.Kind != SafeCoreSemanticTypeKind.Reference || !receiver.Type.IsMutable ||
            receiver.Type.ElementType?.Kind != SafeCoreSemanticTypeKind.Adt || block.Statements.Count == 0)
            return null;
        SafeCoreMirStatement definition = block.Statements[^1];
        SafeCoreMirRvalue borrow = definition.Value;
        if (definition.DestinationLocalId != receiver.Id || definition.DestinationPlace is not null ||
            borrow.Kind != SafeCoreMirRvalueKind.Unary || borrow.Operator != "&mut" || borrow.Type != receiver.Type ||
            borrow.Operands.Count != 1 || borrow.Operands[0].Place is not { } owner ||
            borrow.Operands[0].Type != receiver.Type.ElementType || definition.Source != call.Source ||
            borrow.Source != call.Source || function.Locals[receiver.Id].Source != call.Source)
            return null;
        int definitions = 0;
        foreach (SafeCoreMirStatement statement in block.Statements)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (statement.DestinationLocalId == receiver.Id) definitions++;
        }
        if (definitions != 1) return null;
        string? display = MutableBorrowedDisplay(mir, function, owner, clock, timeout, maximumOperations,
            ref operations, cancellationToken);
        if (display is null || !ReachesReplacementStore(function, call, owner, clock, timeout, maximumOperations,
                ref operations, cancellationToken)) return null;
        return display;
    }

    internal static bool MatchCanonicalBorrowedDropTrace(SafeCoreMirProgram mir, SafeCoreMirFunction typed,
        SafeCoreOwnershipFunction function, SafeCoreOwnershipPath path, Dictionary<string, SafeCoreOwnershipLocal> locals,
        Stopwatch clock, TimeSpan timeout, int maximumOperations, ref int operations, CancellationToken cancellationToken)
    {
        var actual = new List<string>();
        var branches = new List<int>();
        bool hasBorrowedInstruction = false;
        foreach (SafeCoreOwnershipBlock block in function.Blocks)
        foreach (SafeCoreOwnershipInstruction instruction in block.Instructions)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (instruction.Kind != SafeCoreOwnershipInstructionKind.Drop) continue;
            if (instruction.Place is not { } projected ||
                !projected.Projections.Any(static projection => projection.Kind == SafeCoreOwnershipProjectionKind.Dereference))
                continue;
            Resolved? place = Resolve(mir, typed, locals,
                projected.ToString(),
                clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (place is { ScopeOwned: false }) hasBorrowedInstruction = true;
        }
        foreach (string trace in path.Trace)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (trace.StartsWith("drop ", StringComparison.Ordinal) && trace.Contains(".*", StringComparison.Ordinal))
            {
                Resolved? place = Resolve(mir, typed, locals, trace[5..],
                    clock, timeout, maximumOperations, ref operations, cancellationToken);
                if (place is { ScopeOwned: false })
                {
                    if (!place.Droppable) return false;
                    actual.Add(place.Key);
                }
            }
            else if (trace.StartsWith("branch ", StringComparison.Ordinal))
            {
                if (!int.TryParse(trace.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int target)) return false;
                branches.Add(target);
            }
        }
        if (!hasBorrowedInstruction) return actual.Count == 0;
        if (function.Blocks.Count is < 1 or > 16384) return false;
        var expected = new List<string>();
        int blockId = function.EntryBlockId;
        int branchIndex = 0;
        long maximumBlocks = Math.Min(maximumOperations, (long)function.Blocks.Count * (path.Trace.Length + 1L));
        bool terminal = false;
        for (long visited = 0; visited < maximumBlocks; visited++)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (blockId < 0 || blockId >= function.Blocks.Count || function.Blocks[blockId].Id != blockId) return false;
            SafeCoreOwnershipBlock block = function.Blocks[blockId];
            foreach (SafeCoreOwnershipInstruction instruction in block.Instructions)
            {
                Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                if (instruction.Kind != SafeCoreOwnershipInstructionKind.Drop) continue;
                Resolved? place = Resolve(mir, typed, locals,
                    (instruction.Place ?? SafeCoreOwnershipPlace.Root(instruction.LocalId)).ToString(),
                    clock, timeout, maximumOperations, ref operations, cancellationToken);
                if (place is not { ScopeOwned: false }) continue;
                if (!instruction.IsConditionalDrop || !place.Droppable ||
                    !HasCanonicalBorrowedReplacementDrop(mir, typed, place.Key, instruction.Source,
                        clock, timeout, maximumOperations, ref operations, cancellationToken)) return false;
                expected.Add(place.Key);
            }
            SafeCoreOwnershipTerminator edge = block.Terminator;
            if (edge.Kind == SafeCoreOwnershipTerminatorKind.Goto) blockId = edge.TargetBlockId;
            else if (edge.Kind == SafeCoreOwnershipTerminatorKind.Branch)
            {
                if (branchIndex >= branches.Count) return false;
                blockId = branches[branchIndex++];
                if (blockId != edge.TargetBlockId && blockId != edge.FalseTargetBlockId) return false;
            }
            else
            {
                SafeCoreOwnershipOutcome outcome = edge.Kind switch
                {
                    SafeCoreOwnershipTerminatorKind.Return => SafeCoreOwnershipOutcome.Returned,
                    SafeCoreOwnershipTerminatorKind.Panic => function.PanicStrategy == SafeCorePanicStrategy.Unwind
                        ? SafeCoreOwnershipOutcome.Unwound : SafeCoreOwnershipOutcome.Aborted,
                    SafeCoreOwnershipTerminatorKind.Unreachable => SafeCoreOwnershipOutcome.Unreachable,
                    _ => SafeCoreOwnershipOutcome.LimitExceeded,
                };
                if (outcome != path.Outcome) return false;
                terminal = true;
                break;
            }
        }
        if (!terminal || branchIndex != branches.Count || actual.Count != expected.Count) return false;
        for (int index = 0; index < actual.Count; index++)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool ReachesReplacementStore(SafeCoreMirFunction function, SafeCoreMirTerminator call,
        SafeCoreMirPlace dropped, Stopwatch clock, TimeSpan timeout, int maximumOperations,
        ref int operations, CancellationToken cancellationToken)
    {
        const int maximumBlocks = 4096;
        if (function.Blocks.Count > maximumBlocks)
            throw new SafeCoreMirLimitException("Replacement Drop proof exceeds its bounded CFG arena.");
        var pending = new Queue<int>();
        var scheduled = new HashSet<int>();
        var proved = new Queue<int>();
        var provedStore = new bool[function.Blocks.Count];
        var remainingSuccessors = new int[function.Blocks.Count];
        var predecessors = new List<int>?[function.Blocks.Count];
        if (call.TargetBlockId < 0 || call.TargetBlockId >= function.Blocks.Count) return false;
        scheduled.Add(call.TargetBlockId);
        pending.Enqueue(call.TargetBlockId);
        for (int checkedBlocks = 0; pending.Count > 0 && checkedBlocks < function.Blocks.Count; checkedBlocks++)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            int id = pending.Dequeue();
            SafeCoreMirBlock block = function.Blocks[id];
            bool hasStore = false;
            foreach (SafeCoreMirStatement statement in block.Statements)
            {
                Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                if (statement.DestinationPlace is not { IsRoot: false } destination ||
                    destination.LocalId != dropped.LocalId || destination.Projections.Count > dropped.Projections.Count ||
                    statement.Value.Kind != SafeCoreMirRvalueKind.Use || statement.Value.Operands.Count != 1 ||
                    statement.Value.Operands[0].Kind != SafeCoreMirOperandKind.Local ||
                    statement.Source.SourcePath != call.Source.SourcePath ||
                    statement.Source.Span.Start > call.Source.Span.Start || statement.Source.Span.End < call.Source.Span.End)
                    continue;
                bool prefix = true;
                for (int index = 0; index < destination.Projections.Count; index++)
                {
                    Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                    if (destination.Projections[index] != dropped.Projections[index]) { prefix = false; break; }
                }
                if (prefix) { hasStore = true; break; }
            }
            if (hasStore)
            {
                // Every statement in this block executes before its normal
                // successor. The matching store discharges this path's proof.
                provedStore[id] = true;
                proved.Enqueue(id);
                continue;
            }
            SafeCoreMirTerminator next = block.Terminator;
            if (next.Kind is not (SafeCoreMirTerminatorKind.Goto or SafeCoreMirTerminatorKind.Call or SafeCoreMirTerminatorKind.Branch))
                return false;
            if (!Enqueue(id, next.TargetBlockId)) return false;
            if (next.Kind == SafeCoreMirTerminatorKind.Branch && next.FalseTargetBlockId != next.TargetBlockId &&
                !Enqueue(id, next.FalseTargetBlockId)) return false;
        }

        // A predecessor is proved only once every normal successor is proved.
        // A terminal bypass has already failed above; cycles without a store
        // cannot enter this queue and therefore never discharge their caller.
        for (int provedBlocks = 0; proved.Count > 0 && provedBlocks < function.Blocks.Count; provedBlocks++)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            int id = proved.Dequeue();
            if (predecessors[id] is not { } incoming) continue;
            foreach (int predecessor in incoming)
            {
                Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                if (--remainingSuccessors[predecessor] == 0 && !provedStore[predecessor])
                {
                    provedStore[predecessor] = true;
                    proved.Enqueue(predecessor);
                }
            }
        }
        return provedStore[call.TargetBlockId];

        bool Enqueue(int predecessor, int id)
        {
            if (id < 0 || id >= function.Blocks.Count) return false;
            (predecessors[id] ??= []).Add(predecessor);
            remainingSuccessors[predecessor]++;
            if (scheduled.Add(id)) pending.Enqueue(id);
            return true;
        }
    }

    private static string? MutableBorrowedDisplay(SafeCoreMirProgram mir, SafeCoreMirFunction function, SafeCoreMirPlace place,
        Stopwatch clock, TimeSpan timeout, int maximumOperations, ref int operations, CancellationToken cancellationToken)
    {
        if (place.LocalId < 0 || place.LocalId >= function.Locals.Count) return null;
        SafeCoreType type = function.Locals[place.LocalId].Type;
        var display = new StringBuilder(function.Locals[place.LocalId].Name);
        int selectedVariant = -1;
        bool borrowed = false;
        foreach (SafeCoreMirProjection projection in place.Projections)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (projection.Kind == SafeCoreMirProjectionKind.Dereference)
            {
                if (type.Kind != SafeCoreSemanticTypeKind.Reference || !type.IsMutable || type.ElementType is null) return null;
                borrowed = true;
                display.Append(".*"); type = type.ElementType!;
            }
            else if (projection.Kind is SafeCoreMirProjectionKind.Downcast or SafeCoreMirProjectionKind.Field)
            {
                SafeCoreMirAdtLayout? layout = null;
                foreach (SafeCoreMirAdtLayout candidate in mir.AdtLayouts)
                {
                    Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                    if (candidate.Type == type) { layout = candidate; break; }
                }
                if (layout is null) return null;
                if (projection.Kind == SafeCoreMirProjectionKind.Downcast)
                {
                    if (projection.Index < 0 || projection.Index >= layout.Variants.Count) return null;
                    selectedVariant = projection.Index;
                    continue;
                }
                IReadOnlyList<SafeCoreMirAdtField> fields = selectedVariant < 0 ? layout.Fields : layout.Variants[selectedVariant].Fields;
                int field = -1;
                for (int index = 0; index < fields.Count; index++)
                {
                    Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                    if (fields[index].Name == projection.Name) { field = index; break; }
                }
                if (field < 0) return null;
                display.Append('.').Append(selectedVariant < 0 ? fields[field].Name :
                    layout.Fields[layout.Variants[selectedVariant].FieldOffset + field].Name);
                type = fields[field].Type; selectedVariant = -1;
            }
            else if (projection.Kind == SafeCoreMirProjectionKind.TupleIndex)
            {
                if (type.Kind != SafeCoreSemanticTypeKind.Tuple || projection.Index < 0 || projection.Index >= type.Elements.Count) return null;
                display.Append(".tuple[").Append(projection.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                type = type.Elements[projection.Index];
            }
            else if (projection.Kind is SafeCoreMirProjectionKind.ArrayIndex or SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.FromEndIndex)
            {
                if (type.Kind is not (SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice) || type.ElementType is null) return null;
                display.Append('[');
                if (projection.Kind == SafeCoreMirProjectionKind.DynamicIndex) display.Append("local:");
                if (projection.Kind == SafeCoreMirProjectionKind.FromEndIndex) display.Append("len-");
                display.Append(projection.Index.ToString(CultureInfo.InvariantCulture));
                if (projection.Kind == SafeCoreMirProjectionKind.FromEndIndex)
                    display.Append(";min=").Append(projection.MinimumLength.ToString(CultureInfo.InvariantCulture));
                display.Append(']'); type = type.ElementType!;
            }
            else return null;
        }
        return borrowed ? display.ToString() : null;
    }
}
