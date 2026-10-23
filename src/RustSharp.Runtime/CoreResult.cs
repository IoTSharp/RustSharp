using System.Diagnostics.CodeAnalysis;

namespace RustSharp.Runtime.Core;

/// <summary>Rust-named success/error value with an explicit discriminant.</summary>
[SuppressMessage("Naming", "CA1716", Justification = "Result is a required Rust core name.")]
[SuppressMessage("Design", "CA1000", Justification = "Static constructors mirror Rust core associated functions.")]
public readonly struct Result<T, TError> : IEquatable<Result<T, TError>>
{
    private readonly T _value;
    private readonly TError _error;
    private readonly bool _isOk;

    private Result(T value, TError error, bool isOk)
    {
        _value = value;
        _error = error;
        _isOk = isOk;
    }

    public bool IsOk => _isOk;
    public bool IsErr => !_isOk;
    public static Result<T, TError> Ok(T value) => new(value, default!, true);
    public static Result<T, TError> Err(TError error) => new(default!, error, false);

    public T Unwrap() => _isOk
        ? _value
        : throw new InvalidOperationException("called Result::unwrap() on an Err value");

    public T Expect(string message) => _isOk
        ? _value
        : throw new InvalidOperationException(message);

    public TError UnwrapErr() => !_isOk
        ? _error
        : throw new InvalidOperationException("called Result::unwrap_err() on an Ok value");

    public T UnwrapOr(T fallback) => _isOk ? _value : fallback;
    public T UnwrapOrElse(Func<TError, T> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return _isOk ? _value : fallback(_error);
    }

    public Result<TResult, TError> Map<TResult>(Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return _isOk ? Result<TResult, TError>.Ok(mapper(_value)) : Result<TResult, TError>.Err(_error);
    }

    public Result<T, TResultError> MapErr<TResultError>(Func<TError, TResultError> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return _isOk ? Result<T, TResultError>.Ok(_value) : Result<T, TResultError>.Err(mapper(_error));
    }

    public Result<TResult, TError> AndThen<TResult>(Func<T, Result<TResult, TError>> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return _isOk ? mapper(_value) : Result<TResult, TError>.Err(_error);
    }

    public TResult Match<TResult>(Func<T, TResult> ok, Func<TError, TResult> err)
    {
        ArgumentNullException.ThrowIfNull(ok);
        ArgumentNullException.ThrowIfNull(err);
        return _isOk ? ok(_value) : err(_error);
    }

    public bool Equals(Result<T, TError> other) => _isOk == other._isOk &&
        (_isOk ? EqualityComparer<T>.Default.Equals(_value, other._value) : EqualityComparer<TError>.Default.Equals(_error, other._error));

    public override bool Equals(object? obj) => obj is Result<T, TError> other && Equals(other);
    public override int GetHashCode() => _isOk ? HashCode.Combine(1, _value) : HashCode.Combine(0, _error);
    public override string ToString() => _isOk ? $"Ok({_value})" : $"Err({_error})";
    public static bool operator ==(Result<T, TError> left, Result<T, TError> right) => left.Equals(right);
    public static bool operator !=(Result<T, TError> left, Result<T, TError> right) => !left.Equals(right);
}
