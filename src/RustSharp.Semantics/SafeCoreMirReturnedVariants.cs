using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;

namespace RustSharp.Semantics;

/// <summary>The possible active variants of one enum slot in a returned aggregate.</summary>
public sealed record SafeCoreMirReturnedVariant(SafeCoreType Type, IReadOnlyList<SafeCoreMirProjection> ValuePath,
    IReadOnlyList<string> VariantNames);

public sealed record SafeCoreMirReturnedVariantSummary(int FunctionId, IReadOnlyList<SafeCoreMirReturnedVariant> Variants);

public sealed record SafeCoreMirReturnedVariantsResult(IReadOnlyList<SafeCoreMirReturnedVariantSummary> Functions,
    IReadOnlyList<Diagnostic> Diagnostics, bool IsTruncated, int OperationsUsed)
{
    public bool IsSuccessful => Diagnostics.Count == 0 && !IsTruncated;
}

/// <summary>Bounded forward analysis of enum tags, including tags nested in returned value aggregates.
/// Unknown values retain every declared variant; they never imply an inactive payload.</summary>
public static class SafeCoreMirReturnedVariants
{
    public static SafeCoreMirReturnedVariantsResult Analyze(SafeCoreMirProgram program, SafeCoreMirOwnershipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= new();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) || options.MaximumOperations < 1 ||
            options.MaximumPaths < 1 || options.MaximumBlockVisits < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var clock = Stopwatch.StartNew();
        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(program, new()
        {
            Timeout = options.Timeout, MaximumOperations = options.MaximumOperations,
            MaximumFunctions = options.MaximumFunctions, MaximumDiagnostics = options.MaximumDiagnostics,
            MaximumLocals = (int)Math.Min(100_000L, (long)options.MaximumLocalsPerFunction * Math.Max(1, program.Functions.Count)),
            MaximumBlocks = (int)Math.Min(100_000L, (long)options.MaximumBlocksPerFunction * Math.Max(1, program.Functions.Count)),
            MaximumStatements = (int)Math.Min(100_000L, (long)options.MaximumStatementsPerFunction * Math.Max(1, program.Functions.Count)),
            CancellationToken = options.CancellationToken,
        });
        if (!validation.IsSuccessful)
            return new([], validation.Diagnostics.Select(diagnostic => new Diagnostic(diagnostic.Code, diagnostic.Message,
                diagnostic.Source?.Span ?? new(0, 0)) { SourcePath = diagnostic.Source?.SourcePath }).ToArray(), validation.IsTruncated, validation.OperationsUsed);
        int remaining = options.MaximumOperations - validation.OperationsUsed;
        TimeSpan time = options.Timeout - clock.Elapsed;
        if (remaining < 1 || time <= TimeSpan.Zero)
            return new([], [new(SafeCoreMirDiagnosticCodes.LimitReached, "Returned enum variants exhausted their validation budget.", new(0, 0))], true, validation.OperationsUsed);
        SafeCoreMirReturnedVariantsResult result = new Worker(program, options with { MaximumOperations = remaining, Timeout = time }).Run();
        return result with { OperationsUsed = result.OperationsUsed + validation.OperationsUsed };
    }

    private sealed class Worker(SafeCoreMirProgram program, SafeCoreMirOwnershipOptions options)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<string, SafeCoreMirAdtLayout> layouts = program.AdtLayouts.ToDictionary(layout => layout.Type.Name!, StringComparer.Ordinal);
        private readonly Dictionary<int, Facts> summaries = [];
        private int operations;

        private sealed record Slot(SafeCoreType Type, SafeCoreMirProjection[] Path, HashSet<string> Names);
        private sealed class Facts
        {
            public Dictionary<string, Slot> Slots { get; } = new(StringComparer.Ordinal);
        }
        private sealed record State(Facts?[] Locals, SafeCoreMirPlace?[] Aliases);
        private sealed class VariantLimitException : Exception { }

        private void Step()
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (++operations > options.MaximumOperations || clock.Elapsed >= options.Timeout) throw new VariantLimitException();
        }

        public SafeCoreMirReturnedVariantsResult Run()
        {
            try
            {
                Step();
                if (program.Functions.Count + program.ExternalFunctions.Count > options.MaximumFunctions) throw new VariantLimitException();
                foreach (SafeCoreMirFunction function in program.Functions)
                {
                    Step();
                    summaries.Add(function.Id, All(function.ReturnType));
                }
                foreach (SafeCoreMirExternalFunction function in program.ExternalFunctions)
                {
                    Step();
                    Facts facts = All(function.Signature.ReturnType);
                    var declared = new Facts();
                    foreach (SafeCoreExternalReturnedVariant variant in function.ExternalFunction.SourceReturnVariants)
                    {
                        Step();
                        SafeCoreMirProjection[] path = variant.ValuePath.Select(text => SafeCoreSourceOriginCodec.ParseProjection(text, options.CancellationToken)).ToArray();
                        string key = Key(path);
                        if (!facts.Slots.TryGetValue(key, out Slot? slot)) throw new ArgumentException("Imported returned variant path is not a declared enum slot.");
                        SafeCoreMirAdtLayout layout = layouts[slot.Type.Name!];
                        var declarations = new List<SafeCoreExternalVariant>();
                        foreach (SafeCoreExternalValueType producer in function.ExternalFunction.SourceValueTypes)
                        {
                            Step();
                            if (producer.AssemblyName != layout.ExternalAssemblyName || producer.ClrName != layout.ExternalClrName) continue;
                            foreach (SafeCoreExternalVariant declaration in producer.Variants)
                            {
                                Step();
                                if (declaration.Name != variant.VariantName) continue;
                                if (declarations.Count >= options.MaximumPaths) throw new VariantLimitException();
                                declarations.Add(declaration);
                            }
                        }
                        if (declarations.Count == 0 || declarations.Any(declaration => declaration.SourceName != declarations[0].SourceName ||
                            declaration.Discriminant != declarations[0].Discriminant || declaration.FieldOffset != declarations[0].FieldOffset))
                            throw new ArgumentException("Imported returned variants require matching verified original source declarations.");
                        string name = slot.Type.Name + "::" + declarations[0].SourceName;
                        if (!slot.Names.Contains(name)) throw new ArgumentException("Imported returned variant is not declared by its producer layout.");
                        if (!declared.Slots.TryGetValue(key, out Slot? selected))
                            declared.Slots.Add(key, selected = new(slot.Type, path, new(StringComparer.Ordinal)));
                        selected.Names.Add(name);
                    }
                    foreach ((string key, Slot slot) in declared.Slots) { Step(); facts.Slots[key] = slot; }
                    summaries.Add(function.Id, facts);
                }
                bool changed = true;
                for (int iteration = 0; changed && iteration < options.MaximumBlockVisits; iteration++)
                {
                    Step();
                    changed = false;
                    foreach (SafeCoreMirFunction function in program.Functions)
                    {
                        Step();
                        Facts found = Function(function);
                        if (!Equal(summaries[function.Id], found))
                        {
                            summaries[function.Id] = found;
                            changed = true;
                        }
                    }
                    if (changed && iteration + 1 == options.MaximumBlockVisits) throw new VariantLimitException();
                }
                return new(program.Functions.Select(function => new SafeCoreMirReturnedVariantSummary(function.Id,
                    summaries[function.Id].Slots.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                        new SafeCoreMirReturnedVariant(pair.Value.Type, Array.AsReadOnly(pair.Value.Path),
                            Array.AsReadOnly(pair.Value.Names.Order(StringComparer.Ordinal).ToArray()))).ToArray())).ToArray(), [], false, operations);
            }
            catch (VariantLimitException)
            {
                return new([], [new(SafeCoreMirDiagnosticCodes.LimitReached, "Returned enum variants exceed their operation, path or time budget.", new(0, 0))], true, operations);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or OverflowException)
            {
                return new([], [new(SafeCoreMirDiagnosticCodes.InvalidInput, "Returned enum variant evidence is inconsistent: " + exception.Message, new(0, 0))], false, operations);
            }
        }

        private Facts Function(SafeCoreMirFunction function)
        {
            Step();
            if (function.Locals.Count > options.MaximumLocalsPerFunction || function.Blocks.Count > options.MaximumBlocksPerFunction)
                throw new VariantLimitException();
            var states = new State?[function.Blocks.Count];
            var queued = new bool[function.Blocks.Count];
            var visits = new int[function.Blocks.Count];
            var pending = new Queue<int>();
            var entry = new State(new Facts?[function.Locals.Count], new SafeCoreMirPlace?[function.Locals.Count]);
            foreach (SafeCoreMirLocal local in function.Locals)
            {
                Step();
                if (local.Kind == SafeCoreMirLocalKind.Parameter) entry.Locals[local.Id] = All(local.Type);
            }
            states[function.EntryBlockId] = entry;
            pending.Enqueue(function.EntryBlockId);
            queued[function.EntryBlockId] = true;
            var returned = new Facts();
            bool hasReturn = false;
            for (int work = 0; pending.Count > 0 && work < options.MaximumOperations; work++)
            {
                Step();
                int blockId = pending.Dequeue();
                queued[blockId] = false;
                if (++visits[blockId] > options.MaximumBlockVisits) throw new VariantLimitException();
                State state = Copy(states[blockId]!);
                SafeCoreMirBlock block = function.Blocks[blockId];
                foreach (SafeCoreMirStatement statement in block.Statements)
                {
                    Step();
                    SafeCoreMirRvalue value = statement.Value;
                    if (value.Kind == SafeCoreMirRvalueKind.Write)
                    {
                        InvalidateAlias(value.Operands[0], state, function);
                        continue;
                    }
                    Facts result = Rvalue(value, state, function);
                    SafeCoreMirPlace destination = statement.DestinationPlace ?? SafeCoreMirPlace.Root(statement.DestinationLocalId);
                    Store(destination, result, state, function);
                    if (destination.IsRoot)
                    {
                        SafeCoreMirPlace? alias = value.Kind is SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Coerce && value.Operands.Count == 1
                            ? Alias(value.Operands[0], state) : null;
                        if (value.Kind is SafeCoreMirRvalueKind.Unary or SafeCoreMirRvalueKind.PromotedBorrow && value.Operator is "&" or "&mut")
                            alias = OperandPlace(value.Operands[0]);
                        state.Aliases[destination.LocalId] = alias;
                    }
                }
                SafeCoreMirTerminator terminator = block.Terminator;
                if (terminator.Kind == SafeCoreMirTerminatorKind.Return && terminator.Operand is { } operand)
                {
                    Merge(returned, Operand(operand, state, function));
                    hasReturn = true;
                }
                if (terminator.Kind == SafeCoreMirTerminatorKind.Call)
                {
                    foreach (SafeCoreMirOperand argument in terminator.Arguments)
                    {
                        Step();
                        if (argument.Type.Kind == K.Reference && argument.Type.IsMutable) InvalidateAlias(argument, state, function);
                    }
                    if (terminator.DestinationLocalId is int destination)
                        state.Locals[destination] = terminator.Operand is { Kind: SafeCoreMirOperandKind.Function } callee && summaries.TryGetValue(callee.Id, out Facts? called)
                            ? Copy(called) : All(function.Locals[destination].Type);
                }
                if (terminator.Kind is SafeCoreMirTerminatorKind.Goto or SafeCoreMirTerminatorKind.Branch or SafeCoreMirTerminatorKind.Call)
                    Propagate(terminator.TargetBlockId);
                if (terminator.Kind == SafeCoreMirTerminatorKind.Branch) Propagate(terminator.FalseTargetBlockId);

                void Propagate(int target)
                {
                    Step();
                    if (target < 0) return;
                    bool changed;
                    if (states[target] is null) { states[target] = Copy(state); changed = true; }
                    else changed = Join(states[target]!, state, function);
                    if (changed && !queued[target]) { pending.Enqueue(target); queued[target] = true; }
                }
            }
            if (pending.Count != 0) throw new VariantLimitException();
            return hasReturn ? returned : All(function.ReturnType);
        }

        private Facts Rvalue(SafeCoreMirRvalue value, State state, SafeCoreMirFunction function)
        {
            Step();
            if (value.Kind is SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Coerce)
                return Operand(value.Operands[0], state, function);
            if (value.Kind == SafeCoreMirRvalueKind.Field && int.TryParse(value.Operator, CultureInfo.InvariantCulture, out int fieldIndex))
            {
                SafeCoreType aggregate = value.Operands[0].Type;
                SafeCoreMirProjection projection = aggregate.Kind == K.Tuple ? SafeCoreMirProjection.TupleIndex(fieldIndex)
                    : SafeCoreMirProjection.Field(layouts[aggregate.Name!].Fields[fieldIndex].Name);
                return Select(Operand(value.Operands[0], state, function), [projection], value.Type);
            }
            var result = new Facts();
            if (value.Kind == SafeCoreMirRvalueKind.Enum && layouts.TryGetValue(value.Type.Name!, out SafeCoreMirAdtLayout? enumLayout) &&
                int.TryParse(value.Operator, CultureInfo.InvariantCulture, out int variantIndex))
            {
                SafeCoreMirAdtVariant variant = enumLayout.Variants[variantIndex];
                result.Slots.Add("", new(value.Type, [], new([variant.Name], StringComparer.Ordinal)));
                for (int field = 0; field < value.Operands.Count; field++)
                {
                    Step();
                    Prefix(result, Operand(value.Operands[field], state, function),
                        [SafeCoreMirProjection.Field(enumLayout.Fields[variant.FieldOffset + field].Name)]);
                }
                return result;
            }
            if (value.Kind is SafeCoreMirRvalueKind.Tuple or SafeCoreMirRvalueKind.Array or SafeCoreMirRvalueKind.Adt)
            {
                for (int index = 0; index < value.Operands.Count; index++)
                {
                    Step();
                    SafeCoreMirProjection projection = value.Kind switch
                    {
                        SafeCoreMirRvalueKind.Tuple => SafeCoreMirProjection.TupleIndex(index),
                        SafeCoreMirRvalueKind.Array => SafeCoreMirProjection.ArrayIndex(index),
                        _ => SafeCoreMirProjection.Field(layouts[value.Type.Name!].Fields[index].Name),
                    };
                    Prefix(result, Operand(value.Operands[index], state, function), [projection]);
                }
                return result;
            }
            return All(value.Type);
        }

        private Facts Operand(SafeCoreMirOperand operand, State state, SafeCoreMirFunction function)
        {
            Step();
            SafeCoreMirPlace? place = OperandPlace(operand);
            if (place is null) return All(operand.Type);
            Facts source = state.Locals[place.LocalId] ?? All(function.Locals[place.LocalId].Type);
            return Select(source, place.Projections, operand.Type);
        }

        private static SafeCoreMirPlace? OperandPlace(SafeCoreMirOperand operand) => operand.Kind switch
        {
            SafeCoreMirOperandKind.Local => SafeCoreMirPlace.Root(operand.Id),
            SafeCoreMirOperandKind.Place => operand.Place,
            _ => null,
        };

        private static SafeCoreMirPlace? Alias(SafeCoreMirOperand operand, State state) =>
            operand.Kind == SafeCoreMirOperandKind.Local || operand.Place is { IsRoot: true } ? state.Aliases[operand.Id] : null;

        private void InvalidateAlias(SafeCoreMirOperand operand, State state, SafeCoreMirFunction function)
        {
            Step();
            SafeCoreMirPlace? alias = Alias(operand, state);
            if (alias is not null) { state.Locals[alias.LocalId] = All(function.Locals[alias.LocalId].Type); return; }
            // An unknown mutable reference may alias any storage represented by this state.
            for (int index = 0; index < state.Locals.Length; index++)
            {
                Step();
                if (state.Locals[index] is not null) state.Locals[index] = All(function.Locals[index].Type);
            }
        }

        private Facts Select(Facts source, IReadOnlyList<SafeCoreMirProjection> path, SafeCoreType type)
        {
            Step();
            if (path.Count == 0) return Copy(source);
            if (path.Any(projection => projection.Kind is not (SafeCoreMirProjectionKind.Field or SafeCoreMirProjectionKind.TupleIndex or SafeCoreMirProjectionKind.ArrayIndex)))
                return All(type);
            var result = new Facts();
            foreach (Slot slot in source.Slots.Values)
            {
                Step();
                if (!Starts(slot.Path, path)) continue;
                SafeCoreMirProjection[] relative = slot.Path[path.Count..];
                result.Slots.Add(Key(relative), new(slot.Type, relative, new(slot.Names, StringComparer.Ordinal)));
            }
            return result.Slots.Count == 0 ? All(type) : result;
        }

        private void Store(SafeCoreMirPlace destination, Facts result, State state, SafeCoreMirFunction function)
        {
            Step();
            if (destination.IsRoot) { state.Locals[destination.LocalId] = result; return; }
            if (destination.Projections.Any(projection => projection.Kind is not (SafeCoreMirProjectionKind.Field or SafeCoreMirProjectionKind.TupleIndex or SafeCoreMirProjectionKind.ArrayIndex)))
            {
                state.Locals[destination.LocalId] = All(function.Locals[destination.LocalId].Type);
                return;
            }
            Facts root = Copy(state.Locals[destination.LocalId] ?? All(function.Locals[destination.LocalId].Type));
            foreach (string key in root.Slots.Where(pair => Starts(pair.Value.Path, destination.Projections)).Select(pair => pair.Key).ToArray())
            {
                Step();
                root.Slots.Remove(key);
            }
            Prefix(root, result, destination.Projections);
            state.Locals[destination.LocalId] = root;
        }

        private Facts All(SafeCoreType type)
        {
            var result = new Facts();
            Visit(type, [], new HashSet<string>(StringComparer.Ordinal), 0);
            return result;

            void Visit(SafeCoreType current, SafeCoreMirProjection[] path, HashSet<string> active, int depth)
            {
                Step();
                if (depth > 32) throw new VariantLimitException();
                if (current.Kind == K.Adt && layouts.TryGetValue(current.Name!, out SafeCoreMirAdtLayout? layout))
                {
                    if (!active.Add(current.Name!)) return;
                    if (layout.Variants.Count > 0)
                    {
                        if (result.Slots.Count >= options.MaximumPaths) throw new VariantLimitException();
                        result.Slots.Add(Key(path), new(current, path, new(layout.Variants.Select(variant => variant.Name), StringComparer.Ordinal)));
                    }
                    foreach (SafeCoreMirAdtField field in layout.Fields)
                    {
                        Step();
                        Visit(field.Type, [.. path, SafeCoreMirProjection.Field(field.Name)], active, depth + 1);
                    }
                    active.Remove(current.Name!);
                }
                else if (current.Kind == K.Tuple)
                    for (int index = 0; index < current.Elements.Count; index++)
                    {
                        Step();
                        Visit(current.Elements[index], [.. path, SafeCoreMirProjection.TupleIndex(index)], active, depth + 1);
                    }
                else if (current.Kind == K.Array)
                    for (int index = 0; index < current.Length!.Value; index++)
                    {
                        Step();
                        Visit(current.ElementType, [.. path, SafeCoreMirProjection.ArrayIndex(index)], active, depth + 1);
                    }
            }
        }

        private void Prefix(Facts target, Facts source, IReadOnlyList<SafeCoreMirProjection> prefix)
        {
            foreach (Slot slot in source.Slots.Values)
            {
                Step();
                SafeCoreMirProjection[] path = [.. prefix, .. slot.Path];
                if (path.Length > 32 || target.Slots.Count >= options.MaximumPaths) throw new VariantLimitException();
                target.Slots[Key(path)] = new(slot.Type, path, new(slot.Names, StringComparer.Ordinal));
            }
        }

        private bool Merge(Facts target, Facts source)
        {
            bool changed = false;
            foreach ((string key, Slot slot) in source.Slots)
            {
                Step();
                if (!target.Slots.TryGetValue(key, out Slot? previous))
                {
                    target.Slots.Add(key, new(slot.Type, slot.Path, new(slot.Names, StringComparer.Ordinal)));
                    changed = true;
                }
                else
                    foreach (string name in slot.Names) { Step(); changed |= previous.Names.Add(name); }
            }
            return changed;
        }

        private bool Join(State target, State source, SafeCoreMirFunction function)
        {
            bool changed = false;
            for (int index = 0; index < target.Locals.Length; index++)
            {
                Step();
                if (target.Locals[index] is null && source.Locals[index] is null) continue;
                if (target.Locals[index] is null) { target.Locals[index] = All(function.Locals[index].Type); changed = true; }
                changed |= Merge(target.Locals[index]!, source.Locals[index] ?? All(function.Locals[index].Type));
                if (target.Aliases[index] is { } alias && (source.Aliases[index] is not { } other || alias.LocalId != other.LocalId || !alias.Projections.SequenceEqual(other.Projections)))
                { target.Aliases[index] = null; changed = true; }
            }
            return changed;
        }

        private Facts Copy(Facts source)
        {
            var result = new Facts();
            Merge(result, source);
            return result;
        }

        private State Copy(State source) => new(source.Locals.Select(local => local is null ? null : Copy(local)).ToArray(), (SafeCoreMirPlace?[])source.Aliases.Clone());

        private bool Equal(Facts first, Facts second)
        {
            Step();
            return first.Slots.Count == second.Slots.Count && first.Slots.All(pair =>
                second.Slots.TryGetValue(pair.Key, out Slot? other) && pair.Value.Names.SetEquals(other.Names));
        }

        private static bool Starts(SafeCoreMirProjection[] path, IReadOnlyList<SafeCoreMirProjection> prefix) =>
            path.Length >= prefix.Count && path.Take(prefix.Count).SequenceEqual(prefix);

        private static string Key(SafeCoreMirProjection[] path) =>
            string.Join("/", path.Select(projection => ((int)projection.Kind).ToString(CultureInfo.InvariantCulture) + ":" +
                (projection.Name is null ? "" : projection.Name.Length.ToString(CultureInfo.InvariantCulture) + ":" + projection.Name) + ":" +
                projection.Index.ToString(CultureInfo.InvariantCulture)));
    }
}
