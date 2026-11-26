using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Services;

public static class TaskLocationPolicy
{
    public sealed record StationRoute(int PickupArUcoId, int DeliveryArUcoId);
    public static async Task<StationRoute> ResolveRouteAsync(SmartFleetDbContext db, int warehouseId, int pickupId,
        int deliveryId, QrDemoRouteService routes, CancellationToken ct)
    {
        var rows = await db.Stations.AsNoTracking().Include(s => s.Zone).ThenInclude(z => z.Warehouse).Include(s => s.Checkpoint)
            .Where(s => s.StationId == pickupId || s.StationId == deliveryId).ToListAsync(ct);
        var pickup = rows.SingleOrDefault(s => s.StationId == pickupId);
        var delivery = rows.SingleOrDefault(s => s.StationId == deliveryId);
        bool Valid(Model.Station? s, string type) => s != null && s.IsActive && (s.StationType == type || s.StationType == "TRANSFER") && s.Zone.IsActive &&
            s.Zone.Warehouse.IsActive && s.Zone.WarehouseId == warehouseId && s.Checkpoint?.WarehouseId == warehouseId;
        if (pickupId == deliveryId || !Valid(pickup, "PICKUP") || !Valid(delivery, "DELIVERY") || !routes.SupportsWarehouse(warehouseId))
            throw new ArgumentException("Choose different active PICKUP/DELIVERY stations with explicit mappings in the navigation warehouse.");
        var result = new StationRoute(pickup!.Checkpoint!.ArucoId, delivery!.Checkpoint!.ArucoId);
        routes.ValidateTaskSelection(result.PickupArUcoId, result.DeliveryArUcoId);
        return result;
    }

    public static async Task<string?> SynchronizeRouteAsync(SmartFleetDbContext db, long taskId, QrDemoRouteService routes, CancellationToken ct)
    {
        var task = await db.TransportTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId, ct);
        try {
            var resolved = await ResolveRouteAsync(db, task.WarehouseId, task.PickupStationId, task.DeliveryStationId, routes, ct);
            routes.ConfigureTask(taskId, resolved.PickupArUcoId, resolved.DeliveryArUcoId);
            return null;
        } catch (ArgumentException e) { return e.Message; }
    }
    public static Task<bool> HasVerificationQrAsync(SmartFleetDbContext db, long taskId, CancellationToken ct) =>
        db.TransportTasks.AnyAsync(t => t.TaskId == taskId && t.PickupStation.QrCodePayload != null &&
            t.DeliveryStation.QrCodePayload != null && t.PickupStation.QrCodePayload.Trim() != "" &&
            t.DeliveryStation.QrCodePayload.Trim() != "" && t.PickupStation.QrCodePayload.Trim() != t.DeliveryStation.QrCodePayload.Trim(), ct);

    public static Task<bool> IsValidAsync(SmartFleetDbContext db, long taskId, CancellationToken ct) =>
        db.TransportTasks.AnyAsync(t => t.TaskId == taskId && t.Warehouse.IsActive &&
            t.PickupStationId != t.DeliveryStationId &&
            t.PickupStation.IsActive && t.PickupStation.Zone.IsActive &&
            t.PickupStation.Zone.WarehouseId == t.WarehouseId && (t.PickupStation.StationType == "PICKUP" || t.PickupStation.StationType == "TRANSFER") &&
            t.DeliveryStation.IsActive && t.DeliveryStation.Zone.IsActive &&
            t.DeliveryStation.Zone.WarehouseId == t.WarehouseId && (t.DeliveryStation.StationType == "DELIVERY" || t.DeliveryStation.StationType == "TRANSFER"), ct);
}
