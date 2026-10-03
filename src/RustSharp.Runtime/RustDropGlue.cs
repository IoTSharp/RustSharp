namespace RustSharp.Runtime;

/// <summary>Whether aggregate cleanup completed all eligible fields.</summary>
public enum RustDropCleanupOutcome
{
    Completed,
    Failed,
    Aborted,
}

/// <summary>Bounded result of one outer-destructor/field drop sequence.</summary>
public sealed record RustDropCleanupReport(
    RustDropCleanupOutcome Outcome,
    Exception? FirstFailure,
    Exception? SecondFailure,
    int Attempted,
    int Completed)
{
    public bool IsSuccessful => Outcome == RustDropCleanupOutcome.Completed &&
        FirstFailure is null && SecondFailure is null;

    // The original panic is carried by RustPanicBoundary; an aborted cleanup
    // report therefore needs only the destructor failure to identify a double
    // panic.  A second cleanup failure is intentionally absent because unwind
    // cleanup stops at the first failure.
    public bool IsDoublePanic => Outcome == RustDropCleanupOutcome.Aborted &&
        FirstFailure is not null;
}

/// <summary>
/// Shared recursive drop glue for generated aggregates.  The outer destructor
/// runs first, then fields/elements run in declaration order.  A normal cleanup
/// records the first failure and continues; cleanup during unwind aborts after
/// the first destructor failure while retaining both exceptions.
/// </summary>
public static class RustDropGlue
{
    public const int MaximumFields = 4096;

    public static RustDropCleanupReport Run(
        IReadOnlyList<Action?> fields,
        Action? outerDestructor = null,
        bool duringUnwind = false)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count > MaximumFields)
            throw new ArgumentException($"Drop glue supports at most {MaximumFields} fields.", nameof(fields));

        // Generated cleanup owns a fixed obligation set. Snapshot the caller's
        // list before invoking user destructors so a mutable producer cannot
        // reorder, remove, or append fields while cleanup is in progress.
        Action?[] snapshot = new Action?[fields.Count];
        for (int index = 0; index < snapshot.Length; index++)
            snapshot[index] = fields[index];

        Exception? first = null;
        Exception? second = null;
        int attempted = 0;
        int completed = 0;
        foreach (Action? action in Enumerate(outerDestructor, snapshot))
        {
            if (action is null) continue;
            attempted++;
            try
            {
                action();
                completed++;
            }
            catch (Exception exception)
            {
                if (first is null)
                {
                    first = exception;
                }
                else
                {
                    second ??= exception;
                }

                // Rust aborts a process when a destructor panics while an
                // existing panic is unwinding.  Preserve the two failures in
                // the report so a host can apply its process policy.
                if (duringUnwind) break;
            }
        }

        RustDropCleanupOutcome outcome = first is null
            ? RustDropCleanupOutcome.Completed
            : duringUnwind
                ? RustDropCleanupOutcome.Aborted
                : RustDropCleanupOutcome.Failed;
        return new(outcome, first, second, attempted, completed);
    }

    public static RustDropCleanupReport Run(
        IReadOnlyList<IDisposable?> fields,
        IDisposable? outerDestructor = null,
        bool duringUnwind = false)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Action?[] actions = new Action?[fields.Count];
        for (int index = 0; index < fields.Count; index++)
        {
            IDisposable? field = fields[index];
            actions[index] = field is null ? null : field.Dispose;
        }

        return Run(actions, outerDestructor is null ? null : outerDestructor.Dispose, duringUnwind);
    }

    private static IEnumerable<Action?> Enumerate(Action? outer, Action?[] fields)
    {
        if (outer is not null) yield return outer;
        for (int index = 0; index < fields.Length; index++) yield return fields[index];
    }
}

/// <summary>
/// Runtime representation of a non-unit aggregate with an optional outer
/// destructor and owned fields.  Generated aggregate glue can materialize one
/// of these nodes while preserving the declared outer-before-field order.
/// Nested aggregates are ordinary <see cref="IDisposable"/> fields, so their
/// cleanup is recursive and each node consumes its obligation once.
/// </summary>
public sealed class RustDropAggregate : IDisposable
{
    private readonly IReadOnlyList<IDisposable?> fields;
    private readonly IDisposable? outerDestructor;
    private bool disposed;

    public RustDropAggregate(
        IReadOnlyList<IDisposable?> fields,
        IDisposable? outerDestructor = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count > RustDropGlue.MaximumFields)
            throw new ArgumentException($"Drop aggregate supports at most {RustDropGlue.MaximumFields} fields.", nameof(fields));
        var snapshot = new IDisposable?[fields.Count];
        for (int index = 0; index < snapshot.Length; index++)
            snapshot[index] = fields[index];
        this.fields = Array.AsReadOnly(snapshot);
        this.outerDestructor = outerDestructor;
    }

    public bool IsDisposed => disposed;

    public RustDropCleanupReport Cleanup(bool duringUnwind = false)
    {
        if (disposed)
            return new(RustDropCleanupOutcome.Completed, null, null, 0, 0);

        disposed = true;
        return RustDropGlue.Run(fields, outerDestructor, duringUnwind);
    }

    public void Dispose()
    {
        RustDropCleanupReport report = Cleanup();
        if (report.FirstFailure is not null)
            throw report.FirstFailure;
    }
}

/// <summary>
/// One-shot place drop flag used by generated assignment and temporary paths.
/// Moving out clears the cleanup obligation; replacing an initialized value
/// drops the old value once before retaining the new value's obligation.
/// </summary>
public sealed class RustDropSlot : IDisposable
{
    private Action? cleanup;
    private bool initialized;
    private bool moved;
    private bool dropped;

    public bool IsInitialized => initialized;
    public bool IsMoved => moved;
    public bool IsDropped => dropped;
    public bool IsLive => initialized && !moved && !dropped;

    public void Initialize(Action destructor)
    {
        ArgumentNullException.ThrowIfNull(destructor);
        if (IsLive) throw new InvalidOperationException("A live drop slot cannot be initialized twice.");
        cleanup = destructor;
        initialized = true;
        moved = false;
        dropped = false;
    }

    /// <summary>Initializes a slot with a concrete disposable temporary.</summary>
    public void Initialize(IDisposable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Initialize(value.Dispose);
    }

    /// <summary>Consumes the value and returns its destructor to the caller.</summary>
    public Action MoveOut()
    {
        if (!IsLive) throw new InvalidOperationException("Only a live drop slot can be moved.");
        Action destructor = cleanup ?? throw new InvalidOperationException("The drop slot has no destructor.");
        cleanup = null;
        moved = true;
        return destructor;
    }

    /// <summary>
    /// Replaces the value.  The replacement remains live if dropping the old
    /// value throws, matching an exceptional assignment edge.
    /// </summary>
    public void Replace(Action destructor)
    {
        ArgumentNullException.ThrowIfNull(destructor);
        Action? old = IsLive ? cleanup : null;
        cleanup = destructor;
        initialized = true;
        moved = false;
        dropped = false;
        if (old is not null) old();
    }

    /// <summary>Replaces a slot with a concrete disposable assignment value.</summary>
    public void Replace(IDisposable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Replace(value.Dispose);
    }

    public void Dispose()
    {
        if (!IsLive) return;
        Action destructor = cleanup ?? throw new InvalidOperationException("The drop slot has no destructor.");
        cleanup = null;
        dropped = true;
        destructor();
    }
}
