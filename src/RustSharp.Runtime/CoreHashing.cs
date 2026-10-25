using System.Buffers.Binary;

namespace RustSharp.Runtime.Core;

public static class CoreHashing
{
    internal const ulong FnvOffsetBasis = 14695981039346656037UL;
    internal const ulong FnvPrime = 1099511628211UL;

    public static ulong Fnv1a(ReadOnlySpan<byte> bytes)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte value in bytes) hash = unchecked((hash ^ value) * FnvPrime);
        return hash;
    }

    public static ulong HashInt32(int value)
    {
        DefaultHasher hasher = new();
        hasher.WriteInt32(value);
        return hasher.Finish();
    }

    public static ulong HashString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        DefaultHasher hasher = new();
        hasher.WriteString(value);
        return hasher.Finish();
    }
}

/// <summary>Deterministic incremental hasher used by the frozen core-v1 profile.</summary>
/// <remarks>
/// The implementation deliberately exposes only fixed-width UTF-8 and little-endian
/// primitives. It is an AOT-safe Rust# runtime boundary and does not use the process-
/// randomized <see cref="HashCode"/> implementation.
/// </remarks>
public sealed class DefaultHasher
{
    private ulong _state = CoreHashing.FnvOffsetBasis;

    /// <summary>Returns the current stable hash without consuming the hasher.</summary>
    public ulong Finish() => _state;

    /// <summary>Appends bytes in order using the FNV-1a update rule.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) _state = unchecked((_state ^ value) * CoreHashing.FnvPrime);
    }

    public void WriteByte(byte value) => Write([value]);

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Write(bytes);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Write(bytes);
    }

    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Write(System.Text.Encoding.UTF8.GetBytes(value));
    }
}
