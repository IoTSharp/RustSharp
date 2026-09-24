namespace RustSharp.Semantics;

public static partial class SafeCoreMirReferenceProvenance
{
    private sealed partial class Worker
    {
        // One entry per reference-valued slot, including aggregate fields.
        private sealed class CompositeState
        {
            public Dictionary<string, List<SafeCoreMirReferenceOrigin>> Slots { get; } = new(StringComparer.Ordinal);
            public CompositeState Clone(Action step)
            {
                var copy = new CompositeState();
                foreach (var pair in Slots) { step(); copy.Slots[pair.Key] = [.. pair.Value]; }
                return copy;
            }
        }

        private static string Slot(int local, IReadOnlyList<SafeCoreMirProjection> path) => new SafeCoreMirPlace(local, path).ToString();

        private static bool IsVariantSlot(IReadOnlyList<SafeCoreMirProjection> path) =>
            path.Any(projection => projection.Name?.StartsWith("$v", StringComparison.Ordinal) == true);

        private IEnumerable<(IReadOnlyList<SafeCoreMirProjection> Path, SafeCoreType Type)> ReferenceSlots(
            SafeCoreType type, IReadOnlyList<SafeCoreMirProjection>? path = null, int depth = 0)
        {
            Step();
            if (depth >= 128) throw new ProvenanceLimitException();
            path ??= [];
            if (type.Kind == SafeCoreSemanticTypeKind.Reference) { yield return (path, type); yield break; }
            if (!ContainsStoredReference(type, depth)) yield break;
            if (type.Kind == SafeCoreSemanticTypeKind.Tuple)
                for (int index = 0; index < type.Elements.Count; index++)
                    foreach (var item in ReferenceSlots(type.Elements[index], [.. path, SafeCoreMirProjection.TupleIndex(index)], depth + 1)) yield return item;
            if (type.Kind == SafeCoreSemanticTypeKind.Array)
                for (int index = 0; index < type.Length; index++)
                {
                    Step();
                    foreach (var item in ReferenceSlots(type.ElementType!, [.. path, SafeCoreMirProjection.ArrayIndex(index)], depth + 1)) yield return item;
                }
            if (type.Kind == SafeCoreSemanticTypeKind.Adt)
                foreach (SafeCoreMirAdtLayout layout in program.AdtLayouts)
                {
                    Step();
                    if (layout.Type != type) continue;
                    foreach (SafeCoreMirAdtField field in layout.Fields)
                        foreach (var item in ReferenceSlots(field.Type, [.. path, SafeCoreMirProjection.Field(field.Name)], depth + 1)) yield return item;
                    break;
                }
        }

        private List<SafeCoreMirReferenceOrigin> ReadSlot(SafeCoreMirReferenceOrigin address,
            CompositeState state, SafeCoreMirSource source, bool report)
        {
            Step();
            if (address.IsStatic) return [address];
            if (address.IsParameter)
                return [address with { ParameterPath = [.. address.ParameterPath, SafeCoreMirProjection.Dereference(), .. address.Projections], Projections = [] }];
            if (state.Slots.TryGetValue(Slot(address.LocalId, address.Projections), out var origins)) return [.. origins];
            if (address.Projections.Any(p => p.Kind is SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.FromEndIndex))
            {
                var found = new List<SafeCoreMirReferenceOrigin>();
                string prefix = Slot(address.LocalId, address.Projections.TakeWhile(p => p.Kind is not (SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.FromEndIndex)).ToArray());
                foreach (var pair in state.Slots)
                {
                    Step();
                    if (pair.Key.StartsWith(prefix + "[", StringComparison.Ordinal)) MergeOrigins(found, pair.Value);
                }
                if (found.Count != 0) return found;
            }
            if (report) Add(InvalidOrigin, "A reference slot has no provenance initialized on every incoming control-flow path.", source);
            return [];
        }

