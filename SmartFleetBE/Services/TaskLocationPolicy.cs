using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Services;

public static class TaskLocationPolicy
{
    public static Task<bool> HasVerificationQrAsync(SmartFleetDbContext db, long taskId, CancellationToken ct) =>
        db.TransportTasks.AnyAsync(t => t.TaskId == taskId && t.PickupStation.QrCodePayload != null &&
            t.DeliveryStation.QrCodePayload != null && t.PickupStation.QrCodePayload.Trim() != "" &&
            t.DeliveryStation.QrCodePayload.Trim() != "" && t.PickupStation.QrCodePayload.Trim() != t.DeliveryStation.QrCodePayload.Trim(), ct);

    public static Task<bool> IsValidAsync(SmartFleetDbContext db, long taskId, CancellationToken ct) =>
        db.TransportTasks.AnyAsync(t => t.TaskId == taskId && t.Warehouse.IsActive &&
            t.PickupStationId != t.DeliveryStationId &&
            t.PickupStation.IsActive && t.PickupStation.Zone.IsActive &&
            t.PickupStation.Zone.WarehouseId == t.WarehouseId && t.PickupStation.StationType == "PICKUP" &&
            t.DeliveryStation.IsActive && t.DeliveryStation.Zone.IsActive &&
            t.DeliveryStation.Zone.WarehouseId == t.WarehouseId && t.DeliveryStation.StationType == "DELIVERY", ct);
}
