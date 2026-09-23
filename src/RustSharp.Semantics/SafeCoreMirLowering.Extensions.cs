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
            IReadOnlyDictionary<SafeCoreSymbol, SafeCoreMirOperand> Captures);

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
            if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse)) Unsupported(node);
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
            if (initializer.Kind == N.NameExpression && initializer.ReferencedSymbol is { } escapedSymbol &&
                _closures.ContainsKey(escapedSymbol))
                Unsupported(initializer);
            if (initializer.Kind == N.ClosureExpression && pattern.Kind == N.IdentifierPattern &&
                pattern.DeclaredSymbol is not null)
            {
                SafeCoreType closureType = Type(initializer);
                if (closureType.Kind != K.Closure) Unsupported(initializer);
                var captures = new Dictionary<SafeCoreSymbol, SafeCoreMirOperand>();
                CollectClosureCaptures(Child(initializer, initializer.ChildIds.Count - 1), captures, initializer);
                _closures[pattern.DeclaredSymbol] = new(initializer, closureType, captures);
                return Unit(node);
            }
            // Bind a direct borrow into its destination slot so the MIR carries
            // the owner/reference pair without introducing an unproven alias
            // copy through a temporary reference local.
            if (initializer.Kind == N.UnaryExpression && initializer.Value is ("&" or "&mut") &&
                pattern.Kind == N.IdentifierPattern && pattern.DeclaredSymbol is not null)
            {
                SafeCoreType referenceType = Type(initializer);
                int borrowLocal = Local(pattern.Name ?? string.Empty, referenceType, SafeCoreMirLocalKind.User,
                    pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                if (!_bindings.TryAdd(pattern.DeclaredSymbol, borrowLocal)) Invalid(pattern);
                LowerBorrow(initializer, borrowLocal, depth);
                return Unit(node);
            }
            if (Type(initializer).Kind == K.Adt && pattern.Kind != N.IdentifierPattern) Unsupported(initializer);
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
            if (type.Kind == K.Adt && (value.Kind != SafeCoreMirOperandKind.Constant || value.Value != "()" ||
                _loops.Count != 0 || _closureReturns.Count != 0 || _dropScopes.Count == 0)) Unsupported(initializer);
            int local = Local(pattern.Name, type, SafeCoreMirLocalKind.User,
                pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
            if (!_bindings.TryAdd(pattern.DeclaredSymbol!, local)) Invalid(pattern);
            if (value.Kind == SafeCoreMirOperandKind.Local && value.Type.Kind == K.Reference &&
                _referenceOwners.TryGetValue(value.Id, out int ownerId))
                _referenceOwners[local] = ownerId;
            Assign(local, value, node);
            if (type.Kind == K.Adt && _dropFunctions.ContainsKey(type.Name!))
                _dropScopes[^1].Add(local);
            if (initializer.Kind == N.ClosureExpression && type.Kind == K.Closure)
            {
                var captures = new Dictionary<SafeCoreSymbol, SafeCoreMirOperand>();
                CollectClosureCaptures(Child(initializer, initializer.ChildIds.Count - 1), captures, initializer);
                _closures[pattern.DeclaredSymbol!] = new(initializer, type, captures);
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
            bool reborrow = borrowed.Kind == N.UnaryExpression && borrowed.Value == "*";
            SafeCoreHirNode ownerNode = reborrow ? UnwrapExpression(Child(borrowed, 0), depth + 1) : borrowed;
            int owner = -1;
            if (ownerNode.Kind != N.NameExpression || ownerNode.ReferencedSymbol is null ||
                !_bindings.TryGetValue(ownerNode.ReferencedSymbol, out owner)) Unsupported(borrowed);
            SafeCoreType ownerType = _locals[owner].Type;
            SafeCoreType referenceType = _locals[destination].Type;
            bool mutable = node.Value == "&mut";
            if (referenceType.Kind != K.Reference || referenceType.IsMutable != mutable) Invalid(node);
            if (reborrow)
            {
                if (ownerType.Kind != K.Reference || ownerType.ElementType != referenceType.ElementType ||
                    !_referenceOwners.ContainsKey(owner)) Unsupported(node);
                _referenceOwners[destination] = _referenceOwners[owner];
            }
            else
            {
                if (ownerType.Kind == K.Reference || !IsStructuralCopy(ownerType)) Unsupported(node);
                if (ownerType != referenceType.ElementType) Invalid(node);
                if (mutable && !_locals[owner].IsMutable)
                    throw new LoweringException(new(SafeCoreOwnershipDiagnosticCodes.ImmutableBorrow,
                        "A mutable borrow requires a mutable owner binding.", node.Span));
                _referenceOwners[destination] = owner;
            }
            string operation = reborrow ? mutable ? "reborrow_mut" : "reborrow" : node.Value!;
            _current!.Statements.Add(new(destination,
                SafeCoreMirRvalue.Unary(operation, SafeCoreMirOperand.Local(owner, ownerType, Source(ownerNode)),
                    referenceType, Source(node)), Source(node)));
        }

        private void CollectClosureCaptures(SafeCoreHirNode node,
            Dictionary<SafeCoreSymbol, SafeCoreMirOperand> captures, SafeCoreHirNode closure)
        {
            Step(node, 0);
            if (node.Kind == N.NameExpression && node.ReferencedSymbol is { } symbol &&
                _bindings.TryGetValue(symbol, out int local))
            {
                SafeCoreMirLocal source = _locals[local];
                if (!IsStructuralCopy(source.Type)) Unsupported(node);
                // Mutable captures require write-back into the original place.
                // Closure expansion currently snapshots captures into detached
                // locals, so accepting this shape would silently lose writes.
                // Reject it at the lowering boundary until place-aware capture
                // lowering is available.
                if (source.IsMutable) Unsupported(node);
                SafeCoreMirOperand operand = SafeCoreMirOperand.Local(local, source.Type, Source(node));
                if (closure.Modifiers.HasFlag(SafeCoreHirNodeModifiers.MoveCapture))
                    operand = Emit(SafeCoreMirRvalue.Use(operand, Source(node)), source.Type, node);
                captures.TryAdd(symbol, operand);
                return;
            }
            for (int childIndex = 0; childIndex < node.ChildIds.Count; childIndex++)
            {
                SafeCoreHirNode child = Child(node, childIndex);
                CollectClosureCaptures(child, captures, closure);
            }
        }

        private SafeCoreMirOperand ClosureCall(SafeCoreHirNode call, SafeCoreHirNode calleeNode, int depth)
        {
            ClosureBinding closure = null!;
            if (calleeNode.Kind == N.NameExpression && calleeNode.ReferencedSymbol is { } closureSymbol &&
                _closures.TryGetValue(closureSymbol, out ClosureBinding? known)) closure = known;
            else if (calleeNode.Kind == N.ClosureExpression)
                closure = new(calleeNode, Type(calleeNode), new Dictionary<SafeCoreSymbol, SafeCoreMirOperand>());
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
                arguments.Add(argument);
            }

            int? destination = signature.ReturnType.Kind is K.Unit or K.Never ? null : Temp(signature.ReturnType, call);
            BlockBuilder join = Block(call);
            var savedBindings = new Dictionary<SafeCoreSymbol, int>(_bindings);
            _bindings.Clear();
            foreach ((SafeCoreSymbol captureSymbol, SafeCoreMirOperand operand) in closure.Captures)
            {
                int capture = Temp(operand.Type, closure.Node);
                Assign(capture, operand, closure.Node);
                _bindings[captureSymbol] = capture;
            }
            try
            {
                int parameterIndex = 0;
                for (int childIndex = 0; childIndex < closure.Node.ChildIds.Count; childIndex++)
                {
                    SafeCoreHirNode parameter = Child(closure.Node, childIndex);
                    if (parameter.Kind != N.Parameter) continue;
                    SafeCoreHirNode pattern = UnwrapPattern(Child(parameter, 0));
                    if (pattern.Kind != N.IdentifierPattern || pattern.DeclaredSymbol is null)
                        Unsupported(pattern);
                    SafeCoreType type = signature.ParameterTypes[parameterIndex++];
                    int local = Local(pattern.Name ?? $"closure_arg{parameterIndex.ToString(CultureInfo.InvariantCulture)}",
                        type, SafeCoreMirLocalKind.User, pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                    _bindings[pattern.DeclaredSymbol] = local;
                    Assign(local, arguments[parameterIndex - 1], pattern);
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
            }
            return destination is int result ? SafeCoreMirOperand.Local(result, signature.ReturnType, Source(call)) : Unit(call);
        }

        private void BindIrrefutablePattern(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            Step(pattern, depth);
            switch (pattern.Kind)
            {
                case N.WildcardPattern: return;
                case N.IdentifierPattern:
                    if (pattern.DeclaredSymbol is null || pattern.Name is null) Invalid(pattern);
                    int local = Local(pattern.Name, value.Type, SafeCoreMirLocalKind.User,
                        pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                    if (!_bindings.TryAdd(pattern.DeclaredSymbol!, local)) Invalid(pattern);
                    if (value.Kind == SafeCoreMirOperandKind.Local && value.Type.Kind == K.Reference &&
                        _referenceOwners.TryGetValue(value.Id, out int ownerId))
                        _referenceOwners[local] = ownerId;
                    Assign(local, value, pattern);
                    return;
                case N.TuplePattern:
                    for (int index = 0; index < pattern.ChildIds.Count; index++)
                    {
                        SafeCoreHirNode child = Child(pattern, index);
                        if (child.Kind == N.RestPattern) continue;
                        SafeCoreType fieldType = value.Type.Kind == K.Tuple && index < value.Type.Elements.Count
                            ? value.Type.Elements[index] : Type(child);
                        SafeCoreMirOperand field = Emit(SafeCoreMirRvalue.Field(value, index, fieldType, Source(child)), fieldType, child);
                        BindIrrefutablePattern(child, field, depth + 1);
                    }
                    return;
                default: Unsupported(pattern); return;
            }
        }

        private SafeCoreMirOperand Member(SafeCoreHirNode node, int depth)
        {
            SafeCoreMirOperand? target = Expr(Child(node, 0), depth + 1);
            if (target is null) return null!;
            int index = 0;
            if (target.Type.Kind != K.Tuple || !int.TryParse(node.Name, NumberStyles.None, CultureInfo.InvariantCulture, out index) ||
                index < 0 || index >= target.Type.Elements.Count) Unsupported(node);
            SafeCoreType result = target.Type.Elements[index];
            return Emit(SafeCoreMirRvalue.Field(target, index, result, Source(node)), result, node);
        }

        private SafeCoreMirOperand Match(SafeCoreHirNode node, int depth)
        {
            SafeCoreMirOperand? scrutinee = Expr(Child(node, 0), depth + 1);
            if (scrutinee is null) return null!;
            SafeCoreType resultType = EffectiveType(node);
            int? destination = resultType.Kind is K.Unit or K.Never ? null : Temp(resultType, node);
            BlockBuilder join = Block(node);
            Dictionary<SafeCoreSymbol, int> outer = new(_bindings);
            BlockBuilder? test = _current;
            int armCount = node.ChildIds.Count - 1;
            if (armCount > options.MaximumPatternAlternatives) Limit(node);
            for (int index = 0; index < armCount; index++)
            {
                SafeCoreHirNode arm = Child(node, index + 1);
                if (test is null) break;
                _current = test;
                _bindings.Clear(); foreach ((SafeCoreSymbol s, int l) in outer) _bindings[s] = l;
                BindPatternLocals(Child(arm, 0), scrutinee, depth + 1);
                SafeCoreMirOperand condition = PatternCondition(Child(arm, 0), scrutinee, depth + 1);
                BlockBuilder body = Block(arm);
                BlockBuilder fail = index == armCount - 1 ? Block(node) : Block(Child(node, index + 2));
                if (arm.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasGuard))
                {
                    BlockBuilder guardBlock = Block(arm);
                    End(SafeCoreMirTerminator.Branch(condition, guardBlock.Id, fail.Id, Source(arm)));
                    _current = guardBlock;
                    SafeCoreMirOperand? guard = Expr(Child(arm, 1), depth + 1);
                    if (guard is null) return null!;
                    End(SafeCoreMirTerminator.Branch(guard, body.Id, fail.Id, Source(arm)));
                }
                else End(SafeCoreMirTerminator.Branch(condition, body.Id, fail.Id, Source(arm)));
                _current = body;
                SafeCoreMirOperand? value = Expr(Child(arm, arm.ChildIds.Count - 1), depth + 1);
                if (_current is not null) Join(destination, value, join, arm);
                test = fail;
            }
            _current = test;
            if (_current is not null) End(SafeCoreMirTerminator.Unreachable(Source(node)));
            _bindings.Clear(); foreach ((SafeCoreSymbol s, int l) in outer) _bindings[s] = l;
            _current = join;
            return destination is int local ? SafeCoreMirOperand.Local(local, resultType, Source(node)) : Unit(node);
        }

        private void BindPatternLocals(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
              Step(pattern, depth);
              if (pattern.Kind == N.OrPattern && PatternContainsBinding(pattern, depth + 1))
                  Unsupported(pattern);
              switch (pattern.Kind)
            {
                case N.IdentifierPattern when pattern.DeclaredSymbol is not null:
                    int local = Local(pattern.Name ?? "match", value.Type, SafeCoreMirLocalKind.User,
                        pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                    _bindings[pattern.DeclaredSymbol] = local;
                    Assign(local, value, pattern);
                    return;
                case N.TuplePattern:
                    for (int index = 0; index < pattern.ChildIds.Count; index++)
                    {
                        SafeCoreHirNode child = Child(pattern, index);
                        if (child.Kind == N.RestPattern) continue;
                        SafeCoreType fieldType = value.Type.Kind == K.Tuple && index < value.Type.Elements.Count
                            ? value.Type.Elements[index] : Type(child);
                        SafeCoreMirOperand field = Emit(SafeCoreMirRvalue.Field(value, index, fieldType, Source(child)), fieldType, child);
                        BindPatternLocals(child, field, depth + 1);
                    }
                    return;
                case N.OrPattern:
                    BindPatternLocals(Child(pattern, 0), value, depth + 1); return;
                case N.AtPattern:
                    BindPatternLocals(Child(pattern, 0), value, depth + 1);
                    BindPatternLocals(Child(pattern, 1), value, depth + 1); return;
                  default: return;
              }
          }

          private bool PatternContainsBinding(SafeCoreHirNode pattern, int depth)
          {
              Step(pattern, depth);
              if (pattern.Kind is N.IdentifierPattern or N.AtPattern)
                  return true;
              for (int childIndex = 0; childIndex < pattern.ChildIds.Count; childIndex++)
                  if (PatternContainsBinding(Child(pattern, childIndex), depth + 1))
                      return true;
              return false;
          }

        private SafeCoreMirOperand PatternCondition(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            Step(pattern, depth);
            SafeCoreType boolType = SafeCoreType.Primitive(K.Bool);
            switch (pattern.Kind)
            {
                case N.WildcardPattern:
                case N.IdentifierPattern when pattern.DeclaredSymbol is not null:
                    return SafeCoreMirOperand.Constant(boolType, "true", Source(pattern));
                case N.LiteralPattern:
                    SafeCoreMirOperand literal = Literal(pattern, Type(pattern));
                    return Emit(SafeCoreMirRvalue.Binary("==", value, literal, boolType, Source(pattern)), boolType, pattern);
                case N.TuplePattern:
                    SafeCoreMirOperand? condition = null;
                    for (int index = 0; index < pattern.ChildIds.Count; index++)
                    {
                        SafeCoreHirNode child = Child(pattern, index);
                        if (child.Kind == N.RestPattern) continue;
                        SafeCoreType fieldType = value.Type.Kind == K.Tuple && index < value.Type.Elements.Count
                            ? value.Type.Elements[index] : Type(child);
                        SafeCoreMirOperand field = Emit(SafeCoreMirRvalue.Field(value, index, fieldType, Source(child)), fieldType, child);
                        SafeCoreMirOperand item = PatternCondition(child, field, depth + 1);
                        condition = condition is null ? item : CombineCondition(condition, item, and: true, pattern, boolType, allowDynamic: false);
                    }
                    return condition ?? SafeCoreMirOperand.Constant(boolType, "true", Source(pattern));
                case N.OrPattern:
                    SafeCoreMirOperand? alternative = null;
                    for (int childIndex = 0; childIndex < pattern.ChildIds.Count; childIndex++)
                    {
                        SafeCoreHirNode child = Child(pattern, childIndex);
                        SafeCoreMirOperand item = PatternCondition(child, value, depth + 1);
                        alternative = alternative is null ? item : CombineCondition(alternative, item, and: false, pattern, boolType, allowDynamic: true);
                    }
                    return alternative ?? SafeCoreMirOperand.Constant(boolType, "false", Source(pattern));
                case N.AtPattern: return PatternCondition(Child(pattern, 1), value, depth + 1);
                default: Unsupported(pattern); return null!;
            }
        }

        private SafeCoreMirOperand CombineCondition(SafeCoreMirOperand left, SafeCoreMirOperand right,
            bool and, SafeCoreHirNode node, SafeCoreType boolType, bool allowDynamic)
        {
            if (left.Kind == SafeCoreMirOperandKind.Constant && left.Value == (and ? "true" : "false"))
                return and ? right : left;
            if (right.Kind == SafeCoreMirOperandKind.Constant && right.Value == (and ? "true" : "false"))
                return and ? left : right;
            if (left.Kind == SafeCoreMirOperandKind.Constant && left.Value == (and ? "false" : "true"))
                return and ? left : right;
            if (right.Kind == SafeCoreMirOperandKind.Constant && right.Value == (and ? "false" : "true"))
                return and ? right : left;
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
