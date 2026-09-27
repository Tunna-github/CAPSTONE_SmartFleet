namespace SmartFleetBE.Services.Results;

public enum TransportTaskResultStatus
{
    Success,
    NotFound,
    ValidationFailed,
    Conflict
}

public sealed class TransportTaskServiceResult<T>
{
    private TransportTaskServiceResult(
        TransportTaskResultStatus status,
        T? value,
        string? error)
    {
        Status = status;
        Value = value;
        Error = error;
    }

    public TransportTaskResultStatus Status { get; }
    public T? Value { get; }
    public string? Error { get; }

    public static TransportTaskServiceResult<T> Success(T value) =>
        new(TransportTaskResultStatus.Success, value, null);

    public static TransportTaskServiceResult<T> NotFound(string error) =>
        new(TransportTaskResultStatus.NotFound, default, error);

    public static TransportTaskServiceResult<T> ValidationFailed(string error) =>
        new(TransportTaskResultStatus.ValidationFailed, default, error);

    public static TransportTaskServiceResult<T> Conflict(string error) =>
        new(TransportTaskResultStatus.Conflict, default, error);
}
