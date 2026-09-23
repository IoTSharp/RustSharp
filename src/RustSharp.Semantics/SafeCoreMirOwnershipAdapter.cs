using System.Diagnostics;
using System.Globalization;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Independent limits for the typed-MIR ownership bridge.</summary>
public sealed record SafeCoreMirOwnershipOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumDiagnostics { get; init; } = 256;
    public int MaximumFunctions { get; init; } = 4_096;
    public int MaximumLocalsPerFunction { get; init; } = 4_096;
    public int MaximumBlocksPerFunction { get; init; } = 16_384;
    public int MaximumStatementsPerFunction { get; init; } = 65_536;
    public int MaximumInstructionsPerBlock { get; init; } = 65_536;
    public int MaximumPaths { get; init; } = 4_096;
    public int MaximumBlockVisits { get; init; } = 128;
    public bool InferNonLexicalLifetimes { get; init; }
    public bool InferNll { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public static class SafeCoreMirOwnershipDiagnosticCodes
{
    public const string InvalidEvidence = "RSM3001";
    public const string Unsupported = "RSM3002";
    public const string LimitReached = "RSM3003";

    // Descriptive aliases keep callers from having to couple to the profile's
    // historical wording while retaining one machine-readable code per case.
    public const string InvalidInput = InvalidEvidence;
    public const string UnsupportedNode = Unsupported;
}

/// <summary>Result of adapting and checking one typed-MIR ownership profile.</summary>
public sealed record SafeCoreMirOwnershipResult(
    SafeCoreOwnershipProgram? Program,
    SafeCoreOwnershipAnalysisResult? Ownership,
    SafeCoreMirValidationResult? Validation,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool IsTruncated)
{
    public SafeCoreOwnershipAnalysisResult? Analysis => Ownership;
    public bool IsSuccessful => Program is not null &&
        Validation is { IsSuccessful: true } &&
        Ownership is { IsSuccessful: true } &&
        Diagnostics.Count == 0 && !IsTruncated;
}

/// <summary>
/// Converts the typed-MIR profile into the bounded ownership IR. The
/// typed-MIR model has no lifetime, borrow, Drop, or scope metadata, so this
/// bridge accepts only values whose Copy evidence is structural: primitive
/// scalars and finite tuples/arrays made entirely from those scalars. It places
/// every accepted local in root scope 0. It never invents move or borrow facts
/// for references, ADTs, closures, or function values; those inputs receive a
/// stable adapter diagnostic instead.
/// </summary>
public static partial class SafeCoreMirOwnershipAdapter
{
    public const string Profile = "safe-core-mir-ownership-v1";
    public const string InvalidEvidence = SafeCoreMirOwnershipDiagnosticCodes.InvalidEvidence;
    public const string Unsupported = SafeCoreMirOwnershipDiagnosticCodes.Unsupported;
    public const string LimitReached = SafeCoreMirOwnershipDiagnosticCodes.LimitReached;

    private static readonly SafeCoreMirSource InvalidSource =
        new("<invalid>", new TextSpan(0, 0), 0, 0);

    public static SafeCoreMirOwnershipResult Analyze(
        SafeCoreMirProgram program,
        SafeCoreMirOwnershipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= new();
        ValidateOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<Diagnostic>();
        var clock = Stopwatch.StartNew();
        int operations = 0;
        SafeCoreMirValidationResult? validationResult = null;
        try
        {
            SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(program,
                new SafeCoreMirValidationOptions
                {
                    Timeout = options.Timeout,
                    CancellationToken = options.CancellationToken,
                    MaximumOperations = options.MaximumOperations,
                    MaximumFunctions = options.MaximumFunctions,
                    // MIR validation exposes whole-program arenas, while the
                    // adapter options are deliberately per-function. Scale
                    // the validation budgets by the input function count and
                    // keep the validator's own hard ceiling in force.
                    MaximumLocals = AggregateValidationLimit(
                        options.MaximumLocalsPerFunction, program.Functions.Count, 100_000),
                    MaximumBlocks = AggregateValidationLimit(
                        options.MaximumBlocksPerFunction, program.Functions.Count, 100_000),
                    MaximumStatements = AggregateValidationLimit(
                        options.MaximumStatementsPerFunction, program.Functions.Count, 100_000),
                    MaximumDiagnostics = options.MaximumDiagnostics,
                });
            validationResult = validation;
            if (!validation.IsSuccessful)
            {
                foreach (SafeCoreMirDiagnostic diagnostic in validation.Diagnostics)
                    AddDiagnostic(diagnostics, ToDiagnostic(diagnostic), options);
                return new(null, null, validation, diagnostics.AsReadOnly(), validation.IsTruncated);
            }

            // Validation is part of the adapter's shared bounded work budget.
            // Carry its actual step count forward so adaptation and the
            // ownership analysis cannot reset the caller's operation limit.
            operations = validation.OperationsUsed;

            if (program.Functions.Count == 0)
            {
                AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                    "A typed MIR ownership program requires at least one function.", InvalidSource), options);
                return new(null, null, validation, diagnostics.AsReadOnly(), false);
            }

            var ownershipFunctions = new List<SafeCoreOwnershipFunction>(program.Functions.Count);
            for (int functionIndex = 0; functionIndex < program.Functions.Count; functionIndex++)
            {
                Step(options, clock, ref operations);
                SafeCoreMirFunction function = program.Functions[functionIndex];
                SafeCoreOwnershipFunction? ownership = AdaptFunction(function, options, clock, ref operations, diagnostics);
                if (ownership is null)
                    continue;
                ownershipFunctions.Add(ownership);
            }

            if (diagnostics.Count != 0)
                return new(null, null, validation, diagnostics.AsReadOnly(), false);

            Step(options, clock, ref operations);
            var ownershipProgram = new SafeCoreOwnershipProgram(ownershipFunctions);
            TimeSpan remaining = options.Timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new AdapterLimitException();
            int remainingOperations = options.MaximumOperations - operations;
            if (remainingOperations < 1) throw new AdapterLimitException();
            SafeCoreOwnershipAnalysisResult ownershipResult = SafeCoreOwnershipAnalysis.Analyze(
                ownershipProgram,
                new SafeCoreOwnershipOptions
                {
                    Timeout = remaining > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : remaining,
                    MaximumOperations = remainingOperations,
                    MaximumPaths = options.MaximumPaths,
                    MaximumBlockVisits = options.MaximumBlockVisits,
                    MaximumDiagnostics = options.MaximumDiagnostics,
                    InferNonLexicalLifetimes = options.InferNonLexicalLifetimes,
                    InferNll = options.InferNll,
                    CancellationToken = options.CancellationToken,
                });
            foreach (SafeCoreOwnershipDiagnostic diagnostic in ownershipResult.Diagnostics)
                AddDiagnostic(diagnostics, new Diagnostic(diagnostic.Code, diagnostic.Message, diagnostic.Source.Span)
                { SourcePath = diagnostic.Source.SourcePath }, options);
            return new(ownershipProgram, ownershipResult, validation, diagnostics.AsReadOnly(), ownershipResult.IsTruncated);
        }
        catch (AdapterLimitException)
        {
            AddLimitDiagnostic(diagnostics, options);
            return new(null, null, validationResult, diagnostics.AsReadOnly(), true);
        }
    }

    /// <summary>Alias for callers that name the conversion step explicitly.</summary>
    public static SafeCoreMirOwnershipResult Adapt(
        SafeCoreMirProgram program,
        SafeCoreMirOwnershipOptions? options = null) => Analyze(program, options);

    private static SafeCoreOwnershipFunction? AdaptFunction(
        SafeCoreMirFunction function,
        SafeCoreMirOwnershipOptions options,
        Stopwatch clock,
        ref int operations,
        List<Diagnostic> diagnostics)
    {
        if (function.Locals.Count > options.MaximumLocalsPerFunction ||
            function.Blocks.Count > options.MaximumBlocksPerFunction)
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(LimitReached,
                "Typed MIR function exceeds the ownership adapter arena limit.", function.Source), options);
            return null;
        }

        var locals = new List<SafeCoreOwnershipLocal>(function.Locals.Count);
        var referenceOrigins = new Dictionary<int, SafeCoreOwnershipPlace>();
        for (int blockIndex = 0; blockIndex < function.Blocks.Count; blockIndex++)
        {
            Step(options, clock, ref operations);
            foreach (SafeCoreMirStatement statement in function.Blocks[blockIndex].Statements)
            {
                Step(options, clock, ref operations);
                if (statement.Value.Type.Kind != SafeCoreSemanticTypeKind.Reference) continue;
                SafeCoreMirRvalue value = statement.Value;
                bool directBorrow = value.Kind == SafeCoreMirRvalueKind.Unary &&
                    value.Operator is ("&" or "&mut" or "reborrow" or "reborrow_mut") &&
                    value.Operands.Count == 1 &&
                    value.Operands[0].Kind is SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place;
                bool arraySliceCoercion = value.Kind == SafeCoreMirRvalueKind.Coerce &&
                    value.Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                    value.Type.ElementType?.Kind == SafeCoreSemanticTypeKind.Slice &&
                    value.Operands.Count == 1 &&
                    value.Operands[0].Kind is SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place &&
                    (value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Array ||
                     value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                     value.Operands[0].Type.ElementType?.Kind == SafeCoreSemanticTypeKind.Array);
                bool referenceCopy = value.Kind == SafeCoreMirRvalueKind.Use &&
                    value.Operands.Count == 1 &&
                    (value.Operands[0].Kind is SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place) &&
                    value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Reference;
                if (!directBorrow && !referenceCopy && !arraySliceCoercion)
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                        "Reference locals require one explicit MIR borrow origin; unproven reference copies are unsupported.", value.Source), options);
                    return null;
                }

                SafeCoreOwnershipPlace origin;
                if (directBorrow || arraySliceCoercion)
                {
                    if (!TryOwnershipPlace(value.Operands[0], function, options, clock, ref operations,
                        diagnostics, out origin)) return null;
                }
                else if (value.Operands[0].Kind == SafeCoreMirOperandKind.Place)
                {
                    if (!TryOwnershipPlace(value.Operands[0], function, options, clock, ref operations,
                        diagnostics, out origin)) return null;
                }
                else if (!referenceOrigins.TryGetValue(value.Operands[0].Id, out SafeCoreOwnershipPlace? copiedOrigin) ||
                         copiedOrigin is null)
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                        "A reference copy must name a reference local with established place provenance.", value.Source), options);
                    return null;
                }
                else origin = copiedOrigin;

                if (!referenceOrigins.TryAdd(statement.DestinationLocalId, origin))
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                        "A reference local may have only one explicit MIR provenance fact.", value.Source), options);
                    return null;
                }
            }
        }
        bool valid = true;
        for (int index = 0; index < function.Locals.Count; index++)
        {
            Step(options, clock, ref operations);
            SafeCoreMirLocal local = function.Locals[index];
            if (local.Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                (local.Kind == SafeCoreMirLocalKind.Parameter || !referenceOrigins.ContainsKey(local.Id)))
            {
                AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                    "A reference requires local MIR provenance; imported reference contracts are not available.", local.Source), options);
                valid = false;
                continue;
            }
            if (!IsStructuralCopy(local.Type) && local.Type.Kind != SafeCoreSemanticTypeKind.Reference &&
                !local.IsUnitAdt && !local.DestructorFunctionId.HasValue)
            {
                AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                    "The typed-MIR ownership bridge requires structural Copy locals; references, ADTs, closures, and function values need explicit ownership evidence.",
                    local.Source), options);
                valid = false;
                continue;
            }

            locals.Add(new SafeCoreOwnershipLocal(
                local.Id,
                local.Name,
                local.Type,
                local.Type.Kind == SafeCoreSemanticTypeKind.Reference && local.Type.IsMutable ||
                local.DestructorFunctionId.HasValue
                    ? SafeCoreOwnershipKind.Move : SafeCoreOwnershipKind.Copy,
                HasDrop: local.DestructorFunctionId.HasValue,
                ScopeId: 0,
                IsReference: local.Type.Kind == SafeCoreSemanticTypeKind.Reference,
                InitiallyInitialized: local.Kind == SafeCoreMirLocalKind.Parameter,
                local.Source));
        }

        if (!valid) return null;

        var referenceRoots = new Dictionary<int, SafeCoreOwnershipPlace>();
        foreach (int reference in referenceOrigins.Keys)
        {
            Step(options, clock, ref operations);
            SafeCoreOwnershipPlace current = referenceOrigins[reference];
            for (int depth = 0; depth <= function.Locals.Count; depth++)
            {
                Step(options, clock, ref operations);
                if (current.LocalId < 0 || current.LocalId >= function.Locals.Count) break;
                if (function.Locals[current.LocalId].Type.Kind != SafeCoreSemanticTypeKind.Reference)
                {
                    referenceRoots.Add(reference, current);
                    break;
                }
                if (!referenceOrigins.TryGetValue(current.LocalId, out SafeCoreOwnershipPlace? next) || next is null) break;
                if (current.Projections.Count != 0)
                    foreach (SafeCoreOwnershipProjection projection in current.Projections)
                        next = next.Append(projection);
                current = next;
            }
            if (!referenceRoots.ContainsKey(reference))
            {
                AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                    "MIR reference provenance must terminate at an in-scope owner local.", function.Locals[reference].Source), options);
                return null;
            }
        }

        var blocks = new List<SafeCoreOwnershipBlock>(function.Blocks.Count);
        int statements = 0;
        for (int blockIndex = 0; blockIndex < function.Blocks.Count; blockIndex++)
        {
            Step(options, clock, ref operations);
            SafeCoreMirBlock block = function.Blocks[blockIndex];
            if (block.Statements.Count > options.MaximumInstructionsPerBlock ||
                (long)statements + block.Statements.Count > options.MaximumStatementsPerFunction)
            {
                AddDiagnostic(diagnostics, MakeDiagnostic(LimitReached,
                    "Typed MIR statements exceed the ownership adapter instruction limit.", block.Source), options);
                valid = false;
                continue;
            }

            statements += block.Statements.Count;
            var instructions = new List<SafeCoreOwnershipInstruction>(block.Statements.Count * 2);
            for (int statementIndex = 0; statementIndex < block.Statements.Count; statementIndex++)
            {
                Step(options, clock, ref operations);
                SafeCoreMirStatement statement = block.Statements[statementIndex];
                if (statement.DestinationLocalId < 0 || statement.DestinationLocalId >= function.Locals.Count ||
                    statement.Value is null || !Enum.IsDefined(statement.Value.Kind))
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                        "Typed MIR assignment evidence cannot be mapped to an ownership destination.", statement.Source), options);
                    valid = false;
                    continue;
                }

                SafeCoreMirRvalue value = statement.Value;
                bool referenceAssignment = value.Kind == SafeCoreMirRvalueKind.Use &&
                    value.Operands.Count == 1 &&
                    value.Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                    function.Locals[statement.DestinationLocalId].Type.Kind == SafeCoreSemanticTypeKind.Reference;
                bool nonCopyMove = !IsStructuralCopy(value.Type) &&
                    value.Kind == SafeCoreMirRvalueKind.Use && value.Operands.Count == 1 &&
                    value.Operands[0].Kind is SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place &&
                    function.Locals[statement.DestinationLocalId].Type == value.Type &&
                    !referenceAssignment;
                bool unsizingCoercion = value.Kind == SafeCoreMirRvalueKind.Coerce &&
                    value.Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                    value.Type.ElementType?.Kind == SafeCoreSemanticTypeKind.Slice;
                if (!IsStructuralCopy(value.Type) && !unsizingCoercion && !nonCopyMove && value.Kind != SafeCoreMirRvalueKind.Unary &&
                    value.Kind != SafeCoreMirRvalueKind.Write && !referenceAssignment &&
                    !(function.Locals[statement.DestinationLocalId].IsUnitAdt && value.Kind == SafeCoreMirRvalueKind.Use &&
                        value.Operands[0].Kind == SafeCoreMirOperandKind.Constant && value.Operands[0].Value == "()"))
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                        "Only structural Copy rvalues are supported by the typed-MIR ownership bridge.", value.Source), options);
                    valid = false;
                    continue;
                }

                // A non-Copy value read from a place is a Rust move.  Keep it
                // explicit in the ownership MIR so the source becomes
                // unavailable, the destination is initialized exactly once,
                // and partial aggregate moves retain their projection path.
                // Treating this as a generic Use followed by Assign would
                // silently turn ownership-bearing values into copies.
                if (nonCopyMove)
                {
                    if (!TryOwnershipPlace(value.Operands[0], function, options, clock, ref operations,
                        diagnostics, out SafeCoreOwnershipPlace sourcePlace))
                    {
                        valid = false;
                    }
                    else
                    {
                        AddInstruction(instructions,
                            SafeCoreOwnershipInstruction.Move(sourcePlace,
                                SafeCoreOwnershipPlace.Root(statement.DestinationLocalId), value.Source),
                            options, diagnostics, value.Source, ref valid);
                    }
                    continue;
                }

                // Borrow creation and dereference writes are explicit ownership
                // effects. They are handled before the generic Copy bridge so
                // reference values never get silently treated as structural
                // copies.
                if (value.Kind == SafeCoreMirRvalueKind.Unary && value.Operator is ("&" or "&mut" or "reborrow" or "reborrow_mut"))
                {
                    SafeCoreMirOperand owner = value.Operands[0];
                    if (!TryOwnershipPlace(owner, function, options, clock, ref operations, diagnostics,
                        out SafeCoreOwnershipPlace ownerPlace))
                    {
                        valid = false;
                    }
                    else AddInstruction(instructions,
                        SafeCoreOwnershipInstruction.Borrow(ownerPlace,
                            SafeCoreOwnershipPlace.Root(statement.DestinationLocalId),
                            value.Operator is "&mut" or "reborrow_mut", value.Source), options, diagnostics, value.Source, ref valid);
                    continue;
                }
                if (unsizingCoercion)
                {
                    if (!TryOwnershipPlace(value.Operands[0], function, options, clock, ref operations,
                        diagnostics, out SafeCoreOwnershipPlace ownerPlace))
                    {
                        valid = false;
                    }
                    else
                    {
                        AddInstruction(instructions,
                            SafeCoreOwnershipInstruction.Borrow(ownerPlace,
                                SafeCoreOwnershipPlace.Root(statement.DestinationLocalId),
                                value.Type.IsMutable, value.Source),
                            options, diagnostics, value.Source, ref valid);
                    }
                    continue;
                }
                if (value.Kind == SafeCoreMirRvalueKind.Write)
                {
                    SafeCoreMirOperand reference = value.Operands[0];
                    if (!TryOwnershipPlace(reference, function, options, clock, ref operations, diagnostics,
                            out SafeCoreOwnershipPlace referencePlace) ||
                        !referenceRoots.TryGetValue(reference.Id, out SafeCoreOwnershipPlace? rootPlace) ||
                        rootPlace is null ||
                        rootPlace.LocalId != statement.DestinationLocalId)
                    {
                        AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                            "A dereference write requires a reference local.", value.Source), options);
                        valid = false;
                    }
                    else
                    {
                        AddInstruction(instructions,
                            SafeCoreOwnershipInstruction.Write(referencePlace, value.Source), options, diagnostics, value.Source, ref valid);
                        SafeCoreMirOperand written = value.Operands[1];
                        if (written.Kind == SafeCoreMirOperandKind.Local)
                        {
                            if (written.Id < 0 || written.Id >= function.Locals.Count)
                            {
                                AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                                    "A dereference write value local is outside the function arena.", written.Source), options);
                                valid = false;
                            }
                            else
                            {
                                AddInstruction(instructions,
                                    SafeCoreOwnershipInstruction.Use(written.Id, written.Source), options, diagnostics, written.Source, ref valid);
                            }
                        }
                        else if (written.Kind == SafeCoreMirOperandKind.Place)
                        {
                            if (TryOwnershipPlace(written, function, options, clock, ref operations, diagnostics,
                                out SafeCoreOwnershipPlace writtenPlace))
                                AddInstruction(instructions,
                                    SafeCoreOwnershipInstruction.Use(writtenPlace, written.Source),
                                    options, diagnostics, written.Source, ref valid);
                            else valid = false;
                        }
                        else if (written.Kind != SafeCoreMirOperandKind.Constant ||
                                 (!IsStructuralCopy(written.Type) && written.Value != "()"))
                        {
                            AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                                "A dereference write value requires a Copy local, place, or constant.", written.Source), options);
                            valid = false;
                        }
                    }
                    continue;
                }

                // Assigning a reference has two distinct contracts. Shared
                // references are Copy and retain the source loan; mutable
                // references are Move and transfer their loan to the new
                // local. Keep this distinction explicit in the ownership MIR
                // so a later use of a moved `&mut` reports RSO1001.
                if (value.Kind == SafeCoreMirRvalueKind.Use && value.Operands.Count == 1 &&
                    (value.Operands[0].Kind is SafeCoreMirOperandKind.Local or SafeCoreMirOperandKind.Place) &&
                    function.Locals[statement.DestinationLocalId].Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                    value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Reference)
                {
                    SafeCoreMirOperand source = value.Operands[0];
                    if (!TryOwnershipPlace(source, function, options, clock, ref operations, diagnostics,
                        out SafeCoreOwnershipPlace sourcePlace))
                    {
                        valid = false;
                    }
                    else if (source.Type.IsMutable)
                    {
                        AddInstruction(instructions,
                            SafeCoreOwnershipInstruction.Move(sourcePlace,
                                SafeCoreOwnershipPlace.Root(statement.DestinationLocalId), value.Source),
                            options, diagnostics, value.Source, ref valid);
                    }
                    else
                    {
                        AddInstruction(instructions,
                            SafeCoreOwnershipInstruction.AssignReference(source.Id, statement.DestinationLocalId, value.Source),
                            options, diagnostics, value.Source, ref valid);
                    }
                    continue;
                }

                for (int operandIndex = 0; operandIndex < value.Operands.Count; operandIndex++)
                {
                    Step(options, clock, ref operations);
                    SafeCoreMirOperand operand = value.Operands[operandIndex];
                    switch (operand.Kind)
                    {
                        case SafeCoreMirOperandKind.Local:
                            if (operand.Id < 0 || operand.Id >= function.Locals.Count)
                            {
                                AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                                    "A typed MIR rvalue local operand is outside the function arena.", operand.Source), options);
                                valid = false;
                            }
                            else AddInstruction(instructions,
                                SafeCoreOwnershipInstruction.Use(operand.Id, operand.Source),
                                options, diagnostics, operand.Source, ref valid);
                            break;
                        case SafeCoreMirOperandKind.Place:
                            if (TryOwnershipPlace(operand, function, options, clock, ref operations, diagnostics,
                                out SafeCoreOwnershipPlace place))
                                AddInstruction(instructions,
                                    SafeCoreOwnershipInstruction.Use(place, operand.Source),
                                    options, diagnostics, operand.Source, ref valid);
                            else valid = false;
                            break;
                        case SafeCoreMirOperandKind.Constant:
                            if (!IsStructuralCopy(operand.Type) &&
                                !(function.Locals[statement.DestinationLocalId].IsUnitAdt && operand.Value == "()"))
                            {
                                AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                                    "A non-Copy constant has no ownership bridge representation.", operand.Source), options);
                                valid = false;
                            }
                            break;
                        case SafeCoreMirOperandKind.Function:
                        default:
                            AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                                "Function-valued and unknown rvalue operands require explicit ownership effects.", operand.Source), options);
                            valid = false;
                            break;
                    }
                }

                AddInstruction(instructions,
                    SafeCoreOwnershipInstruction.Assign(statement.DestinationLocalId, statement.Source),
                    options, diagnostics, statement.Source, ref valid);
            }

            SafeCoreOwnershipTerminator? terminator = AdaptTerminator(
                function, block, instructions, locals, options, clock, ref operations, diagnostics, ref valid);
            if (terminator is not null)
                blocks.Add(new SafeCoreOwnershipBlock(block.Id, 0, instructions, terminator, block.Source));
        }

        if (!valid || blocks.Count != function.Blocks.Count) return null;
        return new SafeCoreOwnershipFunction(
            function.Name,
            locals,
            [new SafeCoreOwnershipScope(0, -1, function.Source)],
            blocks,
            function.EntryBlockId,
            SafeCorePanicStrategy.Unwind,
            function.Source);
    }

    private static SafeCoreOwnershipTerminator? AdaptTerminator(
        SafeCoreMirFunction function,
        SafeCoreMirBlock block,
        List<SafeCoreOwnershipInstruction> instructions,
        List<SafeCoreOwnershipLocal> locals,
        SafeCoreMirOwnershipOptions options,
        Stopwatch clock,
        ref int operations,
        List<Diagnostic> diagnostics,
        ref bool valid)
    {
        SafeCoreMirTerminator? terminator = block.Terminator;
        if (terminator is null)
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                "A typed MIR block requires an explicit terminator before ownership adaptation.", block.Source), options);
            valid = false;
            return null;
        }

        Step(options, clock, ref operations);
        switch (terminator.Kind)
        {
            case SafeCoreMirTerminatorKind.Return:
                if (terminator.Operand is null) return SafeCoreOwnershipTerminator.ReturnUnit(terminator.Source);
                int? returnLocal = MaterializeTerminatorOperand(
                    terminator.Operand, instructions, locals, function, options, clock, ref operations, diagnostics);
                if (returnLocal is null)
                {
                    valid = false;
                    return null;
                }
                return SafeCoreOwnershipTerminator.Return(returnLocal.Value, terminator.Source);

            case SafeCoreMirTerminatorKind.Goto:
                if (!ValidTarget(terminator.TargetBlockId, function.Blocks.Count))
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                        "A typed MIR goto target is outside the function arena.", terminator.Source), options);
                    valid = false;
                    return null;
                }
                return SafeCoreOwnershipTerminator.Goto(terminator.TargetBlockId, terminator.Source);

            case SafeCoreMirTerminatorKind.Branch:
                int? condition = MaterializeTerminatorOperand(
                    terminator.Operand, instructions, locals, function, options, clock, ref operations, diagnostics);
                if (condition is null || !ValidTarget(terminator.TargetBlockId, function.Blocks.Count) ||
                    !ValidTarget(terminator.FalseTargetBlockId, function.Blocks.Count))
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                        "A typed MIR branch requires a boolean local/constant and two in-range targets.", terminator.Source), options);
                    valid = false;
                    return null;
                }
                return SafeCoreOwnershipTerminator.Branch(condition.Value, terminator.TargetBlockId,
                    terminator.FalseTargetBlockId, terminator.Source);

            case SafeCoreMirTerminatorKind.Call:
                if (terminator.Operand is null || terminator.Operand.Kind != SafeCoreMirOperandKind.Function ||
                    terminator.Operand.Type.Kind != SafeCoreSemanticTypeKind.Function)
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                        "Only direct typed-MIR scalar calls have a bounded ownership bridge.", terminator.Source), options);
                    valid = false;
                    return null;
                }
                if (terminator.DropLocalId is int droppedLocal)
                {
                    if (terminator.Arguments.Count != 0 || terminator.DestinationLocalId is not null ||
                        droppedLocal < 0 || droppedLocal >= function.Locals.Count ||
                        !function.Locals[droppedLocal].DestructorFunctionId.HasValue)
                    {
                        AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                            "A MIR destructor call must identify a Drop local with no arguments or destination.", terminator.Source), options);
                        valid = false;
                    }
                    else
                    {
                        AddInstruction(instructions,
                            SafeCoreOwnershipInstruction.Drop(droppedLocal, terminator.Source),
                            options, diagnostics, terminator.Source, ref valid);
                    }
                    if (!valid) return null;
                    return terminator.Operand.Type.ReturnType.Kind == SafeCoreSemanticTypeKind.Never
                        ? SafeCoreOwnershipTerminator.Unreachable(terminator.Source)
                        : ValidTarget(terminator.TargetBlockId, function.Blocks.Count)
                            ? SafeCoreOwnershipTerminator.Goto(terminator.TargetBlockId, terminator.Source)
                            : InvalidCallTarget(terminator.Source, diagnostics, options, ref valid);
                }
                SafeCoreType callType = terminator.Operand.Type;
                if (callType.ParameterTypes.Any(parameterType => !IsStructuralCopy(parameterType)) ||
                    (!IsStructuralCopy(callType.ReturnType) && callType.ReturnType.Kind != SafeCoreSemanticTypeKind.Never))
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                        "The typed-MIR ownership bridge only accepts direct calls with structural Copy parameters and structural Copy or diverging results.",
                        terminator.Operand.Source), options);
                    valid = false;
                    return null;
                }
                for (int argumentIndex = 0; argumentIndex < terminator.Arguments.Count; argumentIndex++)
                {
                    Step(options, clock, ref operations);
                    SafeCoreMirOperand argument = terminator.Arguments[argumentIndex];
                    if (argument.Kind == SafeCoreMirOperandKind.Local)
                    {
                        if (argument.Id < 0 || argument.Id >= function.Locals.Count)
                        {
                            AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                                "A typed MIR call local argument is outside the function arena.", argument.Source), options);
                            valid = false;
                        }
                        else if (!IsStructuralCopy(argument.Type))
                        {
                            AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                                "A non-Copy call local argument has no ownership bridge representation.", argument.Source), options);
                            valid = false;
                        }
                        else AddInstruction(instructions,
                            SafeCoreOwnershipInstruction.Use(argument.Id, argument.Source),
                            options, diagnostics, argument.Source, ref valid);
                    }
                    else if (argument.Kind == SafeCoreMirOperandKind.Constant && IsStructuralCopy(argument.Type))
                    {
                        // Constants do not consume or move an ownership local.
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                            "A call argument requires a scalar local or constant ownership fact.", argument.Source), options);
                        valid = false;
                    }
                }
                if (terminator.DestinationLocalId is int destination)
                {
                    if (destination < 0 || destination >= function.Locals.Count)
                    {
                        AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                            "A typed MIR call destination is outside the function arena.", terminator.Source), options);
                        valid = false;
                    }
                    else AddInstruction(instructions,
                        SafeCoreOwnershipInstruction.Assign(destination, terminator.Source),
                        options, diagnostics, terminator.Source, ref valid);
                }
                if (!valid) return null;
                return terminator.Operand.Type.ReturnType.Kind == SafeCoreSemanticTypeKind.Never
                    ? SafeCoreOwnershipTerminator.Unreachable(terminator.Source)
                    : ValidTarget(terminator.TargetBlockId, function.Blocks.Count)
                        ? SafeCoreOwnershipTerminator.Goto(terminator.TargetBlockId, terminator.Source)
                        : InvalidCallTarget(terminator.Source, diagnostics, options, ref valid);

            case SafeCoreMirTerminatorKind.Unreachable:
                return SafeCoreOwnershipTerminator.Unreachable(terminator.Source);

            default:
                AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                    "The typed MIR terminator kind has no ownership bridge.", terminator.Source), options);
                valid = false;
                return null;
        }
    }

    private static SafeCoreOwnershipTerminator? InvalidCallTarget(
        SafeCoreMirSource source,
        List<Diagnostic> diagnostics,
        SafeCoreMirOwnershipOptions options,
        ref bool valid)
    {
        AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
            "A non-diverging typed MIR call requires an in-range continuation target.", source), options);
        valid = false;
        return null;
    }

    private static int? MaterializeTerminatorOperand(
        SafeCoreMirOperand? operand,
        List<SafeCoreOwnershipInstruction> instructions,
        List<SafeCoreOwnershipLocal> locals,
        SafeCoreMirFunction function,
        SafeCoreMirOwnershipOptions options,
        Stopwatch clock,
        ref int operations,
        List<Diagnostic> diagnostics)
    {
        Step(options, clock, ref operations);
        if (operand is null)
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                "A typed MIR terminator requires an operand in this ownership profile.", function.Source), options);
            return null;
        }

        if (operand.Kind == SafeCoreMirOperandKind.Local)
        {
            if (operand.Id < 0 || operand.Id >= function.Locals.Count)
            {
                AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                    "A typed MIR terminator local operand is outside the function arena.", operand.Source), options);
                return null;
            }
            return operand.Id;
        }

        if (operand.Kind != SafeCoreMirOperandKind.Constant || !IsStructuralCopy(operand.Type))
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                "Only structural Copy locals and constants can be represented by an ownership terminator.", operand.Source), options);
            return null;
        }

        // Synthetic locals are part of the ownership arena too. Keep their
        // deterministic ID allocation inside the same per-function bound as
        // input locals; otherwise a block-heavy MIR can bypass the adapter
        // limit solely by returning or branching on constants.
        if (locals.Count >= options.MaximumLocalsPerFunction)
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(LimitReached,
                "Materializing a typed MIR constant would exceed the ownership adapter local limit.",
                operand.Source), options);
            return null;
        }

        if (instructions.Count >= options.MaximumInstructionsPerBlock)
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(LimitReached,
                "Materializing a typed MIR constant would exceed the ownership adapter instruction limit.",
                operand.Source), options);
            return null;
        }

        int localId = locals.Count;
        locals.Add(new SafeCoreOwnershipLocal(
            localId,
            "__mir_const_" + localId.ToString(CultureInfo.InvariantCulture),
            operand.Type,
            SafeCoreOwnershipKind.Copy,
            HasDrop: false,
            ScopeId: 0,
            IsReference: false,
            InitiallyInitialized: false,
            operand.Source));
        instructions.Add(SafeCoreOwnershipInstruction.Assign(localId, operand.Source));
        return localId;
    }

    private static bool AddInstruction(
        List<SafeCoreOwnershipInstruction> instructions,
        SafeCoreOwnershipInstruction instruction,
        SafeCoreMirOwnershipOptions options,
        List<Diagnostic> diagnostics,
        SafeCoreMirSource source,
        ref bool valid)
    {
        if (instructions.Count >= options.MaximumInstructionsPerBlock)
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(LimitReached,
                "Typed MIR ownership instructions exceed the per-block adapter limit.", source), options);
            valid = false;
            return false;
        }

        instructions.Add(instruction);
        return true;
    }

    private static bool IsStructuralCopy(SafeCoreType? type)
    {
        if (type is null) return false;
        return type.Kind switch
        {
            SafeCoreSemanticTypeKind.Unit or
            SafeCoreSemanticTypeKind.Bool or
            SafeCoreSemanticTypeKind.Char or
            SafeCoreSemanticTypeKind.I8 or SafeCoreSemanticTypeKind.I16 or
            SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.I64 or
            SafeCoreSemanticTypeKind.I128 or SafeCoreSemanticTypeKind.Isize or
            SafeCoreSemanticTypeKind.U8 or SafeCoreSemanticTypeKind.U16 or
            SafeCoreSemanticTypeKind.U32 or SafeCoreSemanticTypeKind.U64 or
            SafeCoreSemanticTypeKind.U128 or SafeCoreSemanticTypeKind.Usize or
            SafeCoreSemanticTypeKind.F32 or SafeCoreSemanticTypeKind.F64 => true,
            SafeCoreSemanticTypeKind.Tuple => type.Elements.Count <= 4_096 &&
                type.Elements.All(IsStructuralCopy),
            SafeCoreSemanticTypeKind.Array => type.Length is >= 0 and <= 65_536 &&
                IsStructuralCopy(type.ElementType),
            // References, closures, function items and ADTs require provenance
            // or explicit borrow/Drop facts. Treating them as Copy here would
            // make a successful adapter result claim more than MIR proves.
            _ => false,
        };
    }

    private static bool TryOwnershipPlace(
        SafeCoreMirOperand operand,
        SafeCoreMirFunction function,
        SafeCoreMirOwnershipOptions options,
        Stopwatch clock,
        ref int operations,
        List<Diagnostic> diagnostics,
        out SafeCoreOwnershipPlace place)
    {
        place = null!;
        Step(options, clock, ref operations);
        if (operand.Kind == SafeCoreMirOperandKind.Local)
        {
            if (operand.Id < 0 || operand.Id >= function.Locals.Count)
            {
                AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                    "A MIR local place is outside the function arena.", operand.Source), options);
                return false;
            }

            place = SafeCoreOwnershipPlace.Root(operand.Id);
            return true;
        }

        if (operand.Kind != SafeCoreMirOperandKind.Place || operand.Place is null ||
            operand.Place.LocalId != operand.Id || operand.Id < 0 || operand.Id >= function.Locals.Count ||
            operand.Place.Projections.Count > 128)
        {
            AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                "A MIR place operand must identify an in-range local and a bounded projection chain.", operand.Source), options);
            return false;
        }

        SafeCoreOwnershipPlace converted = SafeCoreOwnershipPlace.Root(operand.Place.LocalId);
        for (int index = 0; index < operand.Place.Projections.Count; index++)
        {
            Step(options, clock, ref operations);
            SafeCoreMirProjection projection = operand.Place.Projections[index];
            SafeCoreOwnershipProjection ownershipProjection;
            switch (projection.Kind)
            {
                case SafeCoreMirProjectionKind.Field when
                    !string.IsNullOrWhiteSpace(projection.Name) && projection.Name.Length <= 4_096 && projection.Index == -1:
                    ownershipProjection = SafeCoreOwnershipProjection.Field(projection.Name);
                    break;
                case SafeCoreMirProjectionKind.TupleIndex when
                    projection.Name is null && projection.Index >= 0:
                    ownershipProjection = SafeCoreOwnershipProjection.TupleIndex(projection.Index);
                    break;
                case SafeCoreMirProjectionKind.ArrayIndex when
                    projection.Name is null && projection.Index >= 0:
                    ownershipProjection = SafeCoreOwnershipProjection.ArrayIndex(projection.Index);
                    break;
                case SafeCoreMirProjectionKind.Dereference when
                    projection.Name is null && projection.Index == -1:
                    ownershipProjection = SafeCoreOwnershipProjection.Dereference();
                    break;
                default:
                    AddDiagnostic(diagnostics, MakeDiagnostic(InvalidEvidence,
                        "A MIR place contains invalid projection metadata.", operand.Source), options);
                    return false;
            }

            converted = converted.Append(ownershipProjection);
        }

        place = converted;
        return true;
    }

    private static bool ValidTarget(int target, int count) => target >= 0 && target < count;

    private static int AggregateValidationLimit(int perFunctionLimit, int functionCount, int hardLimit)
    {
        long aggregate = (long)perFunctionLimit * Math.Max(1, functionCount);
        return (int)Math.Min(hardLimit, aggregate);
    }

    private static void ValidateOptions(SafeCoreMirOwnershipOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 ||
            options.MaximumDiagnostics is < 1 or > 4_096 ||
            options.MaximumFunctions is < 1 or > 4_096 ||
            options.MaximumLocalsPerFunction is < 1 or > 4_096 ||
            options.MaximumBlocksPerFunction is < 1 or > 16_384 ||
            options.MaximumStatementsPerFunction is < 1 or > 65_536 ||
            options.MaximumInstructionsPerBlock is < 1 or > 65_536 ||
            options.MaximumPaths is < 1 or > 65_536 ||
            options.MaximumBlockVisits is < 1 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static void Step(SafeCoreMirOwnershipOptions options, Stopwatch clock, ref int operations)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (++operations > options.MaximumOperations || clock.Elapsed >= options.Timeout)
            throw new AdapterLimitException();
    }

    private static Diagnostic ToDiagnostic(SafeCoreMirDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.Message, diagnostic.Source?.Span ?? new TextSpan(0, 0))
        {
            SourcePath = diagnostic.Source?.SourcePath,
        };

    private static Diagnostic MakeDiagnostic(string code, string message, SafeCoreMirSource? source)
    {
        SafeCoreMirSource evidence = source ?? InvalidSource;
        return new Diagnostic(code, message, evidence.Span) { SourcePath = evidence.SourcePath };
    }

    private static void AddDiagnostic(List<Diagnostic> diagnostics, Diagnostic diagnostic,
        SafeCoreMirOwnershipOptions options)
    {
        if (diagnostics.Count >= options.MaximumDiagnostics) throw new AdapterLimitException();
        diagnostics.Add(diagnostic);
    }

    private static void AddLimitDiagnostic(List<Diagnostic> diagnostics, SafeCoreMirOwnershipOptions options)
    {
        if (diagnostics.Count < options.MaximumDiagnostics)
            diagnostics.Add(MakeDiagnostic(LimitReached,
                "Typed MIR ownership adaptation exceeded its bounded work or time limit.", InvalidSource));
    }

    private sealed class AdapterLimitException : Exception;
}
