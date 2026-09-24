using System.Globalization;
using System.Text;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private readonly Dictionary<string, SafeCoreMirAdtLayout> _adtLayouts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unitAdts = new(StringComparer.Ordinal);

        private void ValidateLifetimeElision(SafeCoreType signature, SafeCoreHirNode returnTypeNode)
        {
            if (CountElidedLifetimes(signature.ReturnType, returnTypeNode, 0) == 0) return;
            int inputLifetimes = 0;
            for (int index = 0; index < signature.ParameterTypes.Count && inputLifetimes < 2; index++)
            {
                Step(returnTypeNode, 0);
                inputLifetimes += CountElidedLifetimes(signature.ParameterTypes[index], returnTypeNode, 0);
            }
            if (inputLifetimes != 1)
                throw new LoweringException(new(InvalidLifetimeElision,
                    "An elided reference return requires exactly one input reference lifetime; explicit lifetime parameters are outside this profile.",
                    returnTypeNode.Span));
        }

        private int CountElidedLifetimes(SafeCoreType type, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            // Each reference occurrence introduces an independent input
            // lifetime, including references nested in tuple/array types.
            // A function-pointer type owns a separate elision scope and is
            // still rejected by the executable value-type boundary.
            if (type.Kind is not (K.Reference or K.Tuple or K.Array or K.Slice)) return 0;
            int count = type.Kind == K.Reference ? 1 : 0;
            for (int index = 0; index < type.Elements.Count && count < 2; index++)
                count += CountElidedLifetimes(type.Elements[index], node, depth + 1);
            return Math.Min(count, 2);
        }

        private SafeCoreMirSource? StorageScope(SafeCoreHirNode node, SafeCoreMirLocalKind kind)
        {
            if (!options.EnableP1Extensions || kind == SafeCoreMirLocalKind.Parameter) return null;
            if (kind == SafeCoreMirLocalKind.Temporary &&
                _extendedTemporaryScopes.TryGetValue(node.Id, out SafeCoreMirSource? extension)) return extension;
            SafeCoreMirSource? selected = null;
            // Static closure expansion retains the declaration's original
            // source span even when its call appears in a nested block.
            for (int index = _storageScopes.Count - 1; index >= 0; index--)
            {
                Step(node, 0);
                SafeCoreMirSource scope = _storageScopes[index];
                if (scope.Span.Start <= node.Span.Start && scope.Span.End >= node.Span.End)
                { selected = scope; break; }
            }
            if (kind == SafeCoreMirLocalKind.Temporary)
                for (int index = _temporaryScopes.Count - 1; index >= 0; index--)
                {
                    Step(node, 0);
                    SafeCoreMirSource scope = _temporaryScopes[index];
                    if (scope.Span.Start <= node.Span.Start && scope.Span.End >= node.Span.End &&
                        (selected is null || scope.Span.Length < selected.Span.Length))
                    { selected = scope; break; }
                }
            return selected;
        }

        private void MarkExtendedTemporaries(SafeCoreHirNode node, SafeCoreMirSource targetScope, int depth)
        {
            Step(node, depth);
            _extendedTemporaryScopes[node.Id] = targetScope;
            // Rust extends the initializer's temporary and the operands of
            // these syntactic extending expressions. Calls do not extend
            // their arguments: `let r = id(&make())` must expire at the
            // statement even though `let r = &make()` extends the owner.
            if (node.Kind is N.TupleExpression or N.ArrayExpression or N.StructExpression or N.StructExpressionField)
            {
                for (int index = 0; index < node.ChildIds.Count; index++) MarkExtendedTemporaries(Child(node, index), targetScope, depth + 1);
            }
            else if (node.Kind is N.MemberExpression or N.IndexExpression or N.CastExpression or N.BlockExpression ||
                node.Kind == N.UnaryExpression && node.Value is "&" or "&mut" or "*")
                MarkExtendedTemporaries(Child(node, 0), targetScope, depth + 1);
            else if (node.Kind == N.Block && node.ChildIds.Count != 0)
            {
                SafeCoreHirNode tail = Child(node, node.ChildIds.Count - 1);
                if (tail.Kind is not (N.LetStatement or N.ExpressionStatement or N.ReturnStatement))
                    MarkExtendedTemporaries(tail, targetScope, depth + 1);
            }
        }

        private SafeCoreMirOperand SnapshotCallArgument(SafeCoreMirOperand value, SafeCoreHirNode node)
        {
            if (!options.EnableP1Extensions || value.Type.Kind != K.Reference) return value;
            if (!value.Type.IsMutable) return SnapshotOperand(value, node);
            if (value.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place)) Invalid(node);
            SafeCoreMirPlace place = value.Place ?? SafeCoreMirPlace.Root(value.Id);
            SafeCoreMirOperand referent = SafeCoreMirOperand.PlaceValue(
                ProjectPlace(place, SafeCoreMirProjection.Dereference(), node), value.Type.ElementType, Source(node));
            return Emit(SafeCoreMirRvalue.Unary("&mut", referent, value.Type, Source(node)), value.Type, node);
        }

        private void CollectAdtLayouts()
        {
            for (int index = 0; index < input.Hir.Nodes.Count; index++)
            {
                SafeCoreHirNode node = input.Hir.Nodes[index];
                Step(node, 0);
                if (node.Kind != N.Struct) continue;
                SafeCoreType type = Type(node);
                var fields = new List<SafeCoreMirAdtField>();
                for (int childIndex = 0; childIndex < node.ChildIds.Count; childIndex++)
                {
                    SafeCoreHirNode field = Child(node, childIndex);
                    Step(field, 1);
                    if (field.Kind == N.Attribute) continue;
                    if (field.Kind != N.Field) Unsupported(field);
                    string name = field.Name is { } named ? CanonicalField(named) : fields.Count.ToString(CultureInfo.InvariantCulture);
                    fields.Add(new(name, Type(field), Source(field)));
                }
                if (type.Kind != K.Adt || type.Name is null ||
                    !_adtLayouts.TryAdd(type.Name, new(type, fields, Source(node), cancellationToken: cancellation))) Invalid(node);
                if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct)) _unitAdts.Add(type.Name);
            }
        }

        private SafeCoreMirOperand? ConstructAdt(SafeCoreHirNode node, int depth)
        {
            SafeCoreType type = Type(node);
            if (type.Kind != K.Adt || type.Name is null) Unsupported(node);
            if (!_adtLayouts.TryGetValue(type.Name, out SafeCoreMirAdtLayout? layout)) Unsupported(node);
            ValueType(type, node);
            var operands = new SafeCoreMirOperand[layout.Fields.Count];
            for (int index = 0; index < node.ChildIds.Count; index++)
            {
                SafeCoreHirNode field = Child(node, index);
                Step(field, depth);
                if (field.Kind == N.StructExpressionField)
                {
                    int fieldIndex = FindAdtField(layout, field.Name!, field);
                    SafeCoreMirOperand? value = Expr(Child(field, 0), depth + 1);
                    if (_current is null || value is null) return null;
                    operands[fieldIndex] = SnapshotOperand(value, field);
                }
                else
                {
                    // Evaluate an update base after the explicit initializers,
                    // then move only the fields omitted by the update.
                    (SafeCoreMirPlace basePlace, SafeCoreType baseType) = ResolvePlace(field, depth + 1);
                    if (baseType != type) Invalid(field);
                    for (int fieldIndex = 0; fieldIndex < operands.Length; fieldIndex++)
                    {
                        Step(field, depth + 1);
                        if (operands[fieldIndex] is not null) continue;
                        SafeCoreMirAdtField declared = layout.Fields[fieldIndex];
                        SafeCoreMirOperand value = SafeCoreMirOperand.PlaceValue(
                            ProjectPlace(basePlace, SafeCoreMirProjection.Field(declared.Name), field), declared.Type, Source(field));
                        operands[fieldIndex] = SnapshotOperand(value, field);
                    }
                }
            }
            for (int index = 0; index < operands.Length; index++)
            {
                Step(node, depth);
                if (operands[index] is null) Invalid(node);
            }
            return Emit(SafeCoreMirRvalue.Adt(operands, type, Source(node), cancellation), type, node);
        }

        private SafeCoreMirOperand? ConstructTupleAdt(SafeCoreHirNode node, int depth)
        {
            SafeCoreType type = Type(node);
            if (type.Name is null) Invalid(node);
            if (!_adtLayouts.TryGetValue(type.Name, out SafeCoreMirAdtLayout? layout)) Invalid(node);
            if (layout.Fields.Count != node.ChildIds.Count - 1) Invalid(node);
            var operands = new List<SafeCoreMirOperand>(layout.Fields.Count);
            for (int index = 1; index < node.ChildIds.Count; index++)
            {
                Step(node, depth);
                SafeCoreMirOperand? value = Expr(Child(node, index), depth + 1);
                if (_current is null || value is null) return null;
                operands.Add(SnapshotOperand(value, Child(node, index)));
            }
            return Emit(SafeCoreMirRvalue.Adt(operands, type, Source(node), cancellation), type, node);
        }

        private SafeCoreMirOperand SnapshotOperand(SafeCoreMirOperand value, SafeCoreHirNode node) =>
            value.Kind == SafeCoreMirOperandKind.Constant ? value : Emit(SafeCoreMirRvalue.Use(value, Source(node)), value.Type, node);

        private int FindAdtField(SafeCoreMirAdtLayout layout, string name, SafeCoreHirNode node)
        {
            string canonical = CanonicalField(name);
            for (int index = 0; index < layout.Fields.Count; index++)
            {
                Step(node, 0);
                if (layout.Fields[index].Name == canonical) return index;
            }
            Invalid(node);
            return -1;
        }

        private static string CanonicalField(string name) =>
            (name.StartsWith("r#", StringComparison.Ordinal) ? name[2..] : name).Normalize(NormalizationForm.FormC);

        private SafeCoreMirPlace ProjectPlace(SafeCoreMirPlace place, SafeCoreMirProjection projection, SafeCoreHirNode node)
        {
            Step(node, place.Projections.Count + 1);
            return place.Append(projection);
        }

        private (SafeCoreMirPlace Place, SafeCoreType Type) ResolvePlace(SafeCoreHirNode rawNode, int depth)
        {
            SafeCoreHirNode node = UnwrapExpression(rawNode, depth);
            Step(node, depth);
            if (node.Kind == N.NameExpression && node.ReferencedSymbol is { } symbol && _bindings.TryGetValue(symbol, out int binding))
                return (SafeCoreMirPlace.Root(binding), _locals[binding].Type);
            if (node.Kind == N.UnaryExpression && node.Value == "*")
            {
                (SafeCoreMirPlace inner, SafeCoreType reference) = ResolvePlace(Child(node, 0), depth + 1);
                if (reference.Kind != K.Reference) Invalid(node);
                return (ProjectPlace(inner, SafeCoreMirProjection.Dereference(), node), reference.ElementType!);
            }
            if (node.Kind is N.MemberExpression or N.IndexExpression)
            {
                (SafeCoreMirPlace target, SafeCoreType targetType) = ResolvePlace(Child(node, 0), depth + 1);
                for (int dereference = 0; targetType.Kind == K.Reference && dereference <= options.MaximumNestingDepth; dereference++)
                {
                    Step(node, depth + dereference);
                    target = ProjectPlace(target, SafeCoreMirProjection.Dereference(), node);
                    targetType = targetType.ElementType!;
                }
                if (node.Kind == N.MemberExpression)
                {
                    if (targetType.Kind == K.Tuple && int.TryParse(node.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                        index >= 0 && index < targetType.Elements.Count)
                        return (ProjectPlace(target, SafeCoreMirProjection.TupleIndex(index), node), targetType.Elements[index]);
                    if (targetType.Kind == K.Adt && targetType.Name is { } identity && _adtLayouts.TryGetValue(identity, out SafeCoreMirAdtLayout? layout))
                    {
                        SafeCoreMirAdtField field = layout.Fields[FindAdtField(layout, node.Name!, node)];
                        return (ProjectPlace(target, SafeCoreMirProjection.Field(field.Name), node), field.Type);
                    }
                    Unsupported(node);
                }
                if (targetType.Kind is not (K.Array or K.Slice)) Unsupported(node);
                SafeCoreMirOperand? offset = Expr(Child(node, 1), depth + 1);
                if (_current is null || offset is null || offset.Type.Kind != K.Usize) Unsupported(node);
                SafeCoreMirProjection projection;
                if (offset.Kind == SafeCoreMirOperandKind.Constant && int.TryParse(offset.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int constant))
                {
                    if (targetType.Kind == K.Array && (targetType.Length is not long length || constant >= length)) Invalid(node);
                    projection = SafeCoreMirProjection.ArrayIndex(constant);
                }
                else
                {
                    offset = SnapshotOperand(offset, node);
                    if (offset.Kind != SafeCoreMirOperandKind.Local) Invalid(node);
                    projection = SafeCoreMirProjection.DynamicIndex(offset.Id);
                }
                return (ProjectPlace(target, projection, node), targetType.ElementType!);
            }
            SafeCoreMirOperand? value = Expr(node, depth + 1);
            if (_current is null || value is null) Unsupported(node);
            if (value.Kind != SafeCoreMirOperandKind.Local) value = Emit(SafeCoreMirRvalue.Use(value, Source(node)), value.Type, node);
            return (SafeCoreMirPlace.Root(value.Id), value.Type);
        }

        private SafeCoreMirOperand ReadPlace(SafeCoreHirNode node, int depth)
        {
            (SafeCoreMirPlace place, SafeCoreType type) = ResolvePlace(node, depth + 1);
            return Emit(SafeCoreMirRvalue.Use(SafeCoreMirOperand.PlaceValue(place, type, Source(node)), Source(node)), type, node);
        }

        private SafeCoreMirOperand? AssignPlace(SafeCoreHirNode node, int depth)
        {
            string operation = node.Value!;
            SafeCoreHirNode target = Child(node, 0);
            // Rust's primitive assignments, including compound assignments,
            // evaluate the RHS before evaluating the destination place.
            SafeCoreMirOperand? right = Expr(Child(node, 1), depth + 1);
            if (_current is null || right is null) return null;
            (SafeCoreMirPlace place, SafeCoreType type) = ResolvePlace(target, depth + 1);
            if (_current is null || right is null) return null;
            if (operation != "=")
                right = Emit(SafeCoreMirRvalue.Binary(operation[..^1], SafeCoreMirOperand.PlaceValue(place, type, Source(target)),
                    right, type, Source(node)), type, node);
            _current.Statements.Add(new(place.LocalId, SafeCoreMirRvalue.Use(right, Source(node)), Source(node))
            {
                DestinationPlace = place.IsRoot ? null : place,
            });
            return Unit(node);
        }
    }
}
