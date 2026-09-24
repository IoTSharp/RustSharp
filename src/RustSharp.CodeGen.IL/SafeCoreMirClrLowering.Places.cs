using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

public static partial class SafeCoreMirClrLowering
{
    private sealed partial class BodyLowerer
    {
        private void Runtime(string name, ClrLirType result, params ClrLirType[] parameters) =>
            Emit(new ClrLirCall(new("MirReference." + name, result, parameters)
            {
                ExternalCall = new("RustSharp.Runtime", "RustSharp.Runtime", "MirReference", name),
            }));

        private void ReadReference(ClrLirType type)
        {
            Runtime("Read", ClrLirType.Any, ClrLirType.Any);
            Emit(new ClrLirUnbox(type));
        }

        private void ReadSemanticReference(SafeCoreType type, SafeCoreMirSource source)
        {
            ClrLirType storage = owner.StorageType(type, source);
            if (type.Kind != SafeCoreSemanticTypeKind.Array)
            {
                ReadReference(storage);
                return;
            }
            // Borrowed array rest patterns retain &[T; N], while their owner is
            // a checked window into a larger array. Typed accessors reconstruct
            // the exact value layout when the entire view is dereferenced.
            EmitDefault(type, source);
            Emit(new ClrLirBox(storage));
            Runtime("ReadArray", ClrLirType.Any, ClrLirType.Any, ClrLirType.Any);
            Emit(new ClrLirUnbox(storage));
        }

        private void WriteReference(ClrLirType type)
        {
            Emit(new ClrLirBox(type));
            Runtime("Write", ClrLirType.Void, ClrLirType.Any, ClrLirType.Any);
        }

        private void Load(int id, SafeCoreMirSource source)
        {
            if (!_localTypes.TryGetValue(id, out ClrLirType type))
                Lowerer.Fail(source, "MIR local operand is outside its local arena.");
            Emit(new ClrLirLoadLocal(id));
            ReadReference(type);
        }

        private void EmitStatement(SafeCoreMirStatement statement)
        {
            if (statement.Value.Kind == SafeCoreMirRvalueKind.Write)
            {
                EmitOperand(statement.Value.Operands[0]);
                EmitOperand(statement.Value.Operands[1]);
                WriteReference(owner.StorageType(statement.Value.Operands[1].Type, statement.Source));
                return;
            }
            if (statement.DestinationPlace is { IsRoot: false } destination)
            {
                EmitPlaceAddress(destination, statement.Source, mutable: true);
                EmitRvalue(statement.Value);
                WriteReference(owner.StorageType(statement.Value.Type, statement.Source));
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
            Emit(new ClrLirLoadLocal(place.LocalId));
            int variant = -1;
            foreach (SafeCoreMirProjection projection in place.Projections)
            {
                owner.Step();
                if (projection.Kind == SafeCoreMirProjectionKind.Dereference)
                {
                    ReadReference(owner.StorageType(current, source));
                    current = current.ElementType!;
                    variant = -1;
                    continue;
                }
                if (projection.Kind == SafeCoreMirProjectionKind.Downcast)
                {
                    variant = projection.Index;
                    continue;
                }
                if (current.Kind is SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice)
                {
                    if (projection.Kind == SafeCoreMirProjectionKind.FromEndIndex)
                    {
                        Emit(new ClrLirLoadInt32(projection.Index));
                        Emit(new ClrLirLoadInt32(projection.MinimumLength));
                        Emit(new ClrLirLoadInt32(current.Kind == SafeCoreSemanticTypeKind.Array
                            ? checked((int)current.Length!.Value) : -1));
                        Runtime("FromEndIndex", ClrLirType.Any, ClrLirType.Any, ClrLirType.I32, ClrLirType.I32, ClrLirType.I32);
                        current = current.ElementType!;
                        continue;
                    }
                    if (projection.Kind == SafeCoreMirProjectionKind.DynamicIndex) Load(projection.Index, source);
                    else Emit(new ClrLirLoadInt32(projection.Index));
                    if (current.Kind == SafeCoreSemanticTypeKind.Slice)
                        Runtime("SliceIndex", ClrLirType.Any, ClrLirType.Any, ClrLirType.I32);
                    else
                    {
                        Emit(new ClrLirLoadInt32(checked((int)current.Length!.Value)));
                        Runtime("ArrayIndex", ClrLirType.Any, ClrLirType.Any, ClrLirType.I32, ClrLirType.I32);
                    }
                    current = current.ElementType!;
                    continue;
                }
                ClrLirValueType layout = owner.Layout(current, source);
                int fieldIndex = projection.Index;
                if (variant >= 0)
                {
                    SafeCoreMirAdtVariant selected = owner.Variant(current, variant, source);
                    if (projection.Kind == SafeCoreMirProjectionKind.Field)
                    {
                        fieldIndex = -1;
                        for (int index = 0; index < selected.Fields.Count; index++)
                        {
                            owner.Step();
                            if (selected.Fields[index].Name == projection.Name) { fieldIndex = index; break; }
                        }
                    }
                    if (fieldIndex < 0 || fieldIndex >= selected.Fields.Count)
                        Lowerer.Fail(source, "An enum projection does not match its selected variant.");
                    fieldIndex += selected.FieldOffset;
                    variant = -1;
                }
                else if (projection.Kind == SafeCoreMirProjectionKind.Field)
                {
                    fieldIndex = -1;
                    for (int index = 0; index < layout.Fields.Length; index++)
                    {
                        owner.Step();
                        if (layout.Fields[index].Name == projection.Name) { fieldIndex = index; break; }
                    }
                }
                if (fieldIndex < 0 || fieldIndex >= layout.Fields.Length)
                    Lowerer.Fail(source, "A MIR projection does not match its aggregate layout.");
                Emit(new ClrLirLoadInt32(fieldIndex));
                Runtime("Field", ClrLirType.Any, ClrLirType.Any, ClrLirType.I32);
                current = owner.FieldType(current, fieldIndex, source);
            }
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

        private void EmitDefault(SafeCoreType type, SafeCoreMirSource source)
        {
            owner.Step();
            ClrLirType storage = owner.StorageType(type, source);
            if (storage == ClrLirType.Any) Emit(new ClrLirLoadNull());
            else if (storage == ClrLirType.Bool) Emit(new ClrLirLoadBoolean(false));
            else if (storage == ClrLirType.I32) Emit(new ClrLirLoadInt32(0));
            else
            {
                ClrLirValueType layout = owner.Layout(type, source);
                for (int index = 0; index < layout.Fields.Length; index++)
                    EmitDefault(owner.FieldType(type, index, source), source);
                Emit(new ClrLirConstructValue(layout));
            }
        }
    }
}
