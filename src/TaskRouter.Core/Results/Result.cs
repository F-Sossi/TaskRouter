namespace TaskRouter.Core.Results;

/// <summary>
/// Minimal Result type. Replaces an internal functional-extensions dependency so the
/// library can be published publicly, while keeping the Try/Match idiom the original
/// engine was written in — the port stays mechanical.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly Exception? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
        IsOk = true;
    }

    private Result(Exception error)
    {
        _value = default;
        _error = error;
        IsOk = false;
    }

    public bool IsOk { get; }
    public bool IsError => !IsOk;

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(Exception error) => new(error);

    public static implicit operator Result<T>(T value) => Ok(value);

    public T Unwrap() => IsOk
        ? _value!
        : throw new InvalidOperationException("Unwrap called on a failed Result.", _error);

    public Exception UnwrapError() => IsError
        ? _error!
        : throw new InvalidOperationException("UnwrapError called on a successful Result.");

    public TOut Match<TOut>(Func<T, TOut> onOk, Func<Exception, TOut> onError) =>
        IsOk ? onOk(_value!) : onError(_error!);

    public Result<T> Tap(Action<T> action)
    {
        if (IsOk)
        {
            action(_value!);
        }

        return this;
    }

    public Result<T> TapError(Action<Exception> action)
    {
        if (IsError)
        {
            action(_error!);
        }

        return this;
    }

    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsOk ? Result<TOut>.Ok(map(_value!)) : Result<TOut>.Fail(_error!);
}

/// <summary>Stand-in for a void result.</summary>
public readonly struct Unit
{
    public static readonly Unit Value = default;
}

public static class Try
{
    public static async Task<Result<T>> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Result<T>.Ok(await action().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Result<T>.Fail(ex);
        }
    }

    public static async Task<Result<Unit>> RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail(ex);
        }
    }
}
