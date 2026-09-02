using RustSharp.Runtime;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class VerticalProofTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generic Option monomorphization is deterministic", GenericOptionAsync),
        new("trait solver resolves and diagnoses bounded cases", TraitResolutionAsync),
        new("managed owner enforces shared and mutable borrows", ManagedBorrowAsync),
        new("managed owner rejects use after drop", OwnerUseAfterDropAsync),
        new("managed owner preserves deterministic drop scope", DropScopeAsync),
        new("managed hybrid pins and releases an array", PinnedArrayAsync),
        new("managed interop uses explicit AOT-safe boundary", ManagedInteropAsync),
    ];

    private static Task GenericOptionAsync()
    {
        var definition = new GenericTypeDefinition("Option", ["T"]);
        MonomorphizedType first = MonomorphizedType.Create(definition, [RustType.I32]);
        MonomorphizedType second = MonomorphizedType.Create(definition, [RustType.I32]);
        AssertEx.Equal("Option<i32>", first.ClosedType.ToString());
        AssertEx.Equal(first.ClosedType, second.ClosedType);
        return Task.CompletedTask;
    }

    private static Task TraitResolutionAsync()
    {
        var display = new TraitDefinition("Display");
        var solver = new TraitSolver(new TraitSolverLimits(maximumDepth: 8, maximumWork: 32));
        solver.AddImplementation(new TraitImplementation(display, RustType.I32, "core::fmt"));
        TraitResolutionResult resolved = solver.Resolve(display, RustType.I32);
        AssertEx.True(resolved.IsSuccess, resolved.Diagnostic ?? "trait should resolve");
        TraitResolutionResult missing = solver.Resolve(display, RustType.Bool);
        AssertEx.Equal(TraitResolutionStatus.Missing, missing.Status);

        solver.AddImplementation(new TraitImplementation(display, RustType.Parameter("T"), "test blanket"));
        TraitResolutionResult ambiguous = solver.Resolve(display, RustType.I32);
        AssertEx.Equal(TraitResolutionStatus.Ambiguous, ambiguous.Status);

        RustType deep = RustType.I32;
        for (var index = 0; index < 5; index++)
        {
            deep = RustType.Named("Box", deep);
        }

        var bounded = new TraitSolver(new TraitSolverLimits(maximumDepth: 2, maximumWork: 32));
        bounded.AddImplementation(new TraitImplementation(display, RustType.Parameter("T"), "blanket"));
        TraitResolutionResult limited = bounded.Resolve(display, deep);
        AssertEx.Equal(TraitResolutionStatus.Resolved, limited.Status);

        var recursive = new TraitSolver(new TraitSolverLimits(maximumDepth: 2, maximumWork: 1));
        recursive.AddImplementation(new TraitImplementation(display, RustType.Named("Box", RustType.Parameter("T")), "nested"));
        TraitResolutionResult depthLimited = recursive.Resolve(display, deep);
        AssertEx.Equal(TraitResolutionStatus.LimitExceeded, depthLimited.Status);
        return Task.CompletedTask;
    }

    private static Task ManagedBorrowAsync()
    {
        using var owner = new RustOwner<int>(7);
        using Borrow<int> first = owner.Borrow();
        using Borrow<int> second = owner.Borrow();
        AssertEx.Equal(7, first.Value);
        AssertEx.Throws<InvalidOperationException>(() => owner.BorrowMut());
        second.Dispose();
        AssertEx.Throws<ObjectDisposedException>(() => _ = second.Value);
        first.Dispose();
        using (MutableBorrow<int> mutable = owner.BorrowMut())
        {
            mutable.Value = 9;
        }

        AssertEx.Equal(9, owner.Read());
        return Task.CompletedTask;
    }

    private static Task DropScopeAsync()
    {
        var order = new List<int>();
        using (var scope = new DropScope())
        {
            scope.Track(new RecordingDisposable(() => order.Add(1)));
            scope.Track(new RecordingDisposable(() => order.Add(2)));
        }

        AssertEx.Equal("2,1", string.Join(',', order));
        return Task.CompletedTask;
    }

    private static Task OwnerUseAfterDropAsync()
    {
        var owner = new RustOwner<string>("owned");
        owner.Dispose();
        AssertEx.True(owner.IsDisposed, "The owner must record its dropped state.");
        AssertEx.Throws<ObjectDisposedException>(() => owner.Read());
        owner.Dispose();
        return Task.CompletedTask;
    }

    private static Task ManagedInteropAsync()
    {
        int result = ManagedInterop.Call(new AddOne(), 41);
        AssertEx.Equal(42, result);
        return Task.CompletedTask;
    }

    private static Task PinnedArrayAsync()
    {
        var values = new[] { 1, 2, 3 };
        var pinned = new PinnedArray<int>(values);
        AssertEx.True(pinned.Address != 0, "A pinned array must expose a non-zero address.");
        pinned.Dispose();
        AssertEx.Throws<ObjectDisposedException>(() => _ = pinned.Address);
        return Task.CompletedTask;
    }

    private sealed class AddOne : IManagedCall<int, int>
    {
        public int Invoke(int input) => input + 1;
    }

    private sealed class RecordingDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
