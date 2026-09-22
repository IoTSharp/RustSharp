using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>The value semantics used by the bounded safe-core ownership pass.</summary>
public enum SafeCoreOwnershipKind
{
    Move,
    Copy,
}

/// <summary>How a panic edge disposes values that are still live.</summary>
public enum SafeCorePanicStrategy
{
    Unwind,
    Abort,
}

public enum SafeCoreOwnershipOutcome
{
    Returned,
    Unwound,
    Aborted,
    Unreachable,
    LimitExceeded,
}

public static class SafeCoreOwnershipDiagnosticCodes
{
    public const string InvalidInput = "RSO0001";
    public const string InvalidControlFlow = "RSO0002";
    public const string LimitReached = "RSO0003";
    public const string UseAfterMove = "RSO1001";
    public const string BorrowConflict = "RSO1002";
    public const string MoveWhileBorrowed = "RSO1003";
    public const string InvalidBorrow = "RSO1004";
    public const string Escape = "RSO1005";
    public const string InvalidDrop = "RSO1006";
    public const string ImmutableBorrow = "RSO1007";
}

/// <summary>A local tracked by the ownership pass. IDs index the owning function.</summary>
public sealed record SafeCoreOwnershipLocal(
    int Id,
    string Name,
    SafeCoreType Type,
    SafeCoreOwnershipKind Kind,
    bool HasDrop,
    int ScopeId,
    bool IsReference,
    bool InitiallyInitialized,
    SafeCoreMirSource Source);

public enum SafeCoreOwnershipInstructionKind
{
    Use,
    Assign,
    Move,
    Borrow,
    EndBorrow,
    Write,
    Drop,
    EnterScope,
    ExitScope,
}

