using System.Collections.Immutable;
using System.Diagnostics;

namespace RustSharp.CodeGen.IL;

internal static class ClrLirLimits
{
    public const int MaximumParameters = 256;
    public const int MaximumLocals = 256;
    public const int MaximumBlocks = 4096;
    public const int MaximumInstructionsPerBlock = 65536;
    public const int MaximumInstructions = 1_000_000;
    public const int MaximumValueTypes = 4096;
    public const int MaximumFields = 256;

    public static ImmutableArray<T> CopyBounded<T>(
        IEnumerable<T> values,
        int maximum,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);

        var result = ImmutableArray.CreateBuilder<T>();
        using IEnumerator<T> enumerator = values.GetEnumerator();
        for (var index = 0; index < maximum; index++)
        {
            if (!enumerator.MoveNext())
            {
                return result.ToImmutable();
            }

            result.Add(enumerator.Current);
        }

        if (enumerator.MoveNext())
        {
            throw new ArgumentException(
                $"The {parameterName} sequence exceeds the limit of {maximum} items.",
                parameterName);
        }

        return result.ToImmutable();
    }
}

/// <summary>Closed types understood by CLR LIR validation and emission.</summary>
public enum ClrLirTypeKind
{
    Void,
    I32,
    Bool,
    Text,
    Any,
    Value,
    /// <summary>A managed by-reference signature type (&amp;T).</summary>
    ByReference,
}

