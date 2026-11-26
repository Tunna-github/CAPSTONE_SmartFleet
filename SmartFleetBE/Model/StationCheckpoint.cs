namespace SmartFleetBE.Model;

public sealed class StationCheckpoint
{
    public int StationId { get; set; }
    public int WarehouseId { get; set; }
    public int ArucoId { get; set; }
    public Station Station { get; set; } = null!;
}

// One physical berth per marker. Reservations are explicit, not proof of docking.
public sealed class StationBerth
{
    public int StationId { get; set; }
    public int RobotId { get; set; }
    public DateTime ReservedAt { get; set; }
}
