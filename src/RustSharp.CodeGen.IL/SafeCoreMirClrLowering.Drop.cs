using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

public static partial class SafeCoreMirClrLowering
{
    private sealed partial class Lowerer
    {
        internal bool IsDestructorFunction(int id)
        {
            Step();
            return program.IsDestructorFunction(id);
        }

        internal int? DestructorFor(SafeCoreType type)
        {
            foreach (SafeCoreMirFunction candidate in program.Functions)
            {
                Step();
                if (candidate.IsDestructor && candidate.Locals.Count > 0 &&
                    candidate.Locals[0].Type.ElementType == type) return candidate.Id;
            }
            foreach (SafeCoreMirExternalFunction candidate in program.ExternalFunctions)
            {
                Step();
                if (candidate.IsDestructor && candidate.Signature.ParameterTypes.Count == 1 &&
                    candidate.Signature.ParameterTypes[0].ElementType == type) return candidate.Id;
            }
            return null;
        }

        internal SafeCoreMirAdtLayout? DropLayout(SafeCoreType type)
        {
            foreach (SafeCoreMirAdtLayout layout in program.AdtLayouts)
            {
                Step();
                if (layout.Type == type) return layout;
            }
            return null;
        }
    }

    private sealed partial class BodyLowerer
    {
        private readonly List<(SafeCoreMirPlace Place, int Flag)> _dropPlaces = [];
        private readonly Dictionary<SafeCoreMirTerminator, int> _dropCallFlags = [];
        private readonly Dictionary<int, int[]> _nestedDropFlags = [];
        private readonly Dictionary<int, ClrLirInstruction[][]> _dropFlagGuards = [];

