using System.Text.Json.Serialization;

namespace SmartFleetBE.Navigation;

// Same 18x28 grid and [fromLabel, toLabel] link format as WarehouseMapContext.
public sealed class WarehouseMapDocument
{
    public int Revision { get; set; } = 1;
    public int Rows { get; set; } = 18;
    public int Columns { get; set; } = 28;
    public List<List<WarehouseMapCell>> Cells { get; set; } = [];
    public List<List<string>> Links { get; set; } = [];
}

public sealed class WarehouseMapCell
{
    public string Type { get; set; } = "wall";
    public string? Label { get; set; }
    public string? DisplayName { get; set; }
    public string? OperationalRole { get; set; }
    public int? ArucoId { get; set; }
    public bool IsIntersection { get; set; }
    // Marker +X angle in MAP_N_W_UP: 0=N, +90=W, -90=E. Installation data.
    public double? MarkerYawDeg { get; set; }
    public double? MarkerSizeM { get; set; }
}

public sealed class MapCellUpdateRequest
{
    public int Revision { get; set; }
    public WarehouseMapCell? Cell { get; set; }
}

public sealed class MapLinksUpdateRequest
{
    public int Revision { get; set; }
    public List<List<string>>? Links { get; set; }
}

public sealed class RobotNavigationConfig
{
    // ROS /cmd_vel linear.x command units, NOT a calibrated physical speed.
    public decimal NormalSpeed { get; set; } = 0.18m;
    public decimal ApproachSpeed { get; set; } = 0.10m;
    public decimal ObstacleSpeed { get; set; } = 0.06m;
}

public sealed class IntersectionSelectionRequest
{
    public bool IsIntersection { get; set; }
}

public sealed class ManualClearRequest
{
    public bool PhysicalAreaConfirmedClear { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class RobotNavigationEvent
{
    // APPROACH, ENTER, EXIT or CANCEL.
    public string Event { get; set; } = "";
    public int IntersectionArUcoId { get; set; }
    // Marker before the intersection for APPROACH; after it for EXIT.
    public int? CurrentArUcoId { get; set; }
}

// Robot detected current marker and knows its planned NEXT marker.
// A raw camera ID alone cannot predict a turn at a fork.
public sealed class RobotArUcoScanReport
{
    [JsonRequired]
    public int? CurrentArUcoId { get; set; }
    // Must be present. Use explicit null only when the planned route ends.
    [JsonRequired]
    public int? NextArUcoId { get; set; }
}

public sealed class ApproachingRobotView
{
    public int RobotId { get; init; }
    public string RobotCode { get; init; } = "";
    public int CurrentArUcoId { get; init; }
    public int NextArUcoId { get; init; }
    public string Phase { get; init; } = "WAITING";
    public DateTime LastReportedAtUtc { get; init; }
}

public sealed class NavigationDirective
{
    public string Type { get; init; } = "NAV_CONTROL";
    public required string RobotCode { get; init; }
    public string Mode { get; init; } = "STOP";
    public decimal MaxLinearSpeed { get; init; }
    public bool IntersectionGranted { get; init; }
    public int? IntersectionArUcoId { get; init; }
    public string Reason { get; init; } = "";
    public string Epoch { get; init; } = "";
    public long Sequence { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed class IntersectionTrafficView
{
    public int IntersectionArUcoId { get; init; }
    public string? ActiveRobotCode { get; init; }
    public bool ActiveRobotEntered { get; init; }
    public bool BlockedPendingPhysicalClearance { get; init; }
    public int LastServedRobotId { get; init; }
    // Waiting + granted but not yet entered. Inside/occupied is counted separately.
    public int ApproachingCount { get; init; }
    public int WaitingCount { get; init; }
    public int InsideCount { get; init; }
    public int TotalRelevantCount { get; init; }
    public IReadOnlyList<string> WaitingRobotCodes { get; init; } = [];
    public IReadOnlyList<ApproachingRobotView> ApproachingRobots { get; init; } = [];
    // E.g. {"1":1,"2":1,"3":1,"5":1} for four robots headed to QR4.
    public IReadOnlyDictionary<int, int> ApproachingBySourceArUco { get; init; } =
        new Dictionary<int, int>();
}

public sealed class NavigationOperation
{
    public bool Success { get; init; }
    public bool Conflict { get; init; }
    public bool Unavailable { get; init; }
    public string Error { get; init; } = "";
    public IReadOnlyList<NavigationDirective> Directives { get; init; } = [];

    public static NavigationOperation Ok(IReadOnlyList<NavigationDirective>? directives = null) =>
        new() { Success = true, Directives = directives ?? [] };

    public static NavigationOperation Fail(string message, bool conflict = false, bool unavailable = false) =>
        new() { Error = message, Conflict = conflict, Unavailable = unavailable };
}

// Local device settings, safety locks, and a legacy/mirror copy of the database map.
public sealed class NavigationStoredState
{
    public WarehouseMapDocument? Map { get; set; }
    public Dictionary<string, RobotNavigationConfig> RobotConfigs { get; set; } = [];
    public List<StoredIntersectionLock> IntersectionLocks { get; set; } = [];
    // Last camera checkpoint and any claimed corridor remain occupied across disconnects.
    public Dictionary<string, StoredRobotOccupancy> RobotOccupancies { get; set; } = [];
}

public sealed record StoredRobotOccupancy(int CurrentArUcoId, int? NextArUcoId = null);

public sealed class StoredIntersectionLock
{
    public int ArucoId { get; set; }
    public string? GrantedRobotCode { get; set; }
    public int LastServedRobotId { get; set; }
    public bool Blocked { get; set; }
}
