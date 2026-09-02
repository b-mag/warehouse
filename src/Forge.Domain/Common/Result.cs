using System.Diagnostics.CodeAnalysis;

namespace Forge.Domain.Common;

/// <summary>
/// The outcome of a rejectable domain operation that produces no value (Req 5.5 and the
/// design's Error Handling section). A successful result carries nothing; a failed result
/// carries a typed <see cref="DomainError"/>.
/// <para>
/// Domain and application code returns a <see cref="Result"/> instead of throwing for
/// <em>expected</em> rejections, so a rejection is a plain value that callers inspect. This
/// is what lets rejections leave state unchanged: the caller checks <see cref="IsSuccess"/>
/// before applying any mutation and simply propagates the error otherwise. Exceptions remain
/// reserved for genuinely unexpected/programming faults.
/// </para>
/// </summary>
public readonly record struct Result
{
    private Result(bool isSuccess, DomainError? error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    private readonly DomainError? _error;

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation was rejected and carries a <see cref="Error"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The error for a failed result. Throws if accessed on a success (a programming fault).</summary>
    public DomainError Error =>
        _error ?? throw new InvalidOperationException("Cannot access Error on a successful Result.");

    /// <summary>A successful result.</summary>
    public static Result Success() => new(true, null);

    /// <summary>A failed result carrying the given error.</summary>
    public static Result Failure(DomainError error) => new(false, error);

    /// <summary>Lift a value into a successful <see cref="Result{T}"/>.</summary>
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    /// <summary>Create a failed <see cref="Result{T}"/> carrying the given error.</summary>
    public static Result<T> Failure<T>(DomainError error) => Result<T>.Failure(error);

    /// <summary>Implicitly treat an error as a failed result.</summary>
    public static implicit operator Result(DomainError error) => Failure(error);
}

/// <summary>
/// The outcome of a rejectable domain operation that produces a <typeparamref name="T"/> value
/// on success or a typed <see cref="DomainError"/> on rejection (Req 5.5 and the design's Error
/// Handling section).
/// <para>
/// A rejection carries the error <em>without throwing</em>, so an expected rejection is a value
/// the caller inspects rather than an exception that unwinds the stack. Because the caller must
/// check <see cref="IsSuccess"/> before reading <see cref="Value"/>, no partial mutation happens
/// on the rejection path and state is left unchanged (the repeated requirement across Req 5.5,
/// 6.4, 7.2, 7.4, 7.6, 13.3, 13.5, 15.8, 16.3, 17.6, 20.8, 22.4).
/// </para>
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public readonly record struct Result<T>
{
    private Result(bool isSuccess, T? value, DomainError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    private readonly T? _value;
    private readonly DomainError? _error;

    /// <summary>True when the operation succeeded and carries a <see cref="Value"/>.</summary>
    [MemberNotNullWhen(true, nameof(_value))]
    public bool IsSuccess { get; }

    /// <summary>True when the operation was rejected and carries an <see cref="Error"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The success value. Throws if accessed on a failure (a programming fault).</summary>
    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException("Cannot access Value on a failed Result.");

    /// <summary>The error for a failed result. Throws if accessed on a success (a programming fault).</summary>
    public DomainError Error =>
        _error ?? throw new InvalidOperationException("Cannot access Error on a successful Result.");

    /// <summary>A successful result carrying the given value.</summary>
    public static Result<T> Success(T value) => new(true, value, null);

    /// <summary>A failed result carrying the given error.</summary>
    public static Result<T> Failure(DomainError error) => new(false, default, error);

    /// <summary>
    /// Read the outcome without risking an access exception. Returns true and sets
    /// <paramref name="value"/> on success; returns false and sets <paramref name="error"/> on failure.
    /// </summary>
    public bool TryGet([MaybeNullWhen(false)] out T value, [MaybeNullWhen(true)] out DomainError error)
    {
        if (IsSuccess)
        {
            value = _value!;
            error = null;
            return true;
        }

        value = default;
        error = _error!;
        return false;
    }

    /// <summary>Project the success value, propagating any error unchanged.</summary>
    public Result<TNext> Map<TNext>(Func<T, TNext> selector) =>
        IsSuccess ? Result<TNext>.Success(selector(_value!)) : Result<TNext>.Failure(_error!);

    /// <summary>Chain another rejectable operation, propagating any error unchanged.</summary>
    public Result<TNext> Bind<TNext>(Func<T, Result<TNext>> next) =>
        IsSuccess ? next(_value!) : Result<TNext>.Failure(_error!);

    /// <summary>Return the value on success, or the supplied fallback on failure.</summary>
    public T GetValueOrDefault(T fallback) => IsSuccess ? _value! : fallback;

    /// <summary>Implicitly lift a value into a successful result.</summary>
    public static implicit operator Result<T>(T value) => Success(value);

    /// <summary>Implicitly treat an error as a failed result.</summary>
    public static implicit operator Result<T>(DomainError error) => Failure(error);
}
