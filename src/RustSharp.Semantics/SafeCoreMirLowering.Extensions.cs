using System.Globalization;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private sealed record ClosureBinding(
            SafeCoreHirNode Node,
            SafeCoreType Type,
            IReadOnlyDictionary<SafeCoreSymbol, CapturedPlace> Captures,
            IReadOnlyDictionary<CapturePath, CapturedPlace> ProjectedCaptures);

        private sealed class ClosureReturn(int? destination, BlockBuilder join)
        {
            public int? Destination { get; } = destination;
            public BlockBuilder Join { get; } = join;
            public bool HasReturn { get; set; }
        }

        private readonly Dictionary<SafeCoreSymbol, ClosureBinding> _closures = [];
        private readonly List<ClosureReturn> _closureReturns = [];

        private SafeCoreMirOperand? ExtendedLet(SafeCoreHirNode node, int depth)
        {
            SafeCoreHirNode pattern = UnwrapPattern(Child(node, 0));
            int valueIndex = node.ChildIds.Count > 1 && IsTypeNode(Child(node, 1).Kind) ? 2 : 1;
            // A declaration without an initializer creates an uninitialized
            // storage slot. Its first later assignment is checked by the
            // ownership adapter; treating the missing expression as a value
            // would fabricate initialization and hide use-before-init errors.
            if (valueIndex >= node.ChildIds.Count)
            {
                if (pattern.Kind is not N.IdentifierPattern || pattern.DeclaredSymbol is null)
                    Unsupported(pattern);
                SafeCoreType declarationType = Type(pattern);
                int declarationLocal = Local(pattern.Name ?? string.Empty, declarationType,
                    SafeCoreMirLocalKind.User, pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                if (!_bindings.TryAdd(pattern.DeclaredSymbol, declarationLocal)) Invalid(pattern);
                return Unit(node);
            }
            SafeCoreHirNode initializer = Child(node, valueIndex);
            if (_storageScopes.Count != 0) MarkExtendedTemporaries(initializer, _storageScopes[^1], depth + 1);
            if (initializer.Kind == N.NameExpression && initializer.ReferencedSymbol is { } escapedSymbol &&
                _closures.ContainsKey(escapedSymbol))
                Unsupported(initializer);
            if (initializer.Kind == N.ClosureExpression && pattern.Kind == N.IdentifierPattern &&
                pattern.DeclaredSymbol is not null)
            {
                _closures[pattern.DeclaredSymbol] = CreateClosureBinding(initializer);
                return Unit(node);
            }
            if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse) ||
                pattern.Kind is not (N.IdentifierPattern or N.WildcardPattern) ||
                pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference))
                return PatternLet(node, pattern, initializer, depth + 1);
            // Bind a direct borrow into its destination slot so the MIR carries
            // the owner/reference pair without introducing an unproven alias
            // copy through a temporary reference local.
            if (initializer.Kind == N.UnaryExpression && initializer.Value is ("&" or "&mut") &&
                !input.Promotions.ContainsKey(initializer.Id) &&
                pattern.Kind == N.IdentifierPattern && pattern.DeclaredSymbol is not null)
            {
                // Preserve a declaration's unsizing target. For
                // `let view: &[T] = &array`, the initializer itself is
                // `&[T; N]`; the checker records the array-to-slice target on
                // the declaration and MIR must retain that type/provenance.
                SafeCoreType referenceType = Type(pattern);
                if (referenceType.Kind != K.Reference)
                    referenceType = Type(initializer);
                int borrowLocal = Local(pattern.Name ?? string.Empty, referenceType, SafeCoreMirLocalKind.User,
                    pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                if (node.ChildIds.Count > 1 && HasStaticLifetime(Child(node, 1)))
                    _locals[borrowLocal] = _locals[borrowLocal] with { RequiresStaticLifetime = true };
                if (!_bindings.TryAdd(pattern.DeclaredSymbol, borrowLocal)) Invalid(pattern);
                LowerBorrow(initializer, borrowLocal, depth);
                return Unit(node);
            }
            SafeCoreMirOperand? value = Expr(initializer, depth + 1);
            if (_current is null || value is null) return null;
            // A shared reference is Copy while an exclusive reference is Move.
            // Preserve the source local in MIR so ownership can distinguish the
            // two contracts instead of rejecting the assignment as an opaque
            // value or silently cloning a borrow.
            if (pattern.Kind is not (N.IdentifierPattern or N.WildcardPattern))
            {
                BindIrrefutablePattern(pattern, value, depth + 1);
                return Unit(node);
            }
            if (pattern.Kind == N.WildcardPattern) return Unit(node);
            if (pattern.DeclaredSymbol is null || pattern.Name is null) Invalid(pattern);
            SafeCoreType type = Type(pattern);
            if (type.Kind == K.Adt && _dropFunctions.ContainsKey(type.Name!) &&
                (value.Kind != SafeCoreMirOperandKind.Constant || value.Value != "()" ||
                 _loops.Count != 0 || _closureReturns.Count != 0 || _dropScopes.Count == 0)) Unsupported(initializer);
            int local = Local(pattern.Name, type, SafeCoreMirLocalKind.User,
                pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
            if (node.ChildIds.Count > 1 && HasStaticLifetime(Child(node, 1)))
                _locals[local] = _locals[local] with { RequiresStaticLifetime = true };
            if (!_bindings.TryAdd(pattern.DeclaredSymbol!, local)) Invalid(pattern);
            Assign(local, value, node);
            if (type.Kind == K.Adt && _dropFunctions.ContainsKey(type.Name!))
                _dropScopes[^1].Add(local);
            if (initializer.Kind == N.ClosureExpression && type.Kind == K.Closure)
            {
                _closures[pattern.DeclaredSymbol!] = CreateClosureBinding(initializer);
            }
            return Unit(node);
        }

        private static SafeCoreMirOperand ClosureValue(SafeCoreHirNode node, int depth)
        {
            // Closure values are statically expanded at their call site. A
            // standalone value has no representable CLR LIR shape in this
            // profile, so only the enclosing let/call path may consume it.
            Unsupported(node);
            return null!;
        }

        private void LowerBorrow(SafeCoreHirNode node, int destination, int depth)
        {
            Step(node, depth);
            SafeCoreHirNode borrowed = UnwrapExpression(Child(node, 0), depth + 1);
            if (borrowed.Kind == N.IndexExpression && Child(borrowed, 1).Kind == N.RangeExpression)
            {
                LowerSubsliceBorrow(node, borrowed, destination, depth + 1);
                return;
            }
            (SafeCoreMirPlace place, SafeCoreType ownerType) = ResolvePlace(borrowed, depth + 1);
            SafeCoreType referenceType = _locals[destination].Type;
            bool mutable = node.Value == "&mut";
            if (referenceType.Kind != K.Reference || referenceType.IsMutable != mutable) Invalid(node);
            bool arrayToSlice = referenceType.ElementType?.Kind == K.Slice && ownerType.Kind == K.Array &&
                ownerType.ElementType == referenceType.ElementType.ElementType;
            if (!arrayToSlice && ownerType != referenceType.ElementType) Invalid(node);
            _current!.Statements.Add(new(destination,
                SafeCoreMirRvalue.Unary(node.Value!, SafeCoreMirOperand.PlaceValue(place, ownerType, Source(borrowed)),
                    referenceType, Source(node)), Source(node)));
        }

        private SafeCoreMirOperand ClosureCall(SafeCoreHirNode call, SafeCoreHirNode calleeNode, int depth)
        {
            ClosureBinding closure = null!;
            if (calleeNode.Kind == N.NameExpression && calleeNode.ReferencedSymbol is { } closureSymbol &&
                _closures.TryGetValue(closureSymbol, out ClosureBinding? known)) closure = known;
            else if (calleeNode.Kind == N.ClosureExpression)
                closure = CreateClosureBinding(calleeNode);
            else Unsupported(calleeNode);

            SafeCoreType signature = closure.Type;
            int parameterCount = 0;
            for (int childIndex = 0; childIndex < closure.Node.ChildIds.Count; childIndex++)
                if (Child(closure.Node, childIndex).Kind == N.Parameter) parameterCount++;
            int argumentCount = call.ChildIds.Count - 1;
            if (parameterCount != argumentCount) Invalid(call);
            var arguments = new List<SafeCoreMirOperand>(argumentCount);
            for (int index = 0; index < argumentCount; index++)
            {
                SafeCoreMirOperand? argument = Expr(Child(call, index + 1), depth + 1);
                if (argument is null) return Unit(call);
                arguments.Add(SnapshotCallArgument(argument, Child(call, index + 1)));
            }

            int? destination = signature.ReturnType.Kind is K.Unit or K.Never ? null : Temp(signature.ReturnType, call);
            BlockBuilder join = Block(call);
            var savedBindings = new Dictionary<SafeCoreSymbol, int>(_bindings);
            var savedCaptures = new Dictionary<SafeCoreSymbol, CapturedPlace>(_capturePlaces);
            var savedProjections = new Dictionary<CapturePath, CapturedPlace>(_captureProjections);
            _bindings.Clear();
            _capturePlaces.Clear();
            _captureProjections.Clear();
            foreach ((SafeCoreSymbol captureSymbol, CapturedPlace capture) in closure.Captures)
                _capturePlaces[captureSymbol] = capture;
            foreach ((CapturePath capturePath, CapturedPlace capture) in closure.ProjectedCaptures)
                _captureProjections[capturePath] = capture;
            try
            {
                int parameterIndex = 0;
                for (int childIndex = 0; childIndex < closure.Node.ChildIds.Count; childIndex++)
                {
                    SafeCoreHirNode parameter = Child(closure.Node, childIndex);
                    if (parameter.Kind != N.Parameter) continue;
                    SafeCoreHirNode pattern = UnwrapPattern(Child(parameter, 0));
                    SafeCoreType type = signature.ParameterTypes[parameterIndex++];
                    int local = Local(pattern.Name ?? $"closure_arg{parameterIndex.ToString(CultureInfo.InvariantCulture)}",
                        type, SafeCoreMirLocalKind.User, pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable) ||
                        PatternBorrowsMutably(pattern, depth + 1), pattern);
                    Assign(local, arguments[parameterIndex - 1], pattern);
                    if (pattern.Kind == N.IdentifierPattern && !pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference))
                        _bindings[pattern.DeclaredSymbol!] = local;
                    else BindIrrefutablePattern(pattern, SafeCoreMirOperand.Local(local, type, Source(pattern)), depth + 1);
                }
                BlockBuilder? caller = _current;
                _closureReturns.Add(new(destination, join));
                SafeCoreMirOperand? body = Expr(Child(closure.Node, closure.Node.ChildIds.Count - 1), depth + 1);
                _closureReturns.RemoveAt(_closureReturns.Count - 1);
                if (_current is not null)
                    Join(destination, body, join, call);
                _current = join;
            }
            finally
            {
                _bindings.Clear();
                foreach ((SafeCoreSymbol savedSymbol, int local) in savedBindings) _bindings[savedSymbol] = local;
                _capturePlaces.Clear();
                foreach ((SafeCoreSymbol savedSymbol, CapturedPlace capture) in savedCaptures) _capturePlaces[savedSymbol] = capture;
                _captureProjections.Clear();
                foreach ((CapturePath savedPath, CapturedPlace capture) in savedProjections) _captureProjections[savedPath] = capture;
            }
            return destination is int result ? SafeCoreMirOperand.Local(result, signature.ReturnType, Source(call)) : Unit(call);
        }

        private void BindIrrefutablePattern(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth) =>
            BindDeclarationPattern(pattern, value, pattern, depth);
        private SafeCoreMirOperand PatternField(SafeCoreMirOperand value, SafeCoreMirProjection projection,
            SafeCoreType fieldType, SafeCoreHirNode node)
        {
            SafeCoreMirPlace place = value.Place ?? SafeCoreMirPlace.Root(value.Id);
            return SafeCoreMirOperand.PlaceValue(ProjectPlace(place, projection, node), fieldType, Source(node));
        }

        private SafeCoreMirOperand Member(SafeCoreHirNode node, int depth)
        {
            return ReadPlace(node, depth);
        }

        private SafeCoreMirOperand Match(SafeCoreHirNode node, int depth) => MatchPatterns(node, depth);

        private SafeCoreMirOperand DereferencePattern(SafeCoreMirOperand value, SafeCoreHirNode pattern)
        {
            if (value.Type.Kind != K.Reference) Invalid(pattern);
            return PatternField(value, SafeCoreMirProjection.Dereference(), value.Type.ElementType, pattern);
        }

        private void BindPatternLocals(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            Step(pattern, depth);
            pattern = SelectedPattern(pattern, depth);
            if (TryEnumVariant(pattern, out SafeCoreMirAdtLayout enumLayout, out int variantIndex))
            { BindEnumPattern(pattern, value, enumLayout, variantIndex, depth); return; }
            switch (pattern.Kind)
            {
                case N.IdentifierPattern when pattern.DeclaredSymbol is not null:
                    BindPatternValue(pattern, value); return;
                case N.TuplePattern:
                case N.StructPattern:
                case N.PathPattern when value.Type.Kind == K.Adt || value.Type.Kind == K.Reference:
                    foreach ((SafeCoreHirNode child, SafeCoreMirOperand field) in AggregatePatternFields(pattern, value, depth + 1))
                    { Step(child, depth); BindPatternLocals(child, field, depth + 1); }
                    return;
                case N.ReferencePattern:
                    BindPatternLocals(Child(pattern, 0), DereferencePattern(value, pattern), depth + 1); return;
                case N.SlicePattern:
                    BindSlicePattern(pattern, value, depth + 1); return;
                case N.AtPattern:
                    BindPatternLocals(Child(pattern, 0), value, depth + 1);
                    BindPatternLocals(Child(pattern, 1), value, depth + 1); return;
                default: return;
            }
        }

        private SafeCoreMirOperand PatternCondition(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            Step(pattern, depth);
            pattern = SelectedPattern(pattern, depth);
            if (TryEnumVariant(pattern, out SafeCoreMirAdtLayout enumLayout, out int variantIndex))
                return EnumPatternCondition(pattern, value, enumLayout, variantIndex, depth);
            SafeCoreType boolType = SafeCoreType.Primitive(K.Bool);
            switch (pattern.Kind)
            {
                case N.WildcardPattern:
                case N.RestPattern:
                case N.IdentifierPattern when pattern.DeclaredSymbol is not null:
                    return SafeCoreMirOperand.Constant(boolType, "true", Source(pattern));
                case N.LiteralPattern:
                case N.PathPattern when pattern.ReferencedSymbol?.Kind == SafeCoreSymbolKind.Const:
                    if (value.Type != Type(pattern)) value = PatternValue(value, pattern, depth);
                    SafeCoreMirOperand literal = PatternConstant(pattern, depth + 1);
                    return Emit(SafeCoreMirRvalue.Binary("==", value, literal, boolType, Source(pattern)), boolType, pattern);
                case N.RangePattern:
                    return PatternRangeCondition(pattern, PatternValue(value, pattern, depth), depth + 1);
                case N.TuplePattern:
                case N.StructPattern:
                case N.PathPattern:
                    return AggregatePatternCondition(pattern, value, depth + 1);
                case N.ReferencePattern:
                    return PatternCondition(Child(pattern, 0), DereferencePattern(value, pattern), depth + 1);
                case N.SlicePattern:
                    return SlicePatternCondition(pattern, value, depth + 1);
                case N.AtPattern: return PatternCondition(Child(pattern, 1), value, depth + 1);
                default: Unsupported(pattern); return null!;
            }
        }
        private SafeCoreMirOperand CombineCondition(SafeCoreMirOperand left, SafeCoreMirOperand right,
            bool and, SafeCoreHirNode node, SafeCoreType boolType, bool allowDynamic)
        {
            if (left.Kind == SafeCoreMirOperandKind.Constant)
                return left.Value == (and ? "true" : "false") ? right : left;
            if (right.Kind == SafeCoreMirOperandKind.Constant)
                return right.Value == (and ? "true" : "false") ? left : right;
            if (!allowDynamic) Unsupported(node);
            int result = Temp(boolType, node);
            BlockBuilder evaluateRight = Block(node);
            BlockBuilder shortCircuit = Block(node);
            BlockBuilder join = Block(node);
            End(SafeCoreMirTerminator.Branch(left,
                and ? evaluateRight.Id : shortCircuit.Id,
                and ? shortCircuit.Id : evaluateRight.Id,
                Source(node)));
            _current = shortCircuit;
            Assign(result, SafeCoreMirOperand.Constant(boolType, and ? "false" : "true", Source(node)), node);
            End(SafeCoreMirTerminator.Goto(join.Id, Source(node)));
            _current = evaluateRight;
            Assign(result, right, node);
            End(SafeCoreMirTerminator.Goto(join.Id, Source(node)));
            _current = join;
            return SafeCoreMirOperand.Local(result, boolType, Source(node));
        }
    }
}
