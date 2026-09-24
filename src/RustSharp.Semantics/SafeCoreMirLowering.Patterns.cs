using System.Globalization;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirLowering
{
    private sealed partial class Lowerer
    {
        private IReadOnlyDictionary<int, int>? _patternChoices;
        private Dictionary<SafeCoreSymbol, int>? _patternDestinations;
        private bool _guardBindingPreview;

        private SafeCoreHirNode SelectedPattern(SafeCoreHirNode pattern, int depth)
        {
            for (int index = 0; pattern.Kind == N.OrPattern && index <= options.MaximumNestingDepth; index++)
            {
                Step(pattern, depth + index);
                int choice = _patternChoices is not null && _patternChoices.TryGetValue(pattern.Id, out int selected) ? selected : 0;
                pattern = Child(pattern, choice);
            }
            return pattern;
        }

        private List<Dictionary<int, int>> ExpandPattern(SafeCoreHirNode pattern, int depth)
        {
            Step(pattern, depth);
            if (pattern.Kind == N.OrPattern)
            {
                var alternatives = new List<Dictionary<int, int>>();
                for (int index = 0; index < pattern.ChildIds.Count; index++)
                    foreach (Dictionary<int, int> choice in ExpandPattern(Child(pattern, index), depth + 1))
                    {
                        Step(pattern, depth);
                        if (alternatives.Count >= options.MaximumPatternAlternatives) Limit(pattern);
                        choice.Add(pattern.Id, index);
                        alternatives.Add(choice);
                    }
                return alternatives;
            }
            var product = new List<Dictionary<int, int>> { new() };
            for (int childIndex = 0; childIndex < pattern.ChildIds.Count; childIndex++)
            {
                var next = new List<Dictionary<int, int>>();
                List<Dictionary<int, int>> children = ExpandPattern(Child(pattern, childIndex), depth + 1);
                foreach (Dictionary<int, int> prefix in product)
                foreach (Dictionary<int, int> suffix in children)
                {
                    Step(pattern, depth);
                    if (next.Count >= options.MaximumPatternAlternatives) Limit(pattern);
                    var choice = new Dictionary<int, int>(prefix);
                    foreach ((int node, int selected) in suffix) { Step(pattern, depth); choice.Add(node, selected); }
                    next.Add(choice);
                }
                product = next;
            }
            return product;
        }

        private SafeCoreMirOperand MatchPatterns(SafeCoreHirNode node, int depth)
        {
            (SafeCoreMirPlace place, SafeCoreType type) = ResolvePlace(Child(node, 0), depth + 1);
            SafeCoreMirOperand scrutinee = SafeCoreMirOperand.PlaceValue(place, type, Source(Child(node, 0)));
            SafeCoreType resultType = EffectiveType(node);
            int? destination = resultType.Kind is K.Unit or K.Never ? null : Temp(resultType, node);
            BlockBuilder join = Block(node);
            var outer = new Dictionary<SafeCoreSymbol, int>(_bindings);
            var outerCaptures = new Dictionary<SafeCoreSymbol, CapturedPlace>(_capturePlaces);
            IReadOnlyDictionary<int, int>? savedChoices = _patternChoices;
            int alternativeCount = 0;
            try
            {
                for (int armIndex = 1; armIndex < node.ChildIds.Count; armIndex++)
                {
                    SafeCoreHirNode arm = Child(node, armIndex);
                    foreach (Dictionary<int, int> choices in ExpandPattern(Child(arm, 0), depth + 1))
                    {
                        Step(arm, depth);
                        if (++alternativeCount > options.MaximumPatternAlternatives) Limit(arm);
                        RestorePatternScope(outer, outerCaptures);
                        _patternChoices = choices;
                        SafeCoreMirOperand condition = PatternCondition(Child(arm, 0), scrutinee, depth + 1);
                        BlockBuilder matched = Block(arm);
                        BlockBuilder fail = Block(arm);
                        End(SafeCoreMirTerminator.Branch(condition, matched.Id, fail.Id, Source(arm)));
                        _current = matched;
                        if (arm.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasGuard))
                        {
                            _guardBindingPreview = true;
                            try { BindPatternLocals(Child(arm, 0), scrutinee, depth + 1); }
                            finally { _guardBindingPreview = false; }
                            SafeCoreMirOperand? guard = Expr(Child(arm, 1), depth + 1);
                            if (_current is not null && guard is not null)
                            {
                                BlockBuilder body = Block(arm);
                                End(SafeCoreMirTerminator.Branch(guard, body.Id, fail.Id, Source(arm)));
                                _current = body;
                            }
                            RestorePatternScope(outer, outerCaptures);
                        }
                        if (_current is not null)
                        {
                            BindPatternLocals(Child(arm, 0), scrutinee, depth + 1);
                            SafeCoreMirOperand? body = Expr(Child(arm, arm.ChildIds.Count - 1), depth + 1);
                            if (_current is not null) Join(destination, body, join, arm);
                        }
                        _current = fail;
                    }
                }
                End(SafeCoreMirTerminator.Unreachable(Source(node)));
                _current = join;
                return destination is int local ? SafeCoreMirOperand.Local(local, resultType, Source(node)) : Unit(node);
            }
            finally { _patternChoices = savedChoices; RestorePatternScope(outer, outerCaptures); }
        }

        private void RestorePatternScope(IReadOnlyDictionary<SafeCoreSymbol, int> bindings,
            IReadOnlyDictionary<SafeCoreSymbol, CapturedPlace> captures)
        {
            _bindings.Clear();
            foreach ((SafeCoreSymbol symbol, int local) in bindings) { Step(input.Hir.Root!, 0); _bindings[symbol] = local; }
            _capturePlaces.Clear();
            foreach ((SafeCoreSymbol symbol, CapturedPlace capture) in captures) { Step(input.Hir.Root!, 0); _capturePlaces[symbol] = capture; }
        }

        private SafeCoreMirOperand PatternLet(SafeCoreHirNode declaration, SafeCoreHirNode pattern,
            SafeCoreHirNode initializer, int depth)
        {
            (SafeCoreMirPlace place, SafeCoreType type) = ResolvePlace(initializer, depth + 1);
            SafeCoreMirOperand value = SafeCoreMirOperand.PlaceValue(place, type, Source(initializer));
            BindDeclarationPattern(pattern, value, declaration, depth + 1);
            return Unit(declaration);
        }

        private void BindDeclarationPattern(SafeCoreHirNode pattern, SafeCoreMirOperand value, SafeCoreHirNode declaration, int depth)
        {
            List<Dictionary<int, int>> alternatives = ExpandPattern(pattern, depth + 1);
            if (alternatives.Count == 1 && !declaration.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse))
            { BindPatternLocals(pattern, value, depth + 1); return; }
            var outer = new Dictionary<SafeCoreSymbol, int>(_bindings);
            IReadOnlyDictionary<int, int>? savedChoices = _patternChoices;
            Dictionary<SafeCoreSymbol, int>? savedDestinations = _patternDestinations;
            _patternDestinations = [];
            BlockBuilder join = Block(declaration);
            try
            {
                foreach (Dictionary<int, int> choices in alternatives)
                {
                    Step(pattern, depth);
                    _patternChoices = choices;
                    SafeCoreMirOperand condition = PatternCondition(pattern, value, depth + 1);
                    BlockBuilder matched = Block(pattern);
                    BlockBuilder fail = Block(pattern);
                    End(SafeCoreMirTerminator.Branch(condition, matched.Id, fail.Id, Source(pattern)));
                    _current = matched;
                    BindPatternLocals(pattern, value, depth + 1);
                    End(SafeCoreMirTerminator.Goto(join.Id, Source(pattern)));
                    _current = fail;
                }
                var selectedBindings = new Dictionary<SafeCoreSymbol, int>(_bindings);
                _bindings.Clear(); foreach ((SafeCoreSymbol symbol, int local) in outer) _bindings[symbol] = local;
                if (declaration.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse))
                {
                    _ = Expr(Child(declaration, declaration.ChildIds.Count - 1), depth + 1);
                    if (_current is not null) Invalid(declaration);
                }
                else End(SafeCoreMirTerminator.Unreachable(Source(declaration)));
                _bindings.Clear(); foreach ((SafeCoreSymbol symbol, int local) in selectedBindings) _bindings[symbol] = local;
                _current = join;
            }
            finally { _patternChoices = savedChoices; _patternDestinations = savedDestinations; }
        }

        private void BindGuardPatternValue(SafeCoreHirNode pattern, SafeCoreMirOperand value)
        {
            SafeCoreType expected = Type(pattern);
            if (expected.Kind == K.Reference && expected != value.Type && expected.ElementType == value.Type)
            {
                SafeCoreType shared = SafeCoreType.Reference(value.Type, false);
                SafeCoreMirOperand reference = Emit(SafeCoreMirRvalue.Unary("&", value, shared, Source(pattern)), shared, pattern);
                _bindings[pattern.DeclaredSymbol!] = reference.Id;
                return;
            }
            SafeCoreType previewType = SafeCoreType.Reference(value.Type, false);
            SafeCoreMirOperand preview = Emit(SafeCoreMirRvalue.Unary("&", value, previewType, Source(pattern)), previewType, pattern);
            _capturePlaces[pattern.DeclaredSymbol!] = new(
                ProjectPlace(SafeCoreMirPlace.Root(preview.Id), SafeCoreMirProjection.Dereference(), pattern), value.Type);
        }

        private SafeCoreMirOperand PatternConstant(SafeCoreHirNode pattern, int depth)
        {
            Step(pattern, depth);
            if (pattern.Kind == N.LiteralPattern)
            {
                if (pattern.Value?.StartsWith('-') == true)
                {
                    var positive = new SafeCoreHirNode(pattern.Id, N.LiteralExpression, pattern.Span,
                        pattern.Name, pattern.Value[1..], pattern.Modifiers, null, null, []);
                    return Literal(positive, Type(pattern), negate: true, origin: pattern);
                }
                return Literal(pattern, Type(pattern));
            }
            if (pattern.ReferencedSymbol is { } symbol)
                for (int index = 0; index < input.Hir.Nodes.Count; index++)
                {
                    SafeCoreHirNode declaration = input.Hir.Nodes[index];
                    Step(declaration, depth);
                    if (declaration.DeclaredSymbol == symbol && input.Constants.TryGetValue(declaration.Id, out SafeCoreEvaluatedConstant? constant))
                        return MaterializeConstant(constant, pattern, depth + 1);
                }
            Unsupported(pattern); return null!;
        }

        private SafeCoreMirOperand PatternRangeCondition(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            SafeCoreType boolean = SafeCoreType.Primitive(K.Bool);
            SafeCoreMirOperand condition = SafeCoreMirOperand.Constant(boolean, "true", Source(pattern));
            bool start = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeStart);
            if (start)
                condition = Emit(SafeCoreMirRvalue.Binary(">=", value, PatternConstant(Child(pattern, 0), depth + 1), boolean, Source(pattern)), boolean, pattern);
            if (pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeEnd))
            {
                SafeCoreMirOperand end = Emit(SafeCoreMirRvalue.Binary(pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.InclusiveRange) ? "<=" : "<",
                    value, PatternConstant(Child(pattern, start ? 1 : 0), depth + 1), boolean, Source(pattern)), boolean, pattern);
                condition = CombineCondition(condition, end, and: true, pattern, boolean, allowDynamic: true);
            }
            return condition;
        }

        private List<(SafeCoreHirNode Pattern, SafeCoreMirOperand Value)> AggregatePatternFields(SafeCoreHirNode pattern,
            SafeCoreMirOperand value, int depth)
        {
            value = PatternValue(value, pattern, depth);
            var fields = new List<(SafeCoreHirNode, SafeCoreMirOperand)>();
            if (pattern.Kind == N.TuplePattern && pattern.ChildIds.Count == 1 &&
                !pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma) && Child(pattern, 0).Kind != N.RestPattern)
            { fields.Add((Child(pattern, 0), value)); return fields; }
            SafeCoreMirAdtLayout? layout = value.Type.Kind == K.Adt && value.Type.Name is { } name && _adtLayouts.TryGetValue(name, out var found) ? found : null;
            int arity = value.Type.Kind == K.Tuple ? value.Type.Elements.Count : layout?.Fields.Count ?? 0;
            int position = 0;
            for (int index = 0; index < pattern.ChildIds.Count; index++)
            {
                SafeCoreHirNode child = Child(pattern, index);
                Step(child, depth);
                if (child.Kind == N.RestPattern) { position = arity - (pattern.ChildIds.Count - index - 1); continue; }
                int fieldIndex = pattern.Kind == N.StructPattern && layout is not null ? FindAdtField(layout, child.Name!, child) : position++;
                if (fieldIndex < 0 || fieldIndex >= arity) Invalid(child);
                SafeCoreType fieldType = layout?.Fields[fieldIndex].Type ?? value.Type.Elements[fieldIndex];
                SafeCoreMirProjection projection = layout is null ? SafeCoreMirProjection.TupleIndex(fieldIndex) : SafeCoreMirProjection.Field(layout.Fields[fieldIndex].Name);
                if (child.Kind == N.StructPatternField) child = Child(child, 0);
                fields.Add((child, PatternField(value, projection, fieldType, child)));
            }
            return fields;
        }

        private SafeCoreMirOperand AggregatePatternCondition(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            SafeCoreType boolean = SafeCoreType.Primitive(K.Bool);
            SafeCoreMirOperand condition = SafeCoreMirOperand.Constant(boolean, "true", Source(pattern));
            foreach ((SafeCoreHirNode child, SafeCoreMirOperand field) in AggregatePatternFields(pattern, value, depth + 1))
            {
                Step(child, depth);
                SafeCoreMirOperand test = PatternCondition(child, field, depth + 1);
                condition = CombineCondition(condition, test, and: true, pattern, boolean, allowDynamic: true);
            }
            return condition;
        }

        private int SlicePatternRest(SafeCoreHirNode pattern, int depth)
        {
            for (int index = 0; index < pattern.ChildIds.Count; index++)
            {
                Step(pattern, depth);
                SafeCoreHirNode child = Child(pattern, index);
                if (child.Kind == N.RestPattern || child.Kind == N.AtPattern && Child(child, 1).Kind == N.RestPattern) return index;
            }
            return -1;
        }

        private SafeCoreMirOperand SlicePatternLength(SafeCoreMirOperand value, SafeCoreHirNode pattern)
        {
            if (value.Type.Kind == K.Array)
                return SafeCoreMirOperand.Constant(SafeCoreType.Primitive(K.Usize), value.Type.Length!.Value.ToString(CultureInfo.InvariantCulture), Source(pattern));
            SafeCoreType referenceType = SafeCoreType.Reference(value.Type, false);
            SafeCoreMirOperand reference = Emit(SafeCoreMirRvalue.Unary("&", value, referenceType, Source(pattern)), referenceType, pattern);
            return Emit(SafeCoreMirRvalue.SliceLength(reference, Source(pattern)), SafeCoreType.Primitive(K.Usize), pattern);
        }

        private SafeCoreMirOperand SlicePatternIndex(SafeCoreMirOperand value, SafeCoreMirOperand index, SafeCoreHirNode pattern)
        {
            SafeCoreMirProjection projection;
            if (index.Kind == SafeCoreMirOperandKind.Constant && int.TryParse(index.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
                projection = SafeCoreMirProjection.ArrayIndex(offset);
            else
            {
                if (index.Kind != SafeCoreMirOperandKind.Local) index = SnapshotOperand(index, pattern);
                projection = SafeCoreMirProjection.DynamicIndex(index.Id);
            }
            return PatternField(value, projection, value.Type.ElementType, pattern);
        }

        private List<(SafeCoreHirNode Pattern, SafeCoreMirOperand Value)> SlicePatternFields(SafeCoreHirNode pattern,
            SafeCoreMirOperand value, SafeCoreMirOperand length, int depth)
        {
            int rest = SlicePatternRest(pattern, depth);
            var fields = new List<(SafeCoreHirNode, SafeCoreMirOperand)>();
            for (int index = 0; index < pattern.ChildIds.Count; index++)
            {
                Step(pattern, depth);
                if (index == rest) continue;
                SafeCoreHirNode child = Child(pattern, index);
                SafeCoreMirOperand offset = SafeCoreMirOperand.Constant(length.Type,
                    index.ToString(CultureInfo.InvariantCulture), Source(child));
                if (rest >= 0 && index > rest)
                {
                    int suffix = pattern.ChildIds.Count - index;
                    if (value.Type.Kind == K.Slice)
                    {
                        fields.Add((child, PatternField(value,
                            SafeCoreMirProjection.FromEndIndex(suffix, pattern.ChildIds.Count - 1), value.Type.ElementType, child)));
                        continue;
                    }
                    offset = SafeCoreMirOperand.Constant(length.Type,
                        (value.Type.Length!.Value - suffix).ToString(CultureInfo.InvariantCulture), Source(child));
                }
                fields.Add((child, SlicePatternIndex(value, offset, child)));
            }
            return fields;
        }

        private SafeCoreMirOperand SlicePatternCondition(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            value = PatternValue(value, pattern, depth);
            SafeCoreMirOperand length = SlicePatternLength(value, pattern);
            int rest = SlicePatternRest(pattern, depth);
            SafeCoreType boolean = SafeCoreType.Primitive(K.Bool);
            SafeCoreMirOperand count = SafeCoreMirOperand.Constant(length.Type,
                (pattern.ChildIds.Count - (rest < 0 ? 0 : 1)).ToString(CultureInfo.InvariantCulture), Source(pattern));
            SafeCoreMirOperand matches = Emit(SafeCoreMirRvalue.Binary(rest < 0 ? "==" : ">=", length, count, boolean, Source(pattern)), boolean, pattern);
            int result = Temp(boolean, pattern);
            BlockBuilder payload = Block(pattern), fail = Block(pattern), join = Block(pattern);
            End(SafeCoreMirTerminator.Branch(matches, payload.Id, fail.Id, Source(pattern)));
            _current = fail; Assign(result, SafeCoreMirOperand.Constant(boolean, "false", Source(pattern)), pattern);
            End(SafeCoreMirTerminator.Goto(join.Id, Source(pattern)));
            _current = payload;
            SafeCoreMirOperand condition = SafeCoreMirOperand.Constant(boolean, "true", Source(pattern));
            foreach ((SafeCoreHirNode child, SafeCoreMirOperand field) in SlicePatternFields(pattern, value, length, depth + 1))
            {
                Step(child, depth);
                condition = CombineCondition(condition, PatternCondition(child, field, depth + 1), and: true, pattern, boolean, allowDynamic: true);
            }
            Assign(result, condition, pattern); End(SafeCoreMirTerminator.Goto(join.Id, Source(pattern)));
            _current = join;
            return SafeCoreMirOperand.Local(result, boolean, Source(pattern));
        }

        private void BindSlicePattern(SafeCoreHirNode pattern, SafeCoreMirOperand value, int depth)
        {
            SafeCoreMirOperand original = value;
            value = PatternValue(value, pattern, depth);
            int rest = SlicePatternRest(pattern, depth);
            SafeCoreHirNode? restBinding = rest >= 0 && Child(pattern, rest).Kind == N.AtPattern ? Child(Child(pattern, rest), 0) : null;
            SafeCoreMirOperand? restOwner = null;
            if (restBinding is not null && Type(restBinding).Kind == K.Reference)
            {
                if (original.Type.Kind == K.Reference)
                {
                    restOwner = original;
                    for (int level = 0; restOwner.Type.ElementType.Kind == K.Reference && level < options.MaximumNestingDepth; level++)
                    {
                        Step(pattern, depth + level);
                        restOwner = DereferencePattern(restOwner, pattern);
                    }
                }
                else
                {
                    // All explicit ref bindings share one parent borrow. The
                    // field and rest borrows then derive disjoint child loans.
                    SafeCoreType parentType = SafeCoreType.Reference(value.Type, PatternBorrowsMutably(pattern, depth + 1) && !_guardBindingPreview);
                    restOwner = Emit(SafeCoreMirRvalue.Unary(parentType.IsMutable ? "&mut" : "&", value, parentType, Source(pattern)), parentType, pattern);
                    value = DereferencePattern(restOwner, pattern);
                }
            }
            SafeCoreMirOperand length = SlicePatternLength(value, pattern);
            foreach ((SafeCoreHirNode child, SafeCoreMirOperand field) in SlicePatternFields(pattern, value, length, depth + 1))
            { Step(child, depth); BindPatternLocals(child, field, depth + 1); }
            if (restBinding is null) return;
            SafeCoreHirNode binding = restBinding;
            SafeCoreType bindingType = Type(binding);
            int suffix = pattern.ChildIds.Count - rest - 1;
            if (bindingType.Kind == K.Array)
            {
                var items = new List<SafeCoreMirOperand>();
                for (int index = 0; index < bindingType.Length!.Value; index++)
                {
                    Step(binding, depth);
                    items.Add(SlicePatternIndex(value, SafeCoreMirOperand.Constant(length.Type,
                        (rest + index).ToString(CultureInfo.InvariantCulture), Source(binding)), binding));
                }
                SafeCoreMirOperand array = Emit(SafeCoreMirRvalue.Array(items, bindingType, Source(binding), cancellation), bindingType, binding);
                BindPatternValue(binding, array);
            }
            else
            {
                SafeCoreType resultType = _guardBindingPreview ? SafeCoreType.Reference(bindingType.ElementType, false) : bindingType;
                SafeCoreMirOperand slice = Emit(SafeCoreMirRvalue.PatternSubslice(restOwner!, rest, suffix, resultType, Source(binding)), resultType, binding);
                if (_guardBindingPreview) _bindings[binding.DeclaredSymbol!] = slice.Id;
                else BindPatternValue(binding, slice);
            }
        }

        private bool PatternBorrowsMutably(SafeCoreHirNode pattern, int depth)
        {
            Step(pattern, depth);
            if (pattern.Kind == N.IdentifierPattern && Type(pattern).Kind == K.Reference && Type(pattern).IsMutable) return true;
            for (int index = 0; index < pattern.ChildIds.Count; index++)
                if (PatternBorrowsMutably(Child(pattern, index), depth + 1)) return true;
            return false;
        }
    }
}
