namespace GearFlow.Billing.Application.Common;

public sealed record Error(string Code, string Message);

public sealed class Result<T>
{
    private readonly T? value;
    public bool IsSuccess => Error is null;
    public Error? Error { get; }
    public T Value => IsSuccess ? value! : throw new InvalidOperationException("Failed result has no value.");
    private Result(T value) => this.value = value;
    private Result(Error error) => Error = error;
    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(string code, string message) => new(new Error(code, message));
}

public readonly record struct Unit;

public sealed class ConcurrencyException() : Exception("State changed concurrently. Retry the entire unit of work.");
public sealed class DuplicateException() : Exception("A unique identity or business key already exists.");
