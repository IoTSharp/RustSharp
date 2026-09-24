using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>A referent rooted in an input reference parameter or local storage.</summary>
public sealed record SafeCoreMirReferenceOrigin(int LocalId, bool IsParameter,
    IReadOnlyList<SafeCoreMirProjection> Projections, bool IsMutable)
{
    public IReadOnlyList<SafeCoreMirProjection> ValuePath { get; init; } = [];
    public IReadOnlyList<SafeCoreMirProjection> ParameterPath { get; init; } = [];
    public bool IsStatic { get; init; }
    /// <summary>The next slice index is relative to an unknown offset in the recorded owner.</summary>
    public bool HasUnknownSliceOffset { get; init; }
}

public sealed record SafeCoreMirReferenceSummary(int FunctionId,
    IReadOnlyList<SafeCoreMirReferenceOrigin> ReturnOrigins);

public sealed record SafeCoreMirReferenceProvenanceResult(
    IReadOnlyList<SafeCoreMirReferenceSummary> Functions,
    IReadOnlyList<Diagnostic> Diagnostics, bool IsTruncated, int OperationsUsed)
{
    public bool IsSuccessful => Diagnostics.Count == 0 && !IsTruncated;
}

/// <summary>Bounded forward dataflow. Joins union proven origins and intersect initialization.</summary>
public static partial class SafeCoreMirReferenceProvenance
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

    private sealed partial class Worker(SafeCoreMirProgram program, SafeCoreMirOwnershipOptions options)
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
                        List<SafeCoreMirReferenceOrigin> found = AnalyzeCompositeFunction(function, report: false);
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
                    AnalyzeCompositeFunction(function, report: true);
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
                    ? Array.AsReadOnly(origins.Select(origin => origin with { Projections = Array.AsReadOnly(origin.Projections.ToArray()),
                        ValuePath = Array.AsReadOnly(origin.ValuePath.ToArray()), ParameterPath = Array.AsReadOnly(origin.ParameterPath.ToArray()) }).ToArray()) : [])).ToArray()),
            diagnostics.AsReadOnly(), truncated, operations);

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

        private SafeCoreMirReferenceOrigin Append(SafeCoreMirReferenceOrigin origin,
            IReadOnlyList<SafeCoreMirProjection> projections, bool mutable)
        {
            Step();
            if (origin.Projections.Count + projections.Count > 128) throw new ProvenanceLimitException();
            // Runtime indexes are symbolic may-alias components. A callee's
            // local ID must never become a caller-local identity in a summary.
            bool indexedSubslice = origin.HasUnknownSliceOffset && projections.Count != 0 &&
                projections[0].Kind is SafeCoreMirProjectionKind.ArrayIndex or SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.FromEndIndex;
            return origin with { Projections = [.. origin.Projections,
                .. projections.Select((projection, index) => projection.Kind == SafeCoreMirProjectionKind.DynamicIndex || indexedSubslice && index == 0
                    ? SafeCoreMirProjection.DynamicIndex(0) : projection)], IsMutable = mutable,
                HasUnknownSliceOffset = origin.HasUnknownSliceOffset && !indexedSubslice };
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
                        present.IsMutable == origin.IsMutable && present.IsStatic == origin.IsStatic &&
                        present.HasUnknownSliceOffset == origin.HasUnknownSliceOffset &&
                        present.ValuePath.SequenceEqual(origin.ValuePath) && present.ParameterPath.SequenceEqual(origin.ParameterPath) &&
                        present.Projections.SequenceEqual(origin.Projections)) { exists = true; break; }
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

    }

    private sealed class ProvenanceLimitException : Exception;
}
