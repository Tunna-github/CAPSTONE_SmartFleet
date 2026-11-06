using SmartFleetBE;

namespace SmartFleetBE.DTOs;

/// <summary>
/// Current battery snapshot stored in Robots. No extra database migration is required.
/// LastTelemetryTime is the last telemetry message, not necessarily the exact battery sample time.
/// </summary>
public sealed class RobotBatteryResponse
{
    public int RobotId { get; init; }
    public string RobotCode { get; init; } = string.Empty;
    public decimal? BatteryPercent { get; init; }
    public decimal? BatteryVoltage { get; init; }
    public string BatteryLevel { get; init; } = "UNKNOWN";
    public string ConnectionStatus { get; init; } = string.Empty;
    public string OperationalStatus { get; init; } = string.Empty;
    public DateTime? LastTelemetryTime { get; init; }
    public bool IsTelemetryStale { get; init; }
    public DateTime? UpdatedAt { get; init; }

    public static RobotBatteryResponse FromRobot(
        Robot robot,
        DateTime nowUtc,
        int staleAfterSeconds = 45)
    {
        string level = robot.BatteryPercent switch
        {
            null => "UNKNOWN",
            < 10m => "CRITICAL",
            < 20m => "LOW",
            _ => "NORMAL"
        };

        bool stale = !robot.LastTelemetryTime.HasValue ||
            nowUtc - robot.LastTelemetryTime.Value >
                TimeSpan.FromSeconds(staleAfterSeconds);

        return new RobotBatteryResponse
        {
            RobotId = robot.RobotId,
            RobotCode = robot.RobotCode,
            BatteryPercent = robot.BatteryPercent,
            BatteryVoltage = robot.BatteryVoltage,
            BatteryLevel = level,
            ConnectionStatus = robot.ConnectionStatus,
            OperationalStatus = robot.OperationalStatus,
            LastTelemetryTime = robot.LastTelemetryTime,
            IsTelemetryStale = stale,
            UpdatedAt = robot.UpdatedAt
        };
    }
}
