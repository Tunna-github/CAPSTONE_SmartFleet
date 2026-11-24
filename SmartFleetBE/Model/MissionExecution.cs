namespace SmartFleetBE.Model;

// Immutable route/verification expectations plus a concurrency-protected cursor.
public sealed class MissionExecution
{
    public long MissionId { get; set; }
    public string RoutePayloadJson { get; set; } = "";
    public int MapRevision { get; set; }
    public int NextWaypointIndex { get; set; }
    public string ExpectedPickupQr { get; set; } = "";
    public string ExpectedDeliveryQr { get; set; } = "";
    public string? PackageCode { get; set; }
    public DateTime? StationPresenceAt { get; set; }
    public int Revision { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Mission Mission { get; set; } = null!;
}

public sealed class VerificationAttempt
{
    public long Id { get; set; }
    public long MissionId { get; set; }
    public string RequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Type { get; set; } = "";
    public string? ScannedQr { get; set; }
    public string? ScannedPackageCode { get; set; }
    public string ExpectedQr { get; set; } = "";
    public bool Accepted { get; set; }
    public string Result { get; set; } = "";
    public string? Note { get; set; }
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Existing BA scan-event table; navigation scans are distinct from verification.
public sealed class RobotQrScanEvent
{
    public long ScanId { get; set; }
    public int RobotId { get; set; }
    public long? MissionId { get; set; }
    public long? CheckpointId { get; set; }
    public string QrPayloadScanned { get; set; } = "";
    public string ScanPurpose { get; set; } = "NAVIGATION";
    public bool IsValid { get; set; }
    public string? InvalidReason { get; set; }
    public DateTime ScannedAt { get; set; }
    public string? EventId { get; set; }
    public int? WaypointIndex { get; set; }
}
