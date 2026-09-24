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
                if (operandIndex >= 0 && operandIndex < value.Operands.Count) EmitOperand(value.Operands[operandIndex]);
                else EmitDefault(owner.FieldType(value.Type, index, value.Source), value.Source);
            }
            Emit(new ClrLirConstructValue(layout));
        }

        private void EmitDiscriminant(SafeCoreMirRvalue value)
        {
            EmitOperand(value.Operands[0]);
            Emit(new ClrLirReadField(owner.Layout(value.Operands[0].Type, value.Source), 0));
        }
    }
}
