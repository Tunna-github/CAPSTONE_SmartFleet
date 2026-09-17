using System;
using System.Collections.Generic;

namespace SmartFleetBE;

public partial class Robot
{
    public int RobotId { get; set; }

    public int WarehouseId { get; set; }

    public string RobotCode { get; set; } = null!;

    public string RobotType { get; set; } = null!;

    public string Model { get; set; } = null!;

    public string ControlSoftware { get; set; } = null!;

    public string? RobotAgentVersion { get; set; }

    public string? IpAddress { get; set; }

    public string? MacAddress { get; set; }

    public string MqttClientIdentifier { get; set; } = null!;

    public string ConnectionStatus { get; set; } = null!;

    public string OperationalStatus { get; set; } = null!;

    public decimal? BatteryPercent { get; set; }

    public decimal? BatteryVoltage { get; set; }

    public decimal? CurrentCoordX { get; set; }

    public decimal? CurrentCoordY { get; set; }

    public decimal? CurrentHeading { get; set; }

    public int TotalOperatingMinutes { get; set; }

    public DateTime? LastHeartbeatTime { get; set; }

    public DateTime? LastTelemetryTime { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual ICollection<RobotIncident> RobotIncidents { get; set; } = new List<RobotIncident>();

    public virtual ICollection<RobotMaintenanceRecord> RobotMaintenanceRecords { get; set; } = new List<RobotMaintenanceRecord>();

    public virtual ICollection<RobotStatusHistory> RobotStatusHistories { get; set; } = new List<RobotStatusHistory>();

    public virtual ICollection<RobotTelemetryLog> RobotTelemetryLogs { get; set; } = new List<RobotTelemetryLog>();

    public virtual TaskAssignment? TaskAssignment { get; set; }

    public virtual Warehouse Warehouse { get; set; } = null!;
}
