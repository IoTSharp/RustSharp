namespace RustSharp.Tests;

internal static class AssertEx
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message ?? $"Expected '{expected}', but found '{actual}'.");
        }
    }

    public static T NotNull<T>(T? value, string message)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }

        return value;
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
