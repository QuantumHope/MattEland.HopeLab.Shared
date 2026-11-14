namespace MattEland.HopeLab.Common;

/// <summary>The empty value, for results that carry no data.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

/// <summary>Success with a value, or failure with an error message. Ported from Hymnal (D004).</summary>
public readonly record struct Result<T>
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public bool IsSuccess { get; init; }

    public static Result<T> Ok(T value) => new() { Value = value, IsSuccess = true };
    public static Result<T> Fail(string error) => new() { Error = error, IsSuccess = false };

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
        => IsSuccess ? Result<TOut>.Ok(map(Value!)) : Result<TOut>.Fail(Error!);
}

public static class Result
{
    public static Result<Unit> Ok() => Result<Unit>.Ok(Unit.Value);
    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);
    public static Result<Unit> Fail(string error) => Result<Unit>.Fail(error);
}
