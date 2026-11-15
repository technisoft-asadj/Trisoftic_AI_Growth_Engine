namespace TriSoftic.GrowthEngine.BuildingBlocks.Results;

public enum ErrorKind { Validation, NotFound, Conflict, Forbidden, Unauthorized, Policy, Transient, Permanent }

public sealed record Error(string Code, string Message, ErrorKind Kind = ErrorKind.Validation);

public readonly record struct Result(Error? Error)
{
    public bool IsSuccess => Error is null;
    public static Result Ok() => new((Error?)null);
    public static Result Fail(Error e) => new(e);
    public static implicit operator Result(Error e) => Fail(e);
}

public readonly record struct Result<T>(T? Value, Error? Error)
{
    public bool IsSuccess => Error is null;
    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(Error e) => new(default, e);
    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Error e) => Fail(e);
}

/// <summary>Thrown only for programmer errors / illegal state-machine transitions.</summary>
public sealed class IllegalTransitionException(string message) : InvalidOperationException(message);
