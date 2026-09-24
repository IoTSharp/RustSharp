using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>A referent rooted in an input reference parameter or local storage.</summary>
public sealed record SafeCoreMirReferenceOrigin(int LocalId, bool IsParameter,
    IReadOnlyList<SafeCoreMirProjection> Projections, bool IsMutable);

public sealed record SafeCoreMirReferenceSummary(int FunctionId,
    IReadOnlyList<SafeCoreMirReferenceOrigin> ReturnOrigins);

public sealed record SafeCoreMirReferenceProvenanceResult(
    IReadOnlyList<SafeCoreMirReferenceSummary> Functions,
    IReadOnlyList<Diagnostic> Diagnostics, bool IsTruncated, int OperationsUsed)
{
    public bool IsSuccessful => Diagnostics.Count == 0 && !IsTruncated;
}

/// <summary>Bounded forward dataflow. Joins union proven origins and intersect initialization.</summary>
public static class SafeCoreMirReferenceProvenance
{
    public const string InvalidOrigin = "RSM3010";
    public const string EscapingReference = "RSO1005";
    public const string LimitReached = "RSM3003";

    public static SafeCoreMirReferenceProvenanceResult Analyze(SafeCoreMirProgram program,
        SafeCoreMirOwnershipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= new();
        if (options.MaximumOperations < 1 || options.MaximumPaths < 1 || options.MaximumBlockVisits < 1 ||
            options.Timeout <= TimeSpan.Zero || options.MaximumDiagnostics < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
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
            return new([], validation.Diagnostics.Select(d => new Diagnostic(d.Code, d.Message, d.Source?.Span ?? new(0, 0)) { SourcePath = d.Source?.SourcePath }).ToArray(),
                validation.IsTruncated, validation.OperationsUsed);
        int remaining = options.MaximumOperations - validation.OperationsUsed;
        TimeSpan time = options.Timeout - clock.Elapsed;
        if (remaining < 1 || time <= TimeSpan.Zero)
            return new([], [new Diagnostic(LimitReached, "Reference provenance exhausted its validation budget.", new(0, 0))], true, validation.OperationsUsed);
        SafeCoreMirReferenceProvenanceResult result = AnalyzeValidated(program, options with { MaximumOperations = remaining, Timeout = time });
        return result with { OperationsUsed = result.OperationsUsed + validation.OperationsUsed };
    }

    internal static SafeCoreMirReferenceProvenanceResult AnalyzeValidated(SafeCoreMirProgram program,
        SafeCoreMirOwnershipOptions options) => new Worker(program, options).Run();

    private sealed class Worker(SafeCoreMirProgram program, SafeCoreMirOwnershipOptions options)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<Diagnostic> diagnostics = [];
        private readonly Dictionary<int, List<SafeCoreMirReferenceOrigin>> summaries = [];
        private int operations;
        private bool unresolved;

        private void Step()
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (++operations > options.MaximumOperations || clock.Elapsed >= options.Timeout)
                throw new ProvenanceLimitException();
        }

        public SafeCoreMirReferenceProvenanceResult Run()
        {
            try
            {
                if (program.Functions.Count > options.MaximumFunctions) throw new ProvenanceLimitException();
                // Function summaries grow monotonically. A reference-returning recursive
                // cycle without any input-rooted base case never acquires provenance.
                bool changed = true;
                for (int iteration = 0; changed && iteration < options.MaximumBlockVisits; iteration++)
                {
                    Step();
                    changed = false;
                    foreach (SafeCoreMirFunction function in program.Functions)
                    {
                        Step();
                        List<SafeCoreMirReferenceOrigin> found = AnalyzeFunction(function, report: false);
                        if (!summaries.TryGetValue(function.Id, out List<SafeCoreMirReferenceOrigin>? previous))
                            summaries[function.Id] = previous = [];
                        changed |= MergeOrigins(previous, found);
                    }
                    if (changed && iteration + 1 == options.MaximumBlockVisits) throw new ProvenanceLimitException();
                }
                foreach (SafeCoreMirFunction function in program.Functions)
                {
                    Step();
                    unresolved = false;
                    AnalyzeFunction(function, report: true);
                    if (unresolved) Add(InvalidOrigin, "A reference call has no checked input-rooted return provenance.", function.Source);
                }
                return Result(false);
            }
            catch (ProvenanceLimitException)
            {
                if (diagnostics.Count < options.MaximumDiagnostics)
                    diagnostics.Add(new Diagnostic(LimitReached, "Reference provenance exceeded its bounded work, path, visit, or time limit.", new TextSpan(0, 0)));
                return Result(true);
            }
        }

