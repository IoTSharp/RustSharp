namespace RustSharp.Runtime;

/// <summary>Runtime policy for a Rust# panic edge.</summary>
public enum RustPanicStrategy
{
    Unwind,
    Abort,
}

public enum RustPanicOutcome
{
    Returned,
    Unwound,
    Aborted,
}

/// <summary>A managed marker for an explicit Rust# panic.</summary>
public sealed class RustPanicException : Exception
{
    public RustPanicException(string? message = null, Exception? innerException = null)
        : base(message ?? "Rust# panic.", innerException)
    {
    }
}

/// <summary>
/// Observable result of one bounded panic boundary.  The boundary never calls
/// FailFast for abort: doing so would make the semantic profile untestable and
/// would bypass the host's process/cleanup evidence.  A production host may
/// translate <see cref="RustPanicOutcome.Aborted"/> to its own termination
/// policy after inspecting the report.
/// </summary>
public sealed record RustPanicReport(
    RustPanicOutcome Outcome,
    Exception? Panic,
    Exception? CleanupException,
    bool CleanupAttempted,
    bool CleanupCompleted)
{
    public bool IsSuccessful => Outcome == RustPanicOutcome.Returned &&
        Panic is null && CleanupCompleted;
}

public static class RustPanicBoundary
{
    public static RustPanicReport Run(
        Action body,
        DropScope? scope = null,
        RustPanicStrategy strategy = RustPanicStrategy.Unwind)
    {
        ArgumentNullException.ThrowIfNull(body);
        ValidateStrategy(strategy);

        try
        {
            body();
        }
        catch (Exception exception)
        {
            return PanicReport(exception, scope, strategy);
        }

        return ReturnReport(scope);
    }

    public static RustPanicReport Run<T>(
        Func<T> body,
        DropScope? scope,
        RustPanicStrategy strategy,
        out T? result)
    {
        ArgumentNullException.ThrowIfNull(body);
        ValidateStrategy(strategy);
        result = default;
        try
        {
            result = body();
        }
        catch (Exception exception)
        {
            return PanicReport(exception, scope, strategy);
        }

        return ReturnReport(scope);
    }

    /// <summary>Raises an explicit panic that can be captured by the boundary.</summary>
    public static void Panic(string? message = null) => throw new RustPanicException(message);

    private static RustPanicReport PanicReport(Exception panic, DropScope? scope, RustPanicStrategy strategy)
    {
        if (strategy == RustPanicStrategy.Abort)
        {
            // Abort deliberately leaves the scope untouched.  The caller can
            // inspect TrackedCount and decide how the host terminates.
            return new(RustPanicOutcome.Aborted, panic, null, false, false);
        }

        if (scope is null) return new(RustPanicOutcome.Unwound, panic, null, false, true);
        try
        {
            scope.Dispose();
            return new(RustPanicOutcome.Unwound, panic, null, true, true);
        }
        catch (Exception cleanupException)
        {
            return new(RustPanicOutcome.Unwound, panic, cleanupException, true, false);
        }
    }

    private static RustPanicReport ReturnReport(DropScope? scope)
    {
        if (scope is null) return new(RustPanicOutcome.Returned, null, null, false, true);
        try
        {
            scope.Dispose();
            return new(RustPanicOutcome.Returned, null, null, true, true);
        }
        catch (Exception cleanupException)
        {
            return new(RustPanicOutcome.Returned, null, cleanupException, true, false);
        }
    }

    private static void ValidateStrategy(RustPanicStrategy strategy)
    {
        if (!Enum.IsDefined(strategy)) throw new ArgumentOutOfRangeException(nameof(strategy));
    }
}