/// <summary>One bounded ownership operation with original source evidence.</summary>
public sealed record SafeCoreOwnershipInstruction(
    SafeCoreOwnershipInstructionKind Kind,
    int LocalId,
    int RelatedLocalId,
    bool IsMutable,
    int ScopeId,
    SafeCoreMirSource Source)
{
    public static SafeCoreOwnershipInstruction Use(int localId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Use, localId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction Assign(int localId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Assign, localId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction Move(int sourceLocalId, int destinationLocalId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Move, sourceLocalId, destinationLocalId, false, -1, source);

    public static SafeCoreOwnershipInstruction Borrow(int ownerLocalId, int referenceLocalId, bool mutable, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Borrow, ownerLocalId, referenceLocalId, mutable, -1, source);

    public static SafeCoreOwnershipInstruction EndBorrow(int referenceLocalId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.EndBorrow, referenceLocalId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction Write(int referenceLocalId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Write, referenceLocalId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction Drop(int localId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Drop, localId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction EnterScope(int scopeId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.EnterScope, -1, -1, false, scopeId, source);

    public static SafeCoreOwnershipInstruction ExitScope(int scopeId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.ExitScope, -1, -1, false, scopeId, source);
}

public enum SafeCoreOwnershipTerminatorKind
{
    Goto,
    Branch,
    Return,
    Panic,
    Unreachable,
}

/// <summary>One explicit CFG terminator. A return value is a local ID or -1.</summary>
public sealed record SafeCoreOwnershipTerminator(
    SafeCoreOwnershipTerminatorKind Kind,
    int LocalId,
    int TargetBlockId,
    int FalseTargetBlockId,
    SafeCoreMirSource Source)
{
    public static SafeCoreOwnershipTerminator Goto(int targetBlockId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipTerminatorKind.Goto, -1, targetBlockId, -1, source);

    public static SafeCoreOwnershipTerminator Branch(int conditionLocalId, int trueTargetBlockId,
        int falseTargetBlockId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipTerminatorKind.Branch, conditionLocalId, trueTargetBlockId, falseTargetBlockId, source);

    public static SafeCoreOwnershipTerminator Return(int localId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipTerminatorKind.Return, localId, -1, -1, source);

    public static SafeCoreOwnershipTerminator ReturnUnit(SafeCoreMirSource source) => Return(-1, source);

    public static SafeCoreOwnershipTerminator Panic(SafeCoreMirSource source) =>
        new(SafeCoreOwnershipTerminatorKind.Panic, -1, -1, -1, source);

    public static SafeCoreOwnershipTerminator Unreachable(SafeCoreMirSource source) =>
        new(SafeCoreOwnershipTerminatorKind.Unreachable, -1, -1, -1, source);
}

public sealed class SafeCoreOwnershipScope
{
    public SafeCoreOwnershipScope(int id, int parentId, SafeCoreMirSource source)
    {
        Id = id;
        ParentId = parentId;
        Source = source;
    }

    public int Id { get; }
    public int ParentId { get; }
    public SafeCoreMirSource Source { get; }
}

public sealed class SafeCoreOwnershipBlock
{
    public SafeCoreOwnershipBlock(
        int id,
        int scopeId,
        IReadOnlyList<SafeCoreOwnershipInstruction> instructions,
        SafeCoreOwnershipTerminator terminator,
        SafeCoreMirSource source)
    {
        Id = id;
        ScopeId = scopeId;
        Instructions = Freeze(instructions, 65_536, nameof(instructions));
        Terminator = terminator ?? throw new ArgumentNullException(nameof(terminator));
        Source = source;
    }

    public int Id { get; }
    public int ScopeId { get; }
    public IReadOnlyList<SafeCoreOwnershipInstruction> Instructions { get; }
    public SafeCoreOwnershipTerminator Terminator { get; }
    public SafeCoreMirSource Source { get; }

    private static ReadOnlyCollection<T> Freeze<T>(IReadOnlyList<T> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum) throw new ArgumentException("Ownership collection exceeds its bound.", parameterName);
        var copy = new T[values.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = values[index] ?? throw new ArgumentException("Ownership collections cannot contain null values.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

public sealed class SafeCoreOwnershipFunction
{
    public SafeCoreOwnershipFunction(
        string name,
        IReadOnlyList<SafeCoreOwnershipLocal> locals,
        IReadOnlyList<SafeCoreOwnershipScope> scopes,
        IReadOnlyList<SafeCoreOwnershipBlock> blocks,
        int entryBlockId,
        SafeCorePanicStrategy panicStrategy,
        SafeCoreMirSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Locals = Freeze(locals, 4096, nameof(locals));
        Scopes = Freeze(scopes, 4096, nameof(scopes));
        Blocks = Freeze(blocks, 16_384, nameof(blocks));
        EntryBlockId = entryBlockId;
        PanicStrategy = panicStrategy;
        Source = source;
    }

    public string Name { get; }
    public IReadOnlyList<SafeCoreOwnershipLocal> Locals { get; }
    public IReadOnlyList<SafeCoreOwnershipScope> Scopes { get; }
    public IReadOnlyList<SafeCoreOwnershipBlock> Blocks { get; }
    public int EntryBlockId { get; }
    public SafeCorePanicStrategy PanicStrategy { get; }
    public SafeCoreMirSource Source { get; }

    private static ReadOnlyCollection<T> Freeze<T>(IReadOnlyList<T> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum) throw new ArgumentException("Ownership collection exceeds its bound.", parameterName);
        var copy = new T[values.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = values[index] ?? throw new ArgumentException("Ownership collections cannot contain null values.", parameterName);
        return Array.AsReadOnly(copy);
    }
}

public sealed class SafeCoreOwnershipProgram
{
    public SafeCoreOwnershipProgram(IReadOnlyList<SafeCoreOwnershipFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        if (functions.Count is < 1 or > 4096) throw new ArgumentException("Ownership program function count is out of bounds.", nameof(functions));
        var copy = new SafeCoreOwnershipFunction[functions.Count];
        for (int index = 0; index < copy.Length; index++) copy[index] = functions[index] ?? throw new ArgumentException("Function cannot be null.", nameof(functions));
        Functions = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<SafeCoreOwnershipFunction> Functions { get; }
}

public sealed record SafeCoreOwnershipDiagnostic(
    string Code,
    string Message,
    string FunctionName,
    int BlockId,
    int InstructionIndex,
    SafeCoreMirSource Source)
{
    public override string ToString() =>
        $"{Code} {FunctionName} bb{BlockId}:{InstructionIndex}: {Message}";
}

public sealed record SafeCoreOwnershipPath(
    int PathId,
    string FunctionName,
    SafeCoreOwnershipOutcome Outcome,
    ImmutableArray<string> DropOrder,
    ImmutableArray<string> Trace);

public sealed record SafeCoreOwnershipOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumPaths { get; init; } = 4096;
    public int MaximumBlockVisits { get; init; } = 128;
    public int MaximumDiagnostics { get; init; } = 256;
    public CancellationToken CancellationToken { get; init; }
}

public sealed record SafeCoreOwnershipAnalysisResult(
    ImmutableArray<SafeCoreOwnershipDiagnostic> Diagnostics,
    ImmutableArray<SafeCoreOwnershipPath> Paths,
    bool IsTruncated)
{
    public bool IsSuccessful => !IsTruncated && Diagnostics.IsEmpty && !Paths.IsEmpty &&
        Paths.All(path => path.Outcome is SafeCoreOwnershipOutcome.Returned or SafeCoreOwnershipOutcome.Unwound or SafeCoreOwnershipOutcome.Aborted or SafeCoreOwnershipOutcome.Unreachable);
}

/// <summary>
/// Bounded, branch-sensitive ownership analysis for the safe-core MIR boundary.
/// It is deliberately backend independent and records every cleanup path before
/// an IL emitter is allowed to consume ownership evidence.
/// </summary>
public static class SafeCoreOwnershipAnalysis
{
    public const string Profile = "safe-core-ownership-v1";
    private static readonly SafeCoreMirSource InvalidSource =
        new("<invalid>", new TextSpan(0, 0), 0, 0);

    public static SafeCoreOwnershipAnalysisResult Analyze(
        SafeCoreOwnershipProgram program,
        SafeCoreOwnershipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= new();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 || options.MaximumPaths is < 1 or > 65_536 ||
            options.MaximumBlockVisits is < 1 or > 4096 || options.MaximumDiagnostics is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options));

        var diagnostics = ImmutableArray.CreateBuilder<SafeCoreOwnershipDiagnostic>();
        var paths = ImmutableArray.CreateBuilder<SafeCoreOwnershipPath>();
        var diagnosticKeys = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        int operations = 0;
        bool truncated = false;
        int nextPathId = 0;

        void Step()
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (++operations > options.MaximumOperations || clock.Elapsed >= options.Timeout)
                throw new OwnershipLimitException();
        }

        void AddDiagnostic(string code, string message, string functionName, int blockId, int instructionIndex, SafeCoreMirSource source)
        {
            Step();
            source ??= InvalidSource;
            string key = string.Join('\u001f', code, functionName, blockId.ToString(CultureInfo.InvariantCulture),
                instructionIndex.ToString(CultureInfo.InvariantCulture), message);
            if (diagnosticKeys.Add(key))
            {
                if (diagnostics.Count >= options.MaximumDiagnostics) throw new OwnershipLimitException();
                diagnostics.Add(new(code, message, functionName, blockId, instructionIndex, source));
            }
        }

        try
        {
            foreach (SafeCoreOwnershipFunction function in program.Functions)
            {
                Step();
                ValidateShape(function, AddDiagnostic, Step);
                if (diagnostics.Any(diagnostic => diagnostic.FunctionName == function.Name &&
                    diagnostic.Code is SafeCoreOwnershipDiagnosticCodes.InvalidInput or SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow))
                    continue;
                AnalyzeFunction(function, paths, AddDiagnostic, Step, ref nextPathId, options);
            }
        }
        catch (OwnershipLimitException)
        {
            truncated = true;
            if (diagnostics.Count < options.MaximumDiagnostics)
            {
                SafeCoreMirSource source = program.Functions[0].Source;
                diagnostics.Add(new(SafeCoreOwnershipDiagnosticCodes.LimitReached,
                    "Ownership analysis exceeded its work, path, block-visit or time limit.",
                    program.Functions[0].Name, -1, -1, source));
            }
        }

        return new(diagnostics.ToImmutable(), paths.ToImmutable(), truncated);
    }

    private static void ValidateShape(
        SafeCoreOwnershipFunction function,
        Action<string, string, string, int, int, SafeCoreMirSource> add,
        Action step)
    {
        if (!IsValidSource(function.Source))
            add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership function source evidence is invalid.", function.Name, -1, -1, function.Source);

        var blockIds = new HashSet<int>();
        var localIds = new HashSet<int>();
        var scopeIds = new HashSet<int>();
        foreach (SafeCoreOwnershipLocal local in function.Locals)
        {
            step();
            if (!IsValidSource(local.Source))
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership local source evidence is invalid.", function.Name, -1, -1, local.Source);
            if (local.Id < 0 || local.Id != localIds.Count || !localIds.Add(local.Id) ||
                string.IsNullOrWhiteSpace(local.Name) || local.Type is null || !Enum.IsDefined(local.Kind))
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership local IDs and metadata must be dense and valid.", function.Name, -1, -1, local.Source);
        }

        if (!Enum.IsDefined(function.PanicStrategy))
            add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership panic strategy is outside the supported enum.", function.Name, -1, -1, function.Source);

        foreach (SafeCoreOwnershipScope scope in function.Scopes)
        {
            step();
            if (!IsValidSource(scope.Source))
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership scope source evidence is invalid.", function.Name, -1, -1, scope.Source);
            if (scope.Id < 0 || !scopeIds.Add(scope.Id) || scope.ParentId >= scope.Id || scope.ParentId < -1)
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership scopes must have unique IDs and an earlier parent.", function.Name, -1, -1, scope.Source);
        }

        foreach (SafeCoreOwnershipScope scope in function.Scopes)
        {
            step();
            if (scope.Id == 0 && scope.ParentId != -1)
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Root ownership scope must not have a parent.", function.Name, -1, -1, scope.Source);
            else if (scope.Id != 0 && scope.ParentId < 0)
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Non-root ownership scopes require a parent.", function.Name, -1, -1, scope.Source);
            else if (scope.ParentId >= 0 && !scopeIds.Contains(scope.ParentId))
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "An ownership scope parent is outside the scope arena.", function.Name, -1, -1, scope.Source);
        }

        if (!scopeIds.Contains(0))
            add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership functions require root scope 0.", function.Name, -1, -1, function.Source);

        foreach (SafeCoreOwnershipLocal local in function.Locals)
        {
            step();
            if (!scopeIds.Contains(local.ScopeId))
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "An ownership local references an unknown scope.", function.Name, -1, -1, local.Source);
        }

        if (function.EntryBlockId < 0 || function.EntryBlockId >= function.Blocks.Count)
            add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "Entry block is outside the block arena.", function.Name, -1, -1, function.Source);

        foreach (SafeCoreOwnershipBlock block in function.Blocks)
        {
            step();
            if (!IsValidSource(block.Source))
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership block source evidence is invalid.", function.Name, block.Id, -1, block.Source);
            if (block.Id < 0 || block.Id != blockIds.Count || !blockIds.Add(block.Id) || !scopeIds.Contains(block.ScopeId))
                add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "Block IDs and scope references must be dense and valid.", function.Name, block.Id, -1, block.Source);
            ValidateTerminator(function, block, add, step);
            for (int instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                step();
                SafeCoreOwnershipInstruction instruction = block.Instructions[instructionIndex];
                if (!IsValidSource(instruction.Source))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership instruction source evidence is invalid.", function.Name, block.Id, instructionIndex, instruction.Source);
                if (!Enum.IsDefined(instruction.Kind))
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidInput,
                        "Ownership instruction kind is outside the supported enum.",
                        function.Name, block.Id, instructionIndex, instruction.Source);
                    continue;
                }

                bool requiresLocal = instruction.Kind is
                    SafeCoreOwnershipInstructionKind.Use or
                    SafeCoreOwnershipInstructionKind.Assign or
                    SafeCoreOwnershipInstructionKind.Move or
                    SafeCoreOwnershipInstructionKind.Borrow or
                    SafeCoreOwnershipInstructionKind.EndBorrow or
                    SafeCoreOwnershipInstructionKind.Write or
                    SafeCoreOwnershipInstructionKind.Drop;
                bool requiresRelatedLocal = instruction.Kind is
                    SafeCoreOwnershipInstructionKind.Move or
                    SafeCoreOwnershipInstructionKind.Borrow;
                if ((requiresLocal && !IsValidLocalId(instruction.LocalId, function.Locals.Count)) ||
                    (requiresRelatedLocal && !IsValidLocalId(instruction.RelatedLocalId, function.Locals.Count)) ||
                    ((!requiresLocal && !requiresRelatedLocal) &&
                        (instruction.LocalId != -1 || instruction.RelatedLocalId != -1)))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership instruction references a local outside the arena.", function.Name, block.Id, instructionIndex, instruction.Source);
                if (instruction.Kind is SafeCoreOwnershipInstructionKind.EnterScope or SafeCoreOwnershipInstructionKind.ExitScope)
                {
                    if (!scopeIds.Contains(instruction.ScopeId))
                        add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership instruction references an unknown scope.", function.Name, block.Id, instructionIndex, instruction.Source);
                }
                else if (instruction.ScopeId != -1)
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Only scope instructions may carry a scope ID.", function.Name, block.Id, instructionIndex, instruction.Source);
                }
            }
        }
    }

    private static bool IsValidLocalId(int id, int count) => id >= 0 && id < count;

    private static bool IsValidSource(SafeCoreMirSource? source) =>
        source is not null && !string.IsNullOrWhiteSpace(source.SourcePath) && source.SourcePath.Length <= 4_096 &&
        source.HirNodeId >= 0 && source.SourceLength >= 0 && source.SourceLength <= 16 * 1024 * 1024 &&
        source.Span.Start >= 0 && source.Span.Length >= 0 &&
        (long)source.Span.Start + source.Span.Length <= source.SourceLength;

    private static void ValidateTerminator(
        SafeCoreOwnershipFunction function,
        SafeCoreOwnershipBlock block,
        Action<string, string, string, int, int, SafeCoreMirSource> add,
        Action step)
    {
        step();
        SafeCoreOwnershipTerminator terminator = block.Terminator;
        if (!IsValidSource(terminator.Source))
            add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership terminator source evidence is invalid.", function.Name, block.Id, -1, terminator.Source);
        bool validTarget(int target) => target >= 0 && target < function.Blocks.Count;
        bool sentinel(int value) => value == -1;

        switch (terminator.Kind)
        {
            case SafeCoreOwnershipTerminatorKind.Goto:
                if (!validTarget(terminator.TargetBlockId) || !sentinel(terminator.FalseTargetBlockId) || !sentinel(terminator.LocalId))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "A goto terminator must contain one valid target and no local or false target.", function.Name, block.Id, -1, terminator.Source);
                break;
            case SafeCoreOwnershipTerminatorKind.Branch:
                if (!IsValidLocalId(terminator.LocalId, function.Locals.Count))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "A branch condition references a local outside the arena.", function.Name, block.Id, -1, terminator.Source);
                if (!validTarget(terminator.TargetBlockId) || !validTarget(terminator.FalseTargetBlockId))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "A branch terminator must contain two valid targets.", function.Name, block.Id, -1, terminator.Source);
                break;
            case SafeCoreOwnershipTerminatorKind.Return:
                if (terminator.LocalId != -1 && !IsValidLocalId(terminator.LocalId, function.Locals.Count))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "A return value references a local outside the arena.", function.Name, block.Id, -1, terminator.Source);
                if (!sentinel(terminator.TargetBlockId) || !sentinel(terminator.FalseTargetBlockId))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "A return terminator cannot contain control-flow targets.", function.Name, block.Id, -1, terminator.Source);
                break;
            case SafeCoreOwnershipTerminatorKind.Panic:
            case SafeCoreOwnershipTerminatorKind.Unreachable:
                if (!sentinel(terminator.LocalId) || !sentinel(terminator.TargetBlockId) || !sentinel(terminator.FalseTargetBlockId))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "A terminal terminator cannot contain locals or targets.", function.Name, block.Id, -1, terminator.Source);
                break;
            default:
                add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "Unsupported ownership terminator.", function.Name, block.Id, -1, terminator.Source);
                break;
        }
    }

    private static void AnalyzeFunction(
        SafeCoreOwnershipFunction function,
        ImmutableArray<SafeCoreOwnershipPath>.Builder paths,
        Action<string, string, string, int, int, SafeCoreMirSource> add,
        Action step,
        ref int nextPathId,
        SafeCoreOwnershipOptions options)
    {
        var locals = new Dictionary<int, SafeCoreOwnershipLocal>();
        foreach (SafeCoreOwnershipLocal local in function.Locals) locals.Add(local.Id, local);
        var scopes = function.Scopes.ToDictionary(scope => scope.Id);
        var blocks = function.Blocks.ToDictionary(block => block.Id);
        var work = new Stack<WorkItem>();
        work.Push(new WorkItem(function.EntryBlockId, new PathState(function), 0));
        while (work.Count > 0)
        {
            step();
            if (paths.Count >= options.MaximumPaths) throw new OwnershipLimitException();
            WorkItem item = work.Pop();
            if (!blocks.TryGetValue(item.BlockId, out SafeCoreOwnershipBlock? block))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "A work item targets an unknown block.", function.Name, item.BlockId, -1, function.Source);
                continue;
            }

            PathState state = item.State;
            state.Visit(item.BlockId, options.MaximumBlockVisits, step);
            SafeCoreMirSource escapeSource = block.Source;
            void ReportEscape(int ownerId, int referenceId)
            {
                string ownerName = locals.TryGetValue(ownerId, out SafeCoreOwnershipLocal? owner) ? owner.Name : ownerId.ToString(CultureInfo.InvariantCulture);
                string referenceName = locals.TryGetValue(referenceId, out SafeCoreOwnershipLocal? reference) ? reference.Name : referenceId.ToString(CultureInfo.InvariantCulture);
                add(SafeCoreOwnershipDiagnosticCodes.Escape,
                    $"Reference '{referenceName}' outlives owner '{ownerName}' when its scope exits.",
                    function.Name, block.Id, -1, escapeSource);
            }

            state.EnterBlock(block.ScopeId, scopes, locals, step, ReportEscape);
            for (int index = 0; index < block.Instructions.Count; index++)
            {
                step();
                SafeCoreOwnershipInstruction instruction = block.Instructions[index];
                escapeSource = instruction.Source;
                if (!Apply(instruction, index, block, function, state, locals, scopes, add, step, ReportEscape)) break;
            }

            SafeCoreOwnershipTerminator terminator = block.Terminator;
            escapeSource = terminator.Source;
            switch (terminator.Kind)
            {
                case SafeCoreOwnershipTerminatorKind.Goto:
                    work.Push(new WorkItem(terminator.TargetBlockId, state, item.Depth + 1));
                    break;
                case SafeCoreOwnershipTerminatorKind.Branch:
                    if (!state.RequireBool(terminator.LocalId, locals, add, function, block, -1, terminator.Source)) break;
                    PathState falseState = state.Clone();
                    PathState trueState = state;
                    falseState.Trace.Add($"branch {terminator.FalseTargetBlockId}");
                    trueState.Trace.Add($"branch {terminator.TargetBlockId}");
                    work.Push(new WorkItem(terminator.FalseTargetBlockId, falseState, item.Depth + 1));
                    work.Push(new WorkItem(terminator.TargetBlockId, trueState, item.Depth + 1));
                    break;
                case SafeCoreOwnershipTerminatorKind.Return:
                    if (terminator.LocalId >= 0 && !state.Use(terminator.LocalId, locals, add, function, block, -1, terminator.Source)) break;
                    if (terminator.LocalId >= 0 && locals[terminator.LocalId].IsReference && state.IsBorrowActive(terminator.LocalId))
                        add(SafeCoreOwnershipDiagnosticCodes.Escape, "A borrowed reference cannot escape through a return.", function.Name, block.Id, -1, terminator.Source);
                    int? transferredLocalId = terminator.LocalId >= 0 &&
                        locals[terminator.LocalId].Kind == SafeCoreOwnershipKind.Move
                        ? terminator.LocalId
                        : null;
                    state.CleanupAll(locals, scopes, step, includeValues: true, reportEscape: ReportEscape,
                        transferredLocalId: transferredLocalId);
                    state.Trace.Add("return");
                    paths.Add(state.ToPath(nextPathId++, function.Name, SafeCoreOwnershipOutcome.Returned));
                    break;
                case SafeCoreOwnershipTerminatorKind.Panic:
                    if (function.PanicStrategy == SafeCorePanicStrategy.Unwind)
                    {
                        state.CleanupAll(locals, scopes, step, includeValues: true, reportEscape: ReportEscape);
                        state.Trace.Add("panic_unwind");
                        paths.Add(state.ToPath(nextPathId++, function.Name, SafeCoreOwnershipOutcome.Unwound));
                    }
                    else
                    {
                        state.Trace.Add("panic_abort");
                        paths.Add(state.ToPath(nextPathId++, function.Name, SafeCoreOwnershipOutcome.Aborted));
                    }
                    break;
                case SafeCoreOwnershipTerminatorKind.Unreachable:
                    state.Trace.Add("unreachable");
                    paths.Add(state.ToPath(nextPathId++, function.Name, SafeCoreOwnershipOutcome.Unreachable));
                    break;
                default:
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow, "Unsupported ownership terminator.", function.Name, block.Id, -1, terminator.Source);
                    break;
            }
        }
    }

    private static bool Apply(
        SafeCoreOwnershipInstruction instruction,
        int index,
        SafeCoreOwnershipBlock block,
        SafeCoreOwnershipFunction function,
        PathState state,
        IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
        IReadOnlyDictionary<int, SafeCoreOwnershipScope> scopes,
        Action<string, string, string, int, int, SafeCoreMirSource> add,
        Action step,
        Action<int, int> reportEscape)
    {
        switch (instruction.Kind)
        {
            case SafeCoreOwnershipInstructionKind.Use:
                return state.Use(instruction.LocalId, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Assign:
                return state.Assign(instruction.LocalId, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Move:
                return state.Move(instruction.LocalId, instruction.RelatedLocalId, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Borrow:
                return state.Borrow(instruction.LocalId, instruction.RelatedLocalId, instruction.IsMutable, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.EndBorrow:
                return state.EndBorrow(instruction.LocalId, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Write:
                return state.Write(instruction.LocalId, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Drop:
                return state.Drop(instruction.LocalId, locals, add, function, block, index, instruction.Source, step);
            case SafeCoreOwnershipInstructionKind.EnterScope:
                state.EnterBlock(instruction.ScopeId, scopes, locals, step, reportEscape);
                return true;
            case SafeCoreOwnershipInstructionKind.ExitScope:
                state.ExitScope(instruction.ScopeId, scopes, locals, step, reportEscape);
                return true;
            default:
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Unsupported ownership instruction.", function.Name, block.Id, index, instruction.Source);
                return false;
        }
    }

    private sealed class WorkItem(int blockId, PathState state, int depth)
    {
        public int BlockId { get; } = blockId;
        public PathState State { get; } = state;
        public int Depth { get; } = depth;
    }

    private sealed class PathState
    {
        private readonly Dictionary<int, ValueState> values = [];
        private readonly Dictionary<int, BorrowState> borrows = [];
        private readonly HashSet<int> activeScopes = [];
        private readonly Dictionary<int, int> visits = [];
        public List<string> DropOrder { get; } = [];
        public List<string> Trace { get; } = [];

        public PathState(SafeCoreOwnershipFunction function)
        {
            foreach (SafeCoreOwnershipLocal local in function.Locals)
                values[local.Id] = new(
                    local.ScopeId == 0 && local.InitiallyInitialized,
                    local.ScopeId == 0,
                    false,
                    local.HasDrop,
                    null);
            activeScopes.Add(0);
        }

        private PathState(PathState other)
        {
            foreach ((int id, ValueState value) in other.values) values[id] = value with { };
            foreach ((int id, BorrowState borrow) in other.borrows) borrows[id] = borrow with { };
            foreach (int scope in other.activeScopes) activeScopes.Add(scope);
            foreach ((int block, int count) in other.visits) visits[block] = count;
            DropOrder.AddRange(other.DropOrder);
            Trace.AddRange(other.Trace);
        }

        public PathState Clone() => new(this);

        public void Visit(int blockId, int maximum, Action step)
        {
            step();
            visits.TryGetValue(blockId, out int count);
            if (++count > maximum) throw new OwnershipLimitException();
            visits[blockId] = count;
        }

        public void EnterBlock(int targetScope, IReadOnlyDictionary<int, SafeCoreOwnershipScope> scopes,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals, Action step, Action<int, int> reportEscape)
        {
            step();
            if (targetScope < 0) return;

            List<int> targetPath = BuildScopePath(targetScope, scopes, step);
            if (targetPath.Count == 0) return;
            var targetSet = new HashSet<int>(targetPath);

            // A CFG edge can move from a nested scope to a sibling or an
            // ancestor.  Exit every active scope that is not on the target's
            // ancestor chain before activating the missing chain entries.
            var leaving = activeScopes
                .Where(scope => !targetSet.Contains(scope))
                .Select(scope => (Scope: scope, Depth: ScopeDepth(scope, scopes, step)))
                .OrderByDescending(item => item.Depth)
                .ThenByDescending(item => item.Scope)
                .Select(item => item.Scope)
                .ToArray();
            foreach (int scope in leaving)
                ExitScope(scope, scopes, locals, step, reportEscape);

            for (int index = 0; index < targetPath.Count; index++)
            {
                step();
                int scope = targetPath[index];
                if (activeScopes.Add(scope)) ActivateScope(scope, locals);
            }
        }

        public void ExitScope(int scopeId, IReadOnlyDictionary<int, SafeCoreOwnershipScope> scopes,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals, Action step, Action<int, int> reportEscape,
            int? transferredLocalId = null)
        {
            step();
            if (!activeScopes.Contains(scopeId)) return;

            // Explicitly exiting an ancestor also exits any active descendants
            // first, preserving lexical Drop order for arbitrary CFG edges.
            var descendants = activeScopes
                .Where(scope => scope != scopeId && IsDescendant(scope, scopeId, scopes, step))
                .Select(scope => (Scope: scope, Depth: ScopeDepth(scope, scopes, step)))
                .OrderByDescending(item => item.Depth)
                .ThenByDescending(item => item.Scope)
                .Select(item => item.Scope)
                .ToArray();
            foreach (int descendant in descendants)
                ExitScope(descendant, scopes, locals, step, reportEscape, transferredLocalId);

            if (!activeScopes.Contains(scopeId)) return;

            // Borrows end before values are dropped.  If an owner leaves while
            // its reference lives in an outer scope, preserve the evidence as
            // an escape diagnostic and poison the dangling reference state.
            foreach ((int borrowId, BorrowState borrow) in borrows
                         .Where(pair => pair.Value.Active)
                         .ToArray())
            {
                if (!locals.TryGetValue(borrowId, out SafeCoreOwnershipLocal? reference) ||
                    !locals.TryGetValue(borrow.OwnerId, out SafeCoreOwnershipLocal? owner))
                    continue;

                bool ownerLeaves = owner.ScopeId == scopeId;
                bool referenceLeaves = reference.ScopeId == scopeId;
                if (ownerLeaves && !referenceLeaves && !borrow.EscapeReported)
                {
                    reportEscape(borrow.OwnerId, borrowId);
                    borrows[borrowId] = borrow with { EscapeReported = true };
                }

                if (ownerLeaves || referenceLeaves)
                    EndBorrowAtScopeExit(borrowId);
            }

            foreach (SafeCoreOwnershipLocal local in locals.Values.OrderByDescending(local => local.Id))
            {
                step();
                if (local.ScopeId != scopeId) continue;
                if (local.Id == transferredLocalId)
                {
                    if (values.TryGetValue(local.Id, out ValueState? transferred))
                    {
                        values[local.Id] = transferred with
                        {
                            Available = false,
                            Initialized = false,
                            Moved = true,
                            Dropped = true,
                            DropOwned = false,
                            MovedTo = null,
                        };
                    }

                    Trace.Add($"return_move {local.Name}");
                    continue;
                }

                DropValue(local, step);
            }

            foreach (SafeCoreOwnershipLocal local in locals.Values)
            {
                if (local.ScopeId != scopeId || !values.TryGetValue(local.Id, out ValueState? value)) continue;
                values[local.Id] = value with { Available = false, Initialized = false, Dropped = true };
            }
            activeScopes.Remove(scopeId);
            Trace.Add($"scope_exit {scopeId.ToString(CultureInfo.InvariantCulture)}");
        }

        public void CleanupAll(IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            IReadOnlyDictionary<int, SafeCoreOwnershipScope> scopes, Action step, bool includeValues,
            Action<int, int> reportEscape, int? transferredLocalId = null)
        {
            var active = activeScopes
                .Select(scope => (Scope: scope, Depth: ScopeDepth(scope, scopes, step)))
                .OrderByDescending(item => item.Depth)
                .ThenByDescending(item => item.Scope)
                .Select(item => item.Scope)
                .ToArray();
            foreach (int scope in active) ExitScope(scope, scopes, locals, step, reportEscape, transferredLocalId);
            if (!includeValues) return;
            // ExitScope owns cleanup for active scopes.  Deliberately do not
            // sweep every local here: a CFG path may never have entered a
            // non-root scope, and such locals must not be dropped on that path.
        }

        private static List<int> BuildScopePath(int targetScope,
            IReadOnlyDictionary<int, SafeCoreOwnershipScope> scopes, Action step)
        {
            var reverse = new List<int>();
            var seen = new HashSet<int>();
            int cursor = targetScope;
            while (cursor >= 0)
            {
                step();
                if (!seen.Add(cursor) || !scopes.TryGetValue(cursor, out SafeCoreOwnershipScope? scope))
                    return [];
                reverse.Add(cursor);
                cursor = scope.ParentId;
                if (reverse.Count > 4096) throw new OwnershipLimitException();
            }

            reverse.Reverse();
            return reverse;
        }

        private static int ScopeDepth(int scopeId, IReadOnlyDictionary<int, SafeCoreOwnershipScope> scopes, Action step)
        {
            int depth = 0;
            var seen = new HashSet<int>();
            int cursor = scopeId;
            while (cursor >= 0)
            {
                step();
                if (!seen.Add(cursor) || !scopes.TryGetValue(cursor, out SafeCoreOwnershipScope? scope))
                    return int.MaxValue;
                depth++;
                cursor = scope.ParentId;
                if (depth > 4096) throw new OwnershipLimitException();
            }

            return depth;
        }

        private static bool IsDescendant(int candidate, int ancestor,
            IReadOnlyDictionary<int, SafeCoreOwnershipScope> scopes, Action step)
        {
            int cursor = candidate;
            var seen = new HashSet<int>();
            while (cursor >= 0)
            {
                step();
                if (!seen.Add(cursor)) return false;
                if (cursor == ancestor) return true;
                if (!scopes.TryGetValue(cursor, out SafeCoreOwnershipScope? scope)) return false;
                cursor = scope.ParentId;
                if (seen.Count > 4096) throw new OwnershipLimitException();
            }

            return false;
        }

        public bool Use(int id, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!EnsureLocalScope(id, locals, add, function, block, index, source) ||
                !values.TryGetValue(id, out ValueState? value))
            {
                if (!values.ContainsKey(id))
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership local is outside the value arena.", function.Name, block.Id, index, source);
                return false;
            }

            if (!value.Available || !value.Initialized || value.Moved || value.Dropped)
            {
                add(SafeCoreOwnershipDiagnosticCodes.UseAfterMove, "A local is uninitialized, moved, or already dropped.", function.Name, block.Id, index, source);
                return false;
            }

            if (borrows.TryGetValue(id, out BorrowState? borrow) && borrow.Active)
            {
                if (borrow.Suspended || HasActiveChild(id))
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A reference is suspended by an active reborrow.", function.Name, block.Id, index, source);
                    return false;
                }
                Trace.Add($"use_borrow {locals[id].Name}");
            }
            else
            {
                if (HasActiveBorrow(id, mutableOnly: true))
                {
                    add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, "Cannot read an owner while a mutable borrow is active.", function.Name, block.Id, index, source);
                    return false;
                }

                Trace.Add($"use {locals[id].Name}");
            }
            return true;
        }

        public bool RequireBool(int id, Dictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!Use(id, locals, add, function, block, index, source)) return false;
            if (!locals.TryGetValue(id, out SafeCoreOwnershipLocal? local) || local.Type.Kind != SafeCoreSemanticTypeKind.Bool)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "A branch condition must have bool type.", function.Name, block.Id, index, source);
                return false;
            }

            return true;
        }

        public bool Assign(int id, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!EnsureLocalScope(id, locals, add, function, block, index, source) ||
                !values.TryGetValue(id, out ValueState? value) ||
                !locals.TryGetValue(id, out SafeCoreOwnershipLocal? local))
                return false;
            if (borrows.TryGetValue(id, out BorrowState? ownBorrow) && ownBorrow.Active)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "Assignment cannot overwrite an active reference.", function.Name, block.Id, index, source);
                return false;
            }
            if (HasActiveBorrow(id, mutableOnly: false))
            {
                add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, "Cannot assign to an owner while a borrow is active.", function.Name, block.Id, index, source);
                return false;
            }
            if (!value.Available)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Assignment targets a local whose scope is not active.", function.Name, block.Id, index, source);
                return false;
            }
            if (value.Initialized && !value.Moved && !value.Dropped && value.DropOwned)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidDrop, "Assignment would overwrite a live value that still requires Drop.", function.Name, block.Id, index, source);
                return false;
            }

            values[id] = value with { Initialized = true, Moved = false, Dropped = false, DropOwned = local.HasDrop, MovedTo = null };
            Trace.Add($"assign {local.Name}");
            return true;
        }

        public bool Move(int sourceId, int destinationId, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!Use(sourceId, locals, add, function, block, index, source) ||
                !EnsureLocalScope(destinationId, locals, add, function, block, index, source) ||
                !values.TryGetValue(destinationId, out ValueState? destination) ||
                !locals.TryGetValue(sourceId, out SafeCoreOwnershipLocal? sourceLocal) ||
                !locals.TryGetValue(destinationId, out SafeCoreOwnershipLocal? destinationLocal))
                return false;
            if (borrows.TryGetValue(sourceId, out BorrowState? sourceBorrow) && sourceBorrow.Active)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A borrowed reference cannot be moved while active.", function.Name, block.Id, index, source);
                return false;
            }
            if (borrows.TryGetValue(destinationId, out BorrowState? destinationBorrow) && destinationBorrow.Active)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A move destination cannot overwrite an active reference.", function.Name, block.Id, index, source);
                return false;
            }
            if (borrows.Values.Any(borrow => borrow.Active && borrow.OwnerId == sourceId))
            {
                add(SafeCoreOwnershipDiagnosticCodes.MoveWhileBorrowed, "A value cannot move while it is borrowed.", function.Name, block.Id, index, source);
                return false;
            }

            if (destination.Initialized && !destination.Moved && !destination.Dropped)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidDrop, "A move destination is already initialized.", function.Name, block.Id, index, source);
                return false;
            }

            if (sourceLocal.Kind == SafeCoreOwnershipKind.Move)
                values[sourceId] = values[sourceId] with { Initialized = false, Moved = true, MovedTo = destinationId };
            values[destinationId] = destination with { Initialized = true, Moved = false, Dropped = false, DropOwned = values[sourceId].DropOwned || sourceLocal.HasDrop, MovedTo = null };
            Trace.Add($"move {sourceLocal.Name} -> {destinationLocal.Name}");
            return true;
        }

        public bool Borrow(int ownerId, int referenceId, bool mutable, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!EnsureLocalScope(ownerId, locals, add, function, block, index, source) ||
                !EnsureLocalScope(referenceId, locals, add, function, block, index, source) ||
                !values.TryGetValue(referenceId, out ValueState? referenceValue) ||
                !locals.TryGetValue(ownerId, out SafeCoreOwnershipLocal? owner) ||
                !locals.TryGetValue(referenceId, out SafeCoreOwnershipLocal? reference))
                return false;
            if (ownerId == referenceId)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A borrow destination must differ from its owner.", function.Name, block.Id, index, source);
                return false;
            }
            if (!Use(ownerId, locals, add, function, block, index, source)) return false;
            if (!reference.IsReference)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A borrow destination must be a reference local.", function.Name, block.Id, index, source);
                return false;
            }

            if (referenceValue.Initialized && !referenceValue.Moved && !referenceValue.Dropped)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A borrow destination is already initialized.", function.Name, block.Id, index, source);
                return false;
            }

            int? parentReferenceId = null;
            if (owner.IsReference && borrows.TryGetValue(ownerId, out BorrowState? parentBorrow) && parentBorrow.Active)
            {
                parentReferenceId = ownerId;
                if (mutable && !parentBorrow.Mutable)
                {
                    add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, "A mutable reborrow requires a mutable parent reference.", function.Name, block.Id, index, source);
                    return false;
                }
                if (parentBorrow.Suspended || HasActiveChild(ownerId))
                {
                    add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, "A reference cannot be reborrowed while another reborrow is active.", function.Name, block.Id, index, source);
                    return false;
                }
            }
            else if (owner.IsReference && mutable && owner.Type.Kind == SafeCoreSemanticTypeKind.Reference && !owner.Type.IsMutable)
            {
                add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, "A shared reference cannot be mutably reborrowed.", function.Name, block.Id, index, source);
                return false;
            }

            foreach (BorrowState active in borrows.Values.Where(borrow => borrow.Active && borrow.OwnerId == ownerId))
            {
                if (mutable || active.Mutable)
                {
                    add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict,
                        mutable ? "A mutable borrow requires exclusive access." : "A shared borrow conflicts with a mutable borrow.",
                        function.Name, block.Id, index, source);
                    return false;
                }
            }

            borrows[referenceId] = new(ownerId, mutable, true, parentReferenceId, false, false);
            if (parentReferenceId is int parentId && borrows.TryGetValue(parentId, out BorrowState? parent))
                borrows[parentId] = parent with { Suspended = true };
            values[referenceId] = referenceValue with { Initialized = true, Moved = false, Dropped = false, DropOwned = false };
            Trace.Add(mutable ? $"borrow_mut {owner.Name} as {reference.Name}" : $"borrow {owner.Name} as {reference.Name}");
            return true;
        }

        public bool EndBorrow(int id, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!EnsureLocalScope(id, locals, add, function, block, index, source) ||
                !borrows.TryGetValue(id, out BorrowState? borrow) || !borrow.Active)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "The reference is not active.", function.Name, block.Id, index, source);
                return false;
            }

            if (HasActiveChild(id))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A parent reference cannot end while a reborrow is active.", function.Name, block.Id, index, source);
                return false;
            }

            EndBorrowInternal(id);
            if (values.TryGetValue(id, out ValueState? value))
                values[id] = value with { Initialized = false, Dropped = true };
            Trace.Add($"end_borrow {locals[id].Name}");
            return true;
        }

        public bool Write(int id, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!EnsureLocalScope(id, locals, add, function, block, index, source) ||
                !borrows.TryGetValue(id, out BorrowState? borrow) || !borrow.Active)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A write requires an active reference.", function.Name, block.Id, index, source);
                return false;
            }

            if (borrow.Suspended || HasActiveChild(id))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "Cannot use a reference while a reborrow is active.", function.Name, block.Id, index, source);
                return false;
            }

            if (!borrow.Mutable)
            {
                add(SafeCoreOwnershipDiagnosticCodes.ImmutableBorrow, "Cannot write through a shared borrow.", function.Name, block.Id, index, source);
                return false;
            }

            Trace.Add($"write {locals[id].Name}");
            return true;
        }

        public bool Drop(int id, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source, Action step)
        {
            if (!Use(id, locals, add, function, block, index, source)) return false;
            if (borrows.TryGetValue(id, out BorrowState? ownBorrow) && ownBorrow.Active)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "Cannot drop an active reference.", function.Name, block.Id, index, source);
                return false;
            }
            if (borrows.Values.Any(borrow => borrow.Active && borrow.OwnerId == id))
            {
                add(SafeCoreOwnershipDiagnosticCodes.MoveWhileBorrowed, "Cannot drop a value while a borrow is active.", function.Name, block.Id, index, source);
                return false;
            }

            if (!locals.TryGetValue(id, out SafeCoreOwnershipLocal? local)) return false;
            DropValue(local, step);
            return true;
        }

        private void DropValue(SafeCoreOwnershipLocal local, Action step)
        {
            if (!values.TryGetValue(local.Id, out ValueState? value) || !value.Initialized || value.Moved || value.Dropped)
                return;
            step();
            values[local.Id] = value with { Initialized = false, Dropped = true };
            if (value.DropOwned || local.HasDrop)
            {
                DropOrder.Add(local.Name);
                Trace.Add($"drop {local.Name}");
            }
        }

        private bool EnsureLocalScope(int id, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add, SafeCoreOwnershipFunction function,
            SafeCoreOwnershipBlock block, int index, SafeCoreMirSource source)
        {
            if (!locals.TryGetValue(id, out SafeCoreOwnershipLocal? local))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership operation references an unknown local.", function.Name, block.Id, index, source);
                return false;
            }

            if (!activeScopes.Contains(local.ScopeId))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidInput, "Ownership operation uses a local outside its active scope.", function.Name, block.Id, index, source);
                return false;
            }

            return true;
        }

        private void ActivateScope(int scopeId, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals)
        {
            foreach (SafeCoreOwnershipLocal local in locals.Values)
            {
                if (local.ScopeId != scopeId || !values.TryGetValue(local.Id, out ValueState? value)) continue;
                values[local.Id] = value with
                {
                    Available = true,
                    Initialized = local.InitiallyInitialized,
                    Moved = false,
                    Dropped = false,
                    DropOwned = local.HasDrop,
                    MovedTo = null,
                };
            }
        }

        private bool HasActiveBorrow(int ownerId, bool mutableOnly)
        {
            foreach (BorrowState borrow in borrows.Values)
            {
                if (borrow.Active && borrow.OwnerId == ownerId && (!mutableOnly || borrow.Mutable))
                    return true;
            }

            return false;
        }

        private bool HasActiveChild(int parentReferenceId) =>
            borrows.Values.Any(borrow => borrow.Active && borrow.ParentReferenceId == parentReferenceId);

        private void EndBorrowInternal(int id)
        {
            if (!borrows.TryGetValue(id, out BorrowState? borrow) || !borrow.Active) return;
            borrows[id] = borrow with { Active = false, Suspended = false };
            ResumeParent(borrow.ParentReferenceId);
        }

        private void EndBorrowAtScopeExit(int id)
        {
            if (!borrows.TryGetValue(id, out BorrowState? borrow) || !borrow.Active) return;
            borrows[id] = borrow with { Active = false, Suspended = false };
            if (values.TryGetValue(id, out ValueState? value))
                values[id] = value with { Initialized = false, Dropped = true };
            Trace.Add($"end_borrow {id.ToString(CultureInfo.InvariantCulture)} (scope)");
            ResumeParent(borrow.ParentReferenceId);
        }

        private void ResumeParent(int? parentReferenceId)
        {
            if (parentReferenceId is not int parentId || !borrows.TryGetValue(parentId, out BorrowState? parent) || !parent.Active)
                return;
            borrows[parentId] = parent with { Suspended = HasActiveChild(parentId) };
        }

        public bool IsBorrowActive(int id) => borrows.TryGetValue(id, out BorrowState? borrow) && borrow.Active;

        public SafeCoreOwnershipPath ToPath(int id, string functionName, SafeCoreOwnershipOutcome outcome) =>
            new(id, functionName, outcome, DropOrder.ToImmutableArray(), Trace.ToImmutableArray());

        private sealed record ValueState(bool Initialized, bool Available, bool Moved, bool Dropped, bool DropOwned, int? MovedTo)
        {
            public ValueState(bool initialized, bool available, bool moved, bool dropOwned, int? movedTo)
                : this(initialized, available, moved, false, dropOwned, movedTo) { }
        }

        private sealed record BorrowState(
            int OwnerId,
            bool Mutable,
            bool Active,
            int? ParentReferenceId,
            bool Suspended,
            bool EscapeReported);
    }

    private sealed class OwnershipLimitException : Exception;
}
