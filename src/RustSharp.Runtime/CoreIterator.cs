namespace RustSharp.Runtime.Core;

public static class CoreIterator
{
    public static IEnumerable<T> Empty<T>() => Array.Empty<T>();
    public static IEnumerable<T> Once<T>(T value) { yield return value; }

    /// <summary>Adapts a source value to the core <c>IntoIterator</c> boundary.</summary>
    /// <remarks>The source is returned unchanged so enumeration remains lazy and single-owner.</remarks>
    public static IEnumerable<T> IntoIterator<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source;
    }

    public static IEnumerable<TResult> Map<T, TResult>(IEnumerable<T> source, Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mapper);
        foreach (T value in source) yield return mapper(value);
    }
}
