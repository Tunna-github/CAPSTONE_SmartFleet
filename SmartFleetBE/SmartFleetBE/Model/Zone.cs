using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class Zone
{
    public int ZoneId { get; set; }

    public int WarehouseId { get; set; }

    public string ZoneCode { get; set; } = null!;

    public string ZoneName { get; set; } = null!;

    public string ZoneType { get; set; } = null!;

    public decimal SpeedLimit { get; set; }

    public string? BoundaryJson { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<Station> Stations { get; set; } = new List<Station>();

    public virtual Warehouse Warehouse { get; set; } = null!;
}
