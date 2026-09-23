using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Original source evidence for a MIR element. IDs and offsets are nonnegative.</summary>
public sealed record SafeCoreMirSource(string SourcePath, TextSpan Span, int HirNodeId, int SourceLength);

public enum SafeCoreMirLocalKind { Parameter, User, Temporary }
public enum SafeCoreMirOperandKind { Local, Constant, Function, Place }

/// <summary>A typed-MIR storage place: a local plus a bounded projection chain.</summary>
public enum SafeCoreMirProjectionKind { Field, TupleIndex, ArrayIndex, Dereference }

public sealed record SafeCoreMirProjection(SafeCoreMirProjectionKind Kind, string? Name, int Index)
{
    public static SafeCoreMirProjection Field(string name) =>
        new(SafeCoreMirProjectionKind.Field, name, -1);
    public static SafeCoreMirProjection TupleIndex(int index) =>
        new(SafeCoreMirProjectionKind.TupleIndex, null, index);
    public static SafeCoreMirProjection ArrayIndex(int index) =>
        new(SafeCoreMirProjectionKind.ArrayIndex, null, index);
    public static SafeCoreMirProjection Dereference() =>
        new(SafeCoreMirProjectionKind.Dereference, null, -1);
}

public sealed class SafeCoreMirPlace
{
    public SafeCoreMirPlace(int localId, IReadOnlyList<SafeCoreMirProjection>? projections = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(localId);
        projections ??= [];
        if (projections.Count > 128) throw new ArgumentException("MIR projection depth exceeds its bound.", nameof(projections));
        var copy = new SafeCoreMirProjection[projections.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            SafeCoreMirProjection projection = projections[index] ??
                throw new ArgumentException("MIR projections cannot contain null values.", nameof(projections));
            if (!Enum.IsDefined(projection.Kind) ||
                (projection.Kind == SafeCoreMirProjectionKind.Field &&
                 (string.IsNullOrWhiteSpace(projection.Name) || projection.Name.Length > 4096 || projection.Index != -1)) ||
                (projection.Kind is SafeCoreMirProjectionKind.TupleIndex or SafeCoreMirProjectionKind.ArrayIndex &&
                 (projection.Name is not null || projection.Index < 0)) ||
                (projection.Kind == SafeCoreMirProjectionKind.Dereference &&
                 (projection.Name is not null || projection.Index != -1)))
                throw new ArgumentException("MIR projection metadata is invalid.", nameof(projections));
            copy[index] = projection;
        }
        LocalId = localId;
        Projections = Array.AsReadOnly(copy);
    }

    public int LocalId { get; }
    public IReadOnlyList<SafeCoreMirProjection> Projections { get; }
    public bool IsRoot => Projections.Count == 0;
    public static SafeCoreMirPlace Root(int localId) => new(localId);
    public SafeCoreMirPlace Append(SafeCoreMirProjection projection) => new(LocalId, [.. Projections, projection]);

