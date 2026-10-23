using System.Buffers.Binary;

namespace RustSharp.Runtime.Core;

public static class CoreHashing
{
    public static ulong Fnv1a(ReadOnlySpan<byte> bytes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte value in bytes) hash = unchecked((hash ^ value) * prime);
        return hash;
    }

    public static ulong HashInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Fnv1a(bytes);
    }

    public static ulong HashString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Fnv1a(System.Text.Encoding.UTF8.GetBytes(value));
    }
}