        private ClrLirGuardedCleanup[] BuildExceptionCleanup()
        {
            var definitions = new Dictionary<int, SafeCoreMirStatement>();
            foreach (SafeCoreMirBlock block in function.Blocks)
            foreach (SafeCoreMirStatement statement in block.Statements)
            {
                owner.Step();
                if (statement.Value.Kind == SafeCoreMirRvalueKind.Unary && statement.Value.Operator == "&mut")
                    definitions.TryAdd(statement.DestinationLocalId, statement);
            }
            var calls = new List<(SafeCoreMirPlace Place, ClrLirGuardedCleanup Cleanup, int Ordinal)>();
            var byPlace = new Dictionary<string, int>(StringComparer.Ordinal);
            var declarationOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            int declarationOrdinal = 0;
            foreach (SafeCoreMirBlock block in function.Blocks)
            {
                owner.Step();
                SafeCoreMirTerminator terminator = block.Terminator;
                if (terminator.Kind != SafeCoreMirTerminatorKind.Call ||
                    terminator.Operand is not { Kind: SafeCoreMirOperandKind.Function } callee ||
                    !terminator.DropLocalId.HasValue && !owner.IsDestructorFunction(callee.Id)) continue;
                SafeCoreMirPlace place;
                if (terminator.Arguments.Count == 1 &&
                    definitions.TryGetValue(terminator.Arguments[0].Id, out SafeCoreMirStatement? receiver))
                    place = receiver.Value.Operands[0].Place ?? SafeCoreMirPlace.Root(receiver.Value.Operands[0].Id);
                else if (terminator.DropLocalId is int root && terminator.Arguments.Count == 0)
                    place = SafeCoreMirPlace.Root(root);
                else
                    throw new LoweringFailure(new(Unsupported, "Generated destructor cleanup requires an explicit checked receiver place.", terminator.Source.Span));
                string key = place.ToString();
                if (byPlace.TryGetValue(key, out int existingFlag))
                {
                    _dropCallFlags.Add(terminator, existingFlag);
                    continue;
                }
                int flag = Temporary(ClrLirType.Bool, terminator.Source);
                byPlace.Add(key, flag);
                _dropPlaces.Add((place, flag));
                _dropCallFlags.Add(terminator, flag);
                ClrLirType returnType = owner.ReturnType(callee.Type.ReturnType, terminator.Source);
                ClrLirType[] parameters = callee.Type.ParameterTypes.Select(type => owner.StorageType(type, terminator.Source)).ToArray();
                var site = owner.CallSite(callee.Id, returnType, parameters, terminator.Source);
                ClrLirGuardedCleanup cleanup;
                if (parameters.Length == 0) cleanup = new(site, flag);
                else
                {
                    Block? saved = _current;
                    var reconstruction = new Block("cleanup_receiver");
                    _current = reconstruction;
                    try { EmitPlaceAddress(place, terminator.Source, mutable: true); }
                    finally { _current = saved; }
                    cleanup = new(site, flag, receiverInstructions: reconstruction.Instructions);
                }
                calls.Add((place, cleanup, calls.Count));
            }
            foreach (SafeCoreMirLocal local in function.Locals)
            {
                owner.Step();
                CollectOwned(local.Type, SafeCoreMirPlace.Root(local.Id), local.Source, 0);
                if (function.IsDestructor && local.Kind == SafeCoreMirLocalKind.Parameter &&
                    local.Type.ElementType is { } receiverType)
                    CollectOwned(receiverType, SafeCoreMirPlace.Root(local.Id).Append(SafeCoreMirProjection.Dereference()),
                        local.Source, 0, skipOuter: true);
            }
            // Cleanup sites may first appear at an assignment replacement. The
            // declaration layout, rather than that site's position, orders fields.
            foreach (var call in calls)
            {
                owner.Step();
                _nestedDropFlags.Add(call.Cleanup.FlagLocalIndex, _dropPlaces
                    .Where(drop => drop.Flag != call.Cleanup.FlagLocalIndex && ContainsPlace(call.Place, drop.Place))
                    .Select(static drop => drop.Flag).ToArray());
                _dropFlagGuards.Add(call.Cleanup.FlagLocalIndex, call.Cleanup.HasReceiver
                    ? BuildCurrentDropGuards(call.Place, function.Locals[call.Place.LocalId].Source) : []);
            }
            return calls.Where(call => IsOwnedCleanupPlace(call.Place))
                .OrderByDescending(static call => call.Place.LocalId)
                .ThenBy(call => declarationOrder.GetValueOrDefault(call.Place.ToString(), int.MaxValue))
                .ThenBy(static call => call.Ordinal)
                .Select(call => new ClrLirGuardedCleanup(call.Cleanup.Site, call.Cleanup.FlagLocalIndex,
                    call.Cleanup.ReceiverLocalIndex, call.Cleanup.ReceiverInstructions,
                    _nestedDropFlags[call.Cleanup.FlagLocalIndex],
                    _dropFlagGuards[call.Cleanup.FlagLocalIndex])).ToArray();

            void CollectOwned(SafeCoreType type, SafeCoreMirPlace place, SafeCoreMirSource source, int depth,
                bool skipOuter = false, bool inventory = true)
            {
                owner.Step();
                if (depth > 128) Lowerer.Limit("Generated cleanup type recursion exceeded its bound.");
                if (type.Kind == SafeCoreSemanticTypeKind.Reference) return;
                int? destructor = skipOuter ? null : owner.DestructorFor(type);
                if (destructor is not null)
                {
                    string key = place.ToString();
                    declarationOrder.TryAdd(key, declarationOrdinal++);
                    if (inventory && !byPlace.ContainsKey(key))
                    {
                        int flag = Temporary(ClrLirType.Bool, source);
                        byPlace.Add(key, flag);
                        _dropPlaces.Add((place, flag));
                        var site = owner.CallSite(destructor.Value, ClrLirType.Void, [ClrLirType.Any], source);
                        Block? saved = _current;
                        var reconstruction = new Block("cleanup_receiver");
                        _current = reconstruction;
                        try { EmitPlaceAddress(place, source, mutable: true); }
                        finally { _current = saved; }
                        calls.Add((place, new(site, flag, receiverInstructions: reconstruction.Instructions), calls.Count));
                    }
                    // The outer destructor owns automatic field cleanup. Traverse
                    // only to order projected obligations already emitted for writes.
                    inventory = false;
                }
                if (type.Kind == SafeCoreSemanticTypeKind.Tuple)
                {
                    for (int index = 0; index < type.Elements.Count; index++)
                        CollectOwned(type.Elements[index], place.Append(SafeCoreMirProjection.TupleIndex(index)), source, depth + 1, inventory: inventory);
                }
                else if (type.Kind == SafeCoreSemanticTypeKind.Array && type.ElementType is { } element && type.Length is long length)
                {
                    if (length > ClrLirLimits.MaximumFields) Lowerer.Limit("Generated array cleanup exceeds its field bound.");
                    for (int index = 0; index < length; index++)
                        CollectOwned(element, place.Append(SafeCoreMirProjection.ArrayIndex(index)), source, depth + 1, inventory: inventory);
                }
                else if (owner.DropLayout(type) is { } layout)
                {
                    if (layout.Variants.Count != 0)
                    {
                        for (int index = 0; index < layout.Variants.Count; index++)
                        foreach (SafeCoreMirAdtField field in layout.Variants[index].Fields)
                            CollectOwned(field.Type, place.Append(SafeCoreMirProjection.Downcast(index))
                                .Append(SafeCoreMirProjection.Field(field.Name)), source, depth + 1, inventory: inventory);
                    }
                    else
                        foreach (SafeCoreMirAdtField field in layout.Fields)
                            CollectOwned(field.Type, place.Append(SafeCoreMirProjection.Field(field.Name)), source, depth + 1, inventory: inventory);
                }
            }
        }

