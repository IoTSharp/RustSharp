namespace InteropFixtures;

public static class Math
{
    public static int Add(int left, int right) => checked(left + right);
    public static bool Add(bool left, bool right) => left && right;
    internal static int Hidden(int value) => value;
    public static int Unlisted(int value) => value;
}

public static class Algorithms
{
    public static T Identity<T>(T value) where T : struct, IComparable<T> => value;
}

public sealed class Counter(int value)
{
    public int Value { get; } = value;
    public int ReadInstance() => Value;
}

public static class CounterAdapters
{
    public static int Read(Counter counter) => counter.Value;
    public static void Release(Counter counter) => ArgumentNullException.ThrowIfNull(counter);
}
