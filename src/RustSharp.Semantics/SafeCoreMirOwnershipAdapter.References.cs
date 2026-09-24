using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirOwnershipAdapter
{
    /// <summary>Each stored reference has an independent loan, including aggregate fields.</summary>
    private sealed class ReferenceSlots
    {
        private sealed record Slot(SafeCoreMirPlace Place, SafeCoreType Type, int Id);
        private readonly List<Slot> _slots = [];
        private readonly SafeCoreMirProgram _program;
        private readonly SafeCoreMirFunction _function;
        private readonly SafeCoreMirOwnershipOptions _options;
        private readonly System.Diagnostics.Stopwatch _clock;
        private int _mappingOperations;

        public bool HasReferences => _slots.Count != 0;

        public ReferenceSlots(SafeCoreMirProgram program, SafeCoreMirFunction function,
            List<SafeCoreOwnershipLocal> locals, SafeCoreMirOwnershipOptions options,
            System.Diagnostics.Stopwatch clock, ref int operations)
        {
            _program = program; _function = function; _options = options; _clock = clock;
            for (int index = 0; index < function.Locals.Count; index++)
            {
                Step(options, clock, ref operations);
                SafeCoreMirLocal local = function.Locals[index];
                Add(SafeCoreMirPlace.Root(local.Id), local.Type, local, locals, options, clock, ref operations, 0);
            }
        }

        private void Add(SafeCoreMirPlace place, SafeCoreType type, SafeCoreMirLocal owner,
            List<SafeCoreOwnershipLocal> locals, SafeCoreMirOwnershipOptions options,
            System.Diagnostics.Stopwatch clock, ref int operations, int depth, bool optionalEnumPayload = false)
        {
            Step(options, clock, ref operations);
            if (depth >= 128) throw new AdapterLimitException();
            if (!ContainsReference(type, _program, depth)) return;
            if (type.Kind == K.Reference)
            {
                int id = place.IsRoot ? owner.Id : locals.Count;
                if (!place.IsRoot)
                {
                    if (locals.Count >= options.MaximumLocalsPerFunction) throw new AdapterLimitException();
                    locals.Add(new(id, "$refslot" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), type,
                        type.IsMutable ? SafeCoreOwnershipKind.Move : SafeCoreOwnershipKind.Copy,
                        false, 0, true, owner.Kind == SafeCoreMirLocalKind.Parameter, owner.Source)
                    { StoragePlace = Convert(place), IsOptionalEnumPayload = optionalEnumPayload });
                }
                _slots.Add(new(place, type, id));
                return;
            }
            if (type.Kind == K.Tuple)
                for (int index = 0; index < type.Elements.Count; index++)
                    Add(place.Append(SafeCoreMirProjection.TupleIndex(index)), type.Elements[index], owner, locals, options, clock, ref operations, depth + 1, optionalEnumPayload);
            else if (type.Kind == K.Array)
                for (int index = 0; index < type.Length; index++)
                    Add(place.Append(SafeCoreMirProjection.ArrayIndex(index)), type.ElementType!, owner, locals, options, clock, ref operations, depth + 1, optionalEnumPayload);
            else if (type.Kind == K.Adt)
            {
                SafeCoreMirAdtLayout? layout = _program.AdtLayouts.FirstOrDefault(layout => layout.Type == type);
                if (layout is null) return;
                for (int index = 0; index < layout.Fields.Count; index++)
                    Add(place.Append(SafeCoreMirProjection.Field(layout.Fields[index].Name)), layout.Fields[index].Type,
                        owner, locals, options, clock, ref operations, depth + 1, optionalEnumPayload || layout.Variants.Count != 0);
            }
        }

        private SafeCoreMirPlace Canonical(SafeCoreMirPlace place)
        {
            Step(_options, _clock, ref _mappingOperations);
            SafeCoreType type = _function.Locals[place.LocalId].Type;
            SafeCoreMirAdtVariant? variant = null;
            SafeCoreMirPlace canonical = SafeCoreMirPlace.Root(place.LocalId);
            for (int index = 0; index < place.Projections.Count; index++)
            {
                Step(_options, _clock, ref _mappingOperations);
                SafeCoreMirProjection projection = place.Projections[index];
                if (projection.Kind == SafeCoreMirProjectionKind.Downcast)
                {
                    SafeCoreMirAdtLayout layout = _program.AdtLayouts.First(layout => layout.Type == type);
                    variant = layout.Variants[projection.Index];
                    continue;
                }
                if (projection.Kind == SafeCoreMirProjectionKind.Field)
                {
                    SafeCoreMirAdtLayout layout = _program.AdtLayouts.First(layout => layout.Type == type);
                    if (variant is not null)
                    {
                        int fieldIndex = -1;
                        for (int candidate = 0; candidate < variant.Fields.Count; candidate++)
                        {
                            Step(_options, _clock, ref _mappingOperations);
                            if (variant.Fields[candidate].Name == projection.Name) { fieldIndex = candidate; break; }
                        }
                        SafeCoreMirAdtField physical = layout.Fields[variant.FieldOffset + fieldIndex];
                        projection = SafeCoreMirProjection.Field(physical.Name);
                    }
                    type = layout.Fields.First(field => field.Name == projection.Name).Type;
                    variant = null;
                }
                else if (projection.Kind == SafeCoreMirProjectionKind.TupleIndex) type = type.Elements[projection.Index];
                else if (projection.Kind == SafeCoreMirProjectionKind.FromEndIndex)
                {
                    if (type.Kind == K.Array)
                    {
                        long offset = type.Length!.Value - projection.Index;
                        if (offset < 0 || offset > int.MaxValue) throw new AdapterLimitException();
                        projection = SafeCoreMirProjection.ArrayIndex((int)offset);
                    }
                    type = type.ElementType!;
                }
                else if (projection.Kind is SafeCoreMirProjectionKind.ArrayIndex or SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.Dereference) type = type.ElementType!;
                canonical = canonical.Append(projection);
            }
            return canonical;
        }

        public SafeCoreMirPlace Map(SafeCoreMirPlace original)
        {
            SafeCoreMirPlace place = Canonical(original);
            for (int index = 0; index < _slots.Count; index++)
            {
                Step(_options, _clock, ref _mappingOperations);
                Slot slot = _slots[index];
                if (!Prefix(slot.Place, place)) continue;
                return new(slot.Id, place.Projections.Skip(slot.Place.Projections.Count).ToArray());
            }
            return place;
        }

        public (int Id, SafeCoreType Type) ReferenceAt(SafeCoreMirPlace original)
        {
            SafeCoreMirPlace place = Canonical(original);
            Slot slot = _slots.First(slot => Prefix(slot.Place, place) && slot.Place.Projections.Count == place.Projections.Count);
            return (slot.Id, slot.Type);
        }

        public System.Collections.ObjectModel.ReadOnlyCollection<int>? EnumTestSlots(SafeCoreMirRvalue value, SafeCoreMirBlock block, int beforeStatement)
        {
            if (value.Kind != SafeCoreMirRvalueKind.Binary || value.Operator != "==") return null;
            SafeCoreMirOperand tag = value.Operands[0], constant = value.Operands[1];
            if (constant.Kind != SafeCoreMirOperandKind.Constant) (tag, constant) = (constant, tag);
            if (tag.Kind != SafeCoreMirOperandKind.Local || constant.Kind != SafeCoreMirOperandKind.Constant ||
                !int.TryParse(constant.Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int expectedTag)) return null;
            Step(_options, _clock, ref _mappingOperations);
            if (beforeStatement == 0) return null;
            // Pattern lowering places the discriminant immediately before its
            // comparison. Do not reuse a tag across an intervening mutation.
            SafeCoreMirStatement tagStatement = block.Statements[beforeStatement - 1];
            if (tagStatement.DestinationLocalId != tag.Id || tagStatement.DestinationPlace is { IsRoot: false } ||
                tagStatement.Value.Kind != SafeCoreMirRvalueKind.Discriminant) return null;
            SafeCoreMirRvalue tagValue = tagStatement.Value;
            SafeCoreMirOperand operand = tagValue.Operands[0];
            SafeCoreMirAdtLayout layout = _program.AdtLayouts.First(candidate => candidate.Type == operand.Type);
            SafeCoreMirAdtVariant? variant = layout.Variants.FirstOrDefault(candidate => candidate.Discriminant == expectedTag);
            if (variant is null) return null;
            SafeCoreMirPlace owner = Canonical(operand.Place ?? SafeCoreMirPlace.Root(operand.Id));
            var required = new List<int>();
            for (int index = 0; index < variant.Fields.Count; index++)
            {
                Step(_options, _clock, ref _mappingOperations);
                SafeCoreMirAdtField field = layout.Fields[variant.FieldOffset + index];
                CollectGuaranteedSlots(owner.Append(SafeCoreMirProjection.Field(field.Name)), field.Type, required, 0);
            }
            return required.Count == 0 ? null : required.AsReadOnly();
        }

        private void CollectGuaranteedSlots(SafeCoreMirPlace place, SafeCoreType type, List<int> required, int depth)
        {
            Step(_options, _clock, ref _mappingOperations);
            if (depth >= 128) throw new AdapterLimitException();
            if (!ContainsReference(type, _program, depth)) return;
            if (type.Kind == K.Reference)
            {
                Slot? slot = _slots.FirstOrDefault(candidate => Prefix(candidate.Place, place) && candidate.Place.Projections.Count == place.Projections.Count);
                if (slot is not null) required.Add(slot.Id);
                return;
            }
            if (type.Kind == K.Tuple)
                for (int index = 0; index < type.Elements.Count; index++)
                    CollectGuaranteedSlots(place.Append(SafeCoreMirProjection.TupleIndex(index)), type.Elements[index], required, depth + 1);
            else if (type.Kind == K.Array)
                for (int index = 0; index < type.Length; index++)
                    CollectGuaranteedSlots(place.Append(SafeCoreMirProjection.ArrayIndex(index)), type.ElementType!, required, depth + 1);
            else if (type.Kind == K.Adt)
            {
                SafeCoreMirAdtLayout layout = _program.AdtLayouts.First(candidate => candidate.Type == type);
                if (layout.Variants.Count != 0) return;
                for (int index = 0; index < layout.Fields.Count; index++)
                {
                    Step(_options, _clock, ref _mappingOperations);
                    SafeCoreMirAdtField field = layout.Fields[index];
                    CollectGuaranteedSlots(place.Append(SafeCoreMirProjection.Field(field.Name)), field.Type, required, depth + 1);
                }
            }
        }

        public System.Collections.ObjectModel.ReadOnlyCollection<SafeCoreOwnershipPlace> DynamicReferenceAlternatives(SafeCoreMirPlace original, bool referenceValue = true)
        {
            SafeCoreMirPlace place = Canonical(original);
            if (!place.Projections.Any(projection => projection.Kind == SafeCoreMirProjectionKind.DynamicIndex)) return new([]);
            var alternatives = new List<SafeCoreOwnershipPlace>();
            for (int index = 0; index < _slots.Count; index++)
            {
                Step(_options, _clock, ref _mappingOperations);
                Slot slot = _slots[index];
                if (slot.Place.LocalId != place.LocalId || slot.Place.Projections.Count > place.Projections.Count) continue;
                bool matched = true;
                for (int projectionIndex = 0; projectionIndex < slot.Place.Projections.Count; projectionIndex++)
                {
                    Step(_options, _clock, ref _mappingOperations);
                    SafeCoreMirProjection projection = place.Projections[projectionIndex];
                    SafeCoreMirProjection candidate = slot.Place.Projections[projectionIndex];
                    if (projection == candidate || projection.Kind == SafeCoreMirProjectionKind.DynamicIndex && candidate.Kind == SafeCoreMirProjectionKind.ArrayIndex) continue;
                    matched = false; break;
                }
                if (matched)
                {
                    SafeCoreOwnershipPlace candidate = Convert(new(slot.Id, place.Projections.Skip(slot.Place.Projections.Count).ToArray()));
                    if (referenceValue) candidate = candidate.Append(SafeCoreOwnershipProjection.Dereference());
                    alternatives.Add(candidate);
                }
            }
            return alternatives.AsReadOnly();
        }

        public void AddCallArguments(SafeCoreMirOperand operand, List<SafeCoreOwnershipCallArgument> arguments)
        {
            if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place)) return;
            SafeCoreMirPlace place = Canonical(operand.Place ?? SafeCoreMirPlace.Root(operand.Id));
            foreach (Slot slot in _slots)
            {
                Step(_options, _clock, ref _mappingOperations);
                if (!slot.Place.IsRoot && Prefix(place, slot.Place))
                    arguments.Add(new(SafeCoreOwnershipPlace.Root(slot.Id), slot.Type.IsMutable) { AllowAbsent = true });
            }
        }

        public void AddStorageUse(SafeCoreMirOperand operand, List<SafeCoreOwnershipInstruction> instructions,
            SafeCoreMirOwnershipOptions options, System.Diagnostics.Stopwatch clock, ref int operations,
            List<RustSharp.Syntax.Diagnostic> diagnostics, ref bool valid)
        {
            if (operand.Place is null) return;
            Step(options, clock, ref operations);
            SafeCoreMirPlace canonical = Canonical(operand.Place);
            SafeCoreMirPlace mapped = Map(canonical);
            if (mapped.LocalId != canonical.LocalId)
                AddInstruction(instructions, SafeCoreOwnershipInstruction.Use(Convert(canonical), operand.Source),
                    options, diagnostics, operand.Source, ref valid);
        }

        private static bool Prefix(SafeCoreMirPlace prefix, SafeCoreMirPlace place) =>
            prefix.LocalId == place.LocalId && prefix.Projections.Count <= place.Projections.Count &&
            prefix.Projections.SequenceEqual(place.Projections.Take(prefix.Projections.Count));

        public void AddUses(SafeCoreMirOperand operand, List<SafeCoreOwnershipInstruction> instructions,
            SafeCoreMirOwnershipOptions options, System.Diagnostics.Stopwatch clock, ref int operations,
            List<RustSharp.Syntax.Diagnostic> diagnostics, ref bool valid)
        {
            if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place)) return;
            SafeCoreMirPlace place = Canonical(operand.Place ?? SafeCoreMirPlace.Root(operand.Id));
            for (int index = 0; index < _slots.Count; index++)
            {
                Step(options, clock, ref operations);
                Slot slot = _slots[index];
                if (slot.Place.IsRoot || !Prefix(place, slot.Place)) continue;
                AddInstruction(instructions, SafeCoreOwnershipInstruction.Use(slot.Id, operand.Source)
                    with { AllowAbsentReference = true }, options, diagnostics, operand.Source, ref valid);
            }
        }

        public void Transfer(SafeCoreMirPlace destination, SafeCoreMirOperand source, List<SafeCoreOwnershipInstruction> instructions,
            SafeCoreMirOwnershipOptions options, System.Diagnostics.Stopwatch clock, ref int operations,
            List<RustSharp.Syntax.Diagnostic> diagnostics, ref bool valid)
        {
            if (source.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place)) return;
            destination = Canonical(destination);
            SafeCoreMirPlace from = Canonical(source.Place ?? SafeCoreMirPlace.Root(source.Id));
            for (int index = 0; index < _slots.Count; index++)
            {
                Step(options, clock, ref operations);
                Slot target = _slots[index];
                if (!Prefix(destination, target.Place)) continue;
                SafeCoreMirPlace sourceSlot = new(from.LocalId, [.. from.Projections, .. target.Place.Projections.Skip(destination.Projections.Count)]);
                SafeCoreMirPlace mapped = Map(sourceSlot);
                if (mapped.LocalId == target.Id && mapped.IsRoot) continue;
                SafeCoreOwnershipPlace sourcePlace = Convert(mapped);
                SafeCoreOwnershipPlace destinationPlace = SafeCoreOwnershipPlace.Root(target.Id);
                SafeCoreOwnershipInstruction transfer = target.Type.IsMutable
                    ? SafeCoreOwnershipInstruction.Move(sourcePlace, destinationPlace, source.Source)
                    : SafeCoreOwnershipInstruction.AssignReference(sourcePlace, destinationPlace, source.Source);
                if (source.Type.Kind != K.Reference) transfer = transfer with { AllowAbsentReference = true };
                AddInstruction(instructions, transfer,
                    options, diagnostics, source.Source, ref valid);
            }
        }

        public void TransferStatement(SafeCoreMirStatement statement, List<SafeCoreOwnershipInstruction> instructions,
            SafeCoreMirOwnershipOptions options, System.Diagnostics.Stopwatch clock, ref int operations,
            List<RustSharp.Syntax.Diagnostic> diagnostics, ref bool valid)
        {
            SafeCoreMirRvalue value = statement.Value;
            if (value.Type.Kind == K.Reference) return;
            SafeCoreMirPlace destination = statement.DestinationPlace ?? SafeCoreMirPlace.Root(statement.DestinationLocalId);
            if (value.Kind == SafeCoreMirRvalueKind.Use && value.Operands.Count == 1)
                Transfer(destination, value.Operands[0], instructions, options, clock, ref operations, diagnostics, ref valid);
            else if (value.Kind is SafeCoreMirRvalueKind.Tuple or SafeCoreMirRvalueKind.Array or SafeCoreMirRvalueKind.Adt or SafeCoreMirRvalueKind.Enum)
            {
                SafeCoreMirAdtLayout? layout = value.Type.Kind == K.Adt ? _program.AdtLayouts.First(layout => layout.Type == value.Type) : null;
                int fieldOffset = value.Kind == SafeCoreMirRvalueKind.Enum
                    ? layout!.Variants[int.Parse(value.Operator!, System.Globalization.CultureInfo.InvariantCulture)].FieldOffset : 0;
                for (int index = 0; index < value.Operands.Count; index++)
                {
                    Step(options, clock, ref operations);
                    SafeCoreMirProjection projection = value.Kind switch
                    {
                        SafeCoreMirRvalueKind.Tuple => SafeCoreMirProjection.TupleIndex(index),
                        SafeCoreMirRvalueKind.Array => SafeCoreMirProjection.ArrayIndex(index),
                        _ => SafeCoreMirProjection.Field(layout!.Fields[index + fieldOffset].Name),
                    };
                    Transfer(destination.Append(projection), value.Operands[index], instructions, options, clock, ref operations, diagnostics, ref valid);
                }
            }
        }

        public static SafeCoreOwnershipPlace Convert(SafeCoreMirPlace place) => new(place.LocalId,
            place.Projections.Select(projection => projection.Kind switch
            {
                SafeCoreMirProjectionKind.Field => SafeCoreOwnershipProjection.Field(projection.Name!),
                SafeCoreMirProjectionKind.TupleIndex => SafeCoreOwnershipProjection.TupleIndex(projection.Index),
                SafeCoreMirProjectionKind.ArrayIndex => SafeCoreOwnershipProjection.ArrayIndex(projection.Index),
                SafeCoreMirProjectionKind.DynamicIndex => SafeCoreOwnershipProjection.DynamicIndex(projection.Index),
                SafeCoreMirProjectionKind.FromEndIndex => SafeCoreOwnershipProjection.FromEndIndex(projection.Index, projection.MinimumLength),
                SafeCoreMirProjectionKind.Dereference => SafeCoreOwnershipProjection.Dereference(),
                _ => throw new InvalidOperationException("Non-canonical reference projection."),
            }).ToArray());
    }
}
