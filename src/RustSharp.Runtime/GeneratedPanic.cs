using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace RustSharp.Runtime;

/// <summary>Preserves the original panic and the destructor panic at a generated abort edge.</summary>
public sealed class RustGeneratedAbortException : Exception
{
    public RustGeneratedAbortException(Exception panic, Exception? cleanupFailure = null)
        : base(cleanupFailure is null ? "Generated Rust# panic aborted." : "Generated Rust# double panic aborted.", panic)
    {
        ArgumentNullException.ThrowIfNull(panic);
        Panic = panic;
        CleanupFailure = cleanupFailure;
    }

    public Exception Panic { get; }
    public Exception? CleanupFailure { get; }
}

/// <summary>All observable failures from the frozen normal cleanup continuation policy.</summary>
public sealed class RustGeneratedCleanupException : Exception
{
    public const int MaximumSubsequentFailures = 16_384;

    public RustGeneratedCleanupException(Exception firstFailure, IReadOnlyList<Exception> subsequentFailures)
        : base("Generated Rust# normal cleanup failed more than once.", firstFailure)
    {
        ArgumentNullException.ThrowIfNull(firstFailure);
        ArgumentNullException.ThrowIfNull(subsequentFailures);
        if (subsequentFailures.Count is < 1 or > MaximumSubsequentFailures)
            throw new ArgumentException("Generated cleanup failures exceed their bounded obligation count.", nameof(subsequentFailures));
        FirstFailure = firstFailure;
        var snapshot = new Exception[subsequentFailures.Count];
        for (int index = 0; index < snapshot.Length; index++)
            snapshot[index] = subsequentFailures[index] ??
                throw new ArgumentException("Generated cleanup failures cannot contain null.", nameof(subsequentFailures));
        SubsequentFailures = Array.AsReadOnly(snapshot);
    }

    public Exception FirstFailure { get; }
    public IReadOnlyList<Exception> SubsequentFailures { get; }
}

/// <summary>Reflection-free failure operations used by generated CLR catch regions.</summary>
public static class RustGeneratedPanic
{
    [ThreadStatic]
    private static bool unwinding;

    public const int AbortExitCode = 134;

    public static bool IsUnwinding() => unwinding;

    /// <summary>
    /// The v2 native unwind policy allows a destructor body's owned cleanup on Linux.
    /// This selects a cleanup algorithm; it never examines program source or trace text.
    /// Windows retains its native immediate double-panic edge.
    /// </summary>
    public static bool NativeV2UnwindsDestructorBody() => OperatingSystem.IsLinux();

    public static bool SwapUnwinding(bool value)
    {
        bool previous = unwinding;
        unwinding = value;
        return previous;
    }

    public static bool IsAbort(object panic) => panic is RustGeneratedAbortException;

    public static object DoublePanic(object panic, object cleanupFailure) =>
        new RustGeneratedAbortException(RequireException(panic), RequireException(cleanupFailure));

    /// <summary>Preserves the caller panic when an owned destructor returns a nested abort.</summary>
    public static object PreserveNestedAbort(object panic, object cleanupFailure)
    {
        Exception original = RequireException(panic);
        Exception child = RequireException(cleanupFailure);
        if (child is not RustGeneratedAbortException)
            throw new ArgumentException("Nested abort composition requires an abort exception.", nameof(cleanupFailure));
        return new RustGeneratedAbortException(original, child);
    }

    public static object Abort(object panic) => panic is RustGeneratedAbortException
        ? panic : new RustGeneratedAbortException(RequireException(panic));

    public static object ContinueNormalCleanup(object panic, object cleanupFailure)
    {
        Exception first = RequireException(panic);
        Exception next = RequireException(cleanupFailure);
        if (first is not RustGeneratedCleanupException collected)
            return new RustGeneratedCleanupException(first, [next]);
        if (collected.SubsequentFailures.Count >= RustGeneratedCleanupException.MaximumSubsequentFailures)
            throw new InvalidOperationException("Generated cleanup failure count exceeded its obligation bound.");
        return new RustGeneratedCleanupException(collected.FirstFailure, [.. collected.SubsequentFailures, next]);
    }

    [DoesNotReturn]
    public static void Propagate(object panic, bool entryBoundary)
    {
        Exception failure = RequireException(panic);
        if (entryBoundary && failure is RustGeneratedAbortException abort)
        {
            Console.Error.WriteLine(abort.CleanupFailure is null
                ? "RustSharp panic abort: " + abort.Panic.Message
                : "RustSharp double panic abort: " + abort.Panic.Message + " | " + abort.CleanupFailure.Message);
            Environment.Exit(AbortExitCode);
        }
        ExceptionDispatchInfo.Capture(failure).Throw();
        throw new InvalidOperationException("A propagated panic unexpectedly returned.");
    }

    private static Exception RequireException(object value) => value as Exception ??
        throw new ArgumentException("Generated panic values must be managed exceptions.", nameof(value));
}
