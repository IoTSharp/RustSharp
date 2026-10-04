using RustSharp.Runtime;

namespace RustSharp.Tests;

/// <summary>
/// Focused P1-08 regression for recursive aggregate cleanup. The same bounded
/// case is exposed through <see cref="All"/> so the executable harness cannot
/// silently omit the recursive unwind contract.
/// </summary>
internal static class P1NestedDropUnwindTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 nested aggregate Drop propagates unwind failure", VerifyNestedAggregateUnwindPropagationAsync),
        new("P1 nested aggregate Drop preserves two normal failures", VerifyNestedAggregateNormalFailuresAsync),
    ];

    private static Task VerifyNestedAggregateUnwindPropagationAsync()
    {
        VerifyNestedAggregateUnwindPropagation();
        return Task.CompletedTask;
    }

    public static void VerifyNestedAggregateUnwindPropagation()
    {
        var unwindTrace = new List<string>();
        var unwindChild = new RustDropAggregate(
            [
                new RecordingDisposable(() => unwindTrace.Add("child-field-0")),
                new RecordingDisposable(() =>
                {
                    unwindTrace.Add("child-failure");
                    throw new InvalidOperationException("nested failure");
                }),
                new RecordingDisposable(() => unwindTrace.Add("child-field-2")),
            ],
            new RecordingDisposable(() => unwindTrace.Add("child-outer")));
        var unwindParent = new RustDropAggregate(
            [unwindChild, new RecordingDisposable(() => unwindTrace.Add("parent-sibling"))],
            new RecordingDisposable(() => unwindTrace.Add("parent-outer")));

        RustDropCleanupReport unwind = unwindParent.Cleanup(duringUnwind: true);
        Require(unwind.Outcome == RustDropCleanupOutcome.Aborted,
            "Recursive unwind cleanup must report an abort.");
        Require(unwind.FirstFailure?.Message == "nested failure",
            "The nested destructor failure must reach the parent report.");
        Require(unwind.Attempted == 2 && unwind.Completed == 1,
            "Parent unwind glue must stop after the nested aggregate fails.");
        Require(string.Join(',', unwindTrace) ==
            "parent-outer,child-outer,child-field-0,child-failure",
            "Unwind must stop both the child and parent obligation lists at the first failure.");

        var normalTrace = new List<string>();
        var normalChild = new RustDropAggregate(
            [
                new RecordingDisposable(() => normalTrace.Add("child-field-0")),
                new RecordingDisposable(() =>
                {
                    normalTrace.Add("child-failure");
                    throw new InvalidOperationException("normal failure");
                }),
                new RecordingDisposable(() => normalTrace.Add("child-field-2")),
            ],
            new RecordingDisposable(() => normalTrace.Add("child-outer")));
        var normalParent = new RustDropAggregate(
            [normalChild, new RecordingDisposable(() => normalTrace.Add("parent-sibling"))],
            new RecordingDisposable(() => normalTrace.Add("parent-outer")));

        RustDropCleanupReport normal = normalParent.Cleanup();
        Require(normal.Outcome == RustDropCleanupOutcome.Failed,
            "Normal recursive cleanup must preserve the first failure while continuing.");
        Require(string.Join(',', normalTrace) ==
            "parent-outer,child-outer,child-field-0,child-failure,child-field-2,parent-sibling",
            "Normal cleanup must continue through nested and parent siblings.");
    }

    private static Task VerifyNestedAggregateNormalFailuresAsync()
    {
        VerifyNestedAggregateNormalFailures();
        return Task.CompletedTask;
    }

    public static void VerifyNestedAggregateNormalFailures()
    {
        var trace = new List<string>();
        var child = new RustDropAggregate(
            [
                new RecordingDisposable(() =>
                {
                    trace.Add("child-first");
                    throw new InvalidOperationException("child-first");
                }),
                new RecordingDisposable(() =>
                {
                    trace.Add("child-second");
                    throw new InvalidOperationException("child-second");
                }),
            ],
            new RecordingDisposable(() => trace.Add("child-outer")));
        var parent = new RustDropAggregate(
            [
                child,
                new RecordingDisposable(() => trace.Add("parent-sibling")),
            ],
            new RecordingDisposable(() => trace.Add("parent-outer")));

        RustDropCleanupReport report = parent.Cleanup();
        Require(report.Outcome == RustDropCleanupOutcome.Failed,
            "Normal cleanup must remain failed after nested destructor failures.");
        Require(report.FirstFailure?.Message == "child-first" && report.SecondFailure?.Message == "child-second",
            "Nested normal cleanup must preserve both child failures in order.");
        Require(report.Attempted == 3 && report.Completed == 2,
            "Parent cleanup must count the nested aggregate and its sibling exactly once.");
        Require(string.Join(',', trace) ==
            "parent-outer,child-outer,child-first,child-second,parent-sibling",
            "Normal nested cleanup must continue after both child failures.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
