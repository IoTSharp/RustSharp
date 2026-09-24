using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
    private sealed class Cell(object? value) { internal object? Value = value; }
    private sealed record Reference(Cell Owner, int[] Path);
    private sealed record Slice(Reference Owner, int Start, int Length);
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, object>> Promotions = new();

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

    public static void Write(object reference, object? value)
    {
        if (reference is Slice slice)
        {
            if ((uint)slice.Length > 256) throw new InvalidOperationException("A fixed-array view exceeds its field limit.");
            IMirValue array = (IMirValue)value!;
            for (int index = 0; index < slice.Length; index++)
                Write(Field(slice.Owner, checked(slice.Start + index)), array.ReadField(index));
            return;
        }
        Reference target = (Reference)reference;
        var parents = new IMirValue[target.Path.Length];
        object? current = target.Owner.Value;
        for (int index = 0; index < target.Path.Length; index++)
        {
            IMirValue parent = (IMirValue)current!;
            parents[index] = parent;
            current = parent.ReadField(target.Path[index]);
        }
        for (int index = target.Path.Length - 1; index >= 0; index--)
            value = parents[index].WithField(target.Path[index], value);
        target.Owner.Value = value;
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
