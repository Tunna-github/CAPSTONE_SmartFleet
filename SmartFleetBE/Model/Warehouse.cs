using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class Warehouse
{
    public int WarehouseId { get; set; }

    public string WarehouseCode { get; set; } = null!;

    public string WarehouseName { get; set; } = null!;

    public string? Address { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual MapConfiguration? MapConfiguration { get; set; }

    public virtual ICollection<Robot> Robots { get; set; } = new List<Robot>();

    public virtual ICollection<TransportTask> TransportTasks { get; set; } = new List<TransportTask>();

    public virtual ICollection<Zone> Zones { get; set; } = new List<Zone>();
}
