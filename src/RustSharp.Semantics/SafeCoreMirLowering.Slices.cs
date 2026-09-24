namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private void LowerSubsliceBorrow(SafeCoreHirNode borrow, SafeCoreHirNode indexed, int destination, int depth)
        {
            Step(borrow, depth);
            var (place, type) = ResolvePlace(Child(indexed, 0), depth + 1);
            for (int dereference = 0; type.Kind == SafeCoreSemanticTypeKind.Reference && dereference <= options.MaximumNestingDepth; dereference++)
            {
                Step(indexed, depth + dereference);
                place = ProjectPlace(place, SafeCoreMirProjection.Dereference(), indexed);
                type = type.ElementType!;
            }
            if (type.Kind is not (SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice)) Unsupported(indexed);
            SafeCoreType result = _locals[destination].Type;
            SafeCoreType sliceReference = SafeCoreType.Reference(SafeCoreType.Slice(type.ElementType!), result.IsMutable);
            SafeCoreMirOperand owner = Emit(SafeCoreMirRvalue.Unary(result.IsMutable ? "&mut" : "&",
                SafeCoreMirOperand.PlaceValue(place, type, Source(indexed)), sliceReference, Source(borrow)), sliceReference, borrow);
            SafeCoreHirNode range = Child(indexed, 1);
            int childIndex = 0;
            SafeCoreType usize = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize);
            SafeCoreMirOperand start = range.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeStart)
                ? SnapshotOperand(Expr(Child(range, childIndex++), depth + 1)!, range)
                : SafeCoreMirOperand.Constant(usize, "0", Source(range));
            SafeCoreMirOperand end = range.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeEnd)
                ? SnapshotOperand(Expr(Child(range, childIndex), depth + 1)!, range)
                : Emit(SafeCoreMirRvalue.SliceLength(owner, Source(range)), usize, range);
            _current!.Statements.Add(new(destination, SafeCoreMirRvalue.Subslice(owner, start, end, result,
                Source(borrow), range.Modifiers.HasFlag(SafeCoreHirNodeModifiers.InclusiveRange)), Source(borrow)));
        }
    }
}
