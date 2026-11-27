using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

public static class TaskLocationPolicy
{
    public sealed record StationRoute(int PickupArUcoId, int DeliveryArUcoId);
    public static async Task<StationRoute> ResolveRouteAsync(SmartFleetDbContext db, int warehouseId, int pickupId,
        int deliveryId, QrDemoRouteService routes, CancellationToken ct, bool allowInactiveWarehouse = false)
    {
        var rows = await db.Stations.AsNoTracking().Include(s => s.Zone).ThenInclude(z => z.Warehouse).Include(s => s.Checkpoint)
            .Where(s => s.StationId == pickupId || s.StationId == deliveryId).ToListAsync(ct);
        var pickup = rows.SingleOrDefault(s => s.StationId == pickupId);
        var delivery = rows.SingleOrDefault(s => s.StationId == deliveryId);
        bool Valid(Model.Station? s, string type) => s != null && s.IsActive && (s.StationType == type || s.StationType == "TRANSFER") && s.Zone.IsActive &&
            s.Zone.Warehouse.IsActive && s.Zone.WarehouseId == warehouseId && s.Checkpoint?.WarehouseId == warehouseId;
        if (pickupId == deliveryId || !Valid(pickup, "PICKUP") || !Valid(delivery, "DELIVERY"))
            throw new ArgumentException("Choose different active pickup/delivery stations mapped within the selected warehouse.");
        var result = new StationRoute(pickup!.Checkpoint!.ArucoId, delivery!.Checkpoint!.ArucoId);
        if (routes.SupportsWarehouse(warehouseId)) routes.ValidateTaskSelection(result.PickupArUcoId, result.DeliveryArUcoId);
        else {
            if (!allowInactiveWarehouse) throw new ArgumentException("This warehouse's world is inactive. The task stays queued until Admin selects its map.");
            var saved = await db.MapConfigurations.AsNoTracking().SingleOrDefaultAsync(m => m.WarehouseId == warehouseId && m.IsActive, ct);
            if (saved?.MapMetadataJson == null) throw new ArgumentException("Selected warehouse has no commissioned map.");
            using var document = JsonDocument.Parse(saved.MapMetadataJson);
            if (!document.RootElement.TryGetProperty("navigationMap", out var node)) throw new ArgumentException("Selected warehouse has no navigation map.");
            var map = node.Deserialize<WarehouseMapDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            foreach (var id in new[] { result.PickupArUcoId, result.DeliveryArUcoId }) {
                var markers = map.Cells.SelectMany(r => r).Where(c => c.Type == "qr" && c.ArucoId == id).ToList();
                if (markers.Count != 1 || markers[0].IsIntersection || markers[0].MarkerYawDeg == null || markers[0].MarkerSizeM == null ||
                    map.DirectedLinks && markers[0].OperationalRole == "NAVIGATION")
                    throw new ArgumentException("Station mapping must use a commissioned transfer checkpoint outside reserved intersections.");
            }
        }
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
