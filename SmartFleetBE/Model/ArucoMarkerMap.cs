using System;

namespace SmartFleetBE.Model;

// A physical ArUco marker fixed to a warehouse grid cell.
public sealed class ArucoMarkerMap
{
    public int Id { get; set; }
    public int WarehouseId { get; set; }
    public int ArucoId { get; set; }
    public string CellCode { get; set; } = "";
    public int GridColumn { get; set; } // 1-based (A1 is column 1)
    public int GridRow { get; set; }    // 0-based (A1 is row 0)
    public DateTime UpdatedAt { get; set; }
}
