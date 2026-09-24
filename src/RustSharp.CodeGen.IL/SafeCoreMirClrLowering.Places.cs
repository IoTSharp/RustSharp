using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

public static partial class SafeCoreMirClrLowering
{
    private sealed partial class BodyLowerer
    {
        private void EmitStatement(SafeCoreMirStatement statement)
        {
            if (statement.Value.Kind == SafeCoreMirRvalueKind.Write)
            {
                EmitOperand(statement.Value.Operands[0]);
                EmitOperand(statement.Value.Operands[1]);
                Emit(new ClrLirStoreIndirect(owner.StorageType(statement.Value.Operands[1].Type, statement.Source)));
                return;
            }
            if (statement.DestinationPlace is { IsRoot: false } destination)
            {
                EmitPlaceAddress(destination, statement.Source, mutable: true);
                EmitRvalue(statement.Value);
                Emit(new ClrLirStoreIndirect(owner.StorageType(statement.Value.Type, statement.Source)));
                return;
            }
            EmitRvalue(statement.Value);
            Store(statement.DestinationLocalId, statement.Source);
        }

        private void EmitOperandAddress(SafeCoreMirOperand operand, bool mutable)
        {
            if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place))
                Lowerer.FailUnsupported(operand.Source, "A borrow requires a MIR storage place.");
            EmitPlaceAddress(operand.Place ?? SafeCoreMirPlace.Root(operand.Id), operand.Source, mutable);
        }

        private void EmitPlaceAddress(SafeCoreMirPlace place, SafeCoreMirSource source, bool mutable)
        {
            owner.Step();
            SafeCoreType current = function.Locals[place.LocalId].Type;
            int start = 0;
            if (current.Kind == SafeCoreSemanticTypeKind.Reference)
            {
                if (place.Projections.Count == 0 || place.Projections[0].Kind != SafeCoreMirProjectionKind.Dereference)
                    Lowerer.FailUnsupported(source, "Taking the address of a reference slot requires nested managed pointers.");
                Emit(new ClrLirLoadLocal(place.LocalId));
                current = current.ElementType!;
                if (current.Kind == SafeCoreSemanticTypeKind.Slice) current = SliceArray(place.LocalId, source);
                if (!mutable) Emit(new ClrLirReadOnlyReference(owner.StorageType(current, source)));
                start = 1;
            }
            else Emit(new ClrLirLoadLocalAddress(place.LocalId, mutable));

            for (int projectionIndex = start; projectionIndex < place.Projections.Count; projectionIndex++)
            {
                owner.Step();
                SafeCoreMirProjection projection = place.Projections[projectionIndex];
                if (projection.Kind == SafeCoreMirProjectionKind.Dereference)
                    Lowerer.FailUnsupported(source, "Nested reference projections require a CLR byref-like representation.");
                ClrLirValueType layout = owner.Layout(current, source);
                int fieldIndex = projection.Index;
                if (projection.Kind == SafeCoreMirProjectionKind.Field)
                {
                    fieldIndex = -1;
                    for (int index = 0; index < layout.Fields.Length; index++)
                    {
                        owner.Step();
                        if (layout.Fields[index].Name == projection.Name) { fieldIndex = index; break; }
                    }
                    if (fieldIndex < 0) Lowerer.Fail(source, "A named MIR projection does not match its ADT layout.");
                }
                if (projection.Kind == SafeCoreMirProjectionKind.DynamicIndex)
                {
                    Emit(new ClrLirLoadLocal(projection.Index));
                    EmitArrayElementAddress(layout, owner.StorageType(current.ElementType!, source), mutable, source);
                    current = current.ElementType!;
                }
                else
                {
                    Emit(new ClrLirFieldAddress(layout, fieldIndex, mutable));
                    current = owner.FieldType(current, fieldIndex, source);
                }
            }
        }

        // Fixed arrays use sequential fields. A bounded decision chain selects
        // one field address, retaining the real owner through subsequent writes.
        // The index is captured once even when the place appears in a reborrow.
        private void EmitArrayElementAddress(ClrLirValueType layout, ClrLirType elementType, bool mutable, SafeCoreMirSource source)
        {
            owner.Step();
            int indexLocal = Temporary(ClrLirType.I32, source);
            Emit(new ClrLirStoreLocal(indexLocal));
            int ownerLocal = Temporary(ClrLirType.ByReference(layout.Type, mutable), source);
            Emit(new ClrLirStoreLocal(ownerLocal));
            if (layout.Fields.Length == 0)
            {
                // A typed helper has no return instruction: its byref signature
                // permits the surrounding expression while its body always traps.
                Emit(new ClrLirCall(owner.BoundsFailure(elementType, mutable, source)));
                return;
            }
            Block join = NewBlock();
            var cases = new List<Block>(layout.Fields.Length);
            for (int index = 0; index < layout.Fields.Length; index++)
            {
                owner.Step();
                Block selected = NewBlock();
                cases.Add(selected);
                Emit(new ClrLirLoadLocal(indexLocal));
                Emit(new ClrLirLoadInt32(index));
                Emit(new ClrLirBinary(ClrLirBinaryOperator.Equal, ClrLirType.I32));
                Terminate(new ClrLirBranchTrue(selected.Label));
                Start(NewBlock());
            }
            Terminate(new ClrLirThrowIndexOutOfRange());
            for (int index = 0; index < cases.Count; index++)
            {
                owner.Step();
                Start(cases[index]);
                Emit(new ClrLirLoadLocal(ownerLocal));
                Emit(new ClrLirFieldAddress(layout, index, mutable));
                Terminate(new ClrLirBranch(join.Label));
            }
            Start(join);
        }

        private int Temporary(ClrLirType type, SafeCoreMirSource source)
        {
            owner.Step();
            if (_locals.Count >= ClrLirLimits.MaximumLocals)
                Lowerer.Limit("MIR projection execution exceeds the CLR local budget.");
            int id = _locals.Count;
            _locals.Add(new("projection_" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), type));
            return id;
        }

        private SafeCoreType SliceArray(int local, SafeCoreMirSource source)
        {
            if (_sliceArrays.TryGetValue(local, out SafeCoreType? array)) return array;
            Lowerer.FailUnsupported(source, "Slice execution requires a proven full fixed-array layout; a general slice ABI is not implemented.");
            return null!;
        }

        private void CollectSliceLayouts()
        {
            // This map carries only slice ABI layout information, never owner
            // identity. The runtime managed pointer carries the selected owner.
            for (int pass = 0; pass <= function.Locals.Count; pass++)
            {
                owner.Step();
                bool changed = false;
                foreach (SafeCoreMirBlock block in function.Blocks)
                foreach (SafeCoreMirStatement statement in block.Statements)
                {
                    owner.Step();
                    SafeCoreMirRvalue value = statement.Value;
                    if (value.Type.Kind != SafeCoreSemanticTypeKind.Reference ||
                        value.Type.ElementType?.Kind != SafeCoreSemanticTypeKind.Slice || value.Operands.Count != 1) continue;
                    SafeCoreMirOperand origin = value.Operands[0];
                    SafeCoreType candidate = origin.Type.Kind == SafeCoreSemanticTypeKind.Reference ? origin.Type.ElementType! : origin.Type;
                    if (candidate.Kind == SafeCoreSemanticTypeKind.Slice && _sliceArrays.TryGetValue(origin.Id, out SafeCoreType? prior)) candidate = prior;
                    if (candidate.Kind != SafeCoreSemanticTypeKind.Array) continue;
                    if (_sliceArrays.TryGetValue(statement.DestinationLocalId, out SafeCoreType? previous))
                    {
                        if (previous != candidate) Lowerer.FailUnsupported(value.Source, "Slice joins require the same fixed-array layout on every edge.");
                    }
                    else { _sliceArrays.Add(statement.DestinationLocalId, candidate); changed = true; }
                }
                if (!changed) break;
            }
        }
    }
}
