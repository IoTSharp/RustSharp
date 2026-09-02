using System.Collections.Immutable;

namespace RustSharp.CodeGen.IL;

/// <summary>
/// The ownership states needed by the P0 MIR spike.  Copy locals remain usable
/// after a move; owned locals are consumed and are dropped at scope exit.
/// </summary>
public enum OwnershipMirLocalKind
{
    Owned,
    Copy,
}

/// <summary>A local declared by an ownership MIR method.</summary>
public sealed record OwnershipMirLocal
{
    public OwnershipMirLocal(
        string name,
        OwnershipMirLocalKind kind = OwnershipMirLocalKind.Owned,
        bool hasDrop = false,
        bool initiallyInitialized = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Kind = kind;
        HasDrop = hasDrop;
        InitiallyInitialized = initiallyInitialized;
    }

    public string Name { get; }
    public OwnershipMirLocalKind Kind { get; }
    public bool HasDrop { get; }
    public bool InitiallyInitialized { get; }
}

/// <summary>One operation in the intentionally small ownership MIR.</summary>
public abstract record OwnershipMirInstruction
{
    public static OwnershipMirInstruction Move(string source, string destination) =>
        new OwnershipMirMove(source, destination);

    public static OwnershipMirInstruction BorrowShared(string owner, string reference) =>
        new OwnershipMirBorrowShared(owner, reference);

    public static OwnershipMirInstruction BorrowMutable(string owner, string reference) =>
        new OwnershipMirBorrowMutable(owner, reference);

    public static OwnershipMirInstruction EndBorrow(string reference) =>
        new OwnershipMirEndBorrow(reference);

    public static OwnershipMirInstruction Use(string local) => new OwnershipMirUse(local);

    public static OwnershipMirInstruction Write(string reference) => new OwnershipMirWrite(reference);

    public static OwnershipMirInstruction Drop(string local) => new OwnershipMirDrop(local);

    public static OwnershipMirInstruction ScopeExit() => new OwnershipMirScopeExit();

    public static OwnershipMirInstruction Return(string local) => new OwnershipMirReturn(local);
}

public sealed record OwnershipMirMove : OwnershipMirInstruction
{
    public OwnershipMirMove(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        Source = source;
        Destination = destination;
    }

    public string Source { get; }
    public string Destination { get; }
}

public sealed record OwnershipMirBorrowShared : OwnershipMirInstruction
{
    public OwnershipMirBorrowShared(string owner, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        Owner = owner;
        Reference = reference;
    }

    public string Owner { get; }
    public string Reference { get; }
}

public sealed record OwnershipMirBorrowMutable : OwnershipMirInstruction
{
    public OwnershipMirBorrowMutable(string owner, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        Owner = owner;
        Reference = reference;
    }

    public string Owner { get; }
    public string Reference { get; }
}

public sealed record OwnershipMirEndBorrow : OwnershipMirInstruction
{
    public OwnershipMirEndBorrow(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        Reference = reference;
    }

    public string Reference { get; }
}

public sealed record OwnershipMirUse : OwnershipMirInstruction
{
    public OwnershipMirUse(string local)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(local);
        Local = local;
    }

    public string Local { get; }
}

public sealed record OwnershipMirWrite : OwnershipMirInstruction
{
    public OwnershipMirWrite(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        Reference = reference;
    }

    public string Reference { get; }
}

public sealed record OwnershipMirDrop : OwnershipMirInstruction
{
    public OwnershipMirDrop(string local)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(local);
        Local = local;
    }

    public string Local { get; }
}

public sealed record OwnershipMirScopeExit : OwnershipMirInstruction;

/// <summary>Move a value out of a local when returning from a MIR method.</summary>
public sealed record OwnershipMirReturn : OwnershipMirInstruction
{
    public OwnershipMirReturn(string local)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(local);
        Local = local;
    }

    public string Local { get; }
}

public sealed record OwnershipMirDiagnostic(string Code, string Message, int InstructionIndex)
{
    public override string ToString() => $"{Code} ({InstructionIndex}): {Message}";
}

public sealed record OwnershipMirAnalysisResult(
    ImmutableArray<OwnershipMirDiagnostic> Diagnostics,
    ImmutableArray<string> DropOrder,
    ImmutableArray<string> Trace)
{
    public bool IsValid => Diagnostics.IsEmpty;

    public bool Succeeded => IsValid;
}

