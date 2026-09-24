using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private enum CaptureAccess { Shared, Mutable, Owned }
        private sealed record CapturePath(SafeCoreSymbol Symbol, string Fields);
        private sealed record CapturedPlace(SafeCoreMirPlace Place, SafeCoreType Type);
        private sealed record CaptureUse(SafeCoreHirNode Node, CaptureAccess Access);
        private readonly Dictionary<SafeCoreSymbol, CapturedPlace> _capturePlaces = [];
        private readonly Dictionary<CapturePath, CapturedPlace> _captureProjections = [];

        private ClosureBinding CreateClosureBinding(SafeCoreHirNode closure)
        {
            SafeCoreType closureType = Type(closure);
            if (closureType.Kind != K.Closure) Unsupported(closure);
            var captures = new Dictionary<SafeCoreSymbol, CapturedPlace>();
            var projected = new Dictionary<CapturePath, CapturedPlace>();
            var uses = new Dictionary<CapturePath, CaptureUse>();
            CollectCaptureUses(Child(closure, closure.ChildIds.Count - 1), uses, CaptureAccess.Shared, consuming: true, 0);
            foreach ((CapturePath path, CaptureUse use) in uses)
            {
                Step(use.Node, 0);
                (SafeCoreMirPlace place, SafeCoreType type) = ResolvePlace(use.Node, 1);
                SafeCoreMirOperand operand = SafeCoreMirOperand.PlaceValue(place, type, Source(use.Node));
                if (closure.Modifiers.HasFlag(SafeCoreHirNodeModifiers.MoveCapture) || use.Access == CaptureAccess.Owned)
                {
                    SafeCoreMirOperand captured = Emit(SafeCoreMirRvalue.Use(operand, Source(closure)), type, closure);
                    AddCapture(path, new(SafeCoreMirPlace.Root(captured.Id), type), captures, projected);
                    continue;
                }
                bool mutable = use.Access == CaptureAccess.Mutable;
                // An immutable binding containing &mut T can still uniquely
                // reborrow T. Capturing that referent preserves the reference
                // value without requiring mutable access to the binding slot.
                if (mutable && type.Kind == K.Reference && type.IsMutable &&
                    place.IsRoot && !_locals[place.LocalId].IsMutable)
                {
                    SafeCoreMirOperand referent = SafeCoreMirOperand.PlaceValue(
                        ProjectPlace(place, SafeCoreMirProjection.Dereference(), use.Node), type.ElementType, Source(use.Node));
                    SafeCoreMirOperand reborrow = Emit(SafeCoreMirRvalue.Unary("&mut", referent, type, Source(closure)), type, closure);
                    AddCapture(path, new(SafeCoreMirPlace.Root(reborrow.Id), type), captures, projected);
                    continue;
                }
                SafeCoreType captureType = SafeCoreType.Reference(type, mutable);
                SafeCoreMirOperand reference = Emit(SafeCoreMirRvalue.Unary(mutable ? "&mut" : "&", operand,
                    captureType, Source(closure)), captureType, closure);
                AddCapture(path, new(ProjectPlace(SafeCoreMirPlace.Root(reference.Id), SafeCoreMirProjection.Dereference(), closure), type), captures, projected);
            }
            return new(closure, closureType, captures, projected);
        }

        private static void AddCapture(CapturePath path, CapturedPlace place,
            Dictionary<SafeCoreSymbol, CapturedPlace> roots, Dictionary<CapturePath, CapturedPlace> projected)
        {
            if (path.Fields.Length == 0) roots.Add(path.Symbol, place);
            else projected.Add(path, place);
        }

        private bool CapturePathOf(SafeCoreHirNode rawNode, int depth, out CapturePath path)
        {
            SafeCoreHirNode node = UnwrapExpression(rawNode, depth);
            if (node.Kind == N.NameExpression && node.ReferencedSymbol is { } symbol)
            { path = new(symbol, string.Empty); return true; }
            if (node.Kind == N.MemberExpression && Type(Child(node, 0)).Kind != K.Reference &&
                CapturePathOf(Child(node, 0), depth + 1, out CapturePath parent))
            { path = new(parent.Symbol, parent.Fields + "." + CanonicalField(node.Name!)); return true; }
            path = null!; return false;
        }

        private bool IsOuterCapture(CapturePath path, SafeCoreHirNode node, int depth)
        {
            if (_bindings.ContainsKey(path.Symbol) || _capturePlaces.ContainsKey(path.Symbol)) return true;
            foreach (CapturePath known in _captureProjections.Keys)
            { Step(node, depth); if (known.Symbol == path.Symbol) return true; }
            return false;
        }

        private void RecordCapture(Dictionary<CapturePath, CaptureUse> captures, CapturePath path,
            SafeCoreHirNode node, CaptureAccess access, int depth)
        {
            CaptureAccess merged = access;
            var children = new List<CapturePath>();
            foreach ((CapturePath known, CaptureUse use) in captures)
            {
                Step(node, depth);
                if (known.Symbol != path.Symbol) continue;
                if (path.Fields == known.Fields || path.Fields.StartsWith(known.Fields + ".", StringComparison.Ordinal))
                {
                    if (access > use.Access) captures[known] = use with { Access = access };
                    return;
                }
                if (known.Fields.StartsWith(path.Fields + ".", StringComparison.Ordinal))
                { children.Add(known); if (use.Access > merged) merged = use.Access; }
            }
            foreach (CapturePath child in children) { Step(node, depth); captures.Remove(child); }
            captures.Add(path, new(node, merged));
        }

        private void CollectCaptureUses(SafeCoreHirNode node, Dictionary<CapturePath, CaptureUse> captures,
            CaptureAccess access, bool consuming, int depth)
        {
            Step(node, depth);
            if (node.Kind is N.NameExpression or N.MemberExpression &&
                CapturePathOf(node, depth + 1, out CapturePath path) && IsOuterCapture(path, node, depth))
            {
                SafeCoreType type = Type(node);
                CaptureAccess selected = consuming && access == CaptureAccess.Shared && !CaptureCopy(type, node, depth + 1)
                    ? CaptureAccess.Owned : access;
                RecordCapture(captures, path, node, selected, depth);
                return;
            }
            bool assignment = node.Kind == N.BinaryExpression && node.Value is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=";
            if (assignment)
            {
                CollectCaptureUses(Child(node, 0), captures, CaptureAccess.Mutable, consuming: false, depth + 1);
                CollectCaptureUses(Child(node, 1), captures, CaptureAccess.Shared, consuming: true, depth + 1);
                return;
            }
            if (node.Kind == N.UnaryExpression && node.Value is "&" or "&mut" or "*")
            {
                CollectCaptureUses(Child(node, 0), captures,
                    node.Value == "&mut" ? CaptureAccess.Mutable : access, consuming: false, depth + 1);
                return;
            }
            if (node.Kind is N.MemberExpression or N.IndexExpression)
            {
                CollectCaptureUses(Child(node, 0), captures, access, consuming: false, depth + 1);
                for (int index = 1; index < node.ChildIds.Count; index++)
                    CollectCaptureUses(Child(node, index), captures, CaptureAccess.Shared, consuming: true, depth + 1);
                return;
            }
            if (node.Kind == N.CallExpression)
            {
                SafeCoreHirNode callee = Child(node, 0);
                SafeCoreType signature = Type(callee);
                CollectCaptureUses(callee, captures, CaptureAccess.Shared, consuming: false, depth + 1);
                for (int index = 1; index < node.ChildIds.Count; index++)
                {
                    SafeCoreType? parameter = signature.Kind is K.Function or K.Closure && index - 1 < signature.ParameterTypes.Count
                        ? signature.ParameterTypes[index - 1] : null;
                    bool reborrow = parameter?.Kind == K.Reference;
                    CollectCaptureUses(Child(node, index), captures, reborrow && parameter!.IsMutable ? CaptureAccess.Mutable : CaptureAccess.Shared,
                        consuming: !reborrow, depth + 1);
                }
                return;
            }
            for (int index = 0; index < node.ChildIds.Count; index++)
                CollectCaptureUses(Child(node, index), captures, access, consuming, depth + 1);
        }

        private bool CaptureCopy(SafeCoreType type, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (type.Kind == K.Reference) return !type.IsMutable;
            if (type.Kind == K.Adt) return type.Name is { } name && _adtLayouts.TryGetValue(name, out SafeCoreMirAdtLayout? layout) && layout.IsCopy;
            if (type.Kind is K.Tuple or K.Array)
            {
                for (int index = 0; index < type.Elements.Count; index++)
                    if (!CaptureCopy(type.Elements[index], node, depth + 1)) return false;
                return true;
            }
            return IsStructuralCopy(type);
        }

        private bool TryCapturedPlace(SafeCoreHirNode node, out CapturedPlace captured)
        {
            if (node.Kind == N.MemberExpression && CapturePathOf(node, 0, out CapturePath path) &&
                _captureProjections.TryGetValue(path, out CapturedPlace? projected))
            { captured = projected; return true; }
            if (node.ReferencedSymbol is { } symbol && _capturePlaces.TryGetValue(symbol, out CapturedPlace? place))
            { captured = place; return true; }
            captured = null!; return false;
        }

        private SafeCoreMirOperand ReadCapturedPlace(SafeCoreHirNode node, CapturedPlace captured)
        {
            SafeCoreMirOperand value = SafeCoreMirOperand.PlaceValue(captured.Place, captured.Type, Source(node));
            return captured.Type.Kind == K.Reference ? value : Emit(SafeCoreMirRvalue.Use(value, Source(node)), value.Type, node);
        }
    }
}
