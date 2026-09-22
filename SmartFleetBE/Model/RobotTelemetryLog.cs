using System;
using System.Collections.Generic;

namespace SmartFleetBE;

public partial class RobotTelemetryLog
{
    public long TelemetryId { get; set; }

    public int RobotId { get; set; }

    public decimal? PositionX { get; set; }

    public decimal? PositionY { get; set; }

    public decimal? Heading { get; set; }

    public decimal? LinearVelocity { get; set; }

    public decimal? AngularVelocity { get; set; }

    public decimal? BatteryPercentage { get; set; }

    public decimal? BatteryVoltage { get; set; }

    public decimal? BatteryCurrentMa { get; set; }

    public decimal? BatteryPowerMw { get; set; }

    public decimal? CpuTemperature { get; set; }

    public decimal? WifiSignalDbm { get; set; }

    public DateTime RecordedAt { get; set; }

    public virtual Robot Robot { get; set; } = null!;
}
