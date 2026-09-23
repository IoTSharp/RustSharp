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
    public const string InvalidMovePath = "RSO1008";
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

/// <summary>A bounded projection used to identify a move/borrow path.</summary>
public enum SafeCoreOwnershipProjectionKind
{
    Field,
    TupleIndex,
    ArrayIndex,
    Dereference,
}

public sealed record SafeCoreOwnershipProjection(
    SafeCoreOwnershipProjectionKind Kind,
    string? Name,
    int Index)
{
    public static SafeCoreOwnershipProjection Field(string name) =>
        new(SafeCoreOwnershipProjectionKind.Field, name, -1);

    public static SafeCoreOwnershipProjection TupleIndex(int index) =>
        new(SafeCoreOwnershipProjectionKind.TupleIndex, null, index);

    public static SafeCoreOwnershipProjection ArrayIndex(int index) =>
        new(SafeCoreOwnershipProjectionKind.ArrayIndex, null, index);

    public static SafeCoreOwnershipProjection Dereference() =>
        new(SafeCoreOwnershipProjectionKind.Dereference, null, -1);
}

/// <summary>
/// A local plus a finite projection chain.  Root places preserve the original
/// local-only ownership behavior; projected places let the pass model partial
/// moves without pretending that an aggregate's entire value was consumed.
/// </summary>
public sealed class SafeCoreOwnershipPlace
{
    public SafeCoreOwnershipPlace(int localId, IReadOnlyList<SafeCoreOwnershipProjection>? projections = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(localId, nameof(localId));
        projections ??= [];
        if (projections.Count > 128) throw new ArgumentException("Ownership projection depth exceeds its bound.", nameof(projections));
        var copy = new SafeCoreOwnershipProjection[projections.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            SafeCoreOwnershipProjection projection = projections[index] ??
                throw new ArgumentException("Ownership projections cannot contain null values.", nameof(projections));
            if (!Enum.IsDefined(projection.Kind) ||
                (projection.Kind == SafeCoreOwnershipProjectionKind.Field &&
                    (string.IsNullOrWhiteSpace(projection.Name) || projection.Name.Length > 4_096 || projection.Index != -1)) ||
                (projection.Kind is SafeCoreOwnershipProjectionKind.TupleIndex or SafeCoreOwnershipProjectionKind.ArrayIndex &&
                    (projection.Name is not null || projection.Index < 0)) ||
                (projection.Kind is SafeCoreOwnershipProjectionKind.Dereference &&
                    (projection.Name is not null || projection.Index != -1)))
                throw new ArgumentException("Ownership projection metadata is invalid.", nameof(projections));
            copy[index] = projection;
        }

        LocalId = localId;
        Projections = Array.AsReadOnly(copy);
    }

    public int LocalId { get; }
    public IReadOnlyList<SafeCoreOwnershipProjection> Projections { get; }
    public bool IsRoot => Projections.Count == 0;

    public static SafeCoreOwnershipPlace Root(int localId) => new(localId);

    public SafeCoreOwnershipPlace Append(SafeCoreOwnershipProjection projection) =>
        new(LocalId, [.. Projections, projection]);

