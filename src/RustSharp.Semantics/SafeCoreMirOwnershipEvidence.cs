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

            Correlate(mir, evidence.Program, diagnostics, options, clock, ref operations);
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

                if (!string.Equals(mirLocal.Name, ownershipLocal.Name, StringComparison.Ordinal) ||
                    mirLocal.Type != ownershipLocal.Type)
                {
                    AddEvidenceDiagnostic(diagnostics, MakeEvidenceDiagnostic(EvidenceMismatch,
                        $"Ownership fact for local {mirLocal.Id} does not match its typed-MIR name or type.",
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