/// <summary>
/// A bounded, linear MIR model for proving ownership semantics before the full
/// parser/HIR/MIR pipeline exists.  It deliberately has no CLR-specific rules.
/// </summary>
public sealed class OwnershipMirProgram
{
    public const int MaximumLocals = 256;
    public const int MaximumInstructions = 4096;

    public OwnershipMirProgram(
        IEnumerable<OwnershipMirLocal> locals,
        IEnumerable<OwnershipMirInstruction> instructions)
    {
        ArgumentNullException.ThrowIfNull(locals);
        ArgumentNullException.ThrowIfNull(instructions);
        Locals = CopyBounded(locals, MaximumLocals, nameof(locals));
        Instructions = CopyBounded(instructions, MaximumInstructions, nameof(instructions));

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Locals.Length; index++)
        {
            OwnershipMirLocal? local = Locals[index];
            if (local is null)
            {
                throw new ArgumentException(
                    $"Local at index {index} must not be null.",
                    nameof(locals));
            }

            if (!names.Add(local.Name))
            {
                throw new ArgumentException($"Duplicate local name '{local.Name}'.", nameof(locals));
            }
        }

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

    public ImmutableArray<OwnershipMirLocal> Locals { get; }
    public ImmutableArray<OwnershipMirInstruction> Instructions { get; }

    /// <summary>Analyze and execute the ownership state transitions.</summary>
    public OwnershipMirAnalysisResult Analyze() => new Analyzer(this).Run();

    /// <summary>Alias used by callers that treat this spike as a compiler pass.</summary>
    public OwnershipMirAnalysisResult Check() => Analyze();

