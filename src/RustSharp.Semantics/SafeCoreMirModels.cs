using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Original source evidence for a MIR element. IDs and offsets are nonnegative.</summary>
public sealed record SafeCoreMirSource(string SourcePath, TextSpan Span, int HirNodeId, int SourceLength);

public enum SafeCoreMirLocalKind { Parameter, User, Temporary }
public enum SafeCoreMirOperandKind { Local, Constant, Function }
public enum SafeCoreMirRvalueKind { Use, Unary, Binary, Coerce, Cast, Tuple }
public enum SafeCoreMirTerminatorKind { Return, Goto, Branch, Call, Unreachable }

/// <summary>A local slot. ID is its index in the owning function. Parameters precede other slots.</summary>
public sealed record SafeCoreMirLocal(int Id, string Name, SafeCoreType Type,
    SafeCoreMirLocalKind Kind, bool IsMutable, SafeCoreMirSource Source);

/// <summary>One immutable leaf operand. Constants use invariant decimal text, bool words,
/// decimal Unicode scalar values for char, or () for unit; they are not Rust source tokens.</summary>
public sealed record SafeCoreMirOperand(SafeCoreMirOperandKind Kind, SafeCoreType Type,
    int Id, string? Value, SafeCoreMirSource Source)
{
    public static SafeCoreMirOperand Local(int localId, SafeCoreType type, SafeCoreMirSource source) =>
        new(SafeCoreMirOperandKind.Local, type, localId, null, source);
    public static SafeCoreMirOperand Constant(SafeCoreType type, string value, SafeCoreMirSource source) =>
        new(SafeCoreMirOperandKind.Constant, type, -1, value, source);
    public static SafeCoreMirOperand Function(int functionId, SafeCoreType type, SafeCoreMirSource source) =>
        new(SafeCoreMirOperandKind.Function, type, functionId, null, source);
}

/// <summary>An explicit typed computation. Collections are copied with bounded indexed access.</summary>
public sealed class SafeCoreMirRvalue
{
    public SafeCoreMirRvalue(SafeCoreMirRvalueKind kind, SafeCoreType type,
        IReadOnlyList<SafeCoreMirOperand> operands, string? @operator, SafeCoreMirSource source,
        CancellationToken cancellationToken = default)
    {
        Kind = kind;
        Type = type;
        Operands = SafeCoreMirCollections.Freeze(operands, cancellationToken);
        Operator = @operator;
        Source = source;
    }

    public SafeCoreMirRvalueKind Kind { get; }
    public SafeCoreType Type { get; }
    public IReadOnlyList<SafeCoreMirOperand> Operands { get; }
    public string? Operator { get; }
    public SafeCoreMirSource Source { get; }
    public static SafeCoreMirRvalue Use(SafeCoreMirOperand operand, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Use, operand.Type, [operand], null, source);
    public static SafeCoreMirRvalue Unary(string op, SafeCoreMirOperand operand, SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Unary, resultType, [operand], op, source);
    public static SafeCoreMirRvalue Binary(string op, SafeCoreMirOperand left, SafeCoreMirOperand right,
        SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Binary, resultType, [left, right], op, source);
    public static SafeCoreMirRvalue Coerce(SafeCoreMirOperand operand, SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Coerce, resultType, [operand], null, source);
    public static SafeCoreMirRvalue Cast(SafeCoreMirOperand operand, SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Cast, resultType, [operand], null, source);
    public static SafeCoreMirRvalue Tuple(IReadOnlyList<SafeCoreMirOperand> operands, SafeCoreType resultType,
        SafeCoreMirSource source, CancellationToken cancellationToken = default) =>
        new(SafeCoreMirRvalueKind.Tuple, resultType, operands, null, source, cancellationToken);
}

/// <summary>Assign a value to a local slot. Source binding mutability is checked by HIR;
/// MIR locals are storage slots and may be written on separate CFG paths.</summary>
public sealed record SafeCoreMirStatement(int DestinationLocalId, SafeCoreMirRvalue Value, SafeCoreMirSource Source);

/// <summary>Exactly one explicit control-flow terminator ends every basic block.
/// Unused target IDs use -1; a diverging call uses continuation -1 and no destination.</summary>
public sealed class SafeCoreMirTerminator
{
    public SafeCoreMirTerminator(SafeCoreMirTerminatorKind kind, SafeCoreMirOperand? operand,
        IReadOnlyList<SafeCoreMirOperand> arguments, int? destinationLocalId, int targetBlockId,
        int falseTargetBlockId, SafeCoreMirSource source, CancellationToken cancellationToken = default)
    {
        Kind = kind;
        Operand = operand;
        Arguments = SafeCoreMirCollections.Freeze(arguments, cancellationToken);
        DestinationLocalId = destinationLocalId;
        TargetBlockId = targetBlockId;
        FalseTargetBlockId = falseTargetBlockId;
        Source = source;
    }

