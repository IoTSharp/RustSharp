using System.Collections.Immutable;
using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>
/// Explicit ownership facts attached to a typed-MIR program.  Typed MIR by
/// itself describes values and control flow, but deliberately does not guess
/// whether an aggregate is moved, copied, borrowed, or dropped.  This wrapper
/// makes that boundary explicit while retaining the original MIR source
/// evidence for every diagnostic.
/// </summary>
public sealed record SafeCoreMirOwnershipEvidence
{
    public SafeCoreMirOwnershipEvidence(SafeCoreOwnershipProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        Program = program;
    }

    public SafeCoreOwnershipProgram Program { get; }
}

public static partial class SafeCoreMirOwnershipAdapter
{
    /// <summary>Diagnostic emitted when typed-MIR and ownership source facts do not line up.</summary>
    public const string EvidenceMismatch = "RSM3004";

    /// <summary>Diagnostic emitted when a MIR function/local/block has no ownership facts.</summary>
    public const string MissingEvidence = "RSM3005";

    /// <summary>
    /// Checks an explicit ownership program against typed MIR, then runs the
    /// bounded ownership analysis.  This is the production integration path
    /// for non-Copy values: no move, borrow, lifetime, or Drop fact is invented
    /// from a type alone.
    /// </summary>
    public static SafeCoreMirOwnershipResult Analyze(
        SafeCoreMirProgram mir,
        SafeCoreOwnershipProgram ownership,
        SafeCoreMirOwnershipOptions? options = null) =>
        Analyze(mir, new SafeCoreMirOwnershipEvidence(ownership), options);

    /// <summary>Named alias for callers that model evidence as an adaptation step.</summary>
    public static SafeCoreMirOwnershipResult Adapt(
        SafeCoreMirProgram mir,
        SafeCoreOwnershipProgram ownership,
        SafeCoreMirOwnershipOptions? options = null) =>
        Analyze(mir, ownership, options);

