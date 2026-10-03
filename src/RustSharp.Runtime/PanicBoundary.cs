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

    /// <summary>
    /// True when a destructor failed while an existing panic was unwinding.
    /// Both failures remain observable through <see cref="Panic"/> and
    /// <see cref="CleanupException"/>; the host may terminate the process.
    /// </summary>
    public bool IsDoublePanic => Outcome == RustPanicOutcome.Aborted &&
        Panic is not null && CleanupException is not null;
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

    /// <summary>Runs a value-producing body with the default unwind policy.</summary>
    public static RustPanicReport Run<T>(Func<T> body, out T? result) =>
        Run(body, scope: null, strategy: RustPanicStrategy.Unwind, out result);

    /// <summary>Runs a value-producing body with a tracked cleanup scope.</summary>
    public static RustPanicReport Run<T>(Func<T> body, DropScope? scope, out T? result) =>
        Run(body, scope, RustPanicStrategy.Unwind, out result);

    /// <summary>Runs a value-producing body with an explicit panic policy.</summary>
    public static RustPanicReport Run<T>(Func<T> body, RustPanicStrategy strategy, out T? result) =>
        Run(body, scope: null, strategy, out result);

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
            RustDropCleanupReport cleanup = scope.Cleanup(duringUnwind: true);
            if (cleanup.FirstFailure is null)
                return new(RustPanicOutcome.Unwound, panic, null, true, true);

            // A destructor panic during unwinding is a double panic.  Keep the
            // original panic and destructor failure in the report and expose
            // the abort outcome without calling FailFast in the library.
            return new(RustPanicOutcome.Aborted, panic, cleanup.FirstFailure, true, false);
        }
        catch (Exception cleanupException)
        {
            return new(RustPanicOutcome.Aborted, panic, cleanupException, true, false);
        }
    }

    private static RustPanicReport ReturnReport(DropScope? scope)
    {
        if (scope is null) return new(RustPanicOutcome.Returned, null, null, false, true);
        try
        {
            RustDropCleanupReport cleanup = scope.Cleanup(duringUnwind: false);
            return new(RustPanicOutcome.Returned, null, cleanup.FirstFailure, true,
                cleanup.FirstFailure is null);
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
