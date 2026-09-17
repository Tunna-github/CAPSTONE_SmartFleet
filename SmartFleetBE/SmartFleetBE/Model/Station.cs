using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class Station
{
    public int StationId { get; set; }

    public int ZoneId { get; set; }

    public string StationCode { get; set; } = null!;

    public string StationName { get; set; } = null!;

    public string StationType { get; set; } = null!;

    public decimal CoordX { get; set; }

    public decimal CoordY { get; set; }

    public decimal CoordZ { get; set; }

    public decimal YawAngle { get; set; }

    public string? QrCodePayload { get; set; }

    public bool IsOccupied { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<MissionWaypoint> MissionWaypoints { get; set; } = new List<MissionWaypoint>();

    public virtual ICollection<TransportTask> TransportTaskDeliveryStations { get; set; } = new List<TransportTask>();

    public virtual ICollection<TransportTask> TransportTaskPickupStations { get; set; } = new List<TransportTask>();

    public virtual Zone Zone { get; set; } = null!;
}