        private void EmitNormalCleanupEpilogue(IReadOnlyList<ClrLirGuardedCleanup> cleanupCalls, string finalReturnLabel)
        {
            Start(new Block("normal_cleanup_epilogue"));
            foreach (ClrLirGuardedCleanup cleanup in cleanupCalls)
            {
                owner.Step();
                Block drop = NewBlock();
                Block next = NewBlock();
                Emit(new ClrLirLoadLocal(cleanup.FlagLocalIndex));
                Terminate(new ClrLirBranchTrue(drop.Label));
                Start(NewBlock());
                Terminate(new ClrLirBranch(next.Label));
                Start(drop);
                EmitCleanupGuards(cleanup.GuardInstructions, next.Label);
                ConsumeDropFlag(cleanup.FlagLocalIndex);
                SetNormalCleanupMode(isCleanup: true);
                if (cleanup.HasReceiver)
                {
                    EmitCleanupReceiver(cleanup);
                    Runtime("ConsumeDrop", ClrLirType.Void, ClrLirType.Any);
                }
                EmitCleanupReceiver(cleanup);
                Emit(new ClrLirCall(cleanup.Site));
                SetNormalCleanupMode(isCleanup: function.IsDestructor);
                Terminate(new ClrLirBranch(next.Label));
                Start(next);
            }
            Terminate(new ClrLirLeave(finalReturnLabel));
        }