        private List<SafeCoreMirReferenceOrigin> Locations(SafeCoreMirPlace place, CompositeState state,
            SafeCoreMirFunction function, SafeCoreMirSource source, bool report)
        {
            Step();
            var locations = new List<SafeCoreMirReferenceOrigin> { new(place.LocalId, false, [], function.Locals[place.LocalId].IsMutable) };
            SafeCoreType currentType = function.Locals[place.LocalId].Type;
            SafeCoreMirAdtVariant? variant = null;
            foreach (SafeCoreMirProjection originalProjection in place.Projections)
            {
                Step();
                SafeCoreMirProjection projection = originalProjection;
                if (projection.Kind == SafeCoreMirProjectionKind.Downcast)
                {
                    variant = program.AdtLayouts.First(layout => layout.Type == currentType).Variants[projection.Index];
                    continue;
                }
                if (variant is not null)
                {
                    int index = projection.Kind == SafeCoreMirProjectionKind.TupleIndex ? projection.Index :
                        variant.Fields.Select((field, fieldIndex) => (field, fieldIndex)).First(pair => pair.field.Name == projection.Name).fieldIndex;
                    var layout = program.AdtLayouts.First(layout => layout.Type == currentType);
                    currentType = variant.Fields[index].Type;
                    projection = SafeCoreMirProjection.Field(layout.Fields[variant.FieldOffset + index].Name);
                    variant = null;
                }
                else currentType = projection.Kind switch
                {
                    SafeCoreMirProjectionKind.Dereference or SafeCoreMirProjectionKind.ArrayIndex or SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.FromEndIndex => currentType.ElementType!,
                    SafeCoreMirProjectionKind.TupleIndex => currentType.Elements[projection.Index],
                    SafeCoreMirProjectionKind.Field => program.AdtLayouts.First(layout => layout.Type == currentType).Fields.First(field => field.Name == projection.Name).Type,
                    _ => currentType,
                };
                var next = new List<SafeCoreMirReferenceOrigin>();
                foreach (var location in locations)
                {
                    Step();
                    if (projection.Kind == SafeCoreMirProjectionKind.Dereference)
                        MergeOrigins(next, ReadSlot(location, state, source, report));
                    else MergeOrigins(next, [Append(location, [projection], location.IsMutable)]);
                }
                locations = next;
            }
            return locations;
        }

