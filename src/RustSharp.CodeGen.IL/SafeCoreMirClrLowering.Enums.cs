using System.Globalization;
using RustSharp.Semantics;

namespace RustSharp.CodeGen.IL;

public static partial class SafeCoreMirClrLowering
{
    private sealed partial class Lowerer
    {
        internal SafeCoreMirAdtVariant Variant(SafeCoreType type, int index, SafeCoreMirSource source)
        {
            Step();
            SafeCoreMirAdtLayout? layout = program.AdtLayouts.FirstOrDefault(item => item.Type == type);
            if (layout is null || index < 0 || index >= layout.Variants.Count)
                Fail(source, "An enum downcast requires declared variant layout evidence.");
            return layout.Variants[index];
        }
    }

    private sealed partial class BodyLowerer
    {
        private void EmitEnum(SafeCoreMirRvalue value)
        {
            int variantIndex = int.Parse(value.Operator!, NumberStyles.None, CultureInfo.InvariantCulture);
            SafeCoreMirAdtVariant variant = owner.Variant(value.Type, variantIndex, value.Source);
            ClrLirValueType layout = owner.Layout(value.Type, value.Source);
            Emit(new ClrLirLoadInt32(variant.Discriminant));
            for (int index = 1; index < layout.Fields.Length; index++)
            {
                owner.Step();
                int operandIndex = index - variant.FieldOffset;
                if (operandIndex >= 0 && operandIndex < value.Operands.Count)
                {
                    EmitOperand(value.Operands[operandIndex]);
                    ConsumeMovedOperand(value.Operands[operandIndex]);
                }
                else EmitDefault(owner.FieldType(value.Type, index, value.Source), value.Source);
            }
            Emit(new ClrLirConstructValue(layout));
        }

        private void EmitDiscriminant(SafeCoreMirRvalue value)
        {
            EmitOperand(value.Operands[0]);
            Emit(new ClrLirReadField(owner.Layout(value.Operands[0].Type, value.Source), 0));
        }

        private void EmitCleanupDiscriminant(SafeCoreMirStatement statement)
        {
            SafeCoreMirOperand receiver = statement.Value.Operands[0];
            SafeCoreMirPlace place = receiver.Place ?? SafeCoreMirPlace.Root(receiver.Id);
            SafeCoreMirAdtLayout? layout = owner.DropLayout(receiver.Type);
            if (layout is null || layout.Variants.Count == 0)
                Lowerer.Fail(statement.Source, "A checked enum cleanup discriminator requires declared variants.");
            var discriminants = layout.Variants.Select(static variant => variant.Discriminant).ToHashSet();
            int sentinel = int.MinValue;
            for (int index = 0; index <= layout.Variants.Count; index++)
            {
                owner.Step();
                sentinel = int.MinValue + index;
                if (!discriminants.Contains(sentinel)) break;
            }
            int[] liveFlags = _dropPlaces.Where(drop => ContainsPlace(place, drop.Place))
                .Select(static drop => drop.Flag).ToArray();
            if (liveFlags.Length == 0)
            {
                Emit(new ClrLirLoadInt32(sentinel));
                Store(statement.DestinationLocalId, statement.Source);
                return;
            }

            // A moved enum still has its tag in storage; an uninitialized enum
            // does not. Only checked cleanup may test surviving obligations
            // before reading that tag, without creating a whole-value use.
            Emit(new ClrLirLoadBoolean(false));
            foreach (int flag in liveFlags)
            {
                owner.Step();
                Emit(new ClrLirLoadLocal(flag));
                Emit(new ClrLirBinary(ClrLirBinaryOperator.Or, ClrLirType.Bool));
            }
            Block initialized = NewBlock();
            Block next = NewBlock();
            Terminate(new ClrLirBranchTrue(initialized.Label));
            Start(NewBlock());
            Emit(new ClrLirLoadInt32(sentinel));
            Store(statement.DestinationLocalId, statement.Source);
            Terminate(new ClrLirBranch(next.Label));
            Start(initialized);
            EmitDiscriminant(statement.Value);
            Store(statement.DestinationLocalId, statement.Source);
            Terminate(new ClrLirBranch(next.Label));
            Start(next);
        }
    }
}
