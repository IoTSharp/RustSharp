using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Original source evidence for a MIR element. IDs and offsets are nonnegative.</summary>
public sealed record SafeCoreMirSource(string SourcePath, TextSpan Span, int HirNodeId, int SourceLength);

public enum SafeCoreMirLocalKind { Parameter, User, Temporary }
public enum SafeCoreMirOperandKind { Local, Constant, Function, Place }

/// <summary>A typed-MIR storage place: a local plus a bounded projection chain.</summary>
public enum SafeCoreMirProjectionKind { Field, TupleIndex, ArrayIndex, Dereference, DynamicIndex, Downcast, FromEndIndex }

public sealed record SafeCoreMirProjection(SafeCoreMirProjectionKind Kind, string? Name, int Index)
{
    /// <summary>FromEndIndex checks this minimum owner length before computing length minus Index.</summary>
    public int MinimumLength { get; init; }
    public static SafeCoreMirProjection Field(string name) =>
        new(SafeCoreMirProjectionKind.Field, name, -1);
    public static SafeCoreMirProjection TupleIndex(int index) =>
        new(SafeCoreMirProjectionKind.TupleIndex, null, index);
    public static SafeCoreMirProjection ArrayIndex(int index) =>
        new(SafeCoreMirProjectionKind.ArrayIndex, null, index);
    public static SafeCoreMirProjection Dereference() =>
        new(SafeCoreMirProjectionKind.Dereference, null, -1);
    /// <summary>Index is the ID of an evaluated usize local, not an array offset.</summary>
    public static SafeCoreMirProjection DynamicIndex(int localId) =>
        new(SafeCoreMirProjectionKind.DynamicIndex, null, localId);
    /// <summary>Selects one declared enum variant before projecting its payload.</summary>
    public static SafeCoreMirProjection Downcast(int variantIndex) =>
        new(SafeCoreMirProjectionKind.Downcast, null, variantIndex);
    public static SafeCoreMirProjection FromEndIndex(int distance, int minimumLength) =>
        new(SafeCoreMirProjectionKind.FromEndIndex, null, distance) { MinimumLength = minimumLength };
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
                (projection.Kind != SafeCoreMirProjectionKind.FromEndIndex && projection.MinimumLength != 0) ||
                (projection.Kind == SafeCoreMirProjectionKind.FromEndIndex &&
                 (projection.Name is not null || projection.Index <= 0 || projection.MinimumLength < projection.Index)) ||
                (projection.Kind == SafeCoreMirProjectionKind.Field &&
                 (string.IsNullOrWhiteSpace(projection.Name) || projection.Name.Length > 4096 || projection.Index != -1)) ||
                (projection.Kind is SafeCoreMirProjectionKind.TupleIndex or SafeCoreMirProjectionKind.ArrayIndex or SafeCoreMirProjectionKind.DynamicIndex or SafeCoreMirProjectionKind.Downcast &&
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
                case SafeCoreMirProjectionKind.DynamicIndex: builder.Append("[%").Append(projection.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']'); break;
                case SafeCoreMirProjectionKind.Downcast: builder.Append(".variant[").Append(projection.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']'); break;
                case SafeCoreMirProjectionKind.FromEndIndex:
                    builder.Append("[^").Append(projection.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .Append(";min=").Append(projection.MinimumLength.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']'); break;
            }
        }
        return builder.ToString();
    }
}

/// <summary>A checked place occurrence. Mutability describes access through this path,
/// not initialization, borrow liveness, or the lifetime of the ultimate referent.</summary>
public sealed record SafeCoreMirPlaceType(int FunctionId, SafeCoreMirPlace Place,
    SafeCoreType RootType, SafeCoreType Type, bool IsMutable, SafeCoreMirSource Source);

public enum SafeCoreMirRvalueKind { Use, Unary, Binary, Coerce, Cast, Tuple, Print, Array, Index, Field, Write, SliceLength, Adt, Enum, Discriminant, Subslice, PromotedBorrow }
public enum SafeCoreMirTerminatorKind { Return, Goto, Branch, Call, Unreachable }

