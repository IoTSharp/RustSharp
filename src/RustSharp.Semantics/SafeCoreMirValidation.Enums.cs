using System.Globalization;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirValidation
{
    private sealed partial class Validator
    {
        private void ValidateEnumLayout(SafeCoreMirAdtLayout layout)
        {
            Limit(layout.Variants.Count, 4_096, 4_096);
            if (layout.Variants.Count == 0) return;
            bool valid = layout.Fields.Count > 0 && layout.Fields[0].Name == "$tag" &&
                layout.Fields[0].Type.Kind == SafeCoreSemanticTypeKind.I32;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var tags = new HashSet<int>();
            int offset = 1;
            for (int variantIndex = 0; variantIndex < layout.Variants.Count; variantIndex++)
            {
                Step();
                SafeCoreMirAdtVariant variant = layout.Variants[variantIndex];
                Source(variant.Source);
                Limit(variant.Fields.Count, 4_096, 4_096);
                valid &= !string.IsNullOrWhiteSpace(variant.Name) && variant.Name.Length <= 4_096 &&
                    names.Add(variant.Name) && tags.Add(variant.Discriminant) && variant.FieldOffset == offset;
                var fieldNames = new HashSet<string>(StringComparer.Ordinal);
                for (int fieldIndex = 0; fieldIndex < variant.Fields.Count; fieldIndex++)
                {
                    Step();
                    SafeCoreMirAdtField field = variant.Fields[fieldIndex];
                    Source(field.Source);
                    Type(field.Type);
                    valid &= !string.IsNullOrWhiteSpace(field.Name) && field.Name.Length <= 4_096 && fieldNames.Add(field.Name);
                    valid &= offset < layout.Fields.Count && layout.Fields[offset].Type == field.Type &&
                        layout.Fields[offset].RequiresStaticLifetime == field.RequiresStaticLifetime &&
                        layout.Fields[offset].Name == "$v" + variantIndex.ToString(CultureInfo.InvariantCulture) + "$" + field.Name;
                    offset++;
                }
            }
            if (!valid || offset != layout.Fields.Count)
                Error(SafeCoreMirDiagnosticCodes.InvalidInput, "Enum tags, variant names and flattened payload offsets must match their declaration layout.", layout.Source);
        }

        private int FindVariantField(SafeCoreMirAdtVariant variant, string name)
        {
            for (int index = 0; index < variant.Fields.Count; index++)
            {
                Step();
                if (variant.Fields[index].Name == name) return index;
            }
            return -1;
        }

        private SafeCoreMirAdtVariant? EnumVariant(SafeCoreMirRvalue value)
        {
            if (value.Type.Kind != SafeCoreSemanticTypeKind.Adt ||
                !_adtLayouts.TryGetValue(value.Type.Name!, out SafeCoreMirAdtLayout? layout) ||
                !int.TryParse(value.Operator, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                value.Operator != index.ToString(CultureInfo.InvariantCulture) || index < 0 || index >= layout.Variants.Count) return null;
            return layout.Variants[index];
        }

        private bool EnumValue(SafeCoreMirRvalue value)
        {
            SafeCoreMirAdtVariant? variant = EnumVariant(value);
            if (variant is null || variant.Fields.Count != value.Operands.Count) return false;
            for (int index = 0; index < variant.Fields.Count; index++)
            {
                Step();
                if (variant.Fields[index].Type != value.Operands[index].Type) return false;
            }
            return true;
        }

        private static bool Subslice(SafeCoreMirRvalue value)
        {
            SafeCoreType source = value.Operands[0].Type;
            SafeCoreType result = value.Type;
            bool valid = value.Operator is ".." or "..=" or "pattern" && source.Kind == SafeCoreSemanticTypeKind.Reference &&
                source.ElementType.Kind is SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice &&
                result.Kind == SafeCoreSemanticTypeKind.Reference && result.ElementType.Kind is SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice &&
                (!result.IsMutable || source.IsMutable) && source.ElementType.ElementType == result.ElementType.ElementType &&
                value.Operands[1].Type.Kind == SafeCoreSemanticTypeKind.Usize && value.Operands[2].Type.Kind == SafeCoreSemanticTypeKind.Usize;
            if (!valid) return false;
            if (value.Operator == "pattern")
            {
                if (value.Operands[1].Kind != SafeCoreMirOperandKind.Constant || value.Operands[2].Kind != SafeCoreMirOperandKind.Constant ||
                    !int.TryParse(value.Operands[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) ||
                    !int.TryParse(value.Operands[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int suffix)) return false;
                if (source.ElementType.Kind == SafeCoreSemanticTypeKind.Array && (long)prefix + suffix > source.ElementType.Length) return false;
                return result.ElementType.Kind == SafeCoreSemanticTypeKind.Slice ||
                    source.ElementType.Kind == SafeCoreSemanticTypeKind.Array && result.ElementType.Length.HasValue &&
                    source.ElementType.Length!.Value - prefix - suffix == result.ElementType.Length.Value;
            }
            if (result.ElementType.Kind == SafeCoreSemanticTypeKind.Slice) return true;
            return value.Operands[1].Kind == SafeCoreMirOperandKind.Constant && value.Operands[2].Kind == SafeCoreMirOperandKind.Constant &&
                long.TryParse(value.Operands[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long start) &&
                long.TryParse(value.Operands[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long end) &&
                start <= end && end <= int.MaxValue && end - start + (value.Operator == "..=" ? 1 : 0) == result.ElementType.Length;
        }

        private bool PromotedBorrow(SafeCoreMirRvalue value) =>
            value.Type.Kind == SafeCoreSemanticTypeKind.Reference && !value.Type.IsMutable &&
            value.Type.ElementType == value.Operands[0].Type &&
            IsClosedPromotion(value.Operands[0], [], 0);

        private bool IsClosedPromotion(SafeCoreMirOperand operand, HashSet<int> active, int depth)
        {
            Step();
            if (depth > options.MaximumTypeDepth) throw new SafeCoreMirLimitException("Constant promotion exceeds its nesting budget.");
            if (operand.Kind == SafeCoreMirOperandKind.Constant) return Constant(operand.Type, operand.Value);
            if (operand.Kind != SafeCoreMirOperandKind.Local || operand.Id < 0 || operand.Id >= _function!.Locals.Count) return false;
            SafeCoreMirLocal local = _function.Locals[operand.Id];
            if (!local.IsPromotedConstant || local.IsMutable || local.Kind != SafeCoreMirLocalKind.Temporary || !active.Add(local.Id)) return false;
            try
            {
                SafeCoreMirRvalue? definition = null;
                foreach (SafeCoreMirBlock block in _function.Blocks)
                {
                    Step();
                    if (block.Terminator.DestinationLocalId == local.Id) return false;
                    foreach (SafeCoreMirStatement statement in block.Statements)
                    {
                        Step();
                        if (statement.DestinationLocalId != local.Id) continue;
                        if (definition is not null || statement.DestinationPlace is { IsRoot: false }) return false;
                        definition = statement.Value;
                    }
                }
                if (definition is null || definition.Kind is not (SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Tuple or
                    SafeCoreMirRvalueKind.Array or SafeCoreMirRvalueKind.Adt or SafeCoreMirRvalueKind.Enum or SafeCoreMirRvalueKind.PromotedBorrow or SafeCoreMirRvalueKind.Coerce)) return false;
                foreach (SafeCoreMirOperand input in definition.Operands)
                {
                    Step();
                    if (!IsClosedPromotion(input, active, depth + 1)) return false;
                }
                return true;
            }
            finally { active.Remove(local.Id); }
        }
    }
}