    public override string ToString()
    {
        var builder = new StringBuilder(LocalId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (SafeCoreMirProjection projection in Projections)
        {
            switch (projection.Kind)
            {
                case SafeCoreMirProjectionKind.Field: builder.Append('.').Append(projection.Name); break;
                case SafeCoreMirProjectionKind.TupleIndex: builder.Append(".tuple[").Append(projection.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']'); break;
                case SafeCoreMirProjectionKind.ArrayIndex: builder.Append('[').Append(projection.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']'); break;
                case SafeCoreMirProjectionKind.Dereference: builder.Append('.').Append('*'); break;
            }
        }
        return builder.ToString();
    }
}
public enum SafeCoreMirRvalueKind { Use, Unary, Binary, Coerce, Cast, Tuple, Print, Array, Index, Field, Write, SliceLength }
public enum SafeCoreMirTerminatorKind { Return, Goto, Branch, Call, Unreachable }

/// <summary>A local slot. ID is its index in the owning function. Parameters precede other slots.</summary>
public sealed record SafeCoreMirLocal(int Id, string Name, SafeCoreType Type,
    SafeCoreMirLocalKind Kind, bool IsMutable, SafeCoreMirSource Source)
{
    /// <summary>Source-checked zero-field ADT declaration evidence.</summary>
    public bool IsUnitAdt { get; init; }
    /// <summary>Canonical MIR destructor function for an owned local, if any.</summary>
    public int? DestructorFunctionId { get; init; }
}

/// <summary>One immutable leaf operand. Constants use invariant decimal text, bool words,
/// decimal Unicode scalar values for char, or () for unit; they are not Rust source tokens.</summary>
public sealed record SafeCoreMirOperand(SafeCoreMirOperandKind Kind, SafeCoreType Type,
    int Id, string? Value, SafeCoreMirSource Source)
{
    public SafeCoreMirPlace? Place { get; init; }

    public static SafeCoreMirOperand Local(int localId, SafeCoreType type, SafeCoreMirSource source) =>
        new(SafeCoreMirOperandKind.Local, type, localId, null, source);
    public static SafeCoreMirOperand Constant(SafeCoreType type, string value, SafeCoreMirSource source) =>
        new(SafeCoreMirOperandKind.Constant, type, -1, value, source);
    public static SafeCoreMirOperand Function(int functionId, SafeCoreType type, SafeCoreMirSource source) =>
        new(SafeCoreMirOperandKind.Function, type, functionId, null, source);
    public static SafeCoreMirOperand PlaceValue(SafeCoreMirPlace place, SafeCoreType type, SafeCoreMirSource source)
    {
        ArgumentNullException.ThrowIfNull(place);
        return new(SafeCoreMirOperandKind.Place, type, place.LocalId, null, source) { Place = place };
    }
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
    public static SafeCoreMirRvalue Array(IReadOnlyList<SafeCoreMirOperand> operands, SafeCoreType resultType,
        SafeCoreMirSource source, CancellationToken cancellationToken = default) =>
        new(SafeCoreMirRvalueKind.Array, resultType, operands, null, source, cancellationToken);
    public static SafeCoreMirRvalue Index(SafeCoreMirOperand array, SafeCoreMirOperand index,
        SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Index, resultType, [array, index], null, source);
    /// <summary>Returns the bounded length of an array or a full array-to-slice view.</summary>
    public static SafeCoreMirRvalue SliceLength(SafeCoreMirOperand slice,
        SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.SliceLength,
            SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize), [slice], null, source);
    public static SafeCoreMirRvalue Field(SafeCoreMirOperand aggregate, int fieldIndex,
        SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Field, resultType, [aggregate],
            fieldIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), source);
    /// <summary>Writes through a reference. The first operand is a reference,
    /// the second is the value being stored, and the result type is the value
    /// type. The enclosing statement destination is the owner slot used by
    /// executable CLR lowering; ownership lowering records the Write effect.
    /// </summary>
    public static SafeCoreMirRvalue Write(SafeCoreMirOperand reference, SafeCoreMirOperand value,
        SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Write, resultType, [reference, value], null, source);
    public static SafeCoreMirRvalue Print(string format, SafeCoreMirOperand? value, SafeCoreMirSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CA2016 // SafeCoreType is an immutable descriptor and has no collection to freeze.
        return new(SafeCoreMirRvalueKind.Print, SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit),
            value is null ? [] : [value], format, source, cancellationToken);
#pragma warning restore CA2016
    }
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
    /// <summary>An explicit exactly-once cleanup call for this initialized local.</summary>
    public int? DropLocalId { get; init; }
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
        : this(id, name, returnType, locals, blocks, entryBlockId, source,
            isPublic: true, cancellationToken: cancellationToken)
    {
    }

    public SafeCoreMirFunction(int id, string name, SafeCoreType returnType,
        IReadOnlyList<SafeCoreMirLocal> locals, IReadOnlyList<SafeCoreMirBlock> blocks, int entryBlockId,
        SafeCoreMirSource source, bool isPublic, CancellationToken cancellationToken = default)
    {
        Id = id;
        Name = name;
        ReturnType = returnType;
        Locals = SafeCoreMirCollections.Freeze(locals, cancellationToken);
        Blocks = SafeCoreMirCollections.Freeze(blocks, cancellationToken);
        EntryBlockId = entryBlockId;
        Source = source;
        IsPublic = isPublic;
    }
    public int Id { get; }
    public string Name { get; }
    public SafeCoreType ReturnType { get; }
    public IReadOnlyList<SafeCoreMirLocal> Locals { get; }
    public IReadOnlyList<SafeCoreMirBlock> Blocks { get; }
    public int EntryBlockId { get; }
    public SafeCoreMirSource Source { get; }
    /// <summary>Whether the source declaration is visible to external crates.</summary>
    public bool IsPublic { get; }
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