    public static SafeCoreMirOwnershipResult Analyze(
        SafeCoreMirProgram mir,
        SafeCoreMirOwnershipEvidence evidence,
        SafeCoreMirOwnershipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mir);
        ArgumentNullException.ThrowIfNull(evidence);
        options ??= new();
        ValidateEvidenceOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Diagnostic>();
        SafeCoreMirValidationResult? validation = null;
        var clock = Stopwatch.StartNew();
        int operations = 0;
        try
        {
            validation = SafeCoreMirValidation.Validate(mir, new SafeCoreMirValidationOptions
            {
                Timeout = options.Timeout,
                CancellationToken = options.CancellationToken,
                MaximumOperations = options.MaximumOperations,
                MaximumFunctions = options.MaximumFunctions,
                MaximumLocals = AggregateEvidenceValidationLimit(options.MaximumLocalsPerFunction, mir.Functions.Count, 100_000),
                MaximumBlocks = AggregateEvidenceValidationLimit(options.MaximumBlocksPerFunction, mir.Functions.Count, 100_000),
                MaximumStatements = AggregateEvidenceValidationLimit(options.MaximumStatementsPerFunction, mir.Functions.Count, 100_000),
                MaximumDiagnostics = options.MaximumDiagnostics,
            });
            if (!validation.IsSuccessful)
            {
                foreach (SafeCoreMirDiagnostic diagnostic in validation.Diagnostics)
                    AddEvidenceDiagnostic(diagnostics, ToEvidenceDiagnostic(diagnostic), options);
                return new(null, null, validation, diagnostics.AsReadOnly(), validation.IsTruncated);
            }

            // MIR validation and evidence correlation share the adapter's
            // operation budget with the ownership pass.  Preserve the
            // validator's actual bounded work instead of allowing the next
            // phase to restart from the full caller budget.
            operations = validation.OperationsUsed;

            SafeCoreMirReferenceProvenanceResult provenance = SafeCoreMirReferenceProvenance.AnalyzeValidated(mir,
                options with { MaximumOperations = Math.Max(1, options.MaximumOperations - operations), Timeout = Remaining(options.Timeout, clock) });
            operations += provenance.OperationsUsed;
            foreach (Diagnostic diagnostic in provenance.Diagnostics) AddEvidenceDiagnostic(diagnostics, diagnostic, options);
            if (!provenance.IsSuccessful)
                return new(null, null, validation, diagnostics.AsReadOnly(), provenance.IsTruncated);

            Correlate(mir, evidence.Program, diagnostics, options, clock, ref operations);
            if (diagnostics.Count != 0)
                return new(null, null, validation, diagnostics.AsReadOnly(), false);
            CorrelateReferenceEffects(mir, evidence.Program, provenance, diagnostics, options, clock, ref operations);
            if (diagnostics.Count != 0)
                return new(null, null, validation, diagnostics.AsReadOnly(), false);

            StepEvidence(options, clock, ref operations);
            int remainingOperations = options.MaximumOperations - operations;
            if (remainingOperations < 1)
                throw new EvidenceLimitException();
            SafeCoreOwnershipAnalysisResult ownershipResult = SafeCoreOwnershipAnalysis.Analyze(
                evidence.Program,
                new SafeCoreOwnershipOptions
                {
                    Timeout = Remaining(options.Timeout, clock),
                    MaximumOperations = remainingOperations,
                    MaximumPaths = options.MaximumPaths,
                    MaximumBlockVisits = options.MaximumBlockVisits,
                    MaximumDiagnostics = options.MaximumDiagnostics,
                    InferNonLexicalLifetimes = options.InferNonLexicalLifetimes,
                    InferNll = options.InferNll,
                    CancellationToken = options.CancellationToken,
                });
            return new(evidence.Program, ownershipResult, validation, diagnostics.AsReadOnly(), ownershipResult.IsTruncated);
        }
        catch (EvidenceLimitException)
        {
            AddEvidenceLimitDiagnostic(diagnostics, options);
            return new(null, null, validation, diagnostics.AsReadOnly(), true);
        }
    }

    private static void Correlate(
        SafeCoreMirProgram mir,
        SafeCoreOwnershipProgram ownership,
        List<Diagnostic> diagnostics,
        SafeCoreMirOwnershipOptions options,
        Stopwatch clock,
        ref int operations)
    {
        if (ownership.Functions.Count != mir.Functions.Count)
        {
            AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(MissingEvidence,
                $"Typed MIR has {mir.Functions.Count} functions but ownership evidence has {ownership.Functions.Count}.",
                mir.Functions.Count > 0 ? mir.Functions[0].Source : null), options);
            return;
        }

        for (int functionIndex = 0; functionIndex < mir.Functions.Count; functionIndex++)
        {
            StepEvidence(options, clock, ref operations);
            SafeCoreMirFunction mirFunction = mir.Functions[functionIndex];
            SafeCoreOwnershipFunction ownershipFunction = ownership.Functions[functionIndex];
            if (!string.Equals(mirFunction.Name, ownershipFunction.Name, StringComparison.Ordinal))
            {
                AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                    $"Ownership function '{ownershipFunction.Name}' does not match typed MIR function '{mirFunction.Name}'.",
                    ownershipFunction.Source), options);
                continue;
            }

            RequireSameSource(mirFunction.Source, ownershipFunction.Source, diagnostics, options,
                $"Ownership function '{mirFunction.Name}' source evidence differs from typed MIR.");
            if (ownershipFunction.Locals.Count < mirFunction.Locals.Count)
            {
                AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(MissingEvidence,
                    $"Ownership function '{mirFunction.Name}' is missing local ownership facts.", ownershipFunction.Source), options);
            }

            // Correlate ownership locals in both directions.  MIR validation
            // guarantees dense MIR IDs, while explicit evidence is supplied
            // by a separate producer and may contain duplicate, out-of-range,
            // or otherwise extra facts.  Diagnose every such fact before the
            // ownership analyzer can consume it.
            var matchedLocalIds = new HashSet<int>();
            foreach (SafeCoreOwnershipLocal ownershipLocal in ownershipFunction.Locals)
            {
                StepEvidence(options, clock, ref operations);
                if (ownershipLocal.Id < 0 || ownershipLocal.Id >= mirFunction.Locals.Count)
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                        $"Ownership function '{mirFunction.Name}' has an extra local fact {ownershipLocal.Id} ('{ownershipLocal.Name}') not present in typed MIR.",
                        ownershipLocal.Source), options);
                    continue;
                }

                if (!matchedLocalIds.Add(ownershipLocal.Id))
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                        $"Ownership function '{mirFunction.Name}' has duplicate local facts for local {ownershipLocal.Id}.",
                        ownershipLocal.Source), options);
                    continue;
                }

                SafeCoreMirLocal mirLocal = mirFunction.Locals[ownershipLocal.Id];

                SafeCoreOwnershipKind expectedKind = mirLocal.DestructorFunctionId.HasValue || !IsCopyType(mirLocal.Type, mir)
                    ? SafeCoreOwnershipKind.Move : SafeCoreOwnershipKind.Copy;
                bool expectedDrop = mirLocal.DestructorFunctionId.HasValue;
                if (!string.Equals(mirLocal.Name, ownershipLocal.Name, StringComparison.Ordinal) ||
                    mirLocal.Type != ownershipLocal.Type || ownershipLocal.Kind != expectedKind ||
                    ownershipLocal.HasDrop != expectedDrop || ownershipLocal.IsReference != (mirLocal.Type.Kind == SafeCoreSemanticTypeKind.Reference) ||
                    ownershipLocal.InitiallyInitialized != (mirLocal.Kind == SafeCoreMirLocalKind.Parameter))
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                        $"Ownership fact for local {mirLocal.Id} does not match its typed-MIR name, type, move kind, or Drop contract.",
                        ownershipLocal.Source), options);
                }
                RequireSameSource(mirLocal.Source, ownershipLocal.Source, diagnostics, options,
                    $"Ownership fact for local {mirLocal.Id} has different source evidence.");
            }

            foreach (SafeCoreMirLocal mirLocal in mirFunction.Locals)
            {
                StepEvidence(options, clock, ref operations);
                if (!matchedLocalIds.Contains(mirLocal.Id))
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(MissingEvidence,
                        $"Ownership function '{mirFunction.Name}' has no fact for local {mirLocal.Id} ('{mirLocal.Name}').",
                        mirLocal.Source), options);
                }
            }

            // Correlate blocks in both directions.  Ownership evidence may
            // contain an extra fact (which is diagnosed below), but it must
            // never silently omit a typed-MIR block.  Indexing the validated
            // MIR arena keeps the bounded correlation linear rather than
            // performing a potentially quadratic FirstOrDefault scan.
            var mirBlocksById = mirFunction.Blocks.ToDictionary(block => block.Id);
            var matchedBlockIds = new HashSet<int>();
            foreach (SafeCoreOwnershipBlock ownershipBlock in ownershipFunction.Blocks)
            {
                StepEvidence(options, clock, ref operations);
                if (!matchedBlockIds.Add(ownershipBlock.Id))
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                        $"Ownership function '{mirFunction.Name}' has duplicate block facts for block {ownershipBlock.Id}.",
                        ownershipBlock.Source), options);
                    continue;
                }

                if (!mirBlocksById.TryGetValue(ownershipBlock.Id, out SafeCoreMirBlock? mirBlock))
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(MissingEvidence,
                        $"Ownership function '{mirFunction.Name}' references block {ownershipBlock.Id} absent from typed MIR.",
                        ownershipBlock.Source), options);
                    continue;
                }

                RequireSameSource(mirBlock.Source, ownershipBlock.Source, diagnostics, options,
                    $"Ownership block {ownershipBlock.Id} has different source evidence.");

                // Every ownership effect must be anchored to an actual MIR
                // statement or terminator source in this block. This keeps
                // independently produced ownership facts auditable and
                // prevents fabricated projected places at unrelated spans.
                var allowedSources = new List<SafeCoreMirSource> { mirBlock.Source, mirBlock.Terminator.Source };
                var allowedPlaces = new List<SafeCoreMirPlace>();
                foreach (SafeCoreMirStatement statement in mirBlock.Statements)
                {
                    StepEvidence(options, clock, ref operations);
                    allowedSources.Add(statement.Source);
                    if (statement.DestinationPlace is { } destinationPlace) allowedPlaces.Add(destinationPlace);
                    foreach (SafeCoreMirOperand operand in statement.Value.Operands)
                    {
                        StepEvidence(options, clock, ref operations);
                        if (operand.Kind == SafeCoreMirOperandKind.Place && operand.Place is not null)
                            allowedPlaces.Add(operand.Place);
                    }
                }
                if (mirBlock.Terminator.Operand is SafeCoreMirOperand terminatorOperand)
                {
                    if (terminatorOperand.Kind == SafeCoreMirOperandKind.Place && terminatorOperand.Place is not null)
                        allowedPlaces.Add(terminatorOperand.Place);
                }
                foreach (SafeCoreMirOperand argument in mirBlock.Terminator.Arguments)
                {
                    StepEvidence(options, clock, ref operations);
                    if (argument.Kind == SafeCoreMirOperandKind.Place && argument.Place is not null)
                        allowedPlaces.Add(argument.Place);
                }

                foreach (SafeCoreOwnershipInstruction instruction in ownershipBlock.Instructions)
                {
                    StepEvidence(options, clock, ref operations);
                    if (!allowedSources.Any(source => SameSource(source, instruction.Source)))
                    {
                        AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                            $"Ownership instruction in block {ownershipBlock.Id} has source evidence absent from typed MIR.", instruction.Source), options);
                    }
                    ValidateEvidencePlace(instruction.Place, instruction.LocalId, allowedPlaces,
                        instruction.Source, diagnostics, options);
                    ValidateEvidencePlace(instruction.RelatedPlace, instruction.RelatedLocalId, allowedPlaces,
                        instruction.Source, diagnostics, options);
                }
            }

            foreach (SafeCoreMirBlock mirBlock in mirFunction.Blocks)
            {
                StepEvidence(options, clock, ref operations);
                if (!matchedBlockIds.Contains(mirBlock.Id))
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(MissingEvidence,
                        $"Ownership function '{mirFunction.Name}' has no fact for typed-MIR block {mirBlock.Id}.",
                        mirBlock.Source), options);
                }
            }
        }
    }

    private static void CorrelateReferenceEffects(SafeCoreMirProgram mir, SafeCoreOwnershipProgram evidence,
        SafeCoreMirReferenceProvenanceResult provenance, List<Diagnostic> diagnostics,
        SafeCoreMirOwnershipOptions options, Stopwatch clock, ref int operations)
    {
        try
        {
            for (int functionIndex = 0; functionIndex < mir.Functions.Count; functionIndex++)
            {
                StepEvidence(options, clock, ref operations);
                SafeCoreMirFunction function = mir.Functions[functionIndex];
                if (!function.Locals.Any(local => local.Type.Kind == SafeCoreSemanticTypeKind.Reference)) continue;
                SafeCoreOwnershipFunction? expected = AdaptFunction(mir, provenance, function, options, clock, ref operations, diagnostics);
                if (expected is null) continue;
                if (evidence.Functions[functionIndex].Scopes.Count != 1 ||
                    evidence.Functions[functionIndex].Locals.Any(local => local.ScopeId != 0))
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                        "Reference evidence cannot replace typed-MIR storage scopes with invented ownership scopes.", function.Source), options);
                foreach (SafeCoreOwnershipBlock block in expected.Blocks)
                {
                    StepEvidence(options, clock, ref operations);
                    SafeCoreOwnershipBlock actual = evidence.Functions[functionIndex].Blocks.Single(candidate => candidate.Id == block.Id);
                    SafeCoreOwnershipInstruction[] effects = block.Instructions.Where(effect => effect.LocalId < function.Locals.Count).ToArray();
                    if (effects.Length != actual.Instructions.Count || actual.ScopeId != 0)
                        AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                            "Explicit reference evidence must retain the exact typed-MIR ownership effects and their order.", block.Source), options);
                    for (int effectIndex = 0; effectIndex < Math.Min(effects.Length, actual.Instructions.Count); effectIndex++)
                    {
                        StepEvidence(options, clock, ref operations);
                        SafeCoreOwnershipInstruction effect = effects[effectIndex];
                        SafeCoreOwnershipInstruction candidate = actual.Instructions[effectIndex];
                        bool matched = effect.Kind == candidate.Kind && effect.LocalId == candidate.LocalId &&
                                effect.RelatedLocalId == candidate.RelatedLocalId && effect.IsMutable == candidate.IsMutable &&
                                SameSource(effect.Source, candidate.Source) && EqualOwnershipPlace(effect.Place, candidate.Place, effect.LocalId) &&
                                EqualOwnershipPlace(effect.RelatedPlace, candidate.RelatedPlace, effect.RelatedLocalId) &&
                                EqualAlternativePlaces(effect.AlternativePlaces, candidate.AlternativePlaces, options, clock, ref operations) &&
                                EqualReferenceArguments(effect.ReferenceArguments, candidate.ReferenceArguments, options, clock, ref operations);
                        if (!matched) AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                            "Explicit evidence omits or changes a typed-MIR reference ownership effect.", effect.Source), options);
                    }
                    if (block.Terminator.LocalId < function.Locals.Count &&
                        (block.Terminator.Kind != actual.Terminator.Kind || block.Terminator.LocalId != actual.Terminator.LocalId ||
                         block.Terminator.TargetBlockId != actual.Terminator.TargetBlockId || block.Terminator.FalseTargetBlockId != actual.Terminator.FalseTargetBlockId))
                        AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                            "Explicit reference evidence changes the typed-MIR control-flow or return contract.", block.Source), options);
                }
            }
        }
        catch (AdapterLimitException) { throw new EvidenceLimitException(); }
    }

    private static bool EqualOwnershipPlace(SafeCoreOwnershipPlace? expected, SafeCoreOwnershipPlace? actual, int localId)
    {
        if (expected is null || expected.IsRoot) return actual is null || actual.IsRoot && actual.LocalId == localId;
        return actual is not null && expected.LocalId == actual.LocalId && expected.Projections.SequenceEqual(actual.Projections);
    }

    private static bool EqualAlternativePlaces(IReadOnlyList<SafeCoreOwnershipPlace>? expected,
        IReadOnlyList<SafeCoreOwnershipPlace>? actual, SafeCoreMirOwnershipOptions options, Stopwatch clock, ref int operations)
    {
        if ((expected?.Count ?? 0) != (actual?.Count ?? 0)) return false;
        for (int index = 0; index < (expected?.Count ?? 0); index++)
        {
            StepEvidence(options, clock, ref operations);
            if (!EqualOwnershipPlace(expected![index], actual![index], expected[index].LocalId)) return false;
        }
        return true;
    }

    private static bool EqualReferenceArguments(IReadOnlyList<SafeCoreOwnershipCallArgument>? expected,
        IReadOnlyList<SafeCoreOwnershipCallArgument>? actual, SafeCoreMirOwnershipOptions options, Stopwatch clock, ref int operations)
    {
        if ((expected?.Count ?? 0) != (actual?.Count ?? 0)) return false;
        for (int index = 0; index < (expected?.Count ?? 0); index++)
        {
            StepEvidence(options, clock, ref operations);
            if (expected![index].IsMutable != actual![index].IsMutable ||
                !EqualOwnershipPlace(expected[index].Place, actual[index].Place, expected[index].Place.LocalId)) return false;
        }
        return true;
    }

    private static void ValidateEvidencePlace(
        SafeCoreOwnershipPlace? place,
        int localId,
        IReadOnlyList<SafeCoreMirPlace> allowedPlaces,
        SafeCoreMirSource source,
        List<Diagnostic> diagnostics,
        SafeCoreMirOwnershipOptions options)
    {
        if (place is null) return;
        if (place.LocalId != localId || place.Projections.Count > 128)
        {
            AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                "Ownership projected place metadata does not match its instruction local.", source), options);
            return;
        }
        if (place.Projections.Count == 0) return;
        if (!allowedPlaces.Any(candidate => SamePlace(candidate, place)))
        {
            AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                "Ownership projected place has no corresponding typed-MIR place evidence.", source), options);
        }
    }

    private static bool SameSource(SafeCoreMirSource left, SafeCoreMirSource right) =>
        string.Equals(left.SourcePath, right.SourcePath, StringComparison.Ordinal) &&
        left.Span == right.Span && left.HirNodeId == right.HirNodeId && left.SourceLength == right.SourceLength;

    private static bool SamePlace(SafeCoreMirPlace mirPlace, SafeCoreOwnershipPlace ownershipPlace)
    {
        if (mirPlace.LocalId != ownershipPlace.LocalId || mirPlace.Projections.Count != ownershipPlace.Projections.Count)
            return false;
        for (int index = 0; index < mirPlace.Projections.Count; index++)
        {
            SafeCoreMirProjection left = mirPlace.Projections[index];
            SafeCoreOwnershipProjection right = ownershipPlace.Projections[index];
            if ((int)left.Kind != (int)right.Kind || !string.Equals(left.Name, right.Name, StringComparison.Ordinal) || left.Index != right.Index)
                return false;
        }
        return true;
    }

    private static void RequireSameSource(
        SafeCoreMirSource? expected,
        SafeCoreMirSource? actual,
        List<Diagnostic> diagnostics,
        SafeCoreMirOwnershipOptions options,
        string message)
    {
        if (expected is null || actual is null ||
            !string.Equals(expected.SourcePath, actual.SourcePath, StringComparison.Ordinal) ||
            expected.Span != actual.Span || expected.HirNodeId != actual.HirNodeId ||
            expected.SourceLength != actual.SourceLength)
        {
            AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch, message, actual ?? expected), options);
        }
    }

    private static Diagnostic ToEvidenceDiagnostic(SafeCoreMirDiagnostic diagnostic) =>
        MakeEvidenceDiagnostic(diagnostic.Code, diagnostic.Message, diagnostic.Source);

    private static Diagnostic MakeEvidenceDiagnostic(string code, string message, SafeCoreMirSource? source)
    {
        SafeCoreMirSource evidence = source ?? new("<invalid>", new TextSpan(0, 0), 0, 0);
        return new Diagnostic(code, message, evidence.Span) { SourcePath = evidence.SourcePath };
    }

    private static void AddEvidenceDiagnostic(List<Diagnostic> diagnostics, Diagnostic diagnostic,
        SafeCoreMirOwnershipOptions options)
    {
        if (diagnostics.Count >= options.MaximumDiagnostics) throw new EvidenceLimitException();
        diagnostics.Add(diagnostic);
    }

    private static void AddEvidenceLimitDiagnostic(List<Diagnostic> diagnostics, SafeCoreMirOwnershipOptions options)
    {
        if (diagnostics.Count < options.MaximumDiagnostics)
            diagnostics.Add(MakeEvidenceDiagnostic(LimitReached,
                "Typed MIR ownership evidence exceeded its bounded work or time limit.", null));
    }

    private static void ValidateEvidenceOptions(SafeCoreMirOwnershipOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 ||
            options.MaximumDiagnostics is < 1 or > 4_096 ||
            options.MaximumFunctions is < 1 or > 4_096 ||
            options.MaximumLocalsPerFunction is < 1 or > 4_096 ||
            options.MaximumBlocksPerFunction is < 1 or > 16_384 ||
            options.MaximumPaths is < 1 or > 65_536 ||
            options.MaximumBlockVisits is < 1 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static int AggregateEvidenceValidationLimit(int perFunctionLimit, int functionCount, int hardLimit) =>
        (int)Math.Min(hardLimit, (long)perFunctionLimit * Math.Max(1, functionCount));

    private static TimeSpan Remaining(TimeSpan budget, Stopwatch clock)
    {
        TimeSpan remaining = budget - clock.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static void StepEvidence(SafeCoreMirOwnershipOptions options, Stopwatch clock, ref int operations)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (++operations > options.MaximumOperations || clock.Elapsed >= options.Timeout)
            throw new EvidenceLimitException();
    }

    private sealed class EvidenceLimitException : Exception;
}
