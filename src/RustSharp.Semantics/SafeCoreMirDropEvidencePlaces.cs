using System.Diagnostics;
using System.Globalization;

namespace RustSharp.Semantics;

/// <summary>Resolves complete ownership display places against validated typed MIR.</summary>
internal static partial class SafeCoreMirDropEvidencePlaces
{
    internal sealed record Resolved(SafeCoreOwnershipLocal Root, string Key, SafeCoreType Type,
        bool Droppable, bool ScopeOwned = true);

    internal static Resolved? Resolve(SafeCoreMirProgram mir, SafeCoreMirFunction function,
        Dictionary<string, SafeCoreOwnershipLocal> locals, string display,
        Stopwatch clock, TimeSpan timeout, int maximumOperations, ref int operations, CancellationToken cancellationToken) =>
        ResolveCore(mir, function, locals, display, clock, timeout, maximumOperations, ref operations, 0, cancellationToken);

    private static Resolved? ResolveCore(SafeCoreMirProgram mir, SafeCoreMirFunction function,
        Dictionary<string, SafeCoreOwnershipLocal> locals, string display,
        Stopwatch clock, TimeSpan timeout, int maximumOperations, ref int operations, int storageDepth,
        CancellationToken cancellationToken)
    {
        Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
        if (storageDepth >= 128)
            throw new SafeCoreMirLimitException("Drop evidence storage-place nesting exceeds 128.");
        string name = display.EndsWith(" (scope)", StringComparison.Ordinal) ? display[..^8] : display;
        if (name.Length is 0 or > 16384) return null;
        bool exactRoot = locals.TryGetValue(name, out SafeCoreOwnershipLocal? root);
        int projectionStart = exactRoot ? -1 : name.IndexOfAny(['.', '[']);
        string rootName = projectionStart < 0 ? name : name[..projectionStart];
        if (!exactRoot && !locals.TryGetValue(rootName, out root)) return null;
        if (root is null || root.Id < 0) return null;
        bool rootScopeOwned = true;
        if (root.Id < function.Locals.Count)
        {
            SafeCoreMirLocal typedRoot = function.Locals[root.Id];
            if (typedRoot.Id != root.Id || typedRoot.Name != root.Name || typedRoot.Type != root.Type) return null;
        }
        else
        {
            if (root.StoragePlace is not { } storage || storage.LocalId < 0 || storage.LocalId >= function.Locals.Count)
                return null;
            Resolved? backing = ResolveCore(mir, function, locals, storage.ToString(),
                clock, timeout, maximumOperations, ref operations, storageDepth + 1, cancellationToken);
            if (backing?.Type != root.Type) return null;
            rootScopeOwned = backing.ScopeOwned;
        }
        SafeCoreType type = root.Type;
        bool borrowedReferent = !rootScopeOwned;
        int position = rootName.Length;
        int projectionCount = 0;
        while (position < name.Length && projectionCount < 128)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            projectionCount++;
            if (name.AsSpan(position).StartsWith(".*", StringComparison.Ordinal))
            {
                if (type.Kind != SafeCoreSemanticTypeKind.Reference || type.ElementType is null) return null;
                // Ordinary borrow places can participate in use/write flags,
                // but only checked destructor self owns destruction through
                // a reference. A later dereference again reaches borrowed data.
                bool destructorSelf = projectionCount == 1 && function.IsDestructor && root.Id == 0 &&
                    type.IsMutable && function.Locals.Count > 0 &&
                    function.Locals[0].Kind == SafeCoreMirLocalKind.Parameter && function.Locals[0].Type == type;
                borrowedReferent |= !destructorSelf;
                type = type.ElementType!;
                position += 2;
            }
            else if (name.AsSpan(position).StartsWith(".tuple[", StringComparison.Ordinal))
            {
                int close = name.IndexOf(']', position + 7);
                if (close < 0 || type.Kind != SafeCoreSemanticTypeKind.Tuple ||
                    !int.TryParse(name.AsSpan(position + 7, close - position - 7), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int index) || index >= type.Elements.Count)
                    return null;
                type = type.Elements[index];
                position = close + 1;
            }
            else if (name[position] == '.')
            {
                int start = ++position;
                int delimiter = name.IndexOfAny(['.', '['], start);
                position = delimiter < 0 ? name.Length : delimiter;
                if (position == start || type.Kind != SafeCoreSemanticTypeKind.Adt) return null;
                SafeCoreMirAdtLayout? layout = null;
                foreach (SafeCoreMirAdtLayout candidate in mir.AdtLayouts)
                {
                    Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                    if (candidate.Type == type) { layout = candidate; break; }
                }
                if (layout is null) return null;
                SafeCoreMirAdtField? field = null;
                foreach (SafeCoreMirAdtField candidate in layout.Fields)
                {
                    Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                    if (name.AsSpan(start, position - start).SequenceEqual(candidate.Name)) { field = candidate; break; }
                }
                if (field is null) return null;
                type = field.Type;
            }
            else if (name[position] == '[')
            {
                int close = name.IndexOf(']', position + 1);
                if (close < 0 || type.Kind is not (SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice))
                    return null;
                long? length = type.Length;
                ReadOnlySpan<char> indexText = name.AsSpan(position + 1, close - position - 1);
                if (indexText.StartsWith("local:", StringComparison.Ordinal))
                {
                    if (!int.TryParse(indexText[6..], NumberStyles.None, CultureInfo.InvariantCulture, out int localId) ||
                        localId >= function.Locals.Count || !function.Locals[localId].Type.IsInteger) return null;
                }
                else if (indexText.StartsWith("len-", StringComparison.Ordinal))
                {
                    int separator = indexText.IndexOf(";min=", StringComparison.Ordinal);
                    if (separator < 5 || !int.TryParse(indexText[4..separator], NumberStyles.None, CultureInfo.InvariantCulture, out int offset) ||
                        !int.TryParse(indexText[(separator + 5)..], NumberStyles.None, CultureInfo.InvariantCulture, out int minimum) ||
                        offset < 1 || minimum < offset || length is long fromEndCount && fromEndCount < minimum) return null;
                }
                else if (indexText.IndexOf("..", StringComparison.Ordinal) is int range && range >= 0)
                {
                    if (!int.TryParse(indexText[..range], NumberStyles.None, CultureInfo.InvariantCulture, out int start)) return null;
                    ReadOnlySpan<char> endText = indexText[(range + 2)..];
                    if (endText.StartsWith("len-", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(endText[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int omitted) ||
                            length is long sliceCount && start + (long)omitted > sliceCount) return null;
                        type = length is long known ? SafeCoreType.Array(type.ElementType!, known - start - omitted, cancellationToken)
                            : SafeCoreType.Slice(type.ElementType!, cancellationToken);
                    }
                    else
                    {
                        if (!int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out int end) ||
                            end < start || length is long rangeCount && end > rangeCount) return null;
                        type = SafeCoreType.Array(type.ElementType!, end - (long)start, cancellationToken);
                    }
                    position = close + 1;
                    continue;
                }
                else if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                    length is long indexCount && index >= indexCount) return null;
                type = type.ElementType!;
                position = close + 1;
            }
            else return null;
        }
        if (position != name.Length) return null;
        string key = root.Name + name[rootName.Length..];
        bool hasDrop = projectionCount == 0 && root.HasDrop || RequiresDrop(mir, type,
            clock, timeout, maximumOperations, ref operations, 0, cancellationToken);
        bool droppable = hasDrop && (!borrowedReferent || HasCanonicalBorrowedReplacementDrop(mir, function, key,
            null, clock, timeout, maximumOperations, ref operations, cancellationToken));
        return new(root, key, type, droppable, !borrowedReferent);
    }

    private static bool RequiresDrop(SafeCoreMirProgram mir, SafeCoreType type,
        Stopwatch clock, TimeSpan timeout, int maximumOperations, ref int operations, int depth, CancellationToken cancellationToken)
    {
        Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
        if (depth >= 128) throw new SafeCoreMirLimitException("Drop evidence type nesting exceeds 128.");
        foreach (SafeCoreMirFunction function in mir.Functions)
        {
            Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
            if (function.IsDestructor && function.Locals.Count > 0 &&
                function.Locals[0].Kind == SafeCoreMirLocalKind.Parameter &&
                function.Locals[0].Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                function.Locals[0].Type.ElementType == type) return true;
        }
        if (type.Kind == SafeCoreSemanticTypeKind.Adt)
        {
            foreach (SafeCoreMirAdtLayout layout in mir.AdtLayouts)
            {
                Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                if (layout.Type != type) continue;
                foreach (SafeCoreMirAdtField field in layout.Fields)
                {
                    Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                    if (RequiresDrop(mir, field.Type, clock, timeout, maximumOperations,
                        ref operations, depth + 1, cancellationToken)) return true;
                }
                return false;
            }
        }
        else if (type.Kind == SafeCoreSemanticTypeKind.Array)
            return type.Length > 0 && RequiresDrop(mir, type.ElementType!, clock, timeout,
                maximumOperations, ref operations, depth + 1, cancellationToken);
        else if (type.Kind == SafeCoreSemanticTypeKind.Tuple)
            foreach (SafeCoreType element in type.Elements)
            {
                Step(clock, timeout, maximumOperations, ref operations, cancellationToken);
                if (RequiresDrop(mir, element, clock, timeout, maximumOperations,
                    ref operations, depth + 1, cancellationToken)) return true;
            }
        return false;
    }

    private static void Step(Stopwatch clock, TimeSpan timeout,
        int maximumOperations, ref int operations, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++operations > maximumOperations || clock.Elapsed >= timeout)
            throw new SafeCoreMirLimitException("Drop evidence place resolution exceeded its operation/time budget.");
    }
}