        private SafeCoreMirReferenceProvenanceResult Result(bool truncated) => new(
            truncated || diagnostics.Count != 0 ? [] : Array.AsReadOnly(program.Functions.Select(function => new SafeCoreMirReferenceSummary(function.Id,
                summaries.TryGetValue(function.Id, out List<SafeCoreMirReferenceOrigin>? origins)
                    ? Array.AsReadOnly(origins.Select(origin => origin with { Projections = Array.AsReadOnly(origin.Projections.ToArray()) }).ToArray()) : [])).ToArray()),
            diagnostics.AsReadOnly(), truncated, operations);

        private List<SafeCoreMirReferenceOrigin> AnalyzeFunction(SafeCoreMirFunction function, bool report)
        {
            Step();
            if (function.Locals.Count > options.MaximumLocalsPerFunction || function.Blocks.Count > options.MaximumBlocksPerFunction)
                throw new ProvenanceLimitException();
            var result = new List<SafeCoreMirReferenceOrigin>();
            if (function.EntryBlockId < 0 || function.EntryBlockId >= function.Blocks.Count)
            {
                if (report) Add(InvalidOrigin, "Reference provenance requires a valid entry block.", function.Source);
                return result;
            }
            var inputs = new State?[function.Blocks.Count];
            var initial = new State();
            foreach (SafeCoreMirLocal local in function.Locals)
            {
                Step();
                if (report && ContainsStoredReference(local.Type.Kind == SafeCoreSemanticTypeKind.Reference ? local.Type.ElementType! : local.Type, 0))
                    Add(InvalidOrigin, "References stored inside another reference or aggregate require a nested lifetime contract.", local.Source);
                if (local.Kind == SafeCoreMirLocalKind.Parameter && local.Type.Kind == SafeCoreSemanticTypeKind.Reference)
                    initial.Origins[local.Id] = [new(local.Id, true, [], local.Type.IsMutable)];
            }
            inputs[function.EntryBlockId] = initial;
            var queue = new Queue<int>();
            var queued = new HashSet<int> { function.EntryBlockId };
            var visits = new int[function.Blocks.Count];
            queue.Enqueue(function.EntryBlockId);
            for (int work = 0; queue.Count != 0; work++)
            {
                Step();
                if (work >= options.MaximumPaths) throw new ProvenanceLimitException();
                int blockId = queue.Dequeue();
                queued.Remove(blockId);
                if (++visits[blockId] > options.MaximumBlockVisits) throw new ProvenanceLimitException();
                SafeCoreMirBlock block = function.Blocks[blockId];
                State state = inputs[blockId]!.Clone(Step);
                if (block.Statements.Count > options.MaximumStatementsPerFunction) throw new ProvenanceLimitException();
                foreach (SafeCoreMirStatement statement in block.Statements)
                {
                    Step();
                    if (statement.DestinationPlace is { } destinationPlace)
                        CheckReferenceUse(SafeCoreMirOperand.PlaceValue(destinationPlace, statement.Value.Type, statement.Source), state, function, report);
                    foreach (SafeCoreMirOperand operand in statement.Value.Operands)
                    {
                        Step();
                        CheckReferenceUse(operand, state, function, report);
                    }
                    if (statement.Value.Type.Kind != SafeCoreSemanticTypeKind.Reference) continue;
                    SafeCoreMirRvalue value = statement.Value;
                    List<SafeCoreMirReferenceOrigin> origins = [];
                    if (value.Operands.Count == 1 && value.Kind == SafeCoreMirRvalueKind.Unary &&
                        value.Operator is "&" or "&mut" or "reborrow" or "reborrow_mut")
                    {
                        origins = ResolveBorrow(value.Operands[0], state, function, value.Type.IsMutable, report);
                    }
                    else if (value.Operands.Count == 1 && value.Kind is SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Coerce)
                    {
                        SafeCoreMirOperand operand = value.Operands[0];
                        origins = operand.Type.Kind == SafeCoreSemanticTypeKind.Reference
                            ? ResolveReference(operand, state, report)
                            : ResolveBorrow(operand, state, function, value.Type.IsMutable, report);
                        if (value.Kind == SafeCoreMirRvalueKind.Coerce)
                            origins = origins.Select(origin => origin with { IsMutable = origin.IsMutable && value.Type.IsMutable }).ToList();
                    }
                    else if (report) Add(InvalidOrigin, "A reference assignment requires an explicit borrow, reference copy, or checked call origin.", value.Source);
                    if (origins.Count == 0) state.Origins.Remove(statement.DestinationLocalId);
                    else state.Origins[statement.DestinationLocalId] = origins;
                }
                SafeCoreMirTerminator terminator = block.Terminator;
                if (terminator.Operand is { Kind: not SafeCoreMirOperandKind.Function } operandValue)
                    CheckReferenceUse(operandValue, state, function, report);
                foreach (SafeCoreMirOperand argument in terminator.Arguments)
                {
                    Step();
                    CheckReferenceUse(argument, state, function, report);
                }
                if (terminator.Kind == SafeCoreMirTerminatorKind.Return &&
                    terminator.Operand is { Type.Kind: SafeCoreSemanticTypeKind.Reference } returned)
                {
                    List<SafeCoreMirReferenceOrigin> origins = ResolveReference(returned, state, report);
                    foreach (SafeCoreMirReferenceOrigin origin in origins)
                    {
                        Step();
                        if (!origin.IsParameter && report) Add(EscapingReference, "A reference to local storage cannot escape through a return.", terminator.Source);
                    }
                    MergeOrigins(result, origins);
                }
                if (terminator.Kind == SafeCoreMirTerminatorKind.Call && terminator.DestinationLocalId is int destination &&
                    function.Locals[destination].Type.Kind == SafeCoreSemanticTypeKind.Reference)
                {
                    var origins = new List<SafeCoreMirReferenceOrigin>();
                    if (terminator.Operand is { Kind: SafeCoreMirOperandKind.Function } callee &&
                        summaries.TryGetValue(callee.Id, out List<SafeCoreMirReferenceOrigin>? summary) && summary.Count != 0)
                    {
                        foreach (SafeCoreMirReferenceOrigin source in summary)
                        {
                            Step();
                            if (!source.IsParameter || source.LocalId < 0 || source.LocalId >= terminator.Arguments.Count)
                            {
                                if (report) Add(InvalidOrigin, "A call return origin does not name an input reference parameter.", terminator.Source);
                                continue;
                            }
                            foreach (SafeCoreMirReferenceOrigin argument in ResolveReference(terminator.Arguments[source.LocalId], state, report))
                            {
                                Step();
                                MergeOrigins(origins, [Append(argument, source.Projections, source.IsMutable && argument.IsMutable)]);
                            }
                        }
                    }
                    else unresolved = true;
                    if (origins.Count == 0) state.Origins.Remove(destination);
                    else state.Origins[destination] = origins;
                }
                foreach (int target in Successors(terminator))
                {
                    Step();
                    if (target < 0 || target >= inputs.Length) continue;
                    bool changed;
                    if (inputs[target] is null) { inputs[target] = state.Clone(Step); changed = true; }
                    else changed = Join(inputs[target]!, state);
                    if (changed && queued.Add(target)) queue.Enqueue(target);
                }
            }
            return result;
        }

