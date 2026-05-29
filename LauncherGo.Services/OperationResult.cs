namespace LauncherGo.Services;

public sealed class OperationResult
{
    public static OperationResult Success(string? message = null) => new(true, message, null);

    public static OperationResult Failed(string message, Exception? exception = null) => new(false, message, exception);

    private OperationResult(bool isSuccess, string? message, Exception? exception)
    {
        IsSuccess = isSuccess;
        Message = message;
        Exception = exception;
    }

    public bool IsSuccess { get; }

    public string? Message { get; }

    public Exception? Exception { get; }
}

public sealed class OperationResult<T>
{
    public static OperationResult<T> Success(T value, string? message = null) => new(value, true, message, null);

    public static OperationResult<T> Failed(string message, Exception? exception = null) => new(default, false, message, exception);

    private OperationResult(T? value, bool isSuccess, string? message, Exception? exception)
    {
        Value = value;
        IsSuccess = isSuccess;
        Message = message;
        Exception = exception;
    }

    public T? Value { get; }

    public bool IsSuccess { get; }

    public string? Message { get; }

    public Exception? Exception { get; }
}
