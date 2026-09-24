namespace SmartFleetBE.Constants;

public static class TransportTaskStatuses
{
    public const string Pending = "PENDING";
    public const string Queued = "QUEUED";
    public const string Assigned = "ASSIGNED";
    public const string Executing = "EXECUTING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public static class TransportTaskPriorities
{
    public const int Low = 1;
    public const int Medium = 2;
    public const int High = 3;
}