    public SafeCoreMirTerminatorKind Kind { get; }
    public SafeCoreMirOperand? Operand { get; }
    public IReadOnlyList<SafeCoreMirOperand> Arguments { get; }
    public int? DestinationLocalId { get; }
    public int TargetBlockId { get; }
    public int FalseTargetBlockId { get; }
    public SafeCoreMirSource Source { get; }
    public static SafeCoreMirTerminator Return(SafeCoreMirOperand? value, SafeCoreMirSource source) =>
        new(SafeCoreMirTerminatorKind.Return, value, [], null, -1, -1, source);
    public static SafeCoreMirTerminator Goto(int target, SafeCoreMirSource source) =>
        new(SafeCoreMirTerminatorKind.Goto, null, [], null, target, -1, source);
    public static SafeCoreMirTerminator Branch(SafeCoreMirOperand condition, int trueTarget, int falseTarget, SafeCoreMirSource source) =>
        new(SafeCoreMirTerminatorKind.Branch, condition, [], null, trueTarget, falseTarget, source);
    public static SafeCoreMirTerminator Call(SafeCoreMirOperand callee, IReadOnlyList<SafeCoreMirOperand> arguments,
        int? destinationLocalId, int continuationBlockId, SafeCoreMirSource source, CancellationToken cancellationToken = default) =>
        new(SafeCoreMirTerminatorKind.Call, callee, arguments, destinationLocalId, continuationBlockId, -1, source, cancellationToken);
    public static SafeCoreMirTerminator Unreachable(SafeCoreMirSource source) =>
        new(SafeCoreMirTerminatorKind.Unreachable, null, [], null, -1, -1, source);
}

public sealed class SafeCoreMirBlock
{
    public SafeCoreMirBlock(int id, IReadOnlyList<SafeCoreMirStatement> statements,
        SafeCoreMirTerminator terminator, SafeCoreMirSource source, CancellationToken cancellationToken = default)
    {
        Id = id;
        Statements = SafeCoreMirCollections.Freeze(statements, cancellationToken);
        Terminator = terminator;
        Source = source;
    }
    public int Id { get; }
    public IReadOnlyList<SafeCoreMirStatement> Statements { get; }
    public SafeCoreMirTerminator Terminator { get; }
    public SafeCoreMirSource Source { get; }
}

public sealed class SafeCoreMirFunction
{
    public SafeCoreMirFunction(int id, string name, SafeCoreType returnType,
        IReadOnlyList<SafeCoreMirLocal> locals, IReadOnlyList<SafeCoreMirBlock> blocks, int entryBlockId,
        SafeCoreMirSource source, CancellationToken cancellationToken = default)
    {
        Id = id;
        Name = name;
        ReturnType = returnType;
        Locals = SafeCoreMirCollections.Freeze(locals, cancellationToken);
        Blocks = SafeCoreMirCollections.Freeze(blocks, cancellationToken);
        EntryBlockId = entryBlockId;
        Source = source;
    }
    public int Id { get; }
    public string Name { get; }
    public SafeCoreType ReturnType { get; }
    public IReadOnlyList<SafeCoreMirLocal> Locals { get; }
    public IReadOnlyList<SafeCoreMirBlock> Blocks { get; }
    public int EntryBlockId { get; }
    public SafeCoreMirSource Source { get; }
}

/// <summary>Backend-independent typed MIR. IDs index immutable owning collections.</summary>
public sealed class SafeCoreMirProgram
{
    public SafeCoreMirProgram(IReadOnlyList<SafeCoreMirFunction> functions, CancellationToken cancellationToken = default) =>
        Functions = SafeCoreMirCollections.Freeze(functions, cancellationToken);
    public IReadOnlyList<SafeCoreMirFunction> Functions { get; }
}

public sealed class SafeCoreMirLimitException(string message) : Exception(message);

internal static class SafeCoreMirCollections
{
    public static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        int count = items.Count;
        if (count is < 0 or > 100_000) throw new SafeCoreMirLimitException("MIR collection limit reached.");
        var copy = new T[count];
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new SafeCoreMirLimitException("MIR construction timeout reached.");
            copy[index] = items[index];
            ArgumentNullException.ThrowIfNull(copy[index]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new SafeCoreMirLimitException("MIR construction timeout reached.");
        return Array.AsReadOnly(copy);
    }
}
