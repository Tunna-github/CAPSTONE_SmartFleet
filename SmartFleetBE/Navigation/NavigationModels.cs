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
    public int? ArucoId { get; set; }
    public bool IsIntersection { get; set; }
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
    public int ApproachingCount { get; init; }
    public IReadOnlyList<string> WaitingRobotCodes { get; init; } = [];
}

public sealed class NavigationOperation
{
    public bool Success { get; init; }
    public bool Conflict { get; init; }
    public string Error { get; init; } = "";
    public IReadOnlyList<NavigationDirective> Directives { get; init; } = [];

    public static NavigationOperation Ok(IReadOnlyList<NavigationDirective>? directives = null) =>
        new() { Success = true, Directives = directives ?? [] };

    public static NavigationOperation Fail(string message, bool conflict = false) =>
        new() { Error = message, Conflict = conflict };
}

// Local JSON document. No EF migration or DB write is needed.
public sealed class NavigationStoredState
{
    public WarehouseMapDocument? Map { get; set; }
    public Dictionary<string, RobotNavigationConfig> RobotConfigs { get; set; } = [];
    public List<StoredIntersectionLock> IntersectionLocks { get; set; } = [];
}

public sealed class StoredIntersectionLock
{
    public int ArucoId { get; set; }
    public string? GrantedRobotCode { get; set; }
    public int LastServedRobotId { get; set; }
    public bool Blocked { get; set; }
}