    public override string ToString()
    {
        var builder = new System.Text.StringBuilder(LocalId.ToString(CultureInfo.InvariantCulture));
        foreach (SafeCoreOwnershipProjection projection in Projections)
        {
            switch (projection.Kind)
            {
                case SafeCoreOwnershipProjectionKind.Field:
                    builder.Append('.').Append(projection.Name);
                    break;
                case SafeCoreOwnershipProjectionKind.TupleIndex:
                    builder.Append(".tuple[").Append(projection.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case SafeCoreOwnershipProjectionKind.ArrayIndex:
                    builder.Append('[').Append(projection.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case SafeCoreOwnershipProjectionKind.Dereference:
                    builder.Append('.').Append('*');
                    break;
            }
        }

        return builder.ToString();
    }
}

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
    /// <summary>Optional projected place; null means the root local in LocalId.</summary>
    public SafeCoreOwnershipPlace? Place { get; init; }

    /// <summary>Optional projected destination/related place for Move/Borrow.</summary>
    public SafeCoreOwnershipPlace? RelatedPlace { get; init; }

    public static SafeCoreOwnershipInstruction Use(int localId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Use, localId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction Use(SafeCoreOwnershipPlace place, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Use, place.LocalId, -1, false, -1, source) { Place = place };

    public static SafeCoreOwnershipInstruction Assign(int localId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Assign, localId, -1, false, -1, source);

    /// <summary>Assign a copied shared reference while retaining its active loan.</summary>
    public static SafeCoreOwnershipInstruction AssignReference(int sourceLocalId, int destinationLocalId,
        SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Assign, destinationLocalId, sourceLocalId, false, -1, source);

    public static SafeCoreOwnershipInstruction Assign(SafeCoreOwnershipPlace place, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Assign, place.LocalId, -1, false, -1, source) { Place = place };

    public static SafeCoreOwnershipInstruction Move(int sourceLocalId, int destinationLocalId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Move, sourceLocalId, destinationLocalId, false, -1, source);

    public static SafeCoreOwnershipInstruction Move(SafeCoreOwnershipPlace sourcePlace,
        SafeCoreOwnershipPlace destinationPlace, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Move, sourcePlace.LocalId, destinationPlace.LocalId, false, -1, source)
        {
            Place = sourcePlace,
            RelatedPlace = destinationPlace,
        };

    public static SafeCoreOwnershipInstruction Borrow(int ownerLocalId, int referenceLocalId, bool mutable, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Borrow, ownerLocalId, referenceLocalId, mutable, -1, source);

    public static SafeCoreOwnershipInstruction Borrow(SafeCoreOwnershipPlace ownerPlace,
        SafeCoreOwnershipPlace referencePlace, bool mutable, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Borrow, ownerPlace.LocalId, referencePlace.LocalId, mutable, -1, source)
        {
            Place = ownerPlace,
            RelatedPlace = referencePlace,
        };

    public static SafeCoreOwnershipInstruction EndBorrow(int referenceLocalId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.EndBorrow, referenceLocalId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction EndBorrow(SafeCoreOwnershipPlace referencePlace, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.EndBorrow, referencePlace.LocalId, -1, false, -1, source)
        {
            Place = referencePlace,
        };

    public static SafeCoreOwnershipInstruction Write(int referenceLocalId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Write, referenceLocalId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction Write(SafeCoreOwnershipPlace referencePlace, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Write, referencePlace.LocalId, -1, false, -1, source)
        {
            Place = referencePlace,
        };

    public static SafeCoreOwnershipInstruction Drop(int localId, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Drop, localId, -1, false, -1, source);

    public static SafeCoreOwnershipInstruction Drop(SafeCoreOwnershipPlace place, SafeCoreMirSource source) =>
        new(SafeCoreOwnershipInstructionKind.Drop, place.LocalId, -1, false, -1, source) { Place = place };

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
    /// <summary>Infer borrow end points from bounded CFG liveness.</summary>
    public bool InferNonLexicalLifetimes { get; init; }
    /// <summary>Short alias for callers that use the conventional NLL name.</summary>
    public bool InferNll { get; init; }
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
            var functionNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (SafeCoreOwnershipFunction function in program.Functions)
            {
                Step();
                if (!functionNames.Add(function.Name))
                {
                    AddDiagnostic(SafeCoreOwnershipDiagnosticCodes.InvalidInput,
                        "Ownership function names must be unique.", function.Name, -1, -1, function.Source);
                    continue;
                }

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
                ValidatePlace(instruction.Place, instruction.LocalId, function, block, instructionIndex, add);
                ValidatePlace(instruction.RelatedPlace, instruction.RelatedLocalId, function, block, instructionIndex, add);
                if (instruction.Kind is not (SafeCoreOwnershipInstructionKind.Move or SafeCoreOwnershipInstructionKind.Borrow) &&
                    instruction.RelatedPlace is not null)
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidMovePath,
                        "Only move and borrow instructions may carry a related projected place.",
                        function.Name, block.Id, instructionIndex, instruction.Source);
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

    private static void ValidatePlace(
        SafeCoreOwnershipPlace? place,
        int localId,
        SafeCoreOwnershipFunction function,
        SafeCoreOwnershipBlock block,
        int instructionIndex,
        Action<string, string, string, int, int, SafeCoreMirSource> add)
    {
        if (place is null) return;
        if (place.LocalId != localId || !IsValidLocalId(place.LocalId, function.Locals.Count))
        {
            add(SafeCoreOwnershipDiagnosticCodes.InvalidMovePath,
                "A projected place must identify the instruction's local arena slot.",
                function.Name, block.Id, instructionIndex, block.Source);
            return;
        }

        for (int index = 0; index < place.Projections.Count; index++)
        {
            SafeCoreOwnershipProjection? projection = place.Projections[index];
            if (projection is null || !Enum.IsDefined(projection.Kind) ||
                (projection.Kind == SafeCoreOwnershipProjectionKind.Field &&
                    (string.IsNullOrWhiteSpace(projection.Name) || projection.Name.Length > 4_096 || projection.Index != -1)) ||
                (projection.Kind is SafeCoreOwnershipProjectionKind.TupleIndex or SafeCoreOwnershipProjectionKind.ArrayIndex &&
                    (projection.Name is not null || projection.Index < 0)) ||
                (projection.Kind == SafeCoreOwnershipProjectionKind.Dereference &&
                    (projection.Name is not null || projection.Index != -1)))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidMovePath,
                    "A projected place contains invalid projection metadata.",
                    function.Name, block.Id, instructionIndex, block.Source);
            }
        }
    }

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
        NllLiveness? nll = options.InferNonLexicalLifetimes || options.InferNll
            ? BuildNllLiveness(function, locals, step)
            : null;
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
                nll?.EndDeadBorrows(state, block.Id, index, locals, step);
            }

            SafeCoreOwnershipTerminator terminator = block.Terminator;
            nll?.EndDeadBorrowsAtBlockExit(state, block.Id, block.Terminator, locals, step);
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

    private static NllLiveness BuildNllLiveness(
        SafeCoreOwnershipFunction function,
        IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
        Action step)
    {
        var blocks = function.Blocks.ToDictionary(block => block.Id);
        var successors = new Dictionary<int, int[]>(blocks.Count);
        var blockUses = new Dictionary<int, HashSet<int>>(blocks.Count);
        foreach (SafeCoreOwnershipBlock block in function.Blocks)
        {
            step();
            successors[block.Id] = Successors(block.Terminator, blocks.Count);
            var uses = new HashSet<int>();
            foreach (SafeCoreOwnershipInstruction instruction in block.Instructions)
            {
                step();
                AddReferencedLocals(instruction, uses, locals);
            }
            AddReferencedLocals(block.Terminator, uses, locals);
            blockUses[block.Id] = uses;
        }

        var liveIn = blocks.Keys.ToDictionary(id => id, static _ => new HashSet<int>());
        var liveOut = blocks.Keys.ToDictionary(id => id, static _ => new HashSet<int>());
        var predecessors = blocks.Keys.ToDictionary(id => id, static _ => new List<int>());
        foreach ((int blockId, int[] targets) in successors)
        {
            foreach (int target in targets)
            {
                step();
                if (predecessors.TryGetValue(target, out List<int>? incoming))
                    incoming.Add(blockId);
            }
        }

        // Liveness is a finite monotone data-flow problem. A worklist avoids a
        // fragile fixed round count on long CFG chains while the shared step
        // budget still bounds every queue operation and union.
        var queue = new Queue<int>(function.Blocks.Select(static block => block.Id).Reverse());
        var queued = new HashSet<int>(queue);
        while (queue.Count != 0)
        {
            step();
            int blockId = queue.Dequeue();
            queued.Remove(blockId);
            SafeCoreOwnershipBlock block = blocks[blockId];
            var nextOut = new HashSet<int>();
            foreach (int successor in successors[blockId])
            {
                step();
                if (liveIn.TryGetValue(successor, out HashSet<int>? successorLive))
                    nextOut.UnionWith(successorLive);
            }
            var nextIn = new HashSet<int>(blockUses[blockId]);
            nextIn.UnionWith(nextOut);
            if (liveOut[blockId].SetEquals(nextOut) && liveIn[blockId].SetEquals(nextIn))
                continue;
            liveOut[blockId] = nextOut;
            liveIn[blockId] = nextIn;
            foreach (int predecessor in predecessors[blockId].OrderByDescending(static value => value))
            {
                step();
                if (queued.Add(predecessor)) queue.Enqueue(predecessor);
            }
        }

        var afterInstruction = new Dictionary<int, IReadOnlyList<IReadOnlySet<int>>>(blocks.Count);
        foreach (SafeCoreOwnershipBlock block in function.Blocks)
        {
            step();
            var future = new HashSet<int>(liveOut[block.Id]);
            AddReferencedLocals(block.Terminator, future, locals);
            var snapshots = new HashSet<int>[block.Instructions.Count];
            for (int index = block.Instructions.Count - 1; index >= 0; index--)
            {
                step();
                snapshots[index] = new HashSet<int>(future);
                AddReferencedLocals(block.Instructions[index], future, locals);
            }
            afterInstruction[block.Id] = snapshots;
        }

        return new(afterInstruction, liveOut);

        static int[] Successors(SafeCoreOwnershipTerminator terminator, int blockCount) =>
            terminator.Kind switch
            {
                SafeCoreOwnershipTerminatorKind.Goto when terminator.TargetBlockId >= 0 && terminator.TargetBlockId < blockCount
                    => [terminator.TargetBlockId],
                SafeCoreOwnershipTerminatorKind.Branch =>
                    [.. new[] { terminator.TargetBlockId, terminator.FalseTargetBlockId }
                        .Where(target => target >= 0 && target < blockCount).Distinct()],
                _ => [],
            };
    }

    private static void AddReferencedLocals(
        SafeCoreOwnershipInstruction instruction,
        HashSet<int> references,
        IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals)
    {
        if (instruction.Kind is SafeCoreOwnershipInstructionKind.Use or
            SafeCoreOwnershipInstructionKind.Assign or
            SafeCoreOwnershipInstructionKind.Move or
            SafeCoreOwnershipInstructionKind.Borrow or
            SafeCoreOwnershipInstructionKind.EndBorrow or
            SafeCoreOwnershipInstructionKind.Write or
            SafeCoreOwnershipInstructionKind.Drop)
        {
            if (locals.TryGetValue(instruction.LocalId, out SafeCoreOwnershipLocal? local) && local.IsReference)
                references.Add(instruction.LocalId);
            if (locals.TryGetValue(instruction.RelatedLocalId, out SafeCoreOwnershipLocal? related) && related.IsReference)
                references.Add(instruction.RelatedLocalId);
        }
    }

    private static void AddReferencedLocals(
        SafeCoreOwnershipTerminator terminator,
        HashSet<int> references,
        IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals)
    {
        if (locals.TryGetValue(terminator.LocalId, out SafeCoreOwnershipLocal? local) && local.IsReference)
            references.Add(terminator.LocalId);
    }

    private sealed class NllLiveness(
        IReadOnlyDictionary<int, IReadOnlyList<IReadOnlySet<int>>> afterInstruction,
        IReadOnlyDictionary<int, HashSet<int>> liveOut)
    {
        private readonly IReadOnlyDictionary<int, IReadOnlyList<IReadOnlySet<int>>> afterInstruction = afterInstruction;
        private readonly IReadOnlyDictionary<int, HashSet<int>> liveOut = liveOut;

        public void EndDeadBorrows(
            PathState state,
            int blockId,
            int instructionIndex,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action step)
        {
            if (!afterInstruction.TryGetValue(blockId, out IReadOnlyList<IReadOnlySet<int>>? snapshots) ||
                instructionIndex < 0 || instructionIndex >= snapshots.Count) return;
            state.EndDeadBorrows(snapshots[instructionIndex], locals, step);
        }

        public void EndDeadBorrowsAtBlockExit(
            PathState state,
            int blockId,
            SafeCoreOwnershipTerminator terminator,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action step)
        {
            if (!liveOut.TryGetValue(blockId, out HashSet<int>? live)) return;
            var liveThroughTerminator = new HashSet<int>(live);
            AddReferencedLocals(terminator, liveThroughTerminator, locals);
            state.EndDeadBorrows(liveThroughTerminator, locals, step);
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
                return instruction.Place is null || instruction.Place.IsRoot
                    ? state.Use(instruction.LocalId, locals, add, function, block, index, instruction.Source)
                    : state.UsePlace(instruction.Place, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Assign:
                if (instruction.RelatedLocalId >= 0 && instruction.Place is null)
                    return state.CopyReference(instruction.RelatedLocalId, instruction.LocalId, locals, add, function, block, index, instruction.Source);
                return instruction.Place is null || instruction.Place.IsRoot
                    ? state.Assign(instruction.LocalId, locals, add, function, block, index, instruction.Source)
                    : state.AssignPlace(instruction.Place, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Move:
                return instruction.Place is null && instruction.RelatedPlace is null
                    ? state.Move(instruction.LocalId, instruction.RelatedLocalId, locals, add, function, block, index, instruction.Source)
                    : state.MovePlace(instruction.Place ?? SafeCoreOwnershipPlace.Root(instruction.LocalId),
                        instruction.RelatedPlace ?? SafeCoreOwnershipPlace.Root(instruction.RelatedLocalId),
                        locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Borrow:
                return instruction.Place is null && instruction.RelatedPlace is null
                    ? state.Borrow(instruction.LocalId, instruction.RelatedLocalId, instruction.IsMutable, locals, add, function, block, index, instruction.Source)
                    : state.BorrowPlace(instruction.Place ?? SafeCoreOwnershipPlace.Root(instruction.LocalId),
                        instruction.RelatedPlace ?? SafeCoreOwnershipPlace.Root(instruction.RelatedLocalId),
                        instruction.IsMutable, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.EndBorrow:
                return instruction.Place is null || instruction.Place.IsRoot
                    ? state.EndBorrow(instruction.LocalId, locals, add, function, block, index, instruction.Source)
                    : state.EndBorrowPlace(instruction.Place, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Write:
                return instruction.Place is null || instruction.Place.IsRoot
                    ? state.Write(instruction.LocalId, locals, add, function, block, index, instruction.Source)
                    : state.WritePlace(instruction.Place, locals, add, function, block, index, instruction.Source);
            case SafeCoreOwnershipInstructionKind.Drop:
                return instruction.Place is null || instruction.Place.IsRoot
                    ? state.Drop(instruction.LocalId, locals, add, function, block, index, instruction.Source, step)
                    : state.DropPlace(instruction.Place, locals, add, function, block, index, instruction.Source, step);
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
        private readonly Dictionary<string, ValueState> projectedValues = new(StringComparer.Ordinal);
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
            foreach ((string key, ValueState value) in other.projectedValues) projectedValues[key] = value with { };
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

        public bool UsePlace(SafeCoreOwnershipPlace place,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source)
        {
            if (place.IsRoot)
                return Use(place.LocalId, locals, add, function, block, index, source);
            if (!EnsureLocalScope(place.LocalId, locals, add, function, block, index, source)) return false;
            if (!values.TryGetValue(place.LocalId, out ValueState? root) ||
                !root.Available || !root.Initialized || root.Moved || root.Dropped ||
                HasMovedProjection(place))
            {
                add(SafeCoreOwnershipDiagnosticCodes.UseAfterMove,
                    $"The projected place '{DisplayPlace(place, locals)}' is uninitialized, moved, or already dropped.",
                    function.Name, block.Id, index, source);
                return false;
            }

            if (HasActiveBorrow(place.LocalId, mutableOnly: true))
            {
                add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict,
                    "Cannot read a place while a mutable borrow is active.", function.Name, block.Id, index, source);
                return false;
            }

            ValueState value = ReadPlace(place, root);
            if (!value.Available || !value.Initialized || value.Moved || value.Dropped)
            {
                add(SafeCoreOwnershipDiagnosticCodes.UseAfterMove,
                    $"The projected place '{DisplayPlace(place, locals)}' is uninitialized, moved, or already dropped.",
                    function.Name, block.Id, index, source);
                return false;
            }

            Trace.Add($"use {DisplayPlace(place, locals)}");
            return true;
        }

        public bool AssignPlace(SafeCoreOwnershipPlace place,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source)
        {
            if (place.IsRoot)
                return Assign(place.LocalId, locals, add, function, block, index, source);
            if (!EnsureLocalScope(place.LocalId, locals, add, function, block, index, source) ||
                !values.TryGetValue(place.LocalId, out ValueState? root)) return false;
            if (!root.Available || !root.Initialized || root.Moved || root.Dropped)
            {
                add(SafeCoreOwnershipDiagnosticCodes.UseAfterMove,
                    $"The projected place '{DisplayPlace(place, locals)}' has no live aggregate owner.",
                    function.Name, block.Id, index, source);
                return false;
            }
            if (HasActiveBorrow(place.LocalId, mutableOnly: false))
            {
                add(SafeCoreOwnershipDiagnosticCodes.BorrowConflict,
                    "Cannot assign to a place while its owner is borrowed.", function.Name, block.Id, index, source);
                return false;
            }

            ValueState current = ReadPlace(place, root);
            if (current.Initialized && !current.Moved && !current.Dropped)
            {
                // Assignment to a projected field is a replacement operation;
                // it is legal only when that field is Copy or has no pending
                // destructor.  We conservatively require the latter because
                // the typed-MIR evidence does not carry field Drop metadata.
                if (current.DropOwned)
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidDrop,
                        $"Assignment would overwrite live projected place '{DisplayPlace(place, locals)}'.",
                        function.Name, block.Id, index, source);
                    return false;
                }
            }

            WritePlace(place, current with
            {
                Available = true,
                Initialized = true,
                Moved = false,
                Dropped = false,
                DropOwned = false,
                MovedTo = null,
            });
            Trace.Add($"assign {DisplayPlace(place, locals)}");
            return true;
        }

        public bool MovePlace(SafeCoreOwnershipPlace sourcePlace, SafeCoreOwnershipPlace destinationPlace,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source)
        {
            if (sourcePlace.IsRoot && destinationPlace.IsRoot)
                return Move(sourcePlace.LocalId, destinationPlace.LocalId, locals, add, function, block, index, source);
            if (PlacesOverlap(sourcePlace, destinationPlace))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidMovePath,
                    "A move source and destination must not overlap the same ownership path.",
                    function.Name, block.Id, index, source);
                return false;
            }
            if (!sourcePlace.IsRoot &&
                locals.TryGetValue(sourcePlace.LocalId, out SafeCoreOwnershipLocal? sourceLocal) &&
                values.TryGetValue(sourcePlace.LocalId, out ValueState? sourceRoot) &&
                (sourceLocal.HasDrop || sourceRoot.DropOwned))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidMovePath,
                    "A projected value cannot move out of an aggregate that requires Drop.",
                    function.Name, block.Id, index, source);
                return false;
            }
            if (!UsePlace(sourcePlace, locals, add, function, block, index, source) ||
                !EnsureLocalScope(destinationPlace.LocalId, locals, add, function, block, index, source) ||
                !values.TryGetValue(destinationPlace.LocalId, out ValueState? destinationRoot)) return false;
            if (HasMovedProjection(destinationPlace))
            {
                add(SafeCoreOwnershipDiagnosticCodes.UseAfterMove,
                    $"The projected destination '{DisplayPlace(destinationPlace, locals)}' is unavailable after a partial move.",
                    function.Name, block.Id, index, source);
                return false;
            }
            if (HasActiveBorrow(sourcePlace.LocalId, mutableOnly: false))
            {
                add(SafeCoreOwnershipDiagnosticCodes.MoveWhileBorrowed,
                    "A projected value cannot move while its owner is borrowed.", function.Name, block.Id, index, source);
                return false;
            }

            ValueState destination = ReadPlace(destinationPlace, destinationRoot);
            if (destination.Initialized && !destination.Moved && !destination.Dropped)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidDrop,
                    $"A move destination '{DisplayPlace(destinationPlace, locals)}' is already initialized.",
                    function.Name, block.Id, index, source);
                return false;
            }