        private void CheckReferenceUse(SafeCoreMirOperand operand, State state, SafeCoreMirFunction function, bool report)
        {
            if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place)) return;
            if (operand.Id < 0 || operand.Id >= function.Locals.Count) return;
            if (function.Locals[operand.Id].Type.Kind != SafeCoreSemanticTypeKind.Reference) return;
            if (!state.Origins.TryGetValue(operand.Id, out List<SafeCoreMirReferenceOrigin>? origins))
            {
                if (report) Add(InvalidOrigin, "A reference use has no provenance initialized on every incoming control-flow path.", operand.Source);
                return;
            }
            foreach (SafeCoreMirReferenceOrigin origin in origins)
            {
                Step();
                if (!origin.IsParameter && function.Locals[origin.LocalId].StorageScope is SafeCoreMirSource scope &&
                    (!string.Equals(scope.SourcePath, operand.Source.SourcePath, StringComparison.Ordinal) ||
                     operand.Source.Span.Start < scope.Span.Start ||
                     operand.Source.Span.End > scope.Span.End) && report)
                    Add(EscapingReference, "A reference is used after its storage scope ends.", operand.Source);
            }
        }

        private bool ContainsStoredReference(SafeCoreType type, int depth)
        {
            Step();
            if (depth >= 128) throw new ProvenanceLimitException();
            if (type.Kind == SafeCoreSemanticTypeKind.Reference) return true;
            if (type.Kind == SafeCoreSemanticTypeKind.Tuple)
            {
                foreach (SafeCoreType element in type.Elements) if (ContainsStoredReference(element, depth + 1)) return true;
            }
            else if (type.Kind is SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice)
                return ContainsStoredReference(type.ElementType!, depth + 1);
            else if (type.Kind == SafeCoreSemanticTypeKind.Adt)
            {
                foreach (SafeCoreMirAdtLayout layout in program.AdtLayouts)
                {
                    Step();
                    if (layout.Type != type) continue;
                    foreach (SafeCoreMirAdtField field in layout.Fields) if (ContainsStoredReference(field.Type, depth + 1)) return true;
                    break;
                }
            }
            return false;
        }

        private List<SafeCoreMirReferenceOrigin> ResolveReference(SafeCoreMirOperand operand, State state, bool report)
        {
            Step();
            if (operand.Kind is SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place &&
                state.Origins.TryGetValue(operand.Id, out List<SafeCoreMirReferenceOrigin>? origins) &&
                (operand.Place is null || operand.Place.IsRoot)) return [.. origins];
            if (report) Add(InvalidOrigin, "Reference provenance is missing or stored in an unsupported reference-valued aggregate field.", operand.Source);
            return [];
        }

        private List<SafeCoreMirReferenceOrigin> ResolveBorrow(SafeCoreMirOperand operand, State state,
            SafeCoreMirFunction function, bool mutable, bool report)
        {
            Step();
            if (operand.Kind is not (SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place) || operand.Id < 0 || operand.Id >= function.Locals.Count)
            {
                if (report) Add(InvalidOrigin, "A borrow must identify typed local storage.", operand.Source);
                return [];
            }
            IReadOnlyList<SafeCoreMirProjection> projections = operand.Place?.Projections ?? [];
            if (function.Locals[operand.Id].Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                (projections.Count == 0 || projections[0].Kind == SafeCoreMirProjectionKind.Dereference))
            {
                if (!state.Origins.TryGetValue(operand.Id, out List<SafeCoreMirReferenceOrigin>? parents))
                {
                    if (report) Add(InvalidOrigin, "A reborrow requires an initialized parent reference.", operand.Source);
                    return [];
                }
                var result = new List<SafeCoreMirReferenceOrigin>();
                foreach (SafeCoreMirReferenceOrigin parent in parents)
                {
                    Step();
                    if (mutable && !parent.IsMutable && report) Add(InvalidOrigin, "A mutable reborrow cannot originate in a shared reference.", operand.Source);
                    MergeOrigins(result, [Append(parent, projections.Count == 0 ? [] : projections.Skip(1).ToArray(), mutable)]);
                }
                return result;
            }
            return [new(operand.Id, false, projections, mutable)];
        }

        private SafeCoreMirReferenceOrigin Append(SafeCoreMirReferenceOrigin origin,
            IReadOnlyList<SafeCoreMirProjection> projections, bool mutable)
        {
            Step();
            if (origin.Projections.Count + projections.Count > 128) throw new ProvenanceLimitException();
            // Runtime indexes are symbolic may-alias components. A callee's
            // local ID must never become a caller-local identity in a summary.
            return origin with { Projections = [.. origin.Projections,
                .. projections.Select(projection => projection.Kind == SafeCoreMirProjectionKind.DynamicIndex
                    ? SafeCoreMirProjection.DynamicIndex(0) : projection)], IsMutable = mutable };
        }

        private bool Join(State target, State incoming)
        {
            bool changed = false;
            foreach (int local in target.Origins.Keys.ToArray())
            {
                Step();
                if (!incoming.Origins.TryGetValue(local, out List<SafeCoreMirReferenceOrigin>? origins))
                { target.Origins.Remove(local); changed = true; }
                else changed |= MergeOrigins(target.Origins[local], origins);
            }
            return changed;
        }

        private bool MergeOrigins(List<SafeCoreMirReferenceOrigin> destination, IEnumerable<SafeCoreMirReferenceOrigin> incoming)
        {
            bool changed = false;
            foreach (SafeCoreMirReferenceOrigin origin in incoming)
            {
                Step();
                bool exists = false;
                foreach (SafeCoreMirReferenceOrigin present in destination)
                {
                    Step();
                    if (present.LocalId == origin.LocalId && present.IsParameter == origin.IsParameter &&
                        present.IsMutable == origin.IsMutable && present.Projections.SequenceEqual(origin.Projections)) { exists = true; break; }
                }
                if (exists) continue;
                if (destination.Count >= options.MaximumPaths) throw new ProvenanceLimitException();
                destination.Add(origin);
                changed = true;
            }
            return changed;
        }

        private void Add(string code, string message, SafeCoreMirSource source)
        {
            Step();
            if (diagnostics.Any(item => item.Code == code && item.Span == source.Span && item.SourcePath == source.SourcePath && item.Message == message)) return;
            if (diagnostics.Count >= options.MaximumDiagnostics) throw new ProvenanceLimitException();
            diagnostics.Add(new Diagnostic(code, message, source.Span) { SourcePath = source.SourcePath });
        }

        private static int[] Successors(SafeCoreMirTerminator terminator) => terminator.Kind switch
        {
            SafeCoreMirTerminatorKind.Goto or SafeCoreMirTerminatorKind.Call => [terminator.TargetBlockId],
            SafeCoreMirTerminatorKind.Branch => [terminator.TargetBlockId, terminator.FalseTargetBlockId],
            _ => [],
        };

        private sealed class State
        {
            public Dictionary<int, List<SafeCoreMirReferenceOrigin>> Origins { get; } = [];
            public State Clone(Action step)
            {
                var copy = new State();
                foreach ((int local, List<SafeCoreMirReferenceOrigin> origins) in Origins)
                { step(); copy.Origins[local] = [.. origins]; }
                return copy;
            }
        }
    }

    private sealed class ProvenanceLimitException : Exception;
}
