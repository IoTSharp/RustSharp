using System.Diagnostics;
using System.Globalization;

namespace RustSharp.Semantics;

public static partial class SafeCoreMirDropFlagLowering
{
    private const int MaximumTypedPlaces = 16384;

    private sealed class TypedFlagBudget(SafeCoreMirDropFlagLoweringOptions options, Stopwatch clock, int operations)
    {
        public int Operations = operations;
        public void Check() => Step(options, clock, ref Operations);
        public SafeCoreMirDropEvidencePlaces.Resolved? Resolve(SafeCoreMirProgram mir, SafeCoreMirFunction function,
            Dictionary<string, SafeCoreOwnershipLocal> locals, string display) =>
            SafeCoreMirDropEvidencePlaces.Resolve(mir, function, locals, display,
                clock, options.Timeout, options.MaximumOperations, ref Operations, options.CancellationToken);
        public bool IsCanonicalBorrowedDrop(SafeCoreMirProgram mir, SafeCoreMirFunction function, string key, SafeCoreMirSource source) =>
            SafeCoreMirDropEvidencePlaces.HasCanonicalBorrowedReplacementDrop(mir, function, key, source,
                clock, options.Timeout, options.MaximumOperations, ref Operations, options.CancellationToken);
        public bool MatchesBorrowedDropTrace(SafeCoreMirProgram mir, SafeCoreMirFunction typed, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipPath path, Dictionary<string, SafeCoreOwnershipLocal> locals) =>
            SafeCoreMirDropEvidencePlaces.MatchCanonicalBorrowedDropTrace(mir, typed, function, path, locals,
                clock, options.Timeout, options.MaximumOperations, ref Operations, options.CancellationToken);
    }

    private sealed record EnumFlagGuard(string Owner, int Variant, int Discriminant);
    private sealed record TypedFlagPlace(SafeCoreMirDropEvidencePlaces.Resolved Resolved)
    {
        public List<EnumFlagGuard> Guards { get; } = [];
        public string Key => Resolved.Key;
    }

