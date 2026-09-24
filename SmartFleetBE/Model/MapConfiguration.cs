using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class MapConfiguration
{
    public int MapId { get; set; }

    public int WarehouseId { get; set; }

    public string MapVersion { get; set; } = null!;

    public string? MapName { get; set; }

    public string MapSource { get; set; } = null!;

    public string CoordinateSystem { get; set; } = null!;

    public decimal ResolutionMeterPerPixel { get; set; }

    public decimal OriginX { get; set; }

    public decimal OriginY { get; set; }

    public decimal OriginYaw { get; set; }

    public int? ImageWidthPixels { get; set; }

    public int? ImageHeightPixels { get; set; }

    public string? MapImageUrl { get; set; }

    public string? MapMetadataJson { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Warehouse Warehouse { get; set; } = null!;
}
