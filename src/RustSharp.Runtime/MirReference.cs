using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

#pragma warning disable CA2201 // Generated Rust indexing follows CLR array bounds exception semantics.

namespace RustSharp.Runtime;

/// <summary>Generated value accessors. Implementations return fresh value copies on writes.</summary>
public interface IMirValue
{
    object? ReadField(int index);
    object WithField(int index, object? value);
}

/// <summary>GC-owned MIR storage and bounded projection paths, usable by NativeAOT.</summary>
public static class MirReference
{
    private const int MaximumDepth = 128;
    public const int MaximumConsumedDropPlaces = 16384;
    private const int MaximumDropStateOperations = MaximumConsumedDropPlaces * MaximumDepth * 2;
    private static readonly TimeSpan DropStateTimeout = TimeSpan.FromSeconds(5);
    private sealed class Cell(object? value)
    {
        internal object? Value = value;
        internal HashSet<string>? ConsumedDropPlaces;
    }
    private sealed record Reference(Cell Owner, int[] Path);
    private sealed record Slice(Reference Owner, int Start, int Length);
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, object>> Promotions = new();

    private sealed class DropStateBudget(CancellationToken cancellationToken)
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private int operations;
        public void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++operations > MaximumDropStateOperations || Stopwatch.GetElapsedTime(started) >= DropStateTimeout)
                throw new InvalidOperationException("MIR reference Drop state exceeded its work or time limit.");
        }
    }

    public static object Create(object? value) => new Reference(new Cell(value), []);

    public static object Promote(object? value, string identity, object programIdentity) =>
        Promotions.GetValue(programIdentity, static _ => new(StringComparer.Ordinal))
            .GetOrAdd(identity, _ => Create(value));

    public static object? Read(object reference)
    {
        Reference target = (Reference)reference;
        object? value = target.Owner.Value;
        foreach (int index in target.Path)
            value = ((IMirValue)value!).ReadField(index);
        return value;
    }

    public static bool HasVariant(object reference, int discriminant) =>
        Read(reference) is IMirValue value && value.ReadField(0) is int actual && actual == discriminant;

    /// <summary>Reads shared consumption of this exact place, without reading its stored value or ancestors.</summary>
    public static bool IsDropLive(object reference) => IsDropLive(reference, CancellationToken.None);

    public static bool IsDropLive(object reference, CancellationToken cancellationToken)
    {
        Reference target = (Reference)reference;
        var budget = new DropStateBudget(cancellationToken);
        budget.Step();
        return target.Owner.ConsumedDropPlaces is not { } consumed ||
            !consumed.Contains(DropKey(target, budget));
    }

    /// <summary>Consumes only this place; an outer destructor's fields keep their independent obligations.</summary>
    public static void ConsumeDrop(object reference) => ConsumeDrop(reference, CancellationToken.None);

    public static void ConsumeDrop(object reference, CancellationToken cancellationToken)
    {
        Reference target = (Reference)reference;
        var budget = new DropStateBudget(cancellationToken);
        string key = DropKey(target, budget);
        HashSet<string> consumed = target.Owner.ConsumedDropPlaces ??= new(StringComparer.Ordinal);
        if (consumed.Contains(key)) return;
        if (consumed.Count >= MaximumConsumedDropPlaces)
            throw new InvalidOperationException("MIR reference consumed Drop places exceeded their record limit.");
        budget.Step();
        consumed.Add(key);
    }

    public static void Write(object reference, object? value) => Write(reference, value, CancellationToken.None);

    public static void Write(object reference, object? value, CancellationToken cancellationToken) =>
        WriteCore(reference, value, new DropStateBudget(cancellationToken));

    private static void WriteCore(object reference, object? value, DropStateBudget budget)
    {
        budget.Step();
        if (reference is Slice slice)
        {
            if ((uint)slice.Length > 256) throw new InvalidOperationException("A fixed-array view exceeds its field limit.");
            IMirValue array = (IMirValue)value!;
            for (int index = 0; index < slice.Length; index++)
            {
                budget.Step();
                WriteCore(Field(slice.Owner, checked(slice.Start + index)), array.ReadField(index), budget);
            }
            return;
        }
        Reference target = (Reference)reference;
        var parents = new IMirValue[target.Path.Length];
        object? current = target.Owner.Value;
        for (int index = 0; index < target.Path.Length; index++)
        {
            budget.Step();
            IMirValue parent = (IMirValue)current!;
            parents[index] = parent;
            current = parent.ReadField(target.Path[index]);
        }
        for (int index = target.Path.Length - 1; index >= 0; index--)
        {
            budget.Step();
            value = parents[index].WithField(target.Path[index], value);
        }
        // Prepare the new generation state before committing either object.
        // A failed projection/write or cancelled scan leaves both unchanged.
        HashSet<string>? nextConsumed = ResetWrittenDropState(target, budget);
        budget.Step();
        target.Owner.Value = value;
        target.Owner.ConsumedDropPlaces = nextConsumed;
    }

    private static string DropKey(Reference target, DropStateBudget budget)
    {
        budget.Step();
        if (target.Path.Length > MaximumDepth) throw new InvalidOperationException("MIR reference projection depth exceeded.");
        if (target.Path.Length == 0) return string.Empty;
        var key = new StringBuilder(target.Path.Length * 12);
        foreach (int index in target.Path)
        {
            budget.Step();
            key.Append(index.ToString(CultureInfo.InvariantCulture)).Append('/');
        }
        return key.ToString();
    }

    private static HashSet<string>? ResetWrittenDropState(Reference target, DropStateBudget budget)
    {
        budget.Step();
        if (target.Owner.ConsumedDropPlaces is not { Count: > 0 } consumed || target.Path.Length == 0)
            return null;
        string written = DropKey(target, budget);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (string place in consumed)
        {
            budget.Step();
            // The trailing separator distinguishes fields 1 and 10.
            if (!place.StartsWith(written, StringComparison.Ordinal)) retained.Add(place);
        }
        return retained.Count == 0 ? null : retained;
    }

    public static object Field(object reference, int index)
    {
        Reference target = (Reference)reference;
        if (target.Path.Length >= MaximumDepth) throw new InvalidOperationException("MIR reference projection depth exceeded.");
        var path = new int[target.Path.Length + 1];
        target.Path.CopyTo(path, 0);
        path[^1] = index;
        return new Reference(target.Owner, path);
    }

    public static object ArrayIndex(object reference, int index, int length)
    {
        if ((uint)index >= (uint)length) throw new IndexOutOfRangeException();
        if (reference is Slice) return SliceIndex(reference, index);
        return Field(reference, index);
    }

    public static object ReadArray(object reference, object emptyArray)
    {
        if (reference is not Slice slice) return Read(reference)!;
        if ((uint)slice.Length > 256) throw new InvalidOperationException("A fixed-array view exceeds its field limit.");
        IMirValue value = (IMirValue)emptyArray;
        for (int index = 0; index < slice.Length; index++)
            value = (IMirValue)value.WithField(index, Read(Field(slice.Owner, checked(slice.Start + index))));
        return value;
    }

    public static object MakeSlice(object reference, int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (reference is Slice view)
        {
            if (start > view.Length || length > view.Length - start) throw new IndexOutOfRangeException();
            return new Slice(view.Owner, checked(view.Start + start), length);
        }
        return new Slice((Reference)reference, start, length);
    }

    public static object Subslice(object reference, int start, int end, bool inclusive)
    {
        Slice source = (Slice)reference;
        if (inclusive) end = checked(end + 1);
        if (start < 0 || end < start || end > source.Length) throw new IndexOutOfRangeException();
        return new Slice(source.Owner, checked(source.Start + start), end - start);
    }

    public static object PatternSubslice(object reference, int prefix, int suffix)
    {
        Slice source = (Slice)reference;
        if (prefix < 0 || suffix < 0 || prefix > source.Length || suffix > source.Length - prefix)
            throw new IndexOutOfRangeException();
        return new Slice(source.Owner, checked(source.Start + prefix), source.Length - prefix - suffix);
    }

    public static object SliceIndex(object reference, int index)
    {
        Slice source = (Slice)reference;
        if ((uint)index >= (uint)source.Length) throw new IndexOutOfRangeException();
        return Field(source.Owner, checked(source.Start + index));
    }

    public static object FromEndIndex(object reference, int distance, int minimumLength, int staticLength)
    {
        int length = reference is Slice source ? source.Length : staticLength;
        if (distance <= 0 || minimumLength < distance || length < minimumLength)
            throw new IndexOutOfRangeException();
        return ArrayIndex(reference, length - distance, length);
    }

    public static int SliceLength(object reference) => ((Slice)reference).Length;
}
