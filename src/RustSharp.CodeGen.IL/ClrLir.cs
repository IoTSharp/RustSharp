using System.Collections.Immutable;

namespace RustSharp.CodeGen.IL;

internal static class ClrLirLimits
{
    public const int MaximumParameters = 256;
    public const int MaximumLocals = 256;
    public const int MaximumBlocks = 4096;
    public const int MaximumInstructionsPerBlock = 65536;
    public const int MaximumInstructions = 1_000_000;

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

/// <summary>Primitive types understood by the CLR LIR validation spike.</summary>
public enum ClrLirTypeKind
{
    Void,
    I32,
    Bool,
    Text,
    Any,
}

/// <summary>A small, value-typed CLR type descriptor used by LIR instructions.</summary>
public readonly record struct ClrLirType(ClrLirTypeKind Kind)
{
    public static ClrLirType Void => new(ClrLirTypeKind.Void);
    public static ClrLirType I32 => new(ClrLirTypeKind.I32);
    public static ClrLirType Bool => new(ClrLirTypeKind.Bool);
    public static ClrLirType Text => new(ClrLirTypeKind.Text);
    public static ClrLirType Any => new(ClrLirTypeKind.Any);

#pragma warning disable CA1720 // These aliases intentionally mirror CLR type names.
    public static ClrLirType Int32 => I32;
    public static ClrLirType Boolean => Bool;
    public static ClrLirType String => Text;
    public static ClrLirType Object => Any;
#pragma warning restore CA1720

    public override string ToString() => Kind.ToString();

    internal bool IsKnown => Kind is
        ClrLirTypeKind.Void or
        ClrLirTypeKind.I32 or
        ClrLirTypeKind.Bool or
        ClrLirTypeKind.Text or
        ClrLirTypeKind.Any;
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
public sealed record ClrLirStoreLocal(int Index) : ClrLirInstruction;
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
        IEnumerable<ClrLirBlock> blocks)
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
    public ClrLirType ReturnType { get; }
    public ImmutableArray<ClrLirType> Parameters { get; }
    public ImmutableArray<ClrLirLocal> Locals { get; }
    public ImmutableArray<ClrLirBlock> Blocks { get; }

    public ClrLirValidationResult Validate()
    {
        var diagnostics = ImmutableArray.CreateBuilder<ClrLirDiagnostic>();
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
                ClrLirInstruction instruction = block.Instructions[index];
                if (terminated)
                {
                    diagnostics.Add(new("LIR002", "Instructions after a terminator are unreachable.", block.Label, index));
                    break;
                }

                if (!Apply(instruction, block.Label, index, ref stack, diagnostics))
                {
                    break;
                }

                switch (instruction)
                {
                    case ClrLirBranch branch:
                        terminated = true;
                        Propagate(branch.Target, stack, block.Label, index, byLabel, incoming, work, diagnostics);
                        break;
                    case ClrLirBranchTrue branchTrue:
                        Propagate(branchTrue.Target, stack, block.Label, index, byLabel, incoming, work, diagnostics);
                        break;
                    case ClrLirReturn:
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

        return new(diagnostics.ToImmutable());
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
            case ClrLirLoadBoolean:
                stack = stack.Add(ClrLirType.Bool);
                return true;
            case ClrLirLoadString:
                stack = stack.Add(ClrLirType.Text);
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
}
