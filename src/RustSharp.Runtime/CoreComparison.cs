namespace RustSharp.Runtime.Core;

public enum Ordering { Less = -1, Equal = 0, Greater = 1 }

public static class CoreComparison
{
    public static Ordering Compare<T>(T left, T right) => FromInt(Comparer<T>.Default.Compare(left, right));

    public static Option<Ordering> PartialCompare(double left, double right)
    {
        if (double.IsNaN(left) || double.IsNaN(right)) return Option<Ordering>.None;
        return Option<Ordering>.Some(left < right ? Ordering.Less : left > right ? Ordering.Greater : Ordering.Equal);
    }

    public static bool Equal<T>(T left, T right) => EqualityComparer<T>.Default.Equals(left, right);

    private static Ordering FromInt(int value) => value < 0 ? Ordering.Less : value > 0 ? Ordering.Greater : Ordering.Equal;
}
