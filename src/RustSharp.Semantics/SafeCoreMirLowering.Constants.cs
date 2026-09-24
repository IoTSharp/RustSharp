using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private readonly HashSet<int> _runtimeConstFunctions = [];

        private void FindRuntimeConstFunctions()
        {
            var functions = new Dictionary<string, SafeCoreHirNode>(StringComparer.Ordinal);
            for (int index = 0; index < input.Hir.Nodes.Count; index++)
            {
                SafeCoreHirNode node = input.Hir.Nodes[index];
                Step(node, 0);
                if (node.Kind == SafeCoreHirNodeKind.Function && node.DeclaredSymbol is { } symbol)
                    functions.Add(SymbolKey(symbol), node);
            }
            foreach (SafeCoreHirNode function in functions.Values)
            {
                Step(function, 0);
                if (!function.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ConstFunction) ||
                    function.DeclaredSymbol!.IsPublic || function.DeclaredSymbol.Name == "main")
                {
                    _runtimeConstFunctions.Add(function.Id);
                    FindRuntimeConstUses(Child(function, function.ChildIds.Count - 1), functions, 0);
                }
            }
        }

        private void FindRuntimeConstUses(SafeCoreHirNode node, IReadOnlyDictionary<string, SafeCoreHirNode> functions, int depth)
        {
            Step(node, depth);
            if (input.Constants.ContainsKey(node.Id) || input.Promotions.ContainsKey(node.Id)) return;
            if (node.Kind == SafeCoreHirNodeKind.NameExpression && node.ReferencedSymbol is { } symbol &&
                functions.TryGetValue(SymbolKey(symbol), out SafeCoreHirNode? function) &&
                function.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ConstFunction) && _runtimeConstFunctions.Add(function.Id))
                FindRuntimeConstUses(Child(function, function.ChildIds.Count - 1), functions, depth + 1);
            for (int index = 0; index < node.ChildIds.Count; index++)
                FindRuntimeConstUses(Child(node, index), functions, depth + 1);
        }

        private bool CanPromoteConstant(SafeCoreEvaluatedConstant value, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (value.Type.Kind == K.Reference && value.Type.IsMutable ||
                value.Type.Name is { } name && _dropFunctions.ContainsKey(name)) return false;
            for (int index = 0; index < value.Elements.Count; index++)
                if (!CanPromoteConstant(value.Elements[index], node, depth + 1)) return false;
            foreach (SafeCoreEvaluatedConstant field in value.Fields.Values)
                if (!CanPromoteConstant(field, node, depth + 1)) return false;
            return true;
        }

        private SafeCoreMirOperand MaterializeConstant(SafeCoreEvaluatedConstant value, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            ValueType(value.Type, node);
            if (value.Type.Kind == K.Reference)
            {
                if (value.Type.IsMutable || value.Elements.Count != 1 || !CanPromoteConstant(value, node, depth + 1)) Unsupported(node);
                SafeCoreMirOperand referent = MaterializeConstant(value.Elements[0], node, depth + 1);
                SafeCoreType referenceType = SafeCoreType.Reference(referent.Type, false, cancellation);
                SafeCoreMirOperand reference = EmitConstant(
                    SafeCoreMirRvalue.PromotedBorrow(referent, referenceType, Source(node)), node);
                return referenceType == value.Type ? reference :
                    EmitConstant(SafeCoreMirRvalue.Coerce(reference, value.Type, Source(node)), node);
            }
            if (value.Type.Kind == K.Unit || value.Scalar is not null)
                return SafeCoreMirOperand.Constant(value.Type, value.Scalar ?? "()", Source(node));
            IReadOnlyList<SafeCoreMirAdtField>? declaredFields = null;
            int variantIndex = -1;
            if (value.Type.Kind == K.Adt)
            {
                if (value.Type.Name is null || !_adtLayouts.ContainsKey(value.Type.Name)) Invalid(node);
                SafeCoreMirAdtLayout layout = _adtLayouts[value.Type.Name!];
                declaredFields = layout.Fields;
                if (layout.Variants.Count != 0)
                {
                    for (int index = 0; index < layout.Variants.Count; index++)
                    {
                        Step(node, depth);
                        if (layout.Variants[index].Name == value.Constructor)
                        { variantIndex = index; declaredFields = layout.Variants[index].Fields; break; }
                    }
                    if (variantIndex < 0) Invalid(node);
                }
            }
            int count = declaredFields?.Count ?? value.Elements.Count;
            var operands = new SafeCoreMirOperand[count];
            for (int index = 0; index < count; index++)
            {
                Step(node, depth);
                SafeCoreEvaluatedConstant? field = null;
                if (value.Fields.Count != 0 && declaredFields is not null)
                    value.Fields.TryGetValue(declaredFields[index].Name, out field);
                else if (index < value.Elements.Count) field = value.Elements[index];
                if (field is null) Invalid(node);
                operands[index] = MaterializeConstant(field!, node, depth + 1);
            }
            SafeCoreMirRvalue result = value.Type.Kind switch
            {
                K.Tuple => SafeCoreMirRvalue.Tuple(operands, value.Type, Source(node), cancellation),
                K.Array => SafeCoreMirRvalue.Array(operands, value.Type, Source(node), cancellation),
                K.Adt when variantIndex >= 0 => SafeCoreMirRvalue.Enum(variantIndex, operands, value.Type, Source(node), cancellation),
                K.Adt => SafeCoreMirRvalue.Adt(operands, value.Type, Source(node), cancellation),
                _ => throw new LoweringException(new(UnsupportedSyntax, "This checked constant has no MIR value representation.", node.Span)),
            };
            return EmitConstant(result, node);
        }

        private SafeCoreMirOperand EmitConstant(SafeCoreMirRvalue value, SafeCoreHirNode node)
        {
            SafeCoreMirOperand result = Emit(value, value.Type, node);
            _locals[result.Id] = _locals[result.Id] with { IsPromotedConstant = true };
            return result;
        }
    }
}