    private sealed class TypedFlagMap(SafeCoreMirProgram mir, SafeCoreMirFunction typedFunction,
        SafeCoreOwnershipFunction function, TypedFlagBudget budget)
    {
        public Dictionary<string, SafeCoreOwnershipLocal> Locals { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TypedFlagPlace> Places { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DropKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Constructors { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, (string Owner, int Variant, bool Matches)> EnumBranches { get; } = [];

        public void Build()
        {
            foreach (SafeCoreOwnershipLocal local in function.Locals)
            {
                budget.Check();
                Locals.Add(local.Name, local);
            }
            foreach (SafeCoreOwnershipLocal local in function.Locals)
            {
                budget.Check();
                string numeric = local.Id.ToString(CultureInfo.InvariantCulture);
                if (Locals.TryGetValue(numeric, out SafeCoreOwnershipLocal? existing) && existing.Id != local.Id)
                    throw new DropFlagEvidenceException("Ownership local names collide with numeric storage identities.");
                Locals.TryAdd(numeric, local);
                Add(local.Name);
                if (local.HasDrop) DropKeys.Add(local.Name);
            }
            foreach (SafeCoreOwnershipBlock block in function.Blocks)
            {
                budget.Check();
                if (block.Instructions.Count > 65536) throw new DropFlagLimitException();
                foreach (SafeCoreOwnershipInstruction instruction in block.Instructions)
                {
                    budget.Check();
                    if (instruction.LocalId >= 0)
                    {
                        string key = Add(instruction.Place ?? SafeCoreOwnershipPlace.Root(instruction.LocalId));
                        if (instruction.Kind == SafeCoreOwnershipInstructionKind.Drop)
                        {
                            if (!Places[key].Resolved.Droppable)
                                throw new DropFlagEvidenceException("A canonical Drop instruction names a non-droppable typed place: " + key);
                            if (!Places[key].Resolved.ScopeOwned && !budget.IsCanonicalBorrowedDrop(mir, typedFunction, key, instruction.Source))
                                throw new DropFlagEvidenceException("A borrowed Drop instruction is not its checked explicit replacement site: " + key);
                            DropKeys.Add(key);
                        }
                    }
                    if (instruction.RelatedLocalId >= 0)
                        Add(instruction.RelatedPlace ?? SafeCoreOwnershipPlace.Root(instruction.RelatedLocalId));
                    if (instruction.AlternativePlaces is { } alternatives)
                    {
                        if (alternatives.Count > 4096) throw new DropFlagLimitException();
                        foreach (SafeCoreOwnershipPlace alternative in alternatives) { budget.Check(); Add(alternative); }
                    }
                }
            }
            foreach (TypedFlagPlace candidate in Places.Values)
            foreach (TypedFlagPlace owner in Places.Values)
            {
                budget.Check();
                if (owner.Key == candidate.Key || !Contains(owner, candidate)) continue;
                SafeCoreMirAdtLayout? layout = Layout(owner.Resolved.Type);
                if (layout is null || layout.Variants.Count == 0) continue;
                for (int variantIndex = 0; variantIndex < layout.Variants.Count; variantIndex++)
                {
                    budget.Check();
                    SafeCoreMirAdtVariant variant = layout.Variants[variantIndex];
                    for (int fieldIndex = 0; fieldIndex < variant.Fields.Count; fieldIndex++)
                    {
                        budget.Check();
                        string field = owner.Key + "." + layout.Fields[variant.FieldOffset + fieldIndex].Name;
                        if (Prefix(field, candidate.Key)) candidate.Guards.Add(new(owner.Key, variantIndex, variant.Discriminant));
                    }
                }
            }
            BuildEnumFacts();
        }

        public TypedFlagPlace Required(string display, bool drop = false)
        {
            budget.Check();
            string name = display.EndsWith(" (scope)", StringComparison.Ordinal) ? display[..^8] : display;
            SafeCoreMirDropEvidencePlaces.Resolved? resolved = budget.Resolve(mir, typedFunction, Locals, name);
            if (resolved is null && Places.TryGetValue(name, out TypedFlagPlace? synthetic)) resolved = synthetic.Resolved;
            if (resolved is null || !Places.TryGetValue(resolved.Key, out TypedFlagPlace? place) ||
                drop && (!place.Resolved.Droppable || !DropKeys.Contains(place.Key) || name != place.Key))
                throw new DropFlagEvidenceException("Drop-flag evidence references an unknown, noncanonical or non-droppable typed place: " + display);
            return place;
        }

        private string Add(SafeCoreOwnershipPlace place)
        {
            if (!Locals.TryGetValue(place.LocalId.ToString(CultureInfo.InvariantCulture), out SafeCoreOwnershipLocal? root))
                throw new DropFlagEvidenceException("Ownership instruction refers to an unknown local ID.");
            Add(root.Name);
            for (int count = 1; count <= place.Projections.Count; count++)
            {
                budget.Check();
                var prefix = new SafeCoreOwnershipPlace(place.LocalId, place.Projections.Take(count).ToArray());
                string display = prefix.ToString();
                Add(root.Name + display[place.LocalId.ToString(CultureInfo.InvariantCulture).Length..]);
            }
            string full = place.ToString();
            return root.Name + full[place.LocalId.ToString(CultureInfo.InvariantCulture).Length..];
        }

        private void Add(string display)
        {
            budget.Check();
            if (Places.ContainsKey(display)) return;
            if (Places.Count >= MaximumTypedPlaces) throw new DropFlagLimitException();
            SafeCoreMirDropEvidencePlaces.Resolved? resolved = budget.Resolve(mir, typedFunction, Locals, display);
            if (resolved is null && Locals.TryGetValue(display, out SafeCoreOwnershipLocal? constant) &&
                constant.Id >= typedFunction.Locals.Count && constant.StoragePlace is null &&
                constant.Name.StartsWith("__mir_const_", StringComparison.Ordinal) && !constant.HasDrop)
                resolved = new(constant, constant.Name, constant.Type, false);
            if (resolved is null) throw new DropFlagEvidenceException("Ownership instruction has no valid typed place: " + display);
            Places.TryAdd(resolved.Key, new(resolved));
        }

        public SafeCoreMirAdtLayout? Layout(SafeCoreType type)
        {
            foreach (SafeCoreMirAdtLayout layout in mir.AdtLayouts) { budget.Check(); if (layout.Type == type) return layout; }
            return null;
        }

        public void ValidateBorrowedTrace(SafeCoreOwnershipFunction function, SafeCoreOwnershipPath path)
        {
            if (!budget.MatchesBorrowedDropTrace(mir, typedFunction, function, path, Locals))
                throw new DropFlagEvidenceException("Borrowed Drop trace does not match the checked replacement instructions on its CFG path.");
        }

        public bool IsCanonicalReplacement(string key)
        {
            foreach (SafeCoreOwnershipBlock block in function.Blocks)
            foreach (SafeCoreOwnershipInstruction instruction in block.Instructions)
            {
                budget.Check();
                if (instruction.Kind == SafeCoreOwnershipInstructionKind.Drop && instruction.IsConditionalDrop &&
                    budget.IsCanonicalBorrowedDrop(mir, typedFunction, key, instruction.Source)) return true;
            }
            return false;
        }

        private void BuildEnumFacts()
        {
            var tags = new Dictionary<int, string>();
            var conditions = new Dictionary<int, (string Owner, int Variant)>();
            var ambiguousConstructors = new HashSet<string>(StringComparer.Ordinal);
            foreach (SafeCoreMirBlock block in typedFunction.Blocks)
            foreach (SafeCoreMirStatement statement in block.Statements)
            {
                budget.Check();
                SafeCoreMirRvalue value = statement.Value;
                if (value.Kind == SafeCoreMirRvalueKind.Enum && statement.DestinationPlace is null or { IsRoot: true })
                {
                    string destination = typedFunction.Locals[statement.DestinationLocalId].Name;
                    int variant = int.Parse(value.Operator!, CultureInfo.InvariantCulture);
                    if (!ambiguousConstructors.Contains(destination))
                    {
                        if (Constructors.TryGetValue(destination, out int previous) && previous != variant)
                        {
                            Constructors.Remove(destination);
                            ambiguousConstructors.Add(destination);
                        }
                        else Constructors[destination] = variant;
                    }
                }
                else if (value.Kind == SafeCoreMirRvalueKind.Discriminant)
                {
                    SafeCoreMirOperand operand = value.Operands[0];
                    string name = MirDisplay(operand.Place ?? SafeCoreMirPlace.Root(operand.Id));
                    if (Places.ContainsKey(name)) tags[statement.DestinationLocalId] = name;
                }
                else if (value.Kind == SafeCoreMirRvalueKind.Binary && value.Operator == "==" &&
                    value.Operands[0].Kind == SafeCoreMirOperandKind.Local && tags.TryGetValue(value.Operands[0].Id, out string? owner) &&
                    value.Operands[1].Kind == SafeCoreMirOperandKind.Constant &&
                    int.TryParse(value.Operands[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int discriminant) &&
                    Layout(Places[owner].Resolved.Type) is { } layout)
                {
                    for (int index = 0; index < layout.Variants.Count; index++)
                    {
                        budget.Check();
                        if (layout.Variants[index].Discriminant == discriminant)
                            conditions[statement.DestinationLocalId] = (owner, index);
                    }
                }
            }
            foreach (SafeCoreMirBlock block in typedFunction.Blocks)
            {
                budget.Check();
                if (block.Terminator is { Kind: SafeCoreMirTerminatorKind.Branch, Operand: { } condition } branch &&
                    conditions.TryGetValue(condition.Id, out var test))
                {
                    EnumBranches[branch.TargetBlockId] = (test.Owner, test.Variant, true);
                    EnumBranches[branch.FalseTargetBlockId] = (test.Owner, test.Variant, false);
                }
            }
        }

        private string MirDisplay(SafeCoreMirPlace place)
        {
            SafeCoreType type = typedFunction.Locals[place.LocalId].Type;
            string name = typedFunction.Locals[place.LocalId].Name;
            int variant = -1;
            foreach (SafeCoreMirProjection projection in place.Projections)
            {
                budget.Check();
                if (projection.Kind == SafeCoreMirProjectionKind.Downcast) { variant = projection.Index; continue; }
                if (projection.Kind == SafeCoreMirProjectionKind.Dereference)
                { name += ".*"; type = type.ElementType!; continue; }
                if (projection.Kind == SafeCoreMirProjectionKind.Field && Layout(type) is { } layout)
                {
                    IReadOnlyList<SafeCoreMirAdtField> fields = variant < 0 ? layout.Fields : layout.Variants[variant].Fields;
                    int index = -1;
                    for (int field = 0; field < fields.Count; field++)
                    { budget.Check(); if (fields[field].Name == projection.Name) { index = field; break; } }
                    if (index < 0) throw new DropFlagEvidenceException("MIR enum cleanup has no declared field projection.");
                    name += "." + (variant < 0 ? fields[index].Name : layout.Fields[layout.Variants[variant].FieldOffset + index].Name);
                    type = fields[index].Type;
                    variant = -1;
                }
                else
                {
                    name += new SafeCoreMirPlace(place.LocalId, [projection]).ToString()[place.LocalId.ToString(CultureInfo.InvariantCulture).Length..];
                    type = projection.Kind == SafeCoreMirProjectionKind.TupleIndex ? type.Elements[projection.Index] : type.ElementType!;
                }
            }
            return name;
        }
    }

    private static SafeCoreMirDropFlagResult LowerTyped(SafeCoreMirProgram mir, SafeCoreOwnershipProgram program,
        SafeCoreOwnershipAnalysisResult analysis, SafeCoreMirDropFlagLoweringOptions options, Stopwatch clock, ref int operations)
    {
        var budget = new TypedFlagBudget(options, clock, operations);
        if (mir.Functions.Count > 4096 || program.Functions.Count > 4096) throw new DropFlagLimitException();
        var functions = new Dictionary<string, SafeCoreMirFunction>(StringComparer.Ordinal);
        foreach (SafeCoreMirFunction function in mir.Functions)
        {
            budget.Check();
            if (!functions.TryAdd(function.Name, function)) throw new DropFlagEvidenceException("Typed MIR contains duplicate function identities.");
        }
        var paths = new List<SafeCoreMirDropFlagPath>();
        foreach (SafeCoreOwnershipFunction function in program.Functions)
        {
            budget.Check();
            if (!functions.TryGetValue(function.Name, out SafeCoreMirFunction? typed))
                throw new DropFlagEvidenceException("Ownership flags name a function absent from validated MIR.");
            var map = new TypedFlagMap(mir, typed, function, budget);
            map.Build();
            foreach (SafeCoreOwnershipPath path in analysis.Paths)
            {
                budget.Check();
                if (path.FunctionName != function.Name) continue;
                paths.Add(LowerTypedPath(function, path, map, budget, options));
            }
        }
        operations = budget.Operations;
        return new(paths.AsReadOnly(), Format(paths, options, clock, ref operations), [], false);
    }

    private static SafeCoreMirDropFlagPath LowerTypedPath(SafeCoreOwnershipFunction function, SafeCoreOwnershipPath path,
        TypedFlagMap map, TypedFlagBudget budget, SafeCoreMirDropFlagLoweringOptions options)
    {
        var states = new Dictionary<string, SafeCoreDropPlaceState>(StringComparer.Ordinal);
        var generations = new Dictionary<string, int>(StringComparer.Ordinal);
        var choices = new Dictionary<string, int>(StringComparer.Ordinal);
        var excluded = new HashSet<(string Owner, int Variant)>();
        foreach (TypedFlagPlace place in map.Places.Values)
        {
            budget.Check();
            states.Add(place.Key, place.Resolved.Root.InitiallyInitialized ? SafeCoreDropPlaceState.Live : SafeCoreDropPlaceState.Uninitialized);
            generations.Add(place.Key, 0);
        }
        var drops = new List<string>();
        var consumedGenerations = new HashSet<(string Key, int Generation)>();
        var events = new List<IReadOnlyList<SafeCoreMirDropFlag>>();
        bool terminal = false;
        string? terminalName = null;
        for (int traceIndex = 0; traceIndex < path.Trace.Length; traceIndex++)
        {
            budget.Check();
            string trace = path.Trace[traceIndex];
            if (trace.Length > 16384) throw new DropFlagLimitException();
            if (terminal && trace.Length != 0) throw new DropFlagEvidenceException("An ownership trace continues after its terminal edge.");
            if (trace.StartsWith("drop ", StringComparison.Ordinal))
            {
                TypedFlagPlace dropped = map.Required(trace[5..], drop: true);
                RequireLive(dropped, partial: false);
                if (!consumedGenerations.Add((dropped.Key, generations[dropped.Key])))
                    throw new DropFlagEvidenceException("A typed drop repeats a consumed initialization generation: " + dropped.Key);
                drops.Add(dropped.Key);
                Consume(dropped, SafeCoreDropPlaceState.Dropped);
            }
            else if (trace.StartsWith("move ", StringComparison.Ordinal) || trace.StartsWith("copy_ref ", StringComparison.Ordinal))
            {
                bool copy = trace.StartsWith("copy_ref ", StringComparison.Ordinal);
                string body = trace[(copy ? 9 : 5)..];
                int separator = body.IndexOf(" -> ", StringComparison.Ordinal);
                if (separator <= 0) throw new DropFlagEvidenceException("Move/copy evidence has no source and destination.");
                TypedFlagPlace source = map.Required(body[..separator]);
                string targetName = body[(separator + 4)..];
                bool noOp = copy && targetName.EndsWith(" (no-op)", StringComparison.Ordinal);
                if (noOp) targetName = targetName[..^8];
                TypedFlagPlace destination = map.Required(targetName);
                if (noOp && source.Key == destination.Key) { RequireLive(source, false); }
                else
                {
                    if (source.Key == destination.Key) throw new DropFlagEvidenceException("A typed move cannot reuse its source place.");
                    RequireLive(source, false);
                    if (!copy) Consume(source, SafeCoreDropPlaceState.Moved);
                    Initialize(destination, source);
                }
            }
            else if (trace.StartsWith("assign ", StringComparison.Ordinal)) Initialize(map.Required(trace[7..]));
            else if (trace.StartsWith("consume ", StringComparison.Ordinal) || trace.StartsWith("return_move ", StringComparison.Ordinal))
            {
                TypedFlagPlace moved = map.Required(trace[(trace.StartsWith("consume ", StringComparison.Ordinal) ? 8 : 12)..]);
                RequireLive(moved, false);
                Consume(moved, SafeCoreDropPlaceState.Moved);
            }
            else if (trace.StartsWith("borrow ", StringComparison.Ordinal) || trace.StartsWith("borrow_mut ", StringComparison.Ordinal))
            {
                int separator = trace.IndexOf(" as ", StringComparison.Ordinal);
                if (separator < 0) throw new DropFlagEvidenceException("Borrow evidence has no receiving reference.");
                bool mutable = trace.StartsWith("borrow_mut ", StringComparison.Ordinal);
                TypedFlagPlace borrowed = map.Required(trace[(mutable ? 11 : 7)..separator]);
                RequireLive(borrowed, false);
                if (mutable) ForgetMutableEnumFacts(borrowed);
                Initialize(map.Required(trace[(separator + 4)..]));
            }
            else if (trace.StartsWith("borrow_call ", StringComparison.Ordinal)) Initialize(map.Required(trace[12..]));
            else if (trace.StartsWith("write ", StringComparison.Ordinal)) RequireWritable(map.Required(trace[6..]), traceIndex);
            else if (trace.StartsWith("scope_exit ", StringComparison.Ordinal))
            {
                if (!int.TryParse(trace.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int scope) ||
                    !function.Scopes.Any(item => item.Id == scope))
                    throw new DropFlagEvidenceException("An ownership trace names an unknown scope exit.");
                foreach (TypedFlagPlace place in map.Places.Values)
                {
                    budget.Check();
                    if (place.Resolved.Root.ScopeId != scope) continue;
                    if (Eligible(place)) throw new DropFlagEvidenceException("Scope exit omitted a live canonical typed Drop: " + place.Key);
                    if (states[place.Key] is SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved)
                        states[place.Key] = SafeCoreDropPlaceState.Dropped;
                }
            }
            else if (trace.StartsWith("branch ", StringComparison.Ordinal))
            {
                if (!int.TryParse(trace.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int target) ||
                    !function.Blocks.Any(block => block.Id == target))
                    throw new DropFlagEvidenceException("A trace branch names an unknown ownership block.");
                if (map.EnumBranches.TryGetValue(target, out var test))
                {
                    bool hasObligation = false;
                    foreach (TypedFlagPlace place in map.Places.Values)
                    { budget.Check(); if (Prefix(test.Owner, place.Key) && DropLiveCondition(place)) hasObligation = true; }
                    if (hasObligation)
                    {
                        if (choices.TryGetValue(test.Owner, out int known) && (known == test.Variant) != test.Matches)
                            throw new DropFlagEvidenceException("Enum cleanup branch contradicts its typed constructor or move fact.");
                        if (test.Matches) choices[test.Owner] = test.Variant;
                        else excluded.Add((test.Owner, test.Variant));
                        RefineGuards();
                    }
                }
            }
            else if (trace is "return" or "panic_unwind" or "panic_abort" or "unreachable") { terminal = true; terminalName = trace; }
            else if (trace.Length == 0 || trace.StartsWith("use ", StringComparison.Ordinal) ||
                trace.StartsWith("use_borrow ", StringComparison.Ordinal) || trace.StartsWith("end_borrow ", StringComparison.Ordinal) ||
                trace.StartsWith("nll_end ", StringComparison.Ordinal)) { }
            else throw new DropFlagEvidenceException("Unsupported typed drop-flag trace event: " + trace);
            if (events.Count >= options.MaximumEventsPerPath) throw new DropFlagLimitException();
            events.Add(Snapshot());
        }
        string expectedTerminal = path.Outcome switch
        {
            SafeCoreOwnershipOutcome.Returned => "return", SafeCoreOwnershipOutcome.Unwound => "panic_unwind",
            SafeCoreOwnershipOutcome.Aborted => "panic_abort", SafeCoreOwnershipOutcome.Unreachable => "unreachable",
            _ => throw new DropFlagEvidenceException("Typed flag evidence has no complete terminal outcome."),
        };
        if (terminalName != expectedTerminal) throw new DropFlagEvidenceException("Typed trace terminal edge disagrees with its reported outcome.");
        if (drops.Count != path.DropOrder.Length) throw new DropFlagEvidenceException("Typed trace drops and DropOrder have different occurrence counts.");
        for (int index = 0; index < drops.Count; index++)
        {
            budget.Check();
            if (map.Required(path.DropOrder[index], drop: true).Key != drops[index])
                throw new DropFlagEvidenceException("Typed trace drops and DropOrder differ in complete place identity or order.");
        }
        map.ValidateBorrowedTrace(function, path);
        return new(path.PathId, function.Name, path.Outcome, Snapshot(), events.AsReadOnly(), function.Source);

        bool GuardFalse(TypedFlagPlace place) => place.Guards.Any(guard =>
            choices.TryGetValue(guard.Owner, out int selected) ? selected != guard.Variant : excluded.Contains((guard.Owner, guard.Variant)));
        bool DropLiveCondition(TypedFlagPlace place) => map.DropKeys.Contains(place.Key) && place.Resolved.Droppable &&
            states[place.Key] is SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved && !GuardFalse(place);
        bool Eligible(TypedFlagPlace place) => DropLiveCondition(place) && place.Resolved.ScopeOwned;
        void RequireWritable(TypedFlagPlace place, int traceIndex)
        {
            if (states[place.Key] is SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved)
            {
                RequireLive(place, true);
                return;
            }
            string? following = traceIndex + 1 < path.Trace.Length ? path.Trace[traceIndex + 1] : null;
            int separator = following?.IndexOf(" -> ", StringComparison.Ordinal) ?? -1;
            if (states[place.Key] != SafeCoreDropPlaceState.Dropped || GuardFalse(place) ||
                !consumedGenerations.Contains((place.Key, generations[place.Key])) ||
                following is null || !following.StartsWith("move ", StringComparison.Ordinal) || separator <= 5 ||
                following[(separator + 4)..] != place.Key || !place.Resolved.Root.IsReference ||
                !place.Resolved.Root.Type.IsMutable || !map.IsCanonicalReplacement(place.Key))
                throw new DropFlagEvidenceException("A write to a consumed referent lacks its checked replacement store: " + place.Key);
            // The store uses a live reference address after consuming the old
            // referent. Only the following checked move installs a generation;
            // the address-use event itself cannot revive the dropped value.
            RequireLive(map.Required(place.Resolved.Root.Name), true);
            foreach (TypedFlagPlace ancestor in map.Places.Values)
            {
                budget.Check();
                if (ancestor.Key != place.Key && Contains(ancestor, place) &&
                    states[ancestor.Key] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved))
                    throw new DropFlagEvidenceException("A replacement address has an unavailable containing value: " + place.Key);
            }
        }
        void RequireLive(TypedFlagPlace place, bool partial)
        {
            if (GuardFalse(place) || states[place.Key] != SafeCoreDropPlaceState.Live &&
                !(partial && states[place.Key] == SafeCoreDropPlaceState.PartiallyMoved))
                throw new DropFlagEvidenceException("Typed move/drop evidence references a non-live or inactive place: " + place.Key);
            foreach (TypedFlagPlace ancestor in map.Places.Values)
            {
                budget.Check();
                if (ancestor.Key != place.Key && Contains(ancestor, place) &&
                    states[ancestor.Key] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved))
                    throw new DropFlagEvidenceException("Typed place has a moved, dropped or uninitialized ancestor: " + place.Key);
            }
        }
        void RefineGuards()
        {
            foreach (TypedFlagPlace place in map.Places.Values)
            {
                budget.Check();
                if (GuardFalse(place) && states[place.Key] is SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved)
                    states[place.Key] = SafeCoreDropPlaceState.Uninitialized;
            }
        }
        void ForgetMutableEnumFacts(TypedFlagPlace borrowed)
        {
            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (TypedFlagPlace owner in map.Places.Values)
            {
                budget.Check();
                if (!Contains(borrowed, owner) ||
                    states[owner.Key] != SafeCoreDropPlaceState.Live &&
                    !(states[owner.Key] == SafeCoreDropPlaceState.Uninitialized && GuardFalse(owner))) continue;
                if (map.Layout(owner.Resolved.Type) is { Variants.Count: > 0 }) owners.Add(owner.Key);
            }
            if (owners.Count == 0) return;
            // Only guard-suppressed potential payloads may become unknown-live.
            // Consumed generations keep Moved/Dropped, and genuine incomplete
            // initialization is never reconstructed from the mutable borrow.
            var potential = new List<TypedFlagPlace>();
            foreach (TypedFlagPlace place in map.Places.Values)
            {
                budget.Check();
                if (states[place.Key] == SafeCoreDropPlaceState.Uninitialized && GuardFalse(place) &&
                    place.Guards.Any(guard => owners.Contains(guard.Owner))) potential.Add(place);
            }
            foreach (string owner in owners)
            {
                budget.Check();
                choices.Remove(owner);
                excluded.RemoveWhere(item => { budget.Check(); return item.Owner == owner; });
            }
            foreach (TypedFlagPlace place in potential.OrderBy(static item => item.Key.Length))
            {
                budget.Check();
                if (GuardFalse(place)) continue;
                bool unavailable = false;
                foreach (TypedFlagPlace ancestor in map.Places.Values)
                {
                    budget.Check();
                    if (ancestor.Key != place.Key && Contains(ancestor, place) &&
                        states[ancestor.Key] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved))
                        unavailable = true;
                }
                if (!unavailable) states[place.Key] = SafeCoreDropPlaceState.Live;
            }
        }
        void Initialize(TypedFlagPlace destination, TypedFlagPlace? source = null)
        {
            foreach (TypedFlagPlace ancestor in map.Places.Values)
            {
                budget.Check();
                if (ancestor.Key != destination.Key && Contains(ancestor, destination) &&
                    states[ancestor.Key] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved))
                    throw new DropFlagEvidenceException("Assignment cannot reinitialize a field of an unavailable owner: " + destination.Key);
            }
            foreach (TypedFlagPlace place in map.Places.Values)
            {
                budget.Check();
                if (!Contains(destination, place)) continue;
                if (DropLiveCondition(place)) throw new DropFlagEvidenceException("Assignment overwrites an unconsumed typed Drop generation: " + place.Key);
                states[place.Key] = SafeCoreDropPlaceState.Live;
                generations[place.Key]++;
                excluded.RemoveWhere(item => item.Owner == place.Key);
                if (source is not null && choices.TryGetValue(source.Key + place.Key[destination.Key.Length..], out int movedVariant))
                    choices[place.Key] = movedVariant;
                else if (map.Constructors.TryGetValue(place.Key, out int variant)) choices[place.Key] = variant;
                else choices.Remove(place.Key);
            }
            RefineGuards();
            RefreshAncestors(destination);
        }
        void Consume(TypedFlagPlace consumed, SafeCoreDropPlaceState state)
        {
            foreach (TypedFlagPlace place in map.Places.Values)
            {
                budget.Check();
                if (Contains(consumed, place)) states[place.Key] = state;
            }
            RefreshAncestors(consumed);
        }
        void RefreshAncestors(TypedFlagPlace changed)
        {
            foreach (TypedFlagPlace ancestor in map.Places.Values.OrderByDescending(static item => item.Key.Length))
            {
                budget.Check();
                if (ancestor.Key == changed.Key || !Contains(ancestor, changed) ||
                    states[ancestor.Key] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved)) continue;
                bool unavailable = false;
                foreach (TypedFlagPlace child in map.Places.Values)
                {
                    budget.Check();
                    if (child.Key != ancestor.Key && Contains(ancestor, child) && !GuardFalse(child) &&
                        states[child.Key] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved)) unavailable = true;
                }
                states[ancestor.Key] = unavailable ? SafeCoreDropPlaceState.PartiallyMoved : SafeCoreDropPlaceState.Live;
            }
        }
        IReadOnlyList<SafeCoreMirDropFlag> Snapshot()
        {
            var flags = new List<SafeCoreMirDropFlag>(map.Places.Count);
            foreach (TypedFlagPlace place in map.Places.Values.OrderBy(static place => place.Resolved.Root.Id).ThenBy(static place => place.Key, StringComparer.Ordinal))
            {
                budget.Check();
                string? guard = place.Guards.Count == 0 ? null : string.Join(" && ", place.Guards.Select(item =>
                    item.Owner + ".tag == " + item.Discriminant.ToString(CultureInfo.InvariantCulture)));
                flags.Add(new(place.Resolved.Root.Id, place.Key, states[place.Key], Eligible(place), place.Resolved.Root.Source)
                { GuardCondition = guard });
            }
            return flags.AsReadOnly();
        }
    }

    private static bool Contains(TypedFlagPlace parent, TypedFlagPlace child) =>
        parent.Resolved.Root.Id == child.Resolved.Root.Id && Prefix(parent.Key, child.Key);
    private static bool Prefix(string parent, string child) => parent == child || child.Length > parent.Length &&
        child.StartsWith(parent, StringComparison.Ordinal) && child[parent.Length] is '.' or '[';
}
