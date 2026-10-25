using System.Text;
using RustSharp.Runtime.Core;

namespace RustSharp.Tests;

/// <summary>Focused coverage for the core-v1 Hasher and IntoIterator members.</summary>
/// <remarks>The root test registration remains owned by the supervisor because it is a shared file.</remarks>
internal static class P2CoreComparisonIteratorExtensionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.04 DefaultHasher combines segmented UTF-8 writes", SegmentedStringHashAsync),
        new("P2-01.04 DefaultHasher writes fixed-width integers", FixedWidthHashAsync),
        new("P2-01.04 DefaultHasher finish is stable and non-consuming", StableFinishAsync),
        new("P2-01.05 IntoIterator preserves source order", IntoIteratorOrderAsync),
        new("P2-01.05 IntoIterator remains lazy", IntoIteratorLazyAsync),
        new("P2-01.05 IntoIterator rejects a null source", IntoIteratorNullAsync),
    ];

    private static Task SegmentedStringHashAsync()
    {
        DefaultHasher hasher = new();
        hasher.WriteString("rust");
        hasher.WriteString("锐");
        hasher.WriteString("sharp");
        AssertEx.Equal(CoreHashing.HashString("rust锐sharp"), hasher.Finish());
        return Task.CompletedTask;
    }

    private static Task FixedWidthHashAsync()
    {
        DefaultHasher hasher = new();
        hasher.WriteInt32(1);
        hasher.WriteUInt32(2);
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[..4], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], 2);
        AssertEx.Equal(CoreHashing.Fnv1a(bytes), hasher.Finish());
        return Task.CompletedTask;
    }

    private static Task StableFinishAsync()
    {
        DefaultHasher hasher = new();
        hasher.Write(Encoding.UTF8.GetBytes("stable"));
        ulong first = hasher.Finish();
        AssertEx.Equal(first, hasher.Finish());
        return Task.CompletedTask;
    }

    private static Task IntoIteratorOrderAsync()
    {
        int[] source = [1, 2, 3];
        AssertEx.True(ReferenceEquals(source, CoreIterator.IntoIterator(source)));
        AssertEx.True(CoreIterator.IntoIterator(source).SequenceEqual(source));
        return Task.CompletedTask;
    }

    private static Task IntoIteratorLazyAsync()
    {
        int calls = 0;
        IEnumerable<int> source = Iterator();
        IEnumerable<int> adapted = CoreIterator.IntoIterator(source);
        AssertEx.Equal(0, calls);
        AssertEx.Equal(7, adapted.Single());
        AssertEx.Equal(1, calls);
        return Task.CompletedTask;

        IEnumerable<int> Iterator()
        {
            calls++;
            yield return 7;
        }
    }

    private static Task IntoIteratorNullAsync()
    {
        AssertEx.Throws<ArgumentNullException>(() => CoreIterator.IntoIterator<int>(null!));
        return Task.CompletedTask;
    }
}