        private void ConsumeMovedOperand(SafeCoreMirOperand operand, int? destination = null)
        {
            if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place) ||
                operand.Type.Kind == SafeCoreSemanticTypeKind.Reference ||
                operand.Id == destination && (operand.Place is null || operand.Place.IsRoot)) return;
            SafeCoreMirPlace moved = operand.Place ?? SafeCoreMirPlace.Root(operand.Id);
            foreach (var obligation in _dropPlaces)
            {
                owner.Step();
                if (ContainsPlace(moved, obligation.Place)) SetDropFlag(obligation.Flag, live: false);
            }
        }

        private void ConsumeDropFlag(int flag)
        {
            SetDropFlag(flag, live: false);
            foreach (int nested in _nestedDropFlags[flag])
            {
                owner.Step();
                SetDropFlag(nested, live: false);
            }
        }

        private void SetInitializedPlace(SafeCoreMirPlace initialized, SafeCoreMirSource source)
        {
            foreach (var obligation in _dropPlaces)
            {
                owner.Step();
                if (!ContainsPlace(initialized, obligation.Place)) continue;
                // The current tag is tested at cleanup, because a checked
                // mutable call may replace this storage with another variant.
                SetDropFlag(obligation.Flag, live: true);
            }
        }

        private ClrLirInstruction[][] BuildCurrentDropGuards(SafeCoreMirPlace place, SafeCoreMirSource source)
        {
            var guards = new List<ClrLirInstruction[]>();
            SafeCoreType current = function.Locals[place.LocalId].Type;
            SafeCoreMirPlace prefix = SafeCoreMirPlace.Root(place.LocalId);
            SafeCoreMirAdtVariant? variant = null;
            foreach (SafeCoreMirProjection projection in place.Projections)
            {
                owner.Step();
                if (projection.Kind == SafeCoreMirProjectionKind.Dereference)
                {
                    current = current.ElementType!;
                    variant = null;
                }
                else if (projection.Kind == SafeCoreMirProjectionKind.Downcast)
                {
                    variant = owner.Variant(current, projection.Index, source);
                    SafeCoreMirPlace testedPrefix = prefix;
                    int discriminant = variant.Discriminant;
                    guards.Add(Capture(() =>
                    {
                        EmitPlaceAddress(testedPrefix, source, mutable: false);
                        Emit(new ClrLirLoadInt32(discriminant));
                        Runtime("HasVariant", ClrLirType.Bool, ClrLirType.Any, ClrLirType.I32);
                    }));
                }
                else if (current.Kind is SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice)
                    current = current.ElementType!;
                else
                {
                    int index = projection.Index;
                    if (projection.Kind == SafeCoreMirProjectionKind.Field)
                    {
                        index = -1;
                        if (variant is not null)
                        {
                            for (int fieldIndex = 0; fieldIndex < variant.Fields.Count; fieldIndex++)
                            {
                                owner.Step();
                                if (variant.Fields[fieldIndex].Name == projection.Name) { index = fieldIndex; break; }
                            }
                        }
                        else
                        {
                            ClrLirValueType layout = owner.Layout(current, source);
                            for (int fieldIndex = 0; fieldIndex < layout.Fields.Length; fieldIndex++)
                            {
                                owner.Step();
                                if (layout.Fields[fieldIndex].Name == projection.Name) { index = fieldIndex; break; }
                            }
                        }
                    }
                    if (index < 0) Lowerer.Fail(source, "A cleanup guard field does not match its aggregate layout.");
                    if (variant is not null) { index += variant.FieldOffset; variant = null; }
                    current = owner.FieldType(current, index, source);
                }
                prefix = prefix.Append(projection);
            }
            guards.Add(Capture(() =>
            {
                EmitPlaceAddress(place, source, mutable: false);
                Runtime("IsDropLive", ClrLirType.Bool, ClrLirType.Any);
            }));
            return guards.ToArray();

            ClrLirInstruction[] Capture(Action emit)
            {
                Block? saved = _current;
                var guard = new Block("cleanup_guard");
                _current = guard;
                try { emit(); }
                finally { _current = saved; }
                return guard.Instructions.ToArray();
            }
        }

        private void EmitCleanupGuards<TGuard>(IEnumerable<TGuard> guards, string skippedLabel)
            where TGuard : IEnumerable<ClrLirInstruction>
        {
            foreach (TGuard guard in guards)
            {
                owner.Step();
                foreach (ClrLirInstruction instruction in guard) Emit(instruction);
                Block passed = NewBlock();
                Terminate(new ClrLirBranchTrue(passed.Label));
                Start(NewBlock());
                Terminate(new ClrLirBranch(skippedLabel));
                Start(passed);
            }
        }

        private void EmitCleanupReceiver(ClrLirGuardedCleanup cleanup)
        {
            if (cleanup.ReceiverLocalIndex is int receiver) Emit(new ClrLirLoadLocal(receiver));
            foreach (ClrLirInstruction instruction in cleanup.ReceiverInstructions) Emit(instruction);
        }

        private bool IsOwnedCleanupPlace(SafeCoreMirPlace place)
        {
            for (int index = 0; index < place.Projections.Count; index++)
            {
                owner.Step();
                if (place.Projections[index].Kind != SafeCoreMirProjectionKind.Dereference) continue;
                SafeCoreMirLocal root = function.Locals[place.LocalId];
                // A destructor owns fields behind its checked self receiver.
                // A later dereference follows a borrowed field instead.
                if (index != 0 || !function.IsDestructor || root.Kind != SafeCoreMirLocalKind.Parameter ||
                    root.Type is not { Kind: SafeCoreSemanticTypeKind.Reference, IsMutable: true, ElementType.Kind: SafeCoreSemanticTypeKind.Adt })
                    return false;
            }
            return true;
        }

        private void SetNormalCleanupMode(bool isCleanup)
        {
            if (!isCleanup) { SetDropFlag(_normalCleanupLocal, live: false); return; }
            Emit(new ClrLirCall(new("RustGeneratedPanic.IsUnwinding", ClrLirType.Bool, [])
            { ExternalCall = new("RustSharp.Runtime", "RustSharp.Runtime", "RustGeneratedPanic", "IsUnwinding") }));
            Emit(new ClrLirLoadBoolean(true));
            Emit(new ClrLirBinary(ClrLirBinaryOperator.ExclusiveOr, ClrLirType.Bool));
            Emit(new ClrLirStoreLocal(_normalCleanupLocal));
        }

        private static bool ContainsPlace(SafeCoreMirPlace parent, SafeCoreMirPlace child) =>
            parent.LocalId == child.LocalId && parent.Projections.Count <= child.Projections.Count &&
            parent.Projections.SequenceEqual(child.Projections.Take(parent.Projections.Count));
    }
}