/// <summary>A local slot. ID is its index in the owning function. Parameters precede other slots.</summary>
public sealed record SafeCoreMirLocal(int Id, string Name, SafeCoreType Type,
    SafeCoreMirLocalKind Kind, bool IsMutable, SafeCoreMirSource Source)
{
    /// <summary>Source-checked zero-field ADT declaration evidence.</summary>
    public bool IsUnitAdt { get; init; }
    public bool IsPromotedConstant { get; init; }
    public bool RequiresStaticLifetime { get; init; }
    /// <summary>Canonical MIR destructor function for an owned local, if any.</summary>
    public int? DestructorFunctionId { get; init; }
    /// <summary>Lexical storage extent for source locals and temporaries. Parameters
    /// have no local storage extent; missing legacy evidence uses the function extent.</summary>
    public SafeCoreMirSource? StorageScope { get; init; }
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
    /// <summary>Constructs a declared nominal aggregate in declaration field order.</summary>
    public static SafeCoreMirRvalue Adt(IReadOnlyList<SafeCoreMirOperand> operands, SafeCoreType resultType,
        SafeCoreMirSource source, CancellationToken cancellationToken = default) =>
        new(SafeCoreMirRvalueKind.Adt, resultType, operands, null, source, cancellationToken);
    public static SafeCoreMirRvalue Enum(int variantIndex, IReadOnlyList<SafeCoreMirOperand> operands,
        SafeCoreType resultType, SafeCoreMirSource source, CancellationToken cancellationToken = default) =>
        new(SafeCoreMirRvalueKind.Enum, resultType, operands,
            variantIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), source, cancellationToken);
    public static SafeCoreMirRvalue PromotedBorrow(SafeCoreMirOperand operand, SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.PromotedBorrow, resultType, [operand], null, source);
    public static SafeCoreMirRvalue Discriminant(SafeCoreMirOperand value, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Discriminant, SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32), [value], null, source);
    public static SafeCoreMirRvalue Subslice(SafeCoreMirOperand value, SafeCoreMirOperand start,
        SafeCoreMirOperand end, SafeCoreType resultType, SafeCoreMirSource source, bool inclusive = false) =>
        new(SafeCoreMirRvalueKind.Subslice, resultType, [value, start, end], inclusive ? "..=" : "..", source);

    /// <summary>A pattern rest excludes a fixed prefix and suffix; the runtime checks both against the actual length.</summary>
    public static SafeCoreMirRvalue PatternSubslice(SafeCoreMirOperand value, int prefix,
        int suffix, SafeCoreType resultType, SafeCoreMirSource source) =>
        new(SafeCoreMirRvalueKind.Subslice, resultType,
            [value, SafeCoreMirOperand.Constant(SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize), prefix.ToString(System.Globalization.CultureInfo.InvariantCulture), source),
                SafeCoreMirOperand.Constant(SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize), suffix.ToString(System.Globalization.CultureInfo.InvariantCulture), source)], "pattern", source);
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
public sealed record SafeCoreMirStatement(int DestinationLocalId, SafeCoreMirRvalue Value, SafeCoreMirSource Source)
{
    /// <summary>Explicit projected storage destination. Its root must match
    /// DestinationLocalId; null retains the original local-assignment contract.</summary>
    public SafeCoreMirPlace? DestinationPlace { get; init; }
}

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
    public bool ReturnsStaticReference { get; init; }
}

/// <summary>Backend-independent typed MIR. IDs index immutable owning collections.</summary>
public sealed class SafeCoreMirProgram
{
    public SafeCoreMirProgram(IReadOnlyList<SafeCoreMirFunction> functions, CancellationToken cancellationToken = default)
        : this(functions, [], cancellationToken) { }

    public SafeCoreMirProgram(IReadOnlyList<SafeCoreMirFunction> functions,
        IReadOnlyList<SafeCoreMirAdtLayout> adtLayouts, CancellationToken cancellationToken = default)
    {
        Functions = SafeCoreMirCollections.Freeze(functions, cancellationToken);
        AdtLayouts = SafeCoreMirCollections.Freeze(adtLayouts, cancellationToken);
    }
    public IReadOnlyList<SafeCoreMirFunction> Functions { get; }
    public IReadOnlyList<SafeCoreMirAdtLayout> AdtLayouts { get; }
}

/// <summary>A field's declared name, type and original declaration source.</summary>
public sealed record SafeCoreMirAdtField(string Name, SafeCoreType Type, SafeCoreMirSource Source)
{
    public bool RequiresStaticLifetime { get; init; }
}

/// <summary>One enum payload. FieldOffset indexes the enum's physical field layout after its tag.</summary>
public sealed class SafeCoreMirAdtVariant
{
    public SafeCoreMirAdtVariant(string name, int discriminant, int fieldOffset,
        IReadOnlyList<SafeCoreMirAdtField> fields, SafeCoreMirSource source, CancellationToken cancellationToken = default)
    {
        Name = name;
        Discriminant = discriminant;
        FieldOffset = fieldOffset;
        Fields = SafeCoreMirCollections.Freeze(fields, cancellationToken);
        Source = source;
    }
    public string Name { get; }
    public int Discriminant { get; }
    public int FieldOffset { get; }
    public IReadOnlyList<SafeCoreMirAdtField> Fields { get; }
    public SafeCoreMirSource Source { get; }
}

/// <summary>Immutable nominal struct layout. Copy is explicit declaration evidence,
/// never inferred merely because each field is Copy.</summary>
public sealed class SafeCoreMirAdtLayout
{
    public SafeCoreMirAdtLayout(SafeCoreType type, IReadOnlyList<SafeCoreMirAdtField> fields,
        SafeCoreMirSource source, bool isCopy = false, CancellationToken cancellationToken = default)
        : this(type, fields, [], source, isCopy, cancellationToken) { }

    public SafeCoreMirAdtLayout(SafeCoreType type, IReadOnlyList<SafeCoreMirAdtField> fields,
        IReadOnlyList<SafeCoreMirAdtVariant> variants, SafeCoreMirSource source,
        bool isCopy = false, CancellationToken cancellationToken = default)
    {
        Type = type;
        Fields = SafeCoreMirCollections.Freeze(fields, cancellationToken);
        Variants = SafeCoreMirCollections.Freeze(variants, cancellationToken);
        Source = source;
        IsCopy = isCopy;
    }
    public SafeCoreType Type { get; }
    public IReadOnlyList<SafeCoreMirAdtField> Fields { get; }
    public IReadOnlyList<SafeCoreMirAdtVariant> Variants { get; }
    public SafeCoreMirSource Source { get; }
    public bool IsCopy { get; }
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
