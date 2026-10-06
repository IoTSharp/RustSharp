using RustSharp.Runtime;

namespace RustSharp.Tests;

internal static class P1MirReferenceDropStateTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 shared MIR Drop state follows aliases and exact places", AliasesAsync),
        new("P1 shared MIR Drop state resets written subtree without reviving siblings", WriteGenerationAsync),
        new("P1 shared MIR Drop state preserves generations after failed or cancelled writes", FailedWriteAsync),
        new("P1 shared MIR Drop state bounds records and projection depth", BoundsAsync),
    ];

    private static Task AliasesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        object owner = MirReference.Create(new Value("first", "second"));
        object first = MirReference.Field(owner, 0);
        object alias = MirReference.Field(owner, 0);
        object second = MirReference.Field(owner, 1);
        MirReference.ConsumeDrop(owner, deadline.Token);
        AssertEx.False(MirReference.IsDropLive(owner, deadline.Token), "The outer destructor obligation must be consumed.");
        AssertEx.True(MirReference.IsDropLive(first, deadline.Token), "Consuming an outer destructor cannot suppress its automatic fields.");
        MirReference.ConsumeDrop(first, deadline.Token);
        MirReference.ConsumeDrop(alias, deadline.Token);
        AssertEx.False(MirReference.IsDropLive(alias, deadline.Token), "A callee's exact field consumption must be visible through the caller alias.");
        AssertEx.True(MirReference.IsDropLive(second, deadline.Token), "An independent sibling obligation remains live.");
        object separateCell = MirReference.Create(MirReference.Read(owner));
        AssertEx.True(MirReference.IsDropLive(MirReference.Field(separateCell, 0), deadline.Token),
            "Value equality cannot merge the generations of independent storage cells.");
        object uninitialized = MirReference.Field(MirReference.Create(null), 0);
        AssertEx.True(MirReference.IsDropLive(uninitialized, deadline.Token),
            "The shared state query must not dereference uninitialized storage; generated local flags guard initialization.");
        return Task.CompletedTask;
    }

    private static Task WriteGenerationAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        object owner = MirReference.Create(new Value([.. Enumerable.Repeat<object?>(new Value("old", "other"), 16)]));
        object first = MirReference.Field(owner, 1);
        object child = MirReference.Field(first, 0);
        object sibling = MirReference.Field(owner, 10);
        object siblingChild = MirReference.Field(sibling, 0);
        MirReference.ConsumeDrop(owner, deadline.Token);
        MirReference.ConsumeDrop(first, deadline.Token);
        MirReference.ConsumeDrop(child, deadline.Token);
        MirReference.ConsumeDrop(sibling, deadline.Token);
        MirReference.ConsumeDrop(siblingChild, deadline.Token);
        MirReference.Write(MirReference.Field(owner, 1), new Value("fresh", "other"), deadline.Token);
        AssertEx.True(MirReference.IsDropLive(first, deadline.Token) && MirReference.IsDropLive(child, deadline.Token),
            "A successful replacement starts a new generation for its exact place and descendants.");
        AssertEx.False(MirReference.IsDropLive(owner, deadline.Token), "A field write cannot revive its consumed outer destructor.");
        AssertEx.False(MirReference.IsDropLive(sibling, deadline.Token) || MirReference.IsDropLive(siblingChild, deadline.Token),
            "Writing field 1 cannot revive consumed field 10 or its descendants.");
        AssertEx.Equal("fresh", (string)MirReference.Read(child)!);
        MirReference.Write(owner, new Value([.. Enumerable.Repeat<object?>(new Value("new-root", "other"), 16)]), deadline.Token);
        AssertEx.True(MirReference.IsDropLive(owner, deadline.Token) && MirReference.IsDropLive(first, deadline.Token) &&
            MirReference.IsDropLive(siblingChild, deadline.Token), "Whole-owner replacement resets every old exact-place generation.");
        return Task.CompletedTask;
    }

    private static Task FailedWriteAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        object owner = MirReference.Create(new Value("old", "sibling"));
        object first = MirReference.Field(owner, 0);
        MirReference.ConsumeDrop(first, deadline.Token);
        AssertEx.Throws<IndexOutOfRangeException>(() => MirReference.Write(MirReference.Field(owner, 2), "invalid", deadline.Token));
        AssertEx.Equal("old", (string)MirReference.Read(first)!);
        AssertEx.False(MirReference.IsDropLive(first, deadline.Token), "A failed projection cannot reset a consumed generation.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => MirReference.Write(first, "cancelled", cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => MirReference.ConsumeDrop(MirReference.Field(owner, 1), cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => MirReference.IsDropLive(first, cancelled.Token));
        AssertEx.Equal("old", (string)MirReference.Read(first)!);
        AssertEx.False(MirReference.IsDropLive(first, deadline.Token), "Cancellation must preserve the consumed generation.");
        AssertEx.True(MirReference.IsDropLive(MirReference.Field(owner, 1), deadline.Token), "Cancelled consumption cannot affect the sibling.");
        return Task.CompletedTask;
    }

    private static Task BoundsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        object deep = MirReference.Create(null);
        for (int depth = 0; depth < 128; depth++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            deep = MirReference.Field(deep, 0);
        }
        AssertEx.Throws<InvalidOperationException>(() => MirReference.Field(deep, 0));
        MirReference.ConsumeDrop(deep, deadline.Token);
        AssertEx.False(MirReference.IsDropLive(deep, deadline.Token), "The maximum admitted path must retain exact consumption.");

        object owner = MirReference.Create(new Value([.. Enumerable.Repeat<object?>(
            new Value([.. Enumerable.Repeat<object?>(0, 128)]), 128)]));
        for (int index = 0; index < MirReference.MaximumConsumedDropPlaces; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            MirReference.ConsumeDrop(MirReference.Field(MirReference.Field(owner, index / 128), index % 128), deadline.Token);
        }
        AssertEx.Throws<InvalidOperationException>(() => MirReference.ConsumeDrop(owner, deadline.Token));
        AssertEx.True(MirReference.IsDropLive(owner, deadline.Token), "A record-limit failure cannot consume the rejected obligation.");
        object last = MirReference.Field(MirReference.Field(owner, 127), 127);
        AssertEx.False(MirReference.IsDropLive(last, deadline.Token), "Previously admitted records survive the limit failure.");
        MirReference.Write(owner, new Value(), deadline.Token);
        AssertEx.True(MirReference.IsDropLive(last, deadline.Token), "Whole-owner reinitialization releases the bounded old record set.");
        MirReference.ConsumeDrop(owner, deadline.Token);
        AssertEx.False(MirReference.IsDropLive(owner, deadline.Token), "Released record capacity must admit the new generation.");
        return Task.CompletedTask;
    }

    private sealed class Value(params object?[] fields) : IMirValue
    {
        public object? ReadField(int index) => fields[index];
        public object WithField(int index, object? value)
        {
            var replaced = (object?[])fields.Clone();
            replaced[index] = value;
            return new Value(replaced);
        }
    }
}
