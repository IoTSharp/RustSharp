using System.Globalization;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private SafeCoreMirOperand MaterializeOwnedConstantValue(SafeCoreMirOperand value, SafeCoreHirNode node) =>
            value.Kind == SafeCoreMirOperandKind.Constant && RequiresDrop(value.Type)
                ? Emit(SafeCoreMirRvalue.Use(value, Source(node)), value.Type, node) : value;

        private void MaterializeDiscardedDropValue(SafeCoreMirOperand? value, SafeCoreHirNode node)
        {
            if (_current is not null && value is { Kind: SafeCoreMirOperandKind.Constant } &&
                RequiresDrop(value.Type))
                _ = Emit(SafeCoreMirRvalue.Use(value, Source(node)), value.Type, node);
        }

        private void EmitTemporaryDrops(SafeCoreMirSource scope, int depth)
        {
            EmitOwnedScopeDrops([], scope, depth);
        }

        private void EmitOwnedScopeDrops(IReadOnlyList<int> ownedRoots, SafeCoreMirSource scope, int depth)
        {
            SafeCoreHirNode anchor = input.Hir.GetNode(scope.HirNodeId);
            Step(anchor, depth);
            // Destructor receivers introduce new reference temporaries. A
            // fixed snapshot keeps cleanup finite and excludes those slots.
            int localCount = _locals.Count;
            var roots = new HashSet<int>(ownedRoots);
            for (int localId = localCount - 1; localId >= 0 && _current is not null; localId--)
            {
                Step(anchor, depth);
                SafeCoreMirLocal local = _locals[localId];
                bool temporary = local.Kind == SafeCoreMirLocalKind.Temporary &&
                    local.StorageScope?.HirNodeId == scope.HirNodeId;
                if ((roots.Contains(localId) || temporary) && RequiresDrop(local.Type, depth + 1))
                    EmitOwnedPlaceDrop(local.Type, SafeCoreMirPlace.Root(localId), local.Source, depth + 1);
            }
        }

        private void EmitOwnedPlaceDrop(SafeCoreType type, SafeCoreMirPlace place,
            SafeCoreMirSource source, int depth)
        {
            SafeCoreHirNode anchor = input.Hir.GetNode(source.HirNodeId);
            Step(anchor, depth);
            if (type.Name is { } name && _dropFunctions.TryGetValue(name, out int destructor))
                EmitDestructorCall(destructor, _functionNodes[destructor], source,
                    place.IsRoot ? place.LocalId : null, place);
            else
                EmitAggregateFieldDrops(type, source, place, depth + 1);
        }

        private void EmitArrayElementDrops(SafeCoreType type, SafeCoreMirSource source,
            SafeCoreMirPlace receiverPlace, int depth)
        {
            SafeCoreHirNode anchor = input.Hir.GetNode(source.HirNodeId);
            Step(anchor, depth);
            long length = type.Length ?? -1;
            if (type.Kind != K.Array ||
                length < 0 || length > options.MaximumOperations) Invalid(anchor);
            if (!RequiresDrop(type.ElementType, depth + 1)) return;

            // An array type describes one element type plus its cardinality;
            // Elements.Count therefore cannot be used as its destruction count.
            for (int index = 0; index < length; index++)
            {
                Step(anchor, depth);
                EmitTypeDrop(type.ElementType, source,
                    receiverPlace.Append(SafeCoreMirProjection.ArrayIndex(index)), depth + 1);
            }
        }

        private void EmitEnumVariantDrops(SafeCoreMirAdtLayout layout, SafeCoreMirSource source,
            SafeCoreMirPlace receiverPlace, int depth)
        {
            SafeCoreHirNode anchor = input.Hir.GetNode(source.HirNodeId);
            Step(anchor, depth);
            if (layout.Variants.Count == 0) Invalid(anchor);
            SafeCoreType integer = SafeCoreType.Primitive(K.I32);
            SafeCoreType boolean = SafeCoreType.Primitive(K.Bool);
            SafeCoreMirOperand tag = Emit(SafeCoreMirRvalue.Discriminant(
                SafeCoreMirOperand.PlaceValue(receiverPlace, layout.Type, source), source), integer, anchor);
            BlockBuilder join = Block(anchor);
            _current!.Statements[^1] = _current.Statements[^1] with
            {
                IsCleanupDiscriminant = true,
                CleanupDiscriminantJoinBlockId = join.Id,
            };

            // The CLR layout reserves storage for every payload, but only the
            // active Rust variant owns fields. Guard every downcast before a
            // receiver is formed so inactive representation slots never drop.
            for (int index = 0; index < layout.Variants.Count; index++)
            {
                Step(anchor, depth);
                SafeCoreMirAdtVariant variant = layout.Variants[index];
                bool hasOwnedFields = false;
                for (int fieldIndex = 0; fieldIndex < variant.Fields.Count && !hasOwnedFields; fieldIndex++)
                {
                    Step(anchor, depth);
                    hasOwnedFields = RequiresDrop(variant.Fields[fieldIndex].Type, depth + 1);
                }
                if (!hasOwnedFields) continue;
                SafeCoreMirOperand condition = Emit(SafeCoreMirRvalue.Binary("==", tag,
                    SafeCoreMirOperand.Constant(integer,
                        variant.Discriminant.ToString(CultureInfo.InvariantCulture), source),
                    boolean, source), boolean, anchor);
                BlockBuilder payload = Block(anchor);
                BlockBuilder next = Block(anchor);
                End(SafeCoreMirTerminator.Branch(condition, payload.Id, next.Id, source));
                _current = payload;
                SafeCoreMirPlace activePlace = receiverPlace.Append(SafeCoreMirProjection.Downcast(index));
                for (int fieldIndex = 0; fieldIndex < variant.Fields.Count; fieldIndex++)
                {
                    Step(anchor, depth);
                    SafeCoreMirAdtField field = variant.Fields[fieldIndex];
                    EmitTypeDrop(field.Type, source,
                        activePlace.Append(SafeCoreMirProjection.Field(field.Name)), depth + 1);
                }
                End(SafeCoreMirTerminator.Goto(join.Id, source));
                _current = next;
            }
            End(SafeCoreMirTerminator.Goto(join.Id, source));
            _current = join;
        }
    }
}