            ValueState moved = ReadPlace(sourcePlace, values[sourcePlace.LocalId]);
            WritePlace(sourcePlace, moved with { Initialized = false, Moved = true, MovedTo = destinationPlace.LocalId });
            WritePlace(destinationPlace, destination with
            {
                Available = true,
                Initialized = true,
                Moved = false,
                Dropped = false,
                DropOwned = false,
                MovedTo = null,
            });
            Trace.Add($"move {DisplayPlace(sourcePlace, locals)} -> {DisplayPlace(destinationPlace, locals)}");
            return true;
        }

        public bool BorrowPlace(SafeCoreOwnershipPlace ownerPlace, SafeCoreOwnershipPlace referencePlace,
            bool mutable, IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source)
        {
            // Borrow state is still tracked at reference-local granularity. A
            // projected owner is checked against its root and a projected
            // reference is represented by the reference local, preserving the
            // conservative aliasing rule without inventing field layouts.
            if (!ownerPlace.IsRoot || !referencePlace.IsRoot)
            {
                if (!UsePlace(ownerPlace, locals, add, function, block, index, source)) return false;
                if (!locals.TryGetValue(referencePlace.LocalId, out SafeCoreOwnershipLocal? reference) || !reference.IsReference)
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow,
                        "A projected borrow destination must be a reference local.", function.Name, block.Id, index, source);
                    return false;
                }
            }

            return Borrow(ownerPlace.LocalId, referencePlace.LocalId, mutable, locals, add, function, block, index, source);
        }

        public bool EndBorrowPlace(SafeCoreOwnershipPlace place,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source)
        {
            return EndBorrow(place.LocalId, locals, add, function, block, index, source);
        }

        public bool WritePlace(SafeCoreOwnershipPlace place,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source)
        {
            if (place.IsRoot)
                return Write(place.LocalId, locals, add, function, block, index, source);
            // A projected reference still aliases its reference local. The
            // root check intentionally remains conservative until typed MIR
            // supplies field-level borrow provenance.
            return Write(place.LocalId, locals, add, function, block, index, source);
        }

        public bool DropPlace(SafeCoreOwnershipPlace place,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source, Action step)
        {
            if (place.IsRoot)
                return Drop(place.LocalId, locals, add, function, block, index, source, step);
            if (!UsePlace(place, locals, add, function, block, index, source)) return false;
            if (locals.TryGetValue(place.LocalId, out SafeCoreOwnershipLocal? owner) &&
                values.TryGetValue(place.LocalId, out ValueState? root) &&
                (owner.HasDrop || root.DropOwned))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidDrop,
                    "A projected value cannot be dropped out of an aggregate that requires Drop.",
                    function.Name, block.Id, index, source);
                return false;
            }
            if (HasActiveBorrow(place.LocalId, mutableOnly: false))
            {
                add(SafeCoreOwnershipDiagnosticCodes.MoveWhileBorrowed,
                    "Cannot drop a projected value while its owner is borrowed.", function.Name, block.Id, index, source);
                return false;
            }
            ValueState projected = ReadPlace(place, values[place.LocalId]);
            projectedValues[PlaceKey(place)] = projected with { Initialized = false, Dropped = true };
            Trace.Add($"drop {DisplayPlace(place, locals)}");
            DropOrder.Add(DisplayPlace(place, locals));
            return true;
        }

        private static string PlaceKey(SafeCoreOwnershipPlace place)
        {
            // The display form is intentionally human-readable, but it is not
            // a safe identity: a field named "a.b" must differ from the
            // two-field path a -> b. Length-prefix each component so the
            // ownership state key remains structural and collision-free.
            var builder = new System.Text.StringBuilder();
            builder.Append(place.LocalId.ToString(CultureInfo.InvariantCulture)).Append('|');
            foreach (SafeCoreOwnershipProjection projection in place.Projections)
            {
                builder.Append(((int)projection.Kind).ToString(CultureInfo.InvariantCulture)).Append(':');
                if (projection.Kind == SafeCoreOwnershipProjectionKind.Field)
                {
                    string name = projection.Name!;
                    builder.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name);
                }
                else
                {
                    builder.Append(projection.Index.ToString(CultureInfo.InvariantCulture));
                }

                builder.Append('|');
            }

            return builder.ToString();
        }

        private ValueState ReadPlace(SafeCoreOwnershipPlace place, ValueState root)
        {
            if (place.IsRoot) return root;
            return projectedValues.TryGetValue(PlaceKey(place), out ValueState? value)
                ? value
                : root with { DropOwned = false, MovedTo = null };
        }

        private void WritePlace(SafeCoreOwnershipPlace place, ValueState value)
        {
            if (place.IsRoot)
            {
                values[place.LocalId] = value;
                InvalidateProjected(place.LocalId);
                return;
            }

            projectedValues[PlaceKey(place)] = value;
        }

        private void InvalidateProjected(int localId)
        {
            // Use the encoded root key so invalidating local 0 cannot touch
            // projected state belonging to local 01 (or any other prefix).
            string prefix = PlaceKey(SafeCoreOwnershipPlace.Root(localId));
            foreach (string key in projectedValues.Keys
                         .Where(key => IsPlacePrefix(prefix, key))
                         .ToArray())
                projectedValues.Remove(key);
        }

        private bool HasMovedProjection(SafeCoreOwnershipPlace root)
        {
            string key = PlaceKey(root);
            return projectedValues.Any(pair =>
                (IsPlacePrefix(key, pair.Key) || IsPlacePrefix(pair.Key, key) || pair.Key == key) &&
                (pair.Value.Moved || pair.Value.Dropped));
        }

        private static bool PlacesOverlap(SafeCoreOwnershipPlace left, SafeCoreOwnershipPlace right)
        {
            if (left.LocalId != right.LocalId) return false;
            string leftKey = PlaceKey(left);
            string rightKey = PlaceKey(right);
            return leftKey == rightKey || IsPlacePrefix(leftKey, rightKey) || IsPlacePrefix(rightKey, leftKey);
        }

        private static bool IsPlacePrefix(string prefix, string candidate) =>
            candidate.Length > prefix.Length && candidate.StartsWith(prefix, StringComparison.Ordinal);

        private static string DisplayPlace(SafeCoreOwnershipPlace place,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals)
        {
            string display = place.ToString();
            if (!locals.TryGetValue(place.LocalId, out SafeCoreOwnershipLocal? local)) return display;
            string root = place.LocalId.ToString(CultureInfo.InvariantCulture);
            int offset = display.IndexOf(root, StringComparison.Ordinal);
            return offset < 0 ? local.Name : local.Name + display[(offset + root.Length)..];
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

            if (HasMovedProjection(SafeCoreOwnershipPlace.Root(id)))
            {
                add(SafeCoreOwnershipDiagnosticCodes.UseAfterMove,
                    "An aggregate local is partially moved and cannot be used as a whole.",
                    function.Name, block.Id, index, source);
                return false;
            }

            if (borrows.TryGetValue(id, out BorrowState? borrow) && borrow.Active)
            {
                if (borrow.Suspended)
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
                // Replacing a reference ends its previous loan first. A
                // live child reborrow still makes the assignment invalid.
                if (!local.IsReference || HasActiveChild(id))
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "Assignment cannot overwrite an active reference.", function.Name, block.Id, index, source);
                    return false;
                }
                EndBorrowInternal(id);
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

        public bool CopyReference(int sourceId, int destinationId,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action<string, string, string, int, int, SafeCoreMirSource> add,
            SafeCoreOwnershipFunction function, SafeCoreOwnershipBlock block, int index,
            SafeCoreMirSource source)
        {
            if (!EnsureLocalScope(sourceId, locals, add, function, block, index, source) ||
                !EnsureLocalScope(destinationId, locals, add, function, block, index, source) ||
                !locals.TryGetValue(sourceId, out SafeCoreOwnershipLocal? sourceLocal) ||
                !locals.TryGetValue(destinationId, out SafeCoreOwnershipLocal? destinationLocal) ||
                !sourceLocal.IsReference || !destinationLocal.IsReference ||
                !values.TryGetValue(sourceId, out ValueState? sourceValue) ||
                !values.TryGetValue(destinationId, out ValueState? destinationValue))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A shared reference copy requires reference locals.", function.Name, block.Id, index, source);
                return false;
            }
            if (!Use(sourceId, locals, add, function, block, index, source)) return false;
            if (borrows.TryGetValue(destinationId, out BorrowState? destinationBorrow) && destinationBorrow.Active)
            {
                if (HasActiveChild(destinationId))
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A reference destination cannot be replaced while a reborrow is active.", function.Name, block.Id, index, source);
                    return false;
                }
                EndBorrowInternal(destinationId);
            }
            if (destinationValue.Initialized && !destinationValue.Moved && !destinationValue.Dropped && destinationValue.DropOwned)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidDrop, "A shared reference copy would overwrite a live Drop value.", function.Name, block.Id, index, source);
                return false;
            }
            if (!borrows.TryGetValue(sourceId, out BorrowState? borrow) || !borrow.Active || borrow.Mutable)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A shared reference copy requires an active shared loan.", function.Name, block.Id, index, source);
                return false;
            }
            values[destinationId] = destinationValue with
            {
                Initialized = true, Moved = false, Dropped = false, DropOwned = false, MovedTo = null,
            };
            borrows[destinationId] = borrow with { Suspended = false, EscapeReported = false };
            Trace.Add($"copy_ref {sourceLocal.Name} -> {destinationLocal.Name}");
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
            BorrowState? sourceBorrow = null;
            bool transferBorrow = sourceLocal.IsReference &&
                borrows.TryGetValue(sourceId, out sourceBorrow) && sourceBorrow.Active;
            if (transferBorrow && HasActiveChild(sourceId))
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A borrowed reference cannot be moved while a reborrow is active.", function.Name, block.Id, index, source);
                return false;
            }
            if (borrows.TryGetValue(destinationId, out BorrowState? destinationBorrow) && destinationBorrow.Active)
            {
                if (!destinationLocal.IsReference || HasActiveChild(destinationId))
                {
                    add(SafeCoreOwnershipDiagnosticCodes.InvalidBorrow, "A move destination cannot overwrite an active reference.", function.Name, block.Id, index, source);
                    return false;
                }
                EndBorrowInternal(destinationId);
            }
            if (borrows.Values.Any(borrow => borrow.Active && borrow.OwnerId == sourceId))
            {
                add(SafeCoreOwnershipDiagnosticCodes.MoveWhileBorrowed, "A value cannot move while it is borrowed.", function.Name, block.Id, index, source);
                return false;
            }

            if (destination.Initialized && !destination.Moved && !destination.Dropped && !destinationLocal.IsReference)
            {
                add(SafeCoreOwnershipDiagnosticCodes.InvalidDrop, "A move destination is already initialized.", function.Name, block.Id, index, source);
                return false;
            }

            if (sourceLocal.Kind == SafeCoreOwnershipKind.Move ||
                sourceLocal.IsReference && sourceLocal.Type.IsMutable)
                values[sourceId] = values[sourceId] with { Initialized = false, Moved = true, MovedTo = destinationId };
            values[destinationId] = destination with { Initialized = true, Moved = false, Dropped = false, DropOwned = values[sourceId].DropOwned || sourceLocal.HasDrop, MovedTo = null };
            if (transferBorrow)
            {
                if (sourceLocal.Type.IsMutable) borrows.Remove(sourceId);
                borrows[destinationId] = sourceBorrow!;
            }
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
                // A shared reference may be reborrowed by other shared
                // references at the same time.  Only a mutable reborrow (or
                // a reborrow from an already mutable parent) suspends the
                // parent and requires exclusive child access.
                if (parentBorrow.Suspended || (mutable && HasActiveChild(ownerId)))
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
            {
                // Shared-to-shared reborrows do not invalidate the parent.
                // A mutable child, or any child of a mutable parent, does.
                bool suspendParent = mutable || parent.Mutable;
                borrows[parentId] = parent with { Suspended = suspendParent };
            }
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

        public void EndDeadBorrows(
            IReadOnlySet<int> liveReferences,
            IReadOnlyDictionary<int, SafeCoreOwnershipLocal> locals,
            Action step)
        {
            // End innermost borrows first. A parent may become dead only after
            // its last child has ended, so repeat a bounded number of passes.
            int maximumPasses = Math.Max(1, borrows.Count + 1);
            for (int pass = 0; pass < maximumPasses; pass++)
            {
                step();
                bool ended = false;
                foreach ((int referenceId, BorrowState borrow) in borrows
                             .Where(pair => pair.Value.Active && !liveReferences.Contains(pair.Key))
                             .OrderByDescending(pair => pair.Key)
                             .ToArray())
                {
                    step();
                    if (HasActiveChild(referenceId)) continue;
                    EndBorrowAtScopeExit(referenceId);
                    string name = locals.TryGetValue(referenceId, out SafeCoreOwnershipLocal? local)
                        ? local.Name
                        : referenceId.ToString(CultureInfo.InvariantCulture);
                    Trace.Add($"nll_end {name}");
                    ended = true;
                }
                if (!ended) break;
            }
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
            bool hasActiveChild = HasActiveChild(parentId);
            bool hasActiveMutableChild = borrows.Values.Any(borrow =>
                borrow.Active && borrow.ParentReferenceId == parentId && borrow.Mutable);
            // A mutable parent is suspended while any child exists. A shared
            // parent remains usable across concurrent shared children; only a
            // mutable child requires exclusive suspension.
            borrows[parentId] = parent with
            {
                Suspended = (parent.Mutable && hasActiveChild) || hasActiveMutableChild,
            };
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