/// <summary>A small, value-typed CLR type descriptor used by LIR instructions.</summary>
public readonly record struct ClrLirType(ClrLirTypeKind Kind)
{
    public string? Name { get; init; }
    /// <summary>Tracks Rust mutability for ownership contracts; CLR byrefs do not encode it.</summary>
    public bool IsMutable { get; init; }
    public static ClrLirType Void => new(ClrLirTypeKind.Void);
    public static ClrLirType I32 => new(ClrLirTypeKind.I32);
    public static ClrLirType Bool => new(ClrLirTypeKind.Bool);
    public static ClrLirType Text => new(ClrLirTypeKind.Text);
    public static ClrLirType Any => new(ClrLirTypeKind.Any);
    public static ClrLirType Value(string name)
    {
        ValidateName(name);
        return new(ClrLirTypeKind.Value) { Name = name };
    }

    /// <summary>Creates a managed by-reference signature over a closed CLR type.</summary>
    public static ClrLirType ByReference(ClrLirType element, bool mutable = false)
    {
        if (!element.IsKnown || element == Void || element.Kind == ClrLirTypeKind.ByReference)
            throw new ArgumentException("A by-reference type must refer to one closed non-void value.", nameof(element));
        return new(ClrLirTypeKind.ByReference)
        {
            // The referent is stored in the bounded textual identity because
            // this descriptor is a recursive readonly record struct.
            Name = element.ToString(),
            IsMutable = mutable,
        };
    }

#pragma warning disable CA1720 // These aliases intentionally mirror CLR type names.
    public static ClrLirType Int32 => I32;
    public static ClrLirType Boolean => Bool;
    public static ClrLirType String => Text;
    public static ClrLirType Object => Any;
#pragma warning restore CA1720

    public override string ToString() => Kind switch
    {
        ClrLirTypeKind.Value => "Value(" + Name + ")",
        ClrLirTypeKind.ByReference => "&" + Name,
        _ => Kind.ToString(),
    };

    internal bool IsKnown => Kind == ClrLirTypeKind.Value
        ? IsValidName(Name)
        : Kind == ClrLirTypeKind.ByReference
            ? TryGetByReferenceElement(out _)
            : Name is null && Kind is (
                ClrLirTypeKind.Void or
                ClrLirTypeKind.I32 or
                ClrLirTypeKind.Bool or
                ClrLirTypeKind.Text or
                ClrLirTypeKind.Any);

    internal bool TryGetByReferenceElement(out ClrLirType element)
    {
        element = default;
        if (Kind != ClrLirTypeKind.ByReference || string.IsNullOrWhiteSpace(Name) || Name.Length > 1024)
            return false;
        string token = Name;
        if (token is "I32") { element = I32; return true; }
        if (token is "Bool") { element = Bool; return true; }
        if (token is "Text") { element = Text; return true; }
        if (token is "Any") { element = Any; return true; }
        if (token.StartsWith("Value(", StringComparison.Ordinal) && token.EndsWith(')') &&
            token.Length > "Value()".Length)
        {
            try
            {
                element = Value(token["Value(".Length..^1]);
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        return false;
    }

    internal static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 1024 && !name.Contains('\0');

    internal static void ValidateName(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("A CLR value identity must contain 1 to 1024 nonblank characters without NUL.", nameof(name));
    }
}

/// <summary>A field in declaration order in a closed, sequential-layout CLR value type.</summary>
public sealed record ClrLirField
{
    public ClrLirField(string name, ClrLirType type)
    {
        ClrLirType.ValidateName(name);
        Name = name;
        Type = type;
    }

    public string Name { get; }
    public ClrLirType Type { get; }
}

/// <summary>A fully closed value layout. Field order is part of its identity contract.</summary>
public sealed record ClrLirValueType
{
    public ClrLirValueType(string name, IEnumerable<ClrLirField> fields)
    {
        ClrLirType.ValidateName(name);
        Name = name;
        Fields = ClrLirLimits.CopyBounded(fields, ClrLirLimits.MaximumFields, nameof(fields));
        if (Fields.Any(static field => field is null)) throw new ArgumentException("Value fields cannot be null.", nameof(fields));
    }

    public string Name { get; }
    public ImmutableArray<ClrLirField> Fields { get; }
    public ClrLirType Type => ClrLirType.Value(Name);
}

public sealed record ClrLirLocal
{
    public ClrLirLocal(string name, ClrLirType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type;
    }

    public string Name { get; }
    public ClrLirType Type { get; }
}

/// <summary>Metadata needed to encode a static call into another Rust# assembly.</summary>
public sealed record ClrLirExternalCall
{
    public ClrLirExternalCall(
        string assemblyName,
        string typeNamespace,
        string typeName,
        string methodName,
        string? panicStrategy = null,
        IEnumerable<string>? parameterContracts = null,
        string? returnContract = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        if (assemblyName.Length > 256 || typeNamespace.Length > 256 ||
            typeName.Length > 256 || methodName.Length > 4096)
            throw new ArgumentException("External CLR call identity exceeds its bound.");
        AssemblyName = assemblyName;
        TypeNamespace = typeNamespace;
        TypeName = typeName;
        MethodName = methodName;
        PanicStrategy = panicStrategy;
        ParameterContracts = ClrLirLimits.CopyBounded(
            parameterContracts ?? [], ClrLirLimits.MaximumParameters, nameof(parameterContracts));
        ReturnContract = returnContract;
        if (PanicStrategy is not null && PanicStrategy is not ("unwind" or "abort"))
            throw new ArgumentException("External ownership panic strategy must be 'unwind' or 'abort'.", nameof(panicStrategy));
        if (ParameterContracts.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 128))
            throw new ArgumentException("External ownership parameter contracts are invalid.", nameof(parameterContracts));
        if (ReturnContract is not null &&
            (string.IsNullOrWhiteSpace(ReturnContract) || ReturnContract.Length > 128))
            throw new ArgumentException("External ownership return contract is invalid.", nameof(returnContract));
    }

    public string AssemblyName { get; }
    public string TypeNamespace { get; }
    public string TypeName { get; }
    public string MethodName { get; }
    public string? PanicStrategy { get; }
    public ImmutableArray<string> ParameterContracts { get; }
    public string? ReturnContract { get; }
}

public sealed record ClrLirCallSite
{
    public ClrLirCallSite(string name, ClrLirType returnType, IEnumerable<ClrLirType> parameterTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        Name = name;
        ReturnType = returnType;
        ParameterTypes = ClrLirLimits.CopyBounded(
            parameterTypes,
            ClrLirLimits.MaximumParameters,
            nameof(parameterTypes));
    }

    public string Name { get; }
    public ClrLirType ReturnType { get; }
    public ImmutableArray<ClrLirType> ParameterTypes { get; }
    /// <summary>When set, the call resolves to a static method in another assembly.</summary>
    public ClrLirExternalCall? ExternalCall { get; init; }
}

public abstract record ClrLirInstruction;

public sealed record ClrLirLoadInt32(int Value) : ClrLirInstruction;
public sealed record ClrLirLoadBoolean(bool Value) : ClrLirInstruction;
public sealed record ClrLirLoadString : ClrLirInstruction
{
    private const int MaximumStringCharacters = 4 * 1024 * 1024;

    public ClrLirLoadString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumStringCharacters)
        {
            throw new ArgumentException(
                $"The string literal exceeds the {MaximumStringCharacters} character limit.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

public sealed record ClrLirLoadLocal(int Index) : ClrLirInstruction;
/// <summary>Produces a managed pointer to the actual local storage.</summary>
public sealed record ClrLirLoadLocalAddress(int Index, bool IsMutable = false) : ClrLirInstruction;
public sealed record ClrLirFieldAddress : ClrLirInstruction
{
    public ClrLirFieldAddress(ClrLirValueType definition, int fieldIndex, bool isMutable = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        FieldIndex = fieldIndex;
        IsMutable = isMutable;
    }

    public ClrLirValueType Definition { get; }
    public int FieldIndex { get; }
    public bool IsMutable { get; }
}
public sealed record ClrLirLoadIndirect(ClrLirType Type) : ClrLirInstruction;
public sealed record ClrLirStoreIndirect(ClrLirType Type) : ClrLirInstruction;
/// <summary>Erases write access; the managed pointer is unchanged at runtime.</summary>
public sealed record ClrLirReadOnlyReference(ClrLirType ElementType) : ClrLirInstruction;
public sealed record ClrLirThrowIndexOutOfRange : ClrLirInstruction;
public sealed record ClrLirStoreLocal(int Index) : ClrLirInstruction;
public sealed record ClrLirLoadArgument(int Index) : ClrLirInstruction;
public sealed record ClrLirDiscard(ClrLirType Type) : ClrLirInstruction;
/// <summary>Consumes field values in declaration order and produces one value copy.</summary>
public sealed record ClrLirConstructValue : ClrLirInstruction
{
    public ClrLirConstructValue(ClrLirValueType definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    public ClrLirValueType Definition { get; }
}

/// <summary>Consumes a value copy and produces the selected field value.</summary>
public sealed record ClrLirReadField : ClrLirInstruction
{
    public ClrLirReadField(ClrLirValueType definition, int fieldIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        FieldIndex = fieldIndex;
    }

    public ClrLirValueType Definition { get; }
    public int FieldIndex { get; }
}
/// <summary>Formats i32 using the invariant .NET format provider, independent of host culture.</summary>
public sealed record ClrLirFormatInt32 : ClrLirInstruction;

public enum ClrLirBinaryOperator
{
    AddChecked,
    SubtractChecked,
    MultiplyChecked,
    Equal,
    LessThan,
    GreaterThan,
    ExclusiveOr,
    And,
    Or,
}

public sealed record ClrLirBinary(ClrLirBinaryOperator Operator, ClrLirType OperandType) : ClrLirInstruction;
public sealed record ClrLirCall : ClrLirInstruction
{
    public ClrLirCall(ClrLirCallSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        Site = site;
    }

    public ClrLirCallSite Site { get; }
}

public sealed record ClrLirBranch : ClrLirInstruction
{
    public ClrLirBranch(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Target = target;
    }

    public string Target { get; }
}

/// <summary>Leaves a protected CLR region and transfers to a continuation.</summary>
public sealed record ClrLirLeave : ClrLirInstruction
{
    public ClrLirLeave(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Target = target;
    }

    public string Target { get; }
}

public sealed record ClrLirBranchTrue : ClrLirInstruction
{
    public ClrLirBranchTrue(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Target = target;
    }

    public string Target { get; }
}

public sealed record ClrLirReturn : ClrLirInstruction;

public sealed class ClrLirBlock
{
    public ClrLirBlock(string label, IEnumerable<ClrLirInstruction> instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(instructions);
        Label = label;
        Instructions = ClrLirLimits.CopyBounded(
            instructions,
            ClrLirLimits.MaximumInstructionsPerBlock,
            nameof(instructions));
        for (var index = 0; index < Instructions.Length; index++)
        {
            if (Instructions[index] is null)
            {
                throw new ArgumentException(
                    $"Instruction at index {index} must not be null.",
                    nameof(instructions));
            }
        }
    }

    public string Label { get; }
    public ImmutableArray<ClrLirInstruction> Instructions { get; }
}

public sealed record ClrLirDiagnostic(string Code, string Message, string? BlockLabel, int InstructionIndex)
{
    public override string ToString() =>
        BlockLabel is null
            ? $"{Code}: {Message}"
            : $"{Code} ({BlockLabel}:{InstructionIndex}): {Message}";
}

public sealed record ClrLirValidationResult(ImmutableArray<ClrLirDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.IsEmpty;
    public int MaximumStackDepth { get; init; }
}

/// <summary>
/// A typed, stack-based CLR low-level IR. This model is intentionally independent
/// of PE emission so malformed control flow is rejected before an emitter is used.
/// </summary>
public sealed class ClrLirMethod
{
    private const int MaximumValidationStates = 4096;

    public ClrLirMethod(
        string name,
        ClrLirType returnType,
        IEnumerable<ClrLirType> parameters,
        IEnumerable<ClrLirLocal> locals,
        IEnumerable<ClrLirBlock> blocks,
        IEnumerable<ClrLirCallSite>? exceptionCleanup = null,
        int? faultTryBlockCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(locals);
        ArgumentNullException.ThrowIfNull(blocks);
        Name = name;
        ReturnType = returnType;
        Parameters = ClrLirLimits.CopyBounded(
            parameters,
            ClrLirLimits.MaximumParameters,
            nameof(parameters));
        Locals = ClrLirLimits.CopyBounded(
            locals,
            ClrLirLimits.MaximumLocals,
            nameof(locals));
        Blocks = ClrLirLimits.CopyBounded(
            blocks,
            ClrLirLimits.MaximumBlocks,
            nameof(blocks));
        ExceptionCleanup = ClrLirLimits.CopyBounded(
            exceptionCleanup ?? [],
            ClrLirLimits.MaximumBlocks,
            nameof(exceptionCleanup));
        FaultTryBlockCount = faultTryBlockCount ?? Blocks.Length;
        if (FaultTryBlockCount is < 1 or >  ClrLirLimits.MaximumBlocks || FaultTryBlockCount > Blocks.Length)
            throw new ArgumentOutOfRangeException(nameof(faultTryBlockCount));
        for (var index = 0; index < Blocks.Length; index++)
        {
            if (Blocks[index] is null)
            {
                throw new ArgumentException(
                    $"Block at index {index} must not be null.",
                    nameof(blocks));
            }
        }

        var instructionCount = 0;
        foreach (ClrLirBlock block in Blocks)
        {
            instructionCount = checked(instructionCount + block.Instructions.Length);
            if (instructionCount > ClrLirLimits.MaximumInstructions)
            {
                throw new ArgumentException(
                    $"The method exceeds the limit of {ClrLirLimits.MaximumInstructions} instructions.",
                    nameof(blocks));
            }
        }
    }

    public string Name { get; }
    /// <summary>Stable source-level qualified identity, when lowered from HIR.</summary>
    public string? SourceQualifiedName { get; init; }
    /// <summary>Whether a source declaration may be imported by another package.</summary>
    public bool IsPublic { get; init; } = true;
    /// <summary>An internal backend helper without an independent source ownership body.</summary>
    public bool IsCompilerGenerated { get; init; }
    public ClrLirType ReturnType { get; }
    public ImmutableArray<ClrLirType> Parameters { get; }
    public ImmutableArray<ClrLirLocal> Locals { get; }
    public ImmutableArray<ClrLirBlock> Blocks { get; }
    /// <summary>
    /// Calls emitted in a CLR fault handler when the method has source-level
    /// Drop obligations.  Normal return paths already contain explicit MIR
    /// drop calls; the fault list runs only when an exception crosses the
    /// method, preserving cleanup for CoreCLR and Native AOT generated code.
    /// </summary>
    public ImmutableArray<ClrLirCallSite> ExceptionCleanup { get; }
    /// <summary>Number of leading blocks covered by the generated fault region.</summary>
    public int FaultTryBlockCount { get; }

    public ClrLirValidationResult Validate(CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<ClrLirDiagnostic>();
        if (IsCompilerGenerated && (IsPublic || SourceQualifiedName is not null))
            diagnostics.Add(new("LIR021", "Compiler helpers must be internal and have no source declaration identity.", null, -1));
        ValidateType(ReturnType, "return", null, -1, diagnostics, allowVoid: true);
        for (var index = 0; index < Parameters.Length; index++)
        {
            ValidateType(Parameters[index], "parameter", null, index, diagnostics, allowVoid: false);
        }

        for (var index = 0; index < Locals.Length; index++)
        {
            ValidateType(Locals[index].Type, "local", null, index, diagnostics, allowVoid: false);
        }

        if (Blocks.IsEmpty)
        {
            diagnostics.Add(new("LIR000", "A method must contain an entry block.", null, -1));
            return new(diagnostics.ToImmutable());
        }

        var byLabel = new Dictionary<string, ClrLirBlock>(StringComparer.Ordinal);
        foreach (ClrLirBlock block in Blocks)
        {
            CheckBudget();
            if (!byLabel.TryAdd(block.Label, block))
            {
                diagnostics.Add(new("LIR001", $"Duplicate block label '{block.Label}'.", block.Label, -1));
            }
        }

        var incoming = new Dictionary<string, ImmutableArray<ClrLirType>>(StringComparer.Ordinal)
        {
            [Blocks[0].Label] = [],
        };
        var work = new Queue<ClrLirBlock>();
        work.Enqueue(Blocks[0]);
        int stateCount = 0;
        int maximumStackDepth = 0;
        var stateLimitExceeded = false;
        while (work.Count > 0)
        {
            if (stateCount >= MaximumValidationStates)
            {
                stateLimitExceeded = true;
                break;
            }

            stateCount++;
            ClrLirBlock block = work.Dequeue();
            ImmutableArray<ClrLirType> stack = incoming[block.Label];
            bool terminated = false;
            for (int index = 0; index < block.Instructions.Length; index++)
            {
                CheckBudget();
                ClrLirInstruction instruction = block.Instructions[index];
                int inputDepth = stack.Length;
                if (terminated)
                {
                    diagnostics.Add(new("LIR002", "Instructions after a terminator are unreachable.", block.Label, index));
                    break;
                }

                if (!Apply(instruction, block.Label, index, ref stack, diagnostics))
                {
                    break;
                }

                maximumStackDepth = Math.Max(maximumStackDepth, instruction is ClrLirThrowIndexOutOfRange
                    ? inputDepth + 1 : stack.Length + (instruction is ClrLirFormatInt32 ? 1 : 0));
                if (maximumStackDepth > ushort.MaxValue)
                {
                    diagnostics.Add(new("LIR017", "Evaluation stack exceeds the ECMA-335 maxstack limit.", block.Label, index));
                    break;
                }

                switch (instruction)
                {
                    case ClrLirBranch branch:
                        terminated = true;
                        Propagate(branch.Target, stack, block.Label, index, byLabel, incoming, work, diagnostics);
                        break;
                    case ClrLirLeave leave:
                        terminated = true;
                        Propagate(leave.Target, stack, block.Label, index, byLabel, incoming, work, diagnostics);
                        break;
                    case ClrLirBranchTrue branchTrue:
                        Propagate(branchTrue.Target, stack, block.Label, index, byLabel, incoming, work, diagnostics);
                        break;
                    case ClrLirReturn:
                    case ClrLirThrowIndexOutOfRange:
                        terminated = true;
                        break;
                }
            }

            int blockIndex = Blocks.IndexOf(block);
            if (!terminated && blockIndex >= 0 && blockIndex + 1 < Blocks.Length)
            {
                ClrLirBlock next = Blocks[blockIndex + 1];
                Propagate(next.Label, stack, block.Label, block.Instructions.Length, byLabel, incoming, work, diagnostics);
            }

            if (!terminated && blockIndex == Blocks.Length - 1)
            {
                diagnostics.Add(new("LIR003", "Control flow can fall off the end of the method.", block.Label, block.Instructions.Length));
            }
        }

        if (stateLimitExceeded)
        {
            diagnostics.Add(new("LIR004", "Validation state limit exceeded.", null, -1));
        }

        foreach (ClrLirBlock block in Blocks)
        {
            if (!incoming.ContainsKey(block.Label))
            {
                diagnostics.Add(new(
                    "LIR014",
                    "Unreachable blocks are not supported by this LIR spike.",
                    block.Label,
                    -1));
            }
        }

        return new(diagnostics.ToImmutable()) { MaximumStackDepth = maximumStackDepth };

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("CLR LIR validation exceeded its time limit.");
        }
    }

    private bool Apply(
        ClrLirInstruction instruction,
        string blockLabel,
        int instructionIndex,
        ref ImmutableArray<ClrLirType> stack,
        ImmutableArray<ClrLirDiagnostic>.Builder diagnostics)
    {
        switch (instruction)
        {
            case ClrLirLoadInt32:
                stack = stack.Add(ClrLirType.I32);
                return true;
            case ClrLirLoadLocalAddress address:
                if (!TryGetLocal(address.Index, blockLabel, instructionIndex, diagnostics, out ClrLirLocal addressed)) return false;
                if (!addressed.Type.IsKnown || addressed.Type.Kind is ClrLirTypeKind.ByReference or ClrLirTypeKind.Void)
                {
                    diagnostics.Add(new("LIR020", "CLR managed pointers cannot refer to another managed pointer.", blockLabel, instructionIndex));
                    return false;
                }
                stack = stack.Add(ClrLirType.ByReference(addressed.Type, address.IsMutable));
                return true;
            case ClrLirFieldAddress address:
                if (!ValidateValueDefinition(address.Definition, blockLabel, instructionIndex, diagnostics)) return false;
                if ((uint)address.FieldIndex >= (uint)address.Definition.Fields.Length)
                {
                    diagnostics.Add(new("LIR018", "Value field index is out of range.", blockLabel, instructionIndex));
                    return false;
                }
                if (!TryPop(ClrLirType.ByReference(address.Definition.Type, address.IsMutable), ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                stack = stack.Add(ClrLirType.ByReference(address.Definition.Fields[address.FieldIndex].Type, address.IsMutable));
                return true;
            case ClrLirLoadIndirect indirect:
                if (!ValidateIndirectType(indirect.Type, blockLabel, instructionIndex, diagnostics)) return false;
                if (!TryPop(ClrLirType.ByReference(indirect.Type), ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                stack = stack.Add(indirect.Type);
                return true;
            case ClrLirStoreIndirect indirect:
                if (!ValidateIndirectType(indirect.Type, blockLabel, instructionIndex, diagnostics)) return false;
                return TryPop(indirect.Type, ref stack, blockLabel, instructionIndex, diagnostics) &&
                    TryPop(ClrLirType.ByReference(indirect.Type, mutable: true), ref stack, blockLabel, instructionIndex, diagnostics);
            case ClrLirReadOnlyReference readOnly:
                if (!ValidateIndirectType(readOnly.ElementType, blockLabel, instructionIndex, diagnostics)) return false;
                if (!TryPop(ClrLirType.ByReference(readOnly.ElementType), ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                stack = stack.Add(ClrLirType.ByReference(readOnly.ElementType));
                return true;
            case ClrLirThrowIndexOutOfRange:
                stack = [];
                return true;
            case ClrLirLoadBoolean:
                stack = stack.Add(ClrLirType.Bool);
                return true;
            case ClrLirLoadString:
                stack = stack.Add(ClrLirType.Text);
                return true;
            case ClrLirLoadArgument argument:
                if ((uint)argument.Index >= (uint)Parameters.Length)
                {
                    diagnostics.Add(new("LIR015", "Argument index is out of range.", blockLabel, instructionIndex));
                    return false;
                }

                stack = stack.Add(Parameters[argument.Index]);
                return true;
            case ClrLirDiscard discard:
                return TryPop(discard.Type, ref stack, blockLabel, instructionIndex, diagnostics);
            case ClrLirConstructValue construct:
                if (!ValidateValueDefinition(construct.Definition, blockLabel, instructionIndex, diagnostics)) return false;
                for (int index = construct.Definition.Fields.Length - 1; index >= 0; index--)
                    if (!TryPop(construct.Definition.Fields[index].Type, ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                stack = stack.Add(construct.Definition.Type);
                return true;
            case ClrLirReadField field:
                if (!ValidateValueDefinition(field.Definition, blockLabel, instructionIndex, diagnostics)) return false;
                if ((uint)field.FieldIndex >= (uint)field.Definition.Fields.Length)
                {
                    diagnostics.Add(new("LIR018", "Value field index is out of range.", blockLabel, instructionIndex));
                    return false;
                }
                if (!TryPop(field.Definition.Type, ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                stack = stack.Add(field.Definition.Fields[field.FieldIndex].Type);
                return true;
            case ClrLirFormatInt32:
                if (!TryPop(ClrLirType.I32, ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                stack = stack.Add(ClrLirType.Text);
                return true;
            case ClrLirBinary binary:
                bool comparison = binary.Operator is ClrLirBinaryOperator.Equal or ClrLirBinaryOperator.LessThan or ClrLirBinaryOperator.GreaterThan;
                bool validOperand = binary.OperandType == ClrLirType.I32 ||
                    (binary.OperandType == ClrLirType.Bool && binary.Operator is ClrLirBinaryOperator.Equal or ClrLirBinaryOperator.ExclusiveOr or ClrLirBinaryOperator.And or ClrLirBinaryOperator.Or);
                if (!Enum.IsDefined(binary.Operator) || !validOperand)
                {
                    diagnostics.Add(new("LIR016", "Invalid binary operator or operand type.", blockLabel, instructionIndex));
                    return false;
                }

                if (!TryPop(binary.OperandType, ref stack, blockLabel, instructionIndex, diagnostics) ||
                    !TryPop(binary.OperandType, ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                stack = stack.Add(comparison ? ClrLirType.Bool : binary.OperandType);
                return true;
            case ClrLirLoadLocal load:
                if (!TryGetLocal(load.Index, blockLabel, instructionIndex, diagnostics, out ClrLirLocal local)) return false;
                stack = stack.Add(local.Type);
                return true;
            case ClrLirStoreLocal store:
                if (!TryGetLocal(store.Index, blockLabel, instructionIndex, diagnostics, out ClrLirLocal destination) ||
                    !TryPop(destination.Type, ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                return true;
            case ClrLirCall call:
                if (!ValidateCallSite(call.Site, blockLabel, instructionIndex, diagnostics))
                {
                    return false;
                }

                for (int i = call.Site.ParameterTypes.Length - 1; i >= 0; i--)
                {
                    if (!TryPop(call.Site.ParameterTypes[i], ref stack, blockLabel, instructionIndex, diagnostics)) return false;
                }

                if (call.Site.ReturnType != ClrLirType.Void) stack = stack.Add(call.Site.ReturnType);
                return true;
            case ClrLirBranchTrue:
                return TryPop(ClrLirType.Bool, ref stack, blockLabel, instructionIndex, diagnostics);
            case ClrLirBranch:
                return true;
            case ClrLirLeave:
                return true;
            case ClrLirReturn:
                if (ReturnType == ClrLirType.Void)
                {
                    if (!stack.IsEmpty) diagnostics.Add(new("LIR005", "Void return requires an empty stack.", blockLabel, instructionIndex));
                    return stack.IsEmpty;
                }

                if (stack.Length != 1)
                {
                    diagnostics.Add(new("LIR005", $"Return requires exactly one {ReturnType} value.", blockLabel, instructionIndex));
                    return false;
                }

                return TryPop(ReturnType, ref stack, blockLabel, instructionIndex, diagnostics);
            default:
                diagnostics.Add(new("LIR006", $"Unsupported instruction '{instruction?.GetType().Name ?? "<null>"}'.", blockLabel, instructionIndex));
                return false;
        }
    }

    private static bool ValidateIndirectType(ClrLirType type, string blockLabel, int instructionIndex,
        ImmutableArray<ClrLirDiagnostic>.Builder diagnostics)
    {
        if (type.IsKnown && type.Kind is not (ClrLirTypeKind.Void or ClrLirTypeKind.ByReference)) return true;
        diagnostics.Add(new("LIR020", "Indirect operations require a closed non-void, non-reference referent.", blockLabel, instructionIndex));
        return false;
    }

    private bool TryGetLocal(int index, string blockLabel, int instructionIndex, ImmutableArray<ClrLirDiagnostic>.Builder diagnostics, out ClrLirLocal local)
    {
        if ((uint)index < (uint)Locals.Length)
        {
            local = Locals[index];
            return true;
        }

        local = null!;
        diagnostics.Add(new("LIR007", $"Local index {index} is out of range.", blockLabel, instructionIndex));
        return false;
    }

    private static bool TryPop(ClrLirType expected, ref ImmutableArray<ClrLirType> stack, string blockLabel, int instructionIndex, ImmutableArray<ClrLirDiagnostic>.Builder diagnostics)
    {
        if (stack.IsEmpty)
        {
            diagnostics.Add(new("LIR008", $"Expected {expected}, but the evaluation stack is empty.", blockLabel, instructionIndex));
            return false;
        }

        ClrLirType actual = stack[^1];
        stack = stack[..^1];
        if (actual != expected &&
            !(expected.Kind == ClrLirTypeKind.ByReference && !expected.IsMutable &&
              actual.Kind == ClrLirTypeKind.ByReference && actual.Name == expected.Name) &&
            !(expected == ClrLirType.Any && actual is { Kind: ClrLirTypeKind.Text or ClrLirTypeKind.Any }))
        {
            diagnostics.Add(new("LIR009", $"Expected {expected}, but found {actual}.", blockLabel, instructionIndex));
            return false;
        }

        return true;
    }

    private static void Propagate(
        string target,
        ImmutableArray<ClrLirType> stack,
        string blockLabel,
        int instructionIndex,
        Dictionary<string, ClrLirBlock> byLabel,
        Dictionary<string, ImmutableArray<ClrLirType>> incoming,
        Queue<ClrLirBlock> work,
        ImmutableArray<ClrLirDiagnostic>.Builder diagnostics)
    {
        if (!byLabel.TryGetValue(target, out ClrLirBlock? targetBlock))
        {
            diagnostics.Add(new("LIR010", $"Unknown branch target '{target}'.", blockLabel, instructionIndex));
            return;
        }

        if (incoming.TryGetValue(target, out ImmutableArray<ClrLirType> previous))
        {
            if (!previous.SequenceEqual(stack))
            {
                diagnostics.Add(new("LIR011", $"Incoming stack for '{target}' does not match the existing path.", blockLabel, instructionIndex));
            }

            return;
        }

        incoming[target] = stack;
        work.Enqueue(targetBlock);
    }

    private static void ValidateType(
        ClrLirType type,
        string role,
        string? blockLabel,
        int index,
        ImmutableArray<ClrLirDiagnostic>.Builder diagnostics,
        bool allowVoid)
    {
        if (!type.IsKnown)
        {
            diagnostics.Add(new(
                "LIR012",
                $"The {role} type '{type}' is not supported.",
                blockLabel,
                index));
        }
        else if (!allowVoid && type == ClrLirType.Void)
        {
            diagnostics.Add(new(
                "LIR013",
                $"The {role} cannot have type Void.",
                blockLabel,
                index));
        }
    }

    private static bool ValidateCallSite(
        ClrLirCallSite site,
        string blockLabel,
        int instructionIndex,
        ImmutableArray<ClrLirDiagnostic>.Builder diagnostics)
    {
        int diagnosticCount = diagnostics.Count;
        ValidateType(site.ReturnType, "call return", blockLabel, instructionIndex, diagnostics, allowVoid: true);
        for (var index = 0; index < site.ParameterTypes.Length; index++)
        {
            ValidateType(site.ParameterTypes[index], "call parameter", blockLabel, instructionIndex, diagnostics, allowVoid: false);
        }

        return diagnostics.Count == diagnosticCount;
    }

    private static bool ValidateValueDefinition(ClrLirValueType definition, string blockLabel,
        int instructionIndex, ImmutableArray<ClrLirDiagnostic>.Builder diagnostics)
    {
        int count = diagnostics.Count;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClrLirField field in definition.Fields)
        {
            ValidateType(field.Type, "field", blockLabel, instructionIndex, diagnostics, allowVoid: false);
            if (field.Type.Kind == ClrLirTypeKind.ByReference)
                diagnostics.Add(new("LIR020", "Ordinary CLR value layouts cannot contain managed reference fields.", blockLabel, instructionIndex));
            if (!names.Add(field.Name))
                diagnostics.Add(new("LIR019", "Value field names must be unique.", blockLabel, instructionIndex));
        }
        return count == diagnostics.Count;
    }
}

/// <summary>Validates a finite, closed set of aggregate layouts before metadata is emitted.</summary>
internal sealed class ClrLirValueTypeSet
{
    private readonly Dictionary<string, ClrLirValueType> byName = new(StringComparer.Ordinal);
    private readonly Action checkBudget;

    internal ClrLirValueTypeSet(IEnumerable<ClrLirValueType> definitions, Action checkBudget)
    {
        this.checkBudget = checkBudget;
        Definitions = [.. ClrLirLimits.CopyBounded(definitions, ClrLirLimits.MaximumValueTypes, nameof(definitions))
            .OrderBy(static definition => definition?.Name, StringComparer.Ordinal)];
        int fieldCount = 0;
        foreach (ClrLirValueType definition in Definitions)
        {
            checkBudget();
            ArgumentNullException.ThrowIfNull(definition);
            if (!byName.TryAdd(definition.Name, definition))
                throw new InvalidOperationException($"Duplicate CLR value type '{definition.Name}'.");
            fieldCount += definition.Fields.Length;
            if (fieldCount > 65536) throw new InvalidOperationException("CLR value layouts exceed the field budget.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ClrLirField field in definition.Fields)
                if (!names.Add(field.Name)) throw new InvalidOperationException($"Duplicate field '{field.Name}' in '{definition.Name}'.");
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var heights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ClrLirValueType definition in Definitions) Visit(definition, 0);

        long Visit(ClrLirValueType definition, int depth)
        {
            checkBudget();
            if (depth > 128) throw new InvalidOperationException("CLR value layouts exceed the nesting budget.");
            if (sizes.TryGetValue(definition.Name, out long known))
            {
                if (depth + heights[definition.Name] > 128)
                    throw new InvalidOperationException("CLR value layouts exceed the nesting budget.");
                return known;
            }
            if (!active.Add(definition.Name)) throw new InvalidOperationException($"Recursive CLR value layout '{definition.Name}'.");
            long size = 0;
            int height = 0;
            foreach (ClrLirField field in definition.Fields)
            {
                checkBudget();
                ValidateType(field.Type, allowVoid: false);
                if (field.Type.Kind == ClrLirTypeKind.ByReference)
                    throw new InvalidOperationException("Ordinary CLR value layouts cannot contain managed reference fields.");
                long fieldSize = field.Type.Kind == ClrLirTypeKind.Value ? Visit(byName[field.Type.Name!], depth + 1) : 8;
                if (field.Type.Kind == ClrLirTypeKind.Value) height = Math.Max(height, heights[field.Type.Name!] + 1);
                size += (fieldSize + 7) / 8 * 8;
                if (size > 1_048_576) throw new InvalidOperationException("A CLR value layout exceeds the size budget.");
            }
            active.Remove(definition.Name);
            sizes.Add(definition.Name, Math.Max(1, size));
            heights.Add(definition.Name, height);
            return Math.Max(1, size);
        }
    }

    internal ImmutableArray<ClrLirValueType> Definitions { get; }

    internal void ValidateMethod(ClrLirMethod method)
    {
        checkBudget();
        ValidateType(method.ReturnType, allowVoid: true);
        foreach (ClrLirType type in method.Parameters) ValidateType(type, allowVoid: false);
        foreach (ClrLirLocal local in method.Locals) ValidateType(local.Type, allowVoid: false);
        foreach (ClrLirBlock block in method.Blocks)
        {
            foreach (ClrLirInstruction instruction in block.Instructions)
            {
                checkBudget();
                switch (instruction)
                {
                    case ClrLirConstructValue construct: ValidateDefinition(construct.Definition); break;
                    case ClrLirReadField field: ValidateDefinition(field.Definition); break;
                    case ClrLirFieldAddress field: ValidateDefinition(field.Definition); break;
                    case ClrLirLoadIndirect load: ValidateType(load.Type, allowVoid: false); break;
                    case ClrLirStoreIndirect store: ValidateType(store.Type, allowVoid: false); break;
                    case ClrLirDiscard discard: ValidateType(discard.Type, allowVoid: false); break;
                    case ClrLirCall call:
                        ValidateType(call.Site.ReturnType, allowVoid: true);
                        foreach (ClrLirType type in call.Site.ParameterTypes) ValidateType(type, allowVoid: false);
                        break;
                }
            }
        }
    }

    private void ValidateType(ClrLirType type, bool allowVoid)
    {
        if (!type.IsKnown || !allowVoid && type == ClrLirType.Void)
            throw new InvalidOperationException($"Invalid closed CLR type '{type}'.");
        if (type.Kind == ClrLirTypeKind.Value && !byName.ContainsKey(type.Name!))
            throw new InvalidOperationException($"Unknown closed CLR value type '{type.Name}'.");
        if (type.TryGetByReferenceElement(out ClrLirType element)) ValidateType(element, allowVoid: false);
    }

    private void ValidateDefinition(ClrLirValueType definition)
    {
        if (!byName.TryGetValue(definition.Name, out ClrLirValueType? known) ||
            !definition.Fields.SequenceEqual(known.Fields))
            throw new InvalidOperationException($"Instruction layout does not match closed CLR value type '{definition.Name}'.");
    }
}