        private List<SafeCoreMirReferenceOrigin> ValueReferences(SafeCoreMirOperand operand,
            CompositeState state, SafeCoreMirFunction function, bool report)
        {
            var result = new List<SafeCoreMirReferenceOrigin>();
            if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place)) return result;
            foreach (var slot in ReferenceSlots(operand.Type))
            {
                Step();
                var place = new SafeCoreMirPlace(operand.Id, [.. operand.Place?.Projections ?? [], .. slot.Path]);
                foreach (var location in Locations(place, state, function, operand.Source, report))
                    foreach (var origin in ReadSlot(location, state, operand.Source,
                        report && !IsVariantSlot(slot.Path)))
                    {
                        Step();
                        MergeOrigins(result, [origin with { ValuePath = slot.Path, IsMutable = slot.Type.IsMutable }]);
                    }
            }
            return result;
        }

        private void CheckCompositeUse(SafeCoreMirOperand operand, CompositeState state,
            SafeCoreMirFunction function, bool report)
        {
            if (!report || operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place)) return;
            var origins = ValueReferences(operand, state, function, report);
            if (operand.Place is { IsRoot: false } place)
                MergeOrigins(origins, Locations(place, state, function, operand.Source, report));
            foreach (var origin in origins)
            {
                Step();
                CheckOriginExtent(origin, function, operand.Source, false, report);
            }
        }

        private List<SafeCoreMirReferenceOrigin> RvalueReferences(SafeCoreMirRvalue value,
            CompositeState state, SafeCoreMirFunction function, bool report)
        {
            Step();
            var result = new List<SafeCoreMirReferenceOrigin>();
            if (value.Kind == SafeCoreMirRvalueKind.PromotedBorrow)
                return [new(-1, false, [], false) { IsStatic = true }];
            if (value.Kind == SafeCoreMirRvalueKind.Unary && value.Operator is "&" or "&mut" or "reborrow" or "reborrow_mut")
            {
                var operand = value.Operands[0];
                if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place))
                { if (report) Add(InvalidOrigin, "A borrow requires a checked storage place.", value.Source); return []; }
                var place = operand.Place ?? SafeCoreMirPlace.Root(operand.Id);
                if (value.Operator is "reborrow" or "reborrow_mut") place = place.Append(SafeCoreMirProjection.Dereference());
                foreach (var origin in Locations(place, state, function, value.Source, report))
                    MergeOrigins(result, [origin with { IsMutable = value.Type.IsMutable, ValuePath = [] }]);
                return result;
            }
            if (value.Kind is SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Coerce or SafeCoreMirRvalueKind.Subslice)
            {
                result = ValueReferences(value.Operands[0], state, function, report);
                if (value.Type.Kind == SafeCoreSemanticTypeKind.Reference)
                    result = result.Select(origin => origin with { IsMutable = value.Type.IsMutable }).ToList();
                if (value.Kind == SafeCoreMirRvalueKind.Subslice)
                    result = result.Select(origin => origin with { HasUnknownSliceOffset = true }).ToList();
                return result;
            }
            if (value.Kind is SafeCoreMirRvalueKind.Tuple or SafeCoreMirRvalueKind.Array or SafeCoreMirRvalueKind.Adt or SafeCoreMirRvalueKind.Enum)
            {
                var layout = value.Type.Kind == SafeCoreSemanticTypeKind.Adt
                    ? program.AdtLayouts.First(item => item.Type == value.Type) : null;
                int offset = value.Kind == SafeCoreMirRvalueKind.Enum
                    ? layout!.Variants[int.Parse(value.Operator!, System.Globalization.CultureInfo.InvariantCulture)].FieldOffset : 0;
                for (int index = 0; index < value.Operands.Count; index++)
                {
                    Step();
                    var prefix = layout is not null ? SafeCoreMirProjection.Field(layout.Fields[offset + index].Name)
                        : value.Kind == SafeCoreMirRvalueKind.Tuple ? SafeCoreMirProjection.TupleIndex(index) : SafeCoreMirProjection.ArrayIndex(index);
                    foreach (var origin in ValueReferences(value.Operands[index], state, function, report))
                        MergeOrigins(result, [origin with { ValuePath = [prefix, .. origin.ValuePath] }]);
                }
            }
            if (value.Kind == SafeCoreMirRvalueKind.Field && value.Operands.Count == 1)
            {
                var operand = value.Operands[0];
                int index = int.Parse(value.Operator!, System.Globalization.CultureInfo.InvariantCulture);
                SafeCoreMirProjection projection = operand.Type.Kind == SafeCoreSemanticTypeKind.Tuple ? SafeCoreMirProjection.TupleIndex(index)
                    : SafeCoreMirProjection.Field(program.AdtLayouts.First(layout => layout.Type == operand.Type).Fields[index].Name);
                result = ValueReferences(SafeCoreMirOperand.PlaceValue((operand.Place ?? SafeCoreMirPlace.Root(operand.Id)).Append(projection), value.Type, value.Source), state, function, report);
            }
            if (value.Kind == SafeCoreMirRvalueKind.Index)
            {
                var operand = value.Operands[0];
                var index = value.Operands[1];
                var place = operand.Place ?? SafeCoreMirPlace.Root(operand.Id);
                if (operand.Type.Kind == SafeCoreSemanticTypeKind.Reference) place = place.Append(SafeCoreMirProjection.Dereference());
                var projection = index.Kind == SafeCoreMirOperandKind.Constant
                    ? SafeCoreMirProjection.ArrayIndex(int.Parse(index.Value!, System.Globalization.CultureInfo.InvariantCulture))
                    : SafeCoreMirProjection.DynamicIndex(index.Id);
                result = ValueReferences(SafeCoreMirOperand.PlaceValue(place.Append(projection), value.Type, value.Source), state, function, report);
            }
            if (value.Kind == SafeCoreMirRvalueKind.Unary && value.Operator == "*")
            {
                var operand = value.Operands[0];
                result = ValueReferences(SafeCoreMirOperand.PlaceValue((operand.Place ?? SafeCoreMirPlace.Root(operand.Id)).Append(SafeCoreMirProjection.Dereference()), value.Type, value.Source), state, function, report);
            }
            return result;
        }

        private void StoreReferences(SafeCoreMirPlace destination, SafeCoreType type, List<SafeCoreMirReferenceOrigin> origins,
            CompositeState state, SafeCoreMirFunction function, SafeCoreMirSource source, bool report)
        {
            foreach (var slot in ReferenceSlots(type))
            {
                Step();
                var slotPlace = new SafeCoreMirPlace(destination.LocalId, [.. destination.Projections, .. slot.Path]);
                var locations = Locations(slotPlace, state, function, source, report);
                var stored = origins.Where(origin => origin.ValuePath.SequenceEqual(slot.Path)).Select(origin => origin with { ValuePath = [] }).ToList();
                if (report && RequiresStaticPlace(slotPlace, function))
                    foreach (var origin in stored)
                        if (!origin.IsStatic) Add(EscapingReference, "A static reference slot requires promoted static storage.", source);
                foreach (var location in locations)
                {
                    Step();
                    if (location.IsParameter)
                    {
                        foreach (var origin in stored)
                        {
                            CheckOriginExtent(origin, function, source, true, report);
                            if (report && !origin.IsStatic && (origin.LocalId != location.LocalId ||
                                !origin.ParameterPath.SequenceEqual([.. location.ParameterPath, SafeCoreMirProjection.Dereference(), .. location.Projections])))
                                Add(EscapingReference, "Writing a reference into caller storage requires a proven matching lifetime or static origin.", source);
                        }
                        continue;
                    }
                    if (location.Projections.Any(projection => projection.Kind is
                        SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.FromEndIndex))
                    {
                        // A runtime index may overwrite any matching element. Keep
                        // the new origin on each initialized concrete slot so later
                        // constant-index reads and aggregate copies cannot observe
                        // only the stale pre-write provenance. A may-write cannot
                        // establish initialization for an unknown element.
                        foreach (var candidate in ReferenceSlots(function.Locals[location.LocalId].Type))
                        {
                            Step();
                            if (!MaySelectSlot(location.Projections, candidate.Path)) continue;
                            if (state.Slots.TryGetValue(Slot(location.LocalId, candidate.Path), out var previous))
                                MergeOrigins(previous, stored);
                        }
                        continue;
                    }
                    string key = Slot(location.LocalId, location.Projections);
                    // An inactive enum payload has no live reference. Preserve the
                    // initialized-empty fact through copies and joins. Missing source
                    // origins are diagnosed while reading the rvalue, and a direct
                    // reference call with no return origin remains unresolved.
                    if (locations.Count > 1 && state.Slots.TryGetValue(key, out var existing)) MergeOrigins(existing, stored);
                    else state.Slots[key] = [.. stored];
                }
            }
        }

        private bool MaySelectSlot(IReadOnlyList<SafeCoreMirProjection> selected,
            IReadOnlyList<SafeCoreMirProjection> candidate)
        {
            if (selected.Count != candidate.Count) return false;
            for (int index = 0; index < selected.Count; index++)
            {
                Step();
                if (selected[index] == candidate[index]) continue;
                if (selected[index].Kind is SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.FromEndIndex &&
                    candidate[index].Kind == SafeCoreMirProjectionKind.ArrayIndex) continue;
                return false;
            }
            return true;
        }

        private List<SafeCoreMirReferenceOrigin> AnalyzeCompositeFunction(SafeCoreMirFunction function, bool report)
        {
            Step();
            if (function.Locals.Count > options.MaximumLocalsPerFunction || function.Blocks.Count > options.MaximumBlocksPerFunction)
                throw new ProvenanceLimitException();
            var result = new List<SafeCoreMirReferenceOrigin>();
            var initial = new CompositeState();
            foreach (var local in function.Locals)
            {
                Step();
                if (local.Kind != SafeCoreMirLocalKind.Parameter) continue;
                foreach (var slot in ReferenceSlots(local.Type))
                    initial.Slots[Slot(local.Id, slot.Path)] = [new(local.Id, true, [], slot.Type.IsMutable)
                    { ParameterPath = slot.Path, IsStatic = RequiresStaticPlace(new(local.Id, slot.Path), function) }];
            }
            var inputs = new CompositeState?[function.Blocks.Count];
            inputs[function.EntryBlockId] = initial;
            var queue = new Queue<int>();
            var queued = new HashSet<int> { function.EntryBlockId };
            var visits = new int[function.Blocks.Count];
            queue.Enqueue(function.EntryBlockId);
            for (int work = 0; queue.Count != 0 && work < options.MaximumPaths; work++)
            {
                Step();
                int blockId = queue.Dequeue();
                queued.Remove(blockId);
                if (++visits[blockId] > options.MaximumBlockVisits) throw new ProvenanceLimitException();
                var block = function.Blocks[blockId];
                if (block.Statements.Count > options.MaximumStatementsPerFunction) throw new ProvenanceLimitException();
                var state = inputs[blockId]!.Clone(Step);
                foreach (var statement in block.Statements)
                {
                    Step();
                    foreach (var operand in statement.Value.Operands) CheckCompositeUse(operand, state, function, report);
                    var statementDestination = statement.DestinationPlace ?? SafeCoreMirPlace.Root(statement.DestinationLocalId);
                    if (!statementDestination.IsRoot) CheckCompositeUse(SafeCoreMirOperand.PlaceValue(statementDestination, statement.Value.Type, statement.Source), state, function, report);
                    var references = RvalueReferences(statement.Value, state, function, report);
                    StoreReferences(statementDestination, statement.Value.Type, references, state, function, statement.Source, report);
                }
                var terminator = block.Terminator;
                if (terminator.Operand is { Kind: not SafeCoreMirOperandKind.Function } value) CheckCompositeUse(value, state, function, report);
                foreach (var argument in terminator.Arguments) CheckCompositeUse(argument, state, function, report);
                if (terminator.Kind == SafeCoreMirTerminatorKind.Return && terminator.Operand is { } returned)
                    foreach (var origin in ValueReferences(returned, state, function, report))
                    {
                        Step();
                        CheckOriginExtent(origin, function, terminator.Source, true, report);
                        if (report && function.ReturnsStaticReference && !origin.IsStatic)
                            Add(EscapingReference, "A static reference return must originate in promoted static storage.", terminator.Source);
                        MergeOrigins(result, [origin]);
                    }
                if (terminator.Kind == SafeCoreMirTerminatorKind.Call && terminator.DestinationLocalId is int destination &&
                    ReferenceSlots(function.Locals[destination].Type).Any())
                {
                    var returnedOrigins = new List<SafeCoreMirReferenceOrigin>();
                    if (summaries.TryGetValue(terminator.Operand!.Id, out var summary) && summary.Count != 0)
                        foreach (var origin in summary)
                        {
                            Step();
                            if (origin.IsStatic) { MergeOrigins(returnedOrigins, [origin]); continue; }
                            if (!origin.IsParameter || (uint)origin.LocalId >= (uint)terminator.Arguments.Count)
                            { if (report) Add(InvalidOrigin, "A call return requires a checked parameter or static origin.", terminator.Source); continue; }
                            var argument = terminator.Arguments[origin.LocalId];
                            var inputSlot = new SafeCoreMirPlace(argument.Id, [.. argument.Place?.Projections ?? [], .. origin.ParameterPath]);
                            foreach (var location in Locations(inputSlot, state, function, terminator.Source, report))
                                foreach (var parent in ReadSlot(location, state, terminator.Source, report))
                                {
                                    SafeCoreMirReferenceOrigin mapped = Append(parent, origin.Projections, origin.IsMutable);
                                    MergeOrigins(returnedOrigins, [mapped with { ValuePath = origin.ValuePath,
                                        HasUnknownSliceOffset = mapped.HasUnknownSliceOffset || origin.HasUnknownSliceOffset }]);
                                }
                        }
                    else if (ReferenceSlots(function.Locals[destination].Type).Any(slot => !IsVariantSlot(slot.Path))) unresolved = true;
                    StoreReferences(SafeCoreMirPlace.Root(destination), function.Locals[destination].Type, returnedOrigins, state, function, terminator.Source, report);
                }
                if (report && terminator.Kind == SafeCoreMirTerminatorKind.Call && terminator.Operand is { Kind: SafeCoreMirOperandKind.Function } callee)
                    for (int argumentIndex = 0; argumentIndex < terminator.Arguments.Count; argumentIndex++)
                    {
                        Step();
                        if (!program.Functions[callee.Id].Locals[argumentIndex].RequiresStaticLifetime) continue;
                        foreach (var origin in ValueReferences(terminator.Arguments[argumentIndex], state, function, report))
                            if (!origin.IsStatic) Add(EscapingReference, "A static reference parameter requires promoted static storage.", terminator.Arguments[argumentIndex].Source);
                    }
                foreach (int target in Successors(terminator))
                {
                    Step();
                    if ((uint)target >= (uint)inputs.Length) continue;
                    bool changed = false;
                    if (inputs[target] is null) { inputs[target] = state.Clone(Step); changed = true; }
                    else foreach (string key in inputs[target]!.Slots.Keys.ToArray())
                    {
                        Step();
                        if (!state.Slots.TryGetValue(key, out var incoming)) { inputs[target]!.Slots.Remove(key); changed = true; }
                        else changed |= MergeOrigins(inputs[target]!.Slots[key], incoming);
                    }
                    if (changed && queued.Add(target)) queue.Enqueue(target);
                }
            }
            if (queue.Count != 0) throw new ProvenanceLimitException();
            return result;
        }

        private bool RequiresStaticPlace(SafeCoreMirPlace place, SafeCoreMirFunction function)
        {
            SafeCoreType type = function.Locals[place.LocalId].Type;
            bool required = place.IsRoot && function.Locals[place.LocalId].RequiresStaticLifetime;
            SafeCoreMirAdtVariant? variant = null;
            foreach (var projection in place.Projections)
            {
                Step();
                if (projection.Kind == SafeCoreMirProjectionKind.Downcast)
                { variant = program.AdtLayouts.First(layout => layout.Type == type).Variants[projection.Index]; continue; }
                if (projection.Kind == SafeCoreMirProjectionKind.Field || variant is not null)
                {
                    var fields = variant?.Fields ?? program.AdtLayouts.First(layout => layout.Type == type).Fields;
                    var field = projection.Kind == SafeCoreMirProjectionKind.TupleIndex ? fields[projection.Index] : fields.First(f => f.Name == projection.Name);
                    required = field.RequiresStaticLifetime;
                    type = field.Type;
                    variant = null;
                }
                else
                {
                    type = projection.Kind == SafeCoreMirProjectionKind.TupleIndex ? type.Elements[projection.Index] : type.ElementType!;
                    required = false;
                }
            }
            return required;
        }

        private void CheckOriginExtent(SafeCoreMirReferenceOrigin origin, SafeCoreMirFunction function,
            SafeCoreMirSource source, bool returning, bool report)
        {
            Step();
            if (!report || origin.IsStatic || origin.IsParameter) return;
            if (returning) Add(EscapingReference, "A reference to local storage cannot escape through a return.", source);
            else if (function.Locals[origin.LocalId].StorageScope is SafeCoreMirSource scope &&
                (scope.SourcePath != source.SourcePath || source.Span.Start < scope.Span.Start || source.Span.End > scope.Span.End))
                Add(EscapingReference, "A reference is used after its storage scope ends.", source);
        }
    }
}
