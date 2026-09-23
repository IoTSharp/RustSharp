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
        bool valid = true;
        for (int index = 0; index < function.Locals.Count; index++)
        {
            Step(options, clock, ref operations);
            SafeCoreMirLocal local = function.Locals[index];
            if (!IsStructuralCopy(local.Type))
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
                SafeCoreOwnershipKind.Copy,
                HasDrop: false,
                ScopeId: 0,
                IsReference: false,
                InitiallyInitialized: local.Kind == SafeCoreMirLocalKind.Parameter,
                local.Source));
        }

        if (!valid) return null;

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
                if (!IsStructuralCopy(value.Type))
                {
                    AddDiagnostic(diagnostics, MakeDiagnostic(Unsupported,
                        "Only structural Copy rvalues are supported by the typed-MIR ownership bridge.", value.Source), options);
                    valid = false;
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
                        case SafeCoreMirOperandKind.Constant:
                            if (!IsStructuralCopy(operand.Type))
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
