using System.Globalization;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private readonly Dictionary<string, (SafeCoreMirAdtLayout Layout, int Index)> _enumVariants = new(StringComparer.Ordinal);

        private void CollectEnumLayout(SafeCoreHirNode node)
        {
            SafeCoreType type = Type(node);
            var fields = new List<SafeCoreMirAdtField> { new("$tag", SafeCoreType.Primitive(K.I32), Source(node)) };
            var variants = new List<SafeCoreMirAdtVariant>();
            var tags = new HashSet<int>();
            long nextTag = 0;
            for (int childIndex = 0; childIndex < node.ChildIds.Count; childIndex++)
            {
                SafeCoreHirNode variant = Child(node, childIndex);
                Step(variant, 1);
                if (variant.Kind == N.Attribute) continue;
                if (variant.Kind != N.EnumVariant || variant.DeclaredSymbol is null) Unsupported(variant);
                var payload = new List<SafeCoreMirAdtField>();
                long tag = nextTag;
                for (int fieldIndex = 0; fieldIndex < variant.ChildIds.Count; fieldIndex++)
                {
                    SafeCoreHirNode field = Child(variant, fieldIndex);
                    Step(field, 2);
                    if (field.Kind == N.Attribute) continue;
                    if (field.Kind != N.Field)
                    {
                        if (!input.Constants.TryGetValue(field.Id, out SafeCoreEvaluatedConstant? evaluated) ||
                            !long.TryParse(evaluated.Scalar, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out tag)) Unsupported(field);
                        continue;
                    }
                    string name = field.Name is { } named ? CanonicalField(named) : payload.Count.ToString(CultureInfo.InvariantCulture);
                    payload.Add(new(name, Type(field), Source(field)) { RequiresStaticLifetime = Type(field).Kind == K.Reference });
                }
                if (tag < int.MinValue || tag > int.MaxValue || !tags.Add((int)tag)) Invalid(variant);
                int ordinal = variants.Count;
                variants.Add(new(SymbolKey(variant.DeclaredSymbol!), (int)tag, fields.Count, payload, Source(variant), cancellation));
                for (int fieldIndex = 0; fieldIndex < payload.Count; fieldIndex++)
                {
                    Step(variant, 2);
                    SafeCoreMirAdtField field = payload[fieldIndex];
                    fields.Add(field with { Name = "$v" + ordinal.ToString(CultureInfo.InvariantCulture) + "$" + field.Name });
                }
                nextTag = tag + 1;
            }
            if (type.Kind != K.Adt || type.Name is null || variants.Count == 0) Unsupported(node);
            var layout = new SafeCoreMirAdtLayout(type, fields, variants, Source(node), cancellationToken: cancellation);
            if (!_adtLayouts.TryAdd(type.Name!, layout)) Invalid(node);
            for (int index = 0; index < variants.Count; index++)
            {
                Step(node, 0);
                if (!_enumVariants.TryAdd(variants[index].Name, (layout, index))) Invalid(node);
            }
        }

        private bool TryEnumVariant(SafeCoreHirNode node, out SafeCoreMirAdtLayout layout, out int index)
        {
            if (node.ReferencedSymbol is { } symbol && _enumVariants.TryGetValue(SymbolKey(symbol), out var found))
            { layout = found.Layout; index = found.Index; return true; }
            layout = null!; index = -1; return false;
        }

        private SafeCoreMirOperand? ConstructEnum(SafeCoreHirNode node, SafeCoreMirAdtLayout layout, int variantIndex, int depth)
        {
            ValueType(layout.Type, node);
            SafeCoreMirAdtVariant variant = layout.Variants[variantIndex];
            var operands = new SafeCoreMirOperand[variant.Fields.Count];
            int first = node.Kind == N.CallExpression ? 1 : 0;
            for (int index = first; index < node.ChildIds.Count; index++)
            {
                SafeCoreHirNode child = Child(node, index);
                Step(child, depth);
                int ordinal = index - first;
                if (node.Kind == N.StructExpression)
                {
                    if (child.Kind != N.StructExpressionField) Unsupported(child);
                    ordinal = EnumFieldIndex(variant, child.Name!, child);
                    child = Child(child, 0);
                }
                if (ordinal < 0 || ordinal >= operands.Length) Invalid(node);
                SafeCoreMirOperand? operand = Expr(child, depth + 1);
                if (_current is null || operand is null) return null;
                operands[ordinal] = SnapshotOperand(operand, child);
            }
            for (int index = 0; index < operands.Length; index++)
            {
                Step(node, depth);
                if (operands[index] is null) Invalid(node);
            }
            return Emit(SafeCoreMirRvalue.Enum(variantIndex, operands, layout.Type, Source(node), cancellation), layout.Type, node);
        }

        private int EnumFieldIndex(SafeCoreMirAdtVariant variant, string name, SafeCoreHirNode node)
        {
            string canonical = CanonicalField(name);
            for (int index = 0; index < variant.Fields.Count; index++)
            {
                Step(node, 0);
                if (variant.Fields[index].Name == canonical) return index;
            }
            Invalid(node); return -1;
        }

        private SafeCoreMirOperand PatternValue(SafeCoreMirOperand value, SafeCoreHirNode pattern, int depth)
        {
            for (int index = 0; value.Type.Kind == K.Reference && index < options.MaximumNestingDepth; index++)
            {
                Step(pattern, depth + index);
                SafeCoreMirPlace place = value.Place ?? SafeCoreMirPlace.Root(value.Id);
                value = SafeCoreMirOperand.PlaceValue(ProjectPlace(place, SafeCoreMirProjection.Dereference(), pattern), value.Type.ElementType, Source(pattern));
            }
            if (value.Type.Kind == K.Reference) Limit(pattern);
            return value;
        }

        private SafeCoreMirOperand EnumPatternField(SafeCoreMirOperand value, int variantIndex,
            SafeCoreMirAdtField field, SafeCoreHirNode node)
        {
            SafeCoreMirPlace place = value.Place ?? SafeCoreMirPlace.Root(value.Id);
            place = ProjectPlace(place, SafeCoreMirProjection.Downcast(variantIndex), node);
            return PatternField(SafeCoreMirOperand.PlaceValue(place, value.Type, Source(node)),
                SafeCoreMirProjection.Field(field.Name), field.Type, node);
        }

        private void BindEnumPattern(SafeCoreHirNode pattern, SafeCoreMirOperand value,
            SafeCoreMirAdtLayout layout, int variantIndex, int depth)
        {
            value = PatternValue(value, pattern, depth);
            SafeCoreMirAdtVariant variant = layout.Variants[variantIndex];
            int position = 0;
            for (int index = 0; index < pattern.ChildIds.Count; index++)
            {
                SafeCoreHirNode child = Child(pattern, index);
                Step(child, depth);
                if (child.Kind == N.RestPattern)
                { position = variant.Fields.Count - (pattern.ChildIds.Count - index - 1); continue; }
                int fieldIndex = pattern.Kind == N.StructPattern ? EnumFieldIndex(variant, child.Name!, child) : position++;
                if (fieldIndex < 0 || fieldIndex >= variant.Fields.Count) Invalid(child);
                if (child.Kind == N.StructPatternField) child = Child(child, 0);
                BindPatternLocals(child, EnumPatternField(value, variantIndex, variant.Fields[fieldIndex], child), depth + 1);
            }
        }

        private SafeCoreMirOperand EnumPatternCondition(SafeCoreHirNode pattern, SafeCoreMirOperand value,
            SafeCoreMirAdtLayout layout, int variantIndex, int depth)
        {
            value = PatternValue(value, pattern, depth);
            SafeCoreType boolType = SafeCoreType.Primitive(K.Bool);
            SafeCoreMirAdtVariant variant = layout.Variants[variantIndex];
            SafeCoreMirOperand tag = Emit(SafeCoreMirRvalue.Discriminant(value, Source(pattern)), SafeCoreType.Primitive(K.I32), pattern);
            SafeCoreMirOperand equal = Emit(SafeCoreMirRvalue.Binary("==", tag,
                SafeCoreMirOperand.Constant(tag.Type, variant.Discriminant.ToString(CultureInfo.InvariantCulture), Source(pattern)), boolType, Source(pattern)), boolType, pattern);
            if (pattern.ChildIds.Count == 0) return equal;
            // Payload tests execute only on the matching tag edge. Inactive
            // enum fields are representation details, never Rust values.
            int result = Temp(boolType, pattern);
            BlockBuilder payload = Block(pattern);
            BlockBuilder mismatch = Block(pattern);
            BlockBuilder join = Block(pattern);
            End(SafeCoreMirTerminator.Branch(equal, payload.Id, mismatch.Id, Source(pattern)));
            _current = mismatch;
            Assign(result, SafeCoreMirOperand.Constant(boolType, "false", Source(pattern)), pattern);
            End(SafeCoreMirTerminator.Goto(join.Id, Source(pattern)));
            _current = payload;
            SafeCoreMirOperand condition = SafeCoreMirOperand.Constant(boolType, "true", Source(pattern));
            int position = 0;
            for (int index = 0; index < pattern.ChildIds.Count; index++)
            {
                SafeCoreHirNode child = Child(pattern, index);
                Step(child, depth);
                if (child.Kind == N.RestPattern)
                { position = variant.Fields.Count - (pattern.ChildIds.Count - index - 1); continue; }
                int fieldIndex = pattern.Kind == N.StructPattern ? EnumFieldIndex(variant, child.Name!, child) : position++;
                if (fieldIndex < 0 || fieldIndex >= variant.Fields.Count) Invalid(child);
                if (child.Kind == N.StructPatternField) child = Child(child, 0);
                SafeCoreMirOperand field = EnumPatternField(value, variantIndex, variant.Fields[fieldIndex], child);
                SafeCoreMirOperand test = PatternCondition(child, field, depth + 1);
                condition = CombineCondition(condition, test, and: true, pattern, boolType, allowDynamic: true);
            }
            Assign(result, condition, pattern);
            End(SafeCoreMirTerminator.Goto(join.Id, Source(pattern)));
            _current = join;
            return SafeCoreMirOperand.Local(result, boolType, Source(pattern));
        }

        private void BindPatternValue(SafeCoreHirNode pattern, SafeCoreMirOperand value)
        {
            if (_guardBindingPreview) { BindGuardPatternValue(pattern, value); return; }
            SafeCoreType bindingType = Type(pattern);
            if (bindingType.Kind == K.Reference && bindingType != value.Type && bindingType.ElementType == value.Type)
                value = Emit(SafeCoreMirRvalue.Unary(bindingType.IsMutable ? "&mut" : "&", value, bindingType, Source(pattern)), bindingType, pattern);
            int local;
            if (_patternDestinations is not null && _patternDestinations.TryGetValue(pattern.DeclaredSymbol!, out int existing)) local = existing;
            else
            {
                local = Local(pattern.Name ?? "match", value.Type, SafeCoreMirLocalKind.User,
                    pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                _patternDestinations?.Add(pattern.DeclaredSymbol!, local);
            }
            _bindings[pattern.DeclaredSymbol!] = local;
            Assign(local, value, pattern);
        }
    }
}
