using System.Diagnostics.CodeAnalysis;

namespace RustSharp.Runtime.Core;

/// <summary>Rust-named optional value with an explicit discriminant.</summary>
[SuppressMessage("Naming", "CA1716", Justification = "Option is a required Rust core name.")]
[SuppressMessage("Design", "CA1000", Justification = "Static constructors mirror Rust core associated functions.")]
public readonly struct Option<T> : IEquatable<Option<T>>
{
    private readonly T _value;
    private readonly bool _isSome;

    private Option(T value, bool isSome)
    {
        _value = value;
        _isSome = isSome;
    }

    public bool IsSome => _isSome;
    public bool IsNone => !_isSome;

    public static Option<T> None => default;
    public static Option<T> Some(T value) => new(value, true);

    public T Unwrap() => _isSome
        ? _value
        : throw new InvalidOperationException("called Option::unwrap() on a None value");

    public T Expect(string message) => _isSome
        ? _value
        : throw new InvalidOperationException(message);

    public T UnwrapOr(T fallback) => _isSome ? _value : fallback;
    public T UnwrapOrElse(Func<T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return _isSome ? _value : fallback();
    }

    public Option<TResult> Map<TResult>(Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return _isSome ? Option<TResult>.Some(mapper(_value)) : Option<TResult>.None;
    }

    public Option<TResult> AndThen<TResult>(Func<T, Option<TResult>> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return _isSome ? mapper(_value) : Option<TResult>.None;
    }

    public TResult Match<TResult>(Func<T, TResult> some, Func<TResult> none)
    {
        ArgumentNullException.ThrowIfNull(some);
        ArgumentNullException.ThrowIfNull(none);
        return _isSome ? some(_value) : none();
    }

    public bool Equals(Option<T> other) => _isSome == other._isSome &&
        (!_isSome || EqualityComparer<T>.Default.Equals(_value, other._value));

    public override bool Equals(object? obj) => obj is Option<T> other && Equals(other);
    public override int GetHashCode() => !_isSome ? 0 : HashCode.Combine(1, _value);
    public override string ToString() => _isSome ? $"Some({_value})" : "None";
    public static bool operator ==(Option<T> left, Option<T> right) => left.Equals(right);
    public static bool operator !=(Option<T> left, Option<T> right) => !left.Equals(right);
}
