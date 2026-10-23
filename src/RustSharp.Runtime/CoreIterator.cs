namespace RustSharp.Runtime.Core;

public static class CoreIterator
{
    public static IEnumerable<T> Empty<T>() => Array.Empty<T>();
    public static IEnumerable<T> Once<T>(T value) { yield return value; }
    public static IEnumerable<TResult> Map<T, TResult>(IEnumerable<T> source, Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mapper);
        foreach (T value in source) yield return mapper(value);
    }
}