    private static ImmutableArray<T> CopyBounded<T>(IEnumerable<T> values, int maximum, string parameterName)
    {
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

    private sealed class Analyzer
    {
        private readonly OwnershipMirProgram _program;
        private readonly Dictionary<string, LocalState> _locals;
        private readonly Dictionary<string, BorrowState> _borrows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lastUse = new(StringComparer.Ordinal);
        private readonly ImmutableArray<OwnershipMirDiagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<OwnershipMirDiagnostic>();
        private readonly ImmutableArray<string>.Builder _dropOrder = ImmutableArray.CreateBuilder<string>();
        private readonly ImmutableArray<string>.Builder _trace = ImmutableArray.CreateBuilder<string>();
        private bool _scopeExited;

        public Analyzer(OwnershipMirProgram program)
        {
            _program = program;
            _locals = new Dictionary<string, LocalState>(StringComparer.Ordinal);
            foreach (OwnershipMirLocal local in program.Locals)
            {
                _locals.Add(local.Name, new LocalState(local, local.InitiallyInitialized));
            }

            for (var index = 0; index < program.Instructions.Length; index++)
            {
                switch (program.Instructions[index])
                {
                    case OwnershipMirUse use:
                        _lastUse[use.Local] = index;
                        break;
                    case OwnershipMirWrite write:
                        _lastUse[write.Reference] = index;
                        break;
                    case OwnershipMirReturn @return:
                        _lastUse[@return.Local] = index;
                        break;
                    case OwnershipMirEndBorrow endBorrow:
                        // An explicit end is itself a liveness boundary. Keep
                        // the borrow active until this instruction so the
                        // operation remains valid and observable in the trace.
                        _lastUse[endBorrow.Reference] = index;
                        break;
                }
            }
        }

        public OwnershipMirAnalysisResult Run()
        {
            for (var index = 0; index < _program.Instructions.Length; index++)
            {
                OwnershipMirInstruction instruction = _program.Instructions[index];
                if (_scopeExited)
                {
                    Add("OWN012", "Instructions after scope exit are unreachable.", index);
                    continue;
                }

                switch (instruction)
                {
                    case OwnershipMirMove move:
                        Move(move, index);
                        break;
                    case OwnershipMirBorrowShared borrow:
                        Borrow(borrow.Owner, borrow.Reference, mutable: false, index);
                        break;
                    case OwnershipMirBorrowMutable borrow:
                        Borrow(borrow.Owner, borrow.Reference, mutable: true, index);
                        break;
                    case OwnershipMirEndBorrow end:
                        EndBorrow(end.Reference, index);
                        break;
                    case OwnershipMirUse use:
                        Use(use.Local, index);
                        break;
                    case OwnershipMirWrite write:
                        Write(write.Reference, index);
                        break;
                    case OwnershipMirDrop drop:
                        Drop(drop.Local, index);
                        break;
                    case OwnershipMirScopeExit:
                        ExitScope(index);
                        break;
                    case OwnershipMirReturn @return:
                        Return(@return.Local, index);
                        break;
                    default:
                        Add("OWN000", $"Unsupported ownership instruction '{instruction.GetType().Name}'.", index);
                        break;
                }
            }

            if (!_scopeExited)
            {
                ExitScope(_program.Instructions.Length);
            }

            return new(
                _diagnostics.ToImmutable(),
                _dropOrder.ToImmutable(),
                _trace.ToImmutable());
        }

        private void Move(OwnershipMirMove move, int index)
        {
            ExpireDeadBorrows(index);
            if (!TryGetLocal(move.Source, index, out LocalState source) ||
                !TryGetLocal(move.Destination, index, out LocalState destination))
            {
                return;
            }

            if (!EnsureAvailable(source, move.Source, index) ||
                (source.Local.Kind == OwnershipMirLocalKind.Copy
                    ? !EnsureNoMutableBorrow(move.Source, index)
                    : !EnsureNoBorrow(move.Source, index)))
            {
                return;
            }

            if (destination.Initialized)
            {
                Add("OWN010", $"Move destination '{move.Destination}' is already initialized.", index);
                return;
            }

            destination.Initialized = true;
            destination.Moved = false;
            destination.HasDrop = source.HasDrop;
            if (source.Local.Kind != OwnershipMirLocalKind.Copy)
            {
                source.Initialized = false;
                source.Moved = true;
                source.MovedTo = move.Destination;
            }

            _trace.Add($"move {move.Source} -> {move.Destination}");
        }

        private void Borrow(string ownerName, string referenceName, bool mutable, int index)
        {
            ExpireDeadBorrows(index);
            if (!TryGetLocal(ownerName, index, out LocalState owner))
            {
                return;
            }

            if (_locals.ContainsKey(referenceName) || _borrows.ContainsKey(referenceName))
            {
                Add("OWN010", $"Borrow destination '{referenceName}' is already declared.", index);
                return;
            }

            if (!EnsureAvailable(owner, ownerName, index))
            {
                return;
            }

            foreach (BorrowState active in _borrows.Values)
            {
                if (!String.Equals(active.Owner, ownerName, StringComparison.Ordinal) && active.Active)
                {
                    continue;
                }

                if (active.Active && (mutable || active.Mutable))
                {
                    Add(
                        mutable ? "OWN002" : "OWN003",
                        mutable
                            ? $"Cannot mutably borrow '{ownerName}' while another borrow is active."
                            : $"Cannot shared-borrow '{ownerName}' while it has a mutable borrow.",
                        index);
                    return;
                }
            }

            _borrows.Add(referenceName, new BorrowState(ownerName, referenceName, mutable));
            _trace.Add(mutable ? $"borrow_mut {ownerName} as {referenceName}" : $"borrow_shared {ownerName} as {referenceName}");
        }

        private void EndBorrow(string referenceName, int index)
        {
            if (!_borrows.TryGetValue(referenceName, out BorrowState? borrow) || !borrow.Active)
            {
                Add("OWN011", $"Reference '{referenceName}' is not active.", index);
                return;
            }

            borrow.Active = false;
            _trace.Add($"end_borrow {referenceName}");
        }

        private void Use(string name, int index)
        {
            ExpireDeadBorrows(index);
            if (_borrows.TryGetValue(name, out BorrowState? borrow))
            {
                if (!borrow.Active)
                {
                    Add("OWN011", $"Reference '{name}' is not active.", index);
                    return;
                }

                _trace.Add($"use {name}");
                return;
            }

            if (!TryGetLocal(name, index, out LocalState local) || !EnsureAvailable(local, name, index))
            {
                return;
            }

            _trace.Add($"use {name}");
        }

        private void Write(string referenceName, int index)
        {
            ExpireDeadBorrows(index);
            if (!_borrows.TryGetValue(referenceName, out BorrowState? borrow) || !borrow.Active)
            {
                Add("OWN011", $"Reference '{referenceName}' is not active.", index);
                return;
            }

            if (!borrow.Mutable)
            {
                Add("OWN004", $"Cannot mutate through shared reference '{referenceName}'.", index);
                return;
            }

            _trace.Add($"write {referenceName}");
        }

        private void Drop(string name, int index)
        {
            ExpireDeadBorrows(index);
            if (!TryGetLocal(name, index, out LocalState local) || !EnsureAvailable(local, name, index))
            {
                return;
            }

            if (!EnsureNoBorrow(name, index))
            {
                return;
            }

            local.Initialized = false;
            local.Dropped = true;
            RecordDrop(local);
            _trace.Add($"drop {name}");
        }

        private void Return(string name, int index)
        {
            ExpireDeadBorrows(index);
            if (_borrows.TryGetValue(name, out BorrowState? borrow))
            {
                if (borrow.Active)
                {
                    Add("OWN005", $"Reference '{name}' escapes its owner scope.", index);
                }

                return;
            }

            if (!TryGetLocal(name, index, out LocalState local) || !EnsureAvailable(local, name, index))
            {
                return;
            }

            if (!EnsureNoBorrow(name, index))
            {
                return;
            }

            local.Initialized = false;
            local.Moved = true;
            _trace.Add($"return {name}");
        }

        private void ExitScope(int index)
        {
            foreach (BorrowState borrow in _borrows.Values)
            {
                if (borrow.Active)
                {
                    borrow.Active = false;
                    _trace.Add($"end_borrow {borrow.Reference}");
                }
            }

            for (var localIndex = _program.Locals.Length - 1; localIndex >= 0; localIndex--)
            {
                LocalState local = _locals[_program.Locals[localIndex].Name];
                if (!local.Initialized || local.Dropped)
                {
                    continue;
                }

                local.Initialized = false;
                local.Dropped = true;
                RecordDrop(local);
                _trace.Add($"drop {local.Local.Name}");
            }

            _scopeExited = true;
            _trace.Add("scope_exit");
        }

        private bool EnsureAvailable(LocalState local, string name, int index)
        {
            if (local.Initialized && !local.Moved && !local.Dropped)
            {
                return true;
            }

            string detail = local.MovedTo is null
                ? $"Local '{name}' is not initialized."
                : $"Local '{name}' was moved to '{local.MovedTo}'.";
            Add("OWN001", detail, index);
            return false;
        }

        private bool EnsureNoBorrow(string ownerName, int index)
        {
            foreach (BorrowState borrow in _borrows.Values)
            {
                if (borrow.Active && String.Equals(borrow.Owner, ownerName, StringComparison.Ordinal))
                {
                    Add("OWN009", $"Cannot move or drop '{ownerName}' while a borrow is active.", index);
                    return false;
                }
            }

            return true;
        }

        private bool EnsureNoMutableBorrow(string ownerName, int index)
        {
            foreach (BorrowState borrow in _borrows.Values)
            {
                if (borrow.Active && borrow.Mutable && String.Equals(borrow.Owner, ownerName, StringComparison.Ordinal))
                {
                    Add("OWN009", $"Cannot copy '{ownerName}' while a mutable borrow is active.", index);
                    return false;
                }
            }

            return true;
        }

        private bool TryGetLocal(string name, int index, out LocalState local)
        {
            if (_locals.TryGetValue(name, out LocalState? found))
            {
                local = found;
                return true;
            }

            local = null!;
            Add("OWN007", $"Unknown local '{name}'.", index);
            return false;
        }

        private void RecordDrop(LocalState local)
        {
            if (local.HasDrop)
            {
                _dropOrder.Add(local.Local.Name);
            }
        }

        private void ExpireDeadBorrows(int instructionIndex)
        {
            foreach (BorrowState borrow in _borrows.Values)
            {
                if (!borrow.Active ||
                    (_lastUse.TryGetValue(borrow.Reference, out int lastUse) && lastUse >= instructionIndex))
                {
                    continue;
                }

                borrow.Active = false;
                _trace.Add($"end_borrow {borrow.Reference} (nll)");
            }
        }

        private void Add(string code, string message, int index) => _diagnostics.Add(new(code, message, index));

        private sealed class LocalState(OwnershipMirLocal local, bool initialized)
        {
            public OwnershipMirLocal Local { get; } = local;
            public bool Initialized { get; set; } = initialized;
            public bool Moved { get; set; }
            public bool Dropped { get; set; }
            public bool HasDrop { get; set; } = local.HasDrop;
            public string? MovedTo { get; set; }
        }

        private sealed class BorrowState(string owner, string reference, bool mutable)
        {
            public string Owner { get; } = owner;
            public string Reference { get; } = reference;
            public bool Mutable { get; } = mutable;
            public bool Active { get; set; } = true;
        }
    }
}
