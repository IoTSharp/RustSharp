using System.Runtime.InteropServices;

namespace RustSharp.Runtime;

public interface IRustDrop
{
    void Drop();
}

public sealed class RustOwner<T> : IDisposable
{
    public const int MaximumSharedBorrows = 4096;

    private T value;
    private int borrowCount;
    private bool mutableBorrowed;
    private bool disposed;

    public RustOwner(T value)
    {
        this.value = value;
    }

    public bool IsDisposed => disposed;

    public Borrow<T> Borrow()
    {
        EnsureAlive();
        if (mutableBorrowed)
        {
            throw new InvalidOperationException("A shared borrow cannot start while a mutable borrow is active.");
        }

        if (borrowCount >= MaximumSharedBorrows)
        {
            throw new InvalidOperationException($"An owner supports at most {MaximumSharedBorrows} shared borrows.");
        }

        borrowCount++;
        return new(this);
    }

    public MutableBorrow<T> BorrowMut()
    {
        EnsureAlive();
        if (mutableBorrowed || borrowCount != 0)
        {
            throw new InvalidOperationException("A mutable borrow requires exclusive access to the owner.");
        }

        mutableBorrowed = true;
        return new(this, value);
    }

    public T Read()
    {
        EnsureAlive();
        return value;
    }

    public void Replace(T replacement)
    {
        EnsureAlive();
        if (borrowCount != 0 || mutableBorrowed)
        {
            throw new InvalidOperationException("The owner is borrowed.");
        }

        value = replacement;
    }

    internal void CommitMutable(T replacement)
    {
        EnsureAlive();
        if (!mutableBorrowed)
        {
            throw new InvalidOperationException("A mutable borrow is not active.");
        }

        value = replacement;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (borrowCount != 0 || mutableBorrowed)
        {
            throw new InvalidOperationException("Cannot drop an owner while a borrow is active.");
        }

        if (value is IRustDrop drop)
        {
            drop.Drop();
        }

        value = default!;
        disposed = true;
    }

    internal void ReleaseShared()
    {
        if (borrowCount <= 0)
        {
            throw new InvalidOperationException("Shared borrow was released more than once.");
        }

        borrowCount--;
    }

    internal void ReleaseMutable()
    {
        if (!mutableBorrowed)
        {
            throw new InvalidOperationException("Mutable borrow was released more than once.");
        }

        mutableBorrowed = false;
    }

    internal void EnsureAlive() => EnsureAliveCore();

    private void EnsureAliveCore()
    {
        if (disposed)
        {
            ObjectDisposedException.ThrowIf(disposed, nameof(RustOwner<T>));
        }
    }
}

public sealed class Borrow<T> : IDisposable
{
    private RustOwner<T>? owner;

    internal Borrow(RustOwner<T> owner)
    {
        this.owner = owner;
    }

    public T Value
    {
        get
        {
            RustOwner<T>? current = owner;
            ObjectDisposedException.ThrowIf(current is null, nameof(Borrow<T>));
            return current.Read();
        }
    }

    public void Dispose()
    {
        RustOwner<T>? current = Interlocked.Exchange(ref owner, null);
        current?.ReleaseShared();
    }
}

public sealed class MutableBorrow<T> : IDisposable
{
    private RustOwner<T>? owner;
    private T value;

    internal MutableBorrow(RustOwner<T> owner, T value)
    {
        this.owner = owner;
        this.value = value;
    }

    public T Value
    {
        get
        {
            EnsureActive();
            return value;
        }
        set
        {
            EnsureActive();
            this.value = value;
        }
    }

    public void Dispose()
    {
        RustOwner<T>? current = Interlocked.Exchange(ref owner, null);
        if (current is not null)
        {
            current.CommitMutable(value);
            current.ReleaseMutable();
        }
    }

    private void EnsureActive()
    {
        if (owner is null)
        {
            ObjectDisposedException.ThrowIf(true, nameof(MutableBorrow<T>));
        }

        owner.EnsureAlive();
    }
}

public sealed class PinnedArray<T> : IDisposable
    where T : unmanaged
{
    private GCHandle handle;
    private bool disposed;

    public PinnedArray(T[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("A pinned array must not be empty.", nameof(values));
        }

        handle = GCHandle.Alloc(values, GCHandleType.Pinned);
        Values = values;
    }

    public T[] Values { get; }
    public nint Address => EnsureHandle().AddrOfPinnedObject();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        handle.Free();
        disposed = true;
    }

    private GCHandle EnsureHandle()
    {
        if (disposed)
        {
            ObjectDisposedException.ThrowIf(disposed, nameof(PinnedArray<T>));
        }

        return handle;
    }
}

public static class ManagedInterop
{
    public static TResult Call<TInput, TResult>(IManagedCall<TInput, TResult> call, TInput input)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Invoke(input);
    }
}

public interface IManagedCall<in TInput, out TResult>
{
    TResult Invoke(TInput input);
}

public sealed class DropScope : IDisposable
{
    public const int MaximumTrackedValues = 256;

    private readonly List<IDisposable> values = [];
    private bool disposed;

    public void Track(IDisposable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (disposed)
        {
            ObjectDisposedException.ThrowIf(disposed, nameof(DropScope));
        }

        if (values.Count >= MaximumTrackedValues)
        {
            throw new InvalidOperationException($"A drop scope supports at most {MaximumTrackedValues} tracked values.");
        }

        values.Add(value);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Exception? first = null;
        for (var index = values.Count - 1; index >= 0; index--)
        {
            try
            {
                values[index].Dispose();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                first ??= exception;
            }
        }

        values.Clear();
        if (first is not null)
        {
            throw first;
        }
    }
}
