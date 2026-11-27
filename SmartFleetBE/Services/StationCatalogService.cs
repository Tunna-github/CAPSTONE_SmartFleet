using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

public sealed record WarehouseWrite(string Code, string Name, string? Address, bool IsActive, int Revision);
public sealed record ZoneWrite(int WarehouseId, string Code, string Name, string Type, decimal SpeedLimit, bool IsActive, int Revision);
public sealed record StationWrite(int ZoneId, string Code, string Name, string Type, decimal CoordX, decimal CoordY,
    decimal CoordZ, decimal YawAngle, string? QrCodePayload, int? ArucoId, bool IsActive, int Capacity, int Revision);
public sealed record BerthWrite(int RobotId, bool Release, bool PhysicalAreaConfirmedClear, string Reason);

public sealed class StationCatalogService(SmartFleetDbContext db, NavigationCoordinator navigation, IConfiguration? config = null)
{
    public static readonly string[] StationTypes = ["PICKUP", "DELIVERY", "TRANSFER", "WAITING", "CHARGING"];
    public static readonly string[] ZoneTypes = ["PICKUP", "DELIVERY", "WAITING", "CHARGING", "TRANSIT", "DISPATCH", "STORAGE"];

    public async Task<object> Read(CancellationToken ct)
    {
        var warehouses = await db.Warehouses.AsNoTracking().OrderBy(w => w.WarehouseCode).Select(w => new {
            w.WarehouseId, code = w.WarehouseCode, name = w.WarehouseName, w.Address, w.IsActive, w.Revision }).ToListAsync(ct);
        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.ZoneCode).Select(z => new {
            z.ZoneId, z.WarehouseId, code = z.ZoneCode, name = z.ZoneName, type = z.ZoneType, z.SpeedLimit, z.IsActive, z.Revision }).ToListAsync(ct);
        var rows = await db.Stations.AsNoTracking().Include(s => s.Zone).ThenInclude(z => z.Warehouse).Include(s => s.Checkpoint).OrderBy(s => s.StationCode).ToListAsync(ct);
        var berths = await db.StationBerths.AsNoTracking().ToListAsync(ct);
        var robots = await db.Robots.AsNoTracking().Select(r => new { r.RobotId, r.RobotCode, r.WarehouseId }).ToListAsync(ct);
        var map = navigation.GetMap();
        var occupied = navigation.GetCheckpointOccupants();
        var warehouseMaps = new Dictionary<int, WarehouseMapDocument>();
        foreach (var saved in await db.MapConfigurations.AsNoTracking().Where(m => m.IsActive).ToListAsync(ct)) {
            try {
                using var doc = JsonDocument.Parse(saved.MapMetadataJson ?? "{}");
                if (doc.RootElement.TryGetProperty("navigationMap", out var stored))
                    warehouseMaps[saved.WarehouseId] = stored.Deserialize<WarehouseMapDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            } catch (JsonException) { /* Invalid saved maps remain unavailable for task creation. */ }
        }
        if (navigation.WarehouseId is int activeWarehouse) warehouseMaps[activeWarehouse] = map;
        var stations = rows.Select(s => {
            var claim = berths.SingleOrDefault(b => b.StationId == s.StationId);
            var codes = s.Checkpoint != null && s.Checkpoint.WarehouseId == navigation.WarehouseId
                ? occupied.Where(p => p.Value.Contains(s.Checkpoint.ArucoId)).Select(p => p.Key).ToList() : [];
            if (claim != null && robots.SingleOrDefault(r => r.RobotId == claim.RobotId) is { } robot && !codes.Contains(robot.RobotCode)) codes.Add(robot.RobotCode);
            var hasMap = warehouseMaps.TryGetValue(s.Zone.WarehouseId, out var stationMap);
            var unavailableReason = UnavailableReason(s, stationMap ?? map, hasMap ? s.Zone.WarehouseId : navigation.WarehouseId);
            return new { s.StationId, s.ZoneId, s.Zone.WarehouseId, code = s.StationCode, name = s.StationName, type = s.StationType,
                s.CoordX, s.CoordY, s.CoordZ, s.YawAngle, coordinateFrame = "LOCAL_METRIC", s.QrCodePayload,
                arucoId = s.Checkpoint?.ArucoId, s.IsActive, s.Capacity, s.Revision,
                usable = unavailableReason == null, unavailableReason,
                isOccupied = s.IsOccupied || codes.Count > 0, occupantRobotCodes = codes, reservedRobotId = claim?.RobotId };
        }).ToList();
        var mapViews = warehouseMaps.Select(p => (WarehouseId: (int?)p.Key, Map: p.Value)).ToList();
        if (mapViews.Count == 0) mapViews.Add((navigation.WarehouseId, map));
        var transportPoints = mapViews.SelectMany(view => view.Map.Cells.SelectMany(r => r)
            .Where(c => c.Type == "qr" && c.ArucoId != null &&
                (!view.Map.DirectedLinks || c.IsIntersection || c.OperationalRole is "PICKUP" or "DELIVERY" or "TRANSFER"))
            .OrderBy(c => c.ArucoId).Select(c => {
                var station = stations.SingleOrDefault(s => s.WarehouseId == view.WarehouseId && s.arucoId == c.ArucoId);
                var reason = c.IsIntersection ? "Reserved intersection: transit only, no goods transfer." : station?.unavailableReason ??
                    (station == null ? "No operational station is configured at this checkpoint." : null);
                return new { warehouseId = view.WarehouseId, arucoId = c.ArucoId, label = c.Label,
                    stationId = station?.StationId, stationCode = station?.code, stationName = station?.name,
                    pickupEligible = reason == null && station!.type is "PICKUP" or "TRANSFER",
                    deliveryEligible = reason == null && station!.type is "DELIVERY" or "TRANSFER",
                    unavailableReason = reason, stationType = station?.type };
            })).ToList();
        return new { warehouses, zones, stations, transportPoints, robots, navigationWarehouseId = navigation.WarehouseId,
            mapRevision = map.Revision, capacityPolicy = "One robot per marker; reservations and last camera occupancy persist until confirmed clear. A reservation alone never proves physical docking." };
    }

    public static string? UnavailableReason(Station station, WarehouseMapDocument map, int? navigationWarehouseId)
    {
        if (!station.IsActive) return "Station is inactive.";
        if (!station.Zone.IsActive) return "Station zone is inactive.";
        if (!station.Zone.Warehouse.IsActive) return "Warehouse is inactive.";
        if (station.Checkpoint == null) return "Navigation checkpoint is not configured.";
        if (station.Checkpoint.WarehouseId != station.Zone.WarehouseId || station.Checkpoint.WarehouseId != navigationWarehouseId)
            return "Station does not belong to the active navigation warehouse.";
        var markers = map.Cells.SelectMany(row => row).Where(c => c.Type == "qr" && c.ArucoId == station.Checkpoint.ArucoId).ToArray();
        if (markers.Length != 1) return "Mapped checkpoint is missing or duplicated on the active map.";
        if (markers[0].IsIntersection) return "Mapped checkpoint is a reserved intersection.";
        if (map.DirectedLinks && markers[0].OperationalRole == "NAVIGATION") return "Through-lane checkpoint is transit-only.";
        if (markers[0].MarkerYawDeg == null || markers[0].MarkerSizeM == null) return "Mapped checkpoint has not been commissioned.";
        if (station.StationType is "PICKUP" or "DELIVERY" or "TRANSFER" && string.IsNullOrWhiteSpace(station.QrCodePayload))
            return "Station verification QR payload is not configured.";
        return null;
    }

    private static string Required(string? value, string name, int max)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > max) throw new ArgumentException($"{name} requires 1..{max} characters.");
        return value;
    }
    private async Task Guard(int warehouseId, CancellationToken ct)
    {
        if (await db.ChargingJobs.AnyAsync(j => j.Robot.WarehouseId == warehouseId && j.State != "COMPLETED", ct))
            throw new InvalidOperationException("Warehouse has reserved charging cycles. Finish/recover device movement before catalog changes.");
        if (await db.TaskAssignments.AnyAsync(a => a.AssignmentStatus == "ACTIVE" && a.Task.WarehouseId == warehouseId, ct))
            throw new InvalidOperationException("Warehouse has active assignments, including failed load holds. Complete or recover them before catalog changes.");
    }
    private static void Revision(int actual, int expected)
    { if (actual != expected) throw new InvalidOperationException("Catalog changed. Reload before saving."); }
    private void Audit(int userId, string entity, int id, object before, object after, string action = "UPDATE") =>
        db.SystemAuditLogs.Add(new() { UserId = userId, ActionCategory = "WAREHOUSE", ActionName = action,
            EntityType = entity, EntityId = id.ToString(), Description = JsonSerializer.Serialize(new { before, after }), CreatedAt = DateTime.UtcNow });

    public async Task<int> SaveWarehouse(int? id, WarehouseWrite request, int actor, CancellationToken ct)
    {
        var code = Required(request.Code, "Warehouse code", 30).ToUpperInvariant();
        var name = Required(request.Name, "Warehouse name", 120);
        if (request.Address?.Length > 255) throw new ArgumentException("Address must not exceed 255 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (config != null && !await MapSwitchGate.AllowsDispatch(db, config, ct))
            throw new InvalidOperationException("Map change in progress. Wait before changing station configuration.");
        var row = id.HasValue ? await db.Warehouses.SingleOrDefaultAsync(w => w.WarehouseId == id, ct) ?? throw new KeyNotFoundException() : new Warehouse();
        if (id.HasValue) { Revision(row.Revision, request.Revision); await Guard(id.Value, ct); }
        if (await db.Warehouses.AnyAsync(w => w.WarehouseId != (id ?? 0) && w.WarehouseCode.ToUpper() == code, ct)) throw new InvalidOperationException("Duplicate warehouse code.");
        if (!request.IsActive && id.HasValue && (await db.Zones.AnyAsync(z => z.WarehouseId == id && z.IsActive, ct) || await db.Robots.AnyAsync(r => r.WarehouseId == id && r.IsActive, ct)))
            throw new InvalidOperationException("Deactivate child zones/stations and robots before deactivating a warehouse.");
        if (!request.IsActive && id == navigation.WarehouseId) throw new InvalidOperationException("Cannot deactivate the warehouse configured for navigation.");
        var before = new { row.WarehouseCode, row.WarehouseName, row.Address, row.IsActive, row.Revision };
        row.WarehouseCode = code; row.WarehouseName = name; row.Address = request.Address?.Trim(); row.IsActive = request.IsActive; row.UpdatedAt = DateTime.UtcNow;
        if (id.HasValue) row.Revision++; else { row.CreatedAt = DateTime.UtcNow; db.Warehouses.Add(row); }
        await db.SaveChangesAsync(ct); Audit(actor, "Warehouse", row.WarehouseId, before, request, id.HasValue ? "UPDATE" : "CREATE");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row.WarehouseId;
    }

    public async Task<int> SaveZone(int? id, ZoneWrite request, int actor, CancellationToken ct)
    {
        var code = Required(request.Code, "Zone code", 30).ToUpperInvariant(); var name = Required(request.Name, "Zone name", 80);
        if (!ZoneTypes.Contains(request.Type) || request.SpeedLimit is <= 0 or > 1) throw new ArgumentException("Invalid zone type or speed limit (0 < limit <= 1).");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (config != null && !await MapSwitchGate.AllowsDispatch(db, config, ct))
            throw new InvalidOperationException("Map change in progress. Wait before changing station configuration.");
        var row = id.HasValue ? await db.Zones.SingleOrDefaultAsync(z => z.ZoneId == id, ct) ?? throw new KeyNotFoundException() : new Zone();
        if (id.HasValue) { Revision(row.Revision, request.Revision); await Guard(row.WarehouseId, ct); }
        await Guard(request.WarehouseId, ct);
        if (!await db.Warehouses.AnyAsync(w => w.WarehouseId == request.WarehouseId && w.IsActive, ct)) throw new ArgumentException("Choose an active warehouse.");
        if (id.HasValue && request.WarehouseId != row.WarehouseId) throw new InvalidOperationException("A zone cannot move between warehouses. Create a new zone to preserve history.");
        if (await db.Zones.AnyAsync(z => z.ZoneId != (id ?? 0) && z.WarehouseId == request.WarehouseId && (z.ZoneCode.ToUpper() == code || z.ZoneName.ToUpper() == name.ToUpper()), ct)) throw new InvalidOperationException("Duplicate zone code or name in this warehouse.");
        if (!request.IsActive && id.HasValue && await db.Stations.AnyAsync(s => s.ZoneId == id && s.IsActive, ct)) throw new InvalidOperationException("Deactivate stations before their zone.");
        var before = new { row.ZoneCode, row.ZoneName, row.ZoneType, row.SpeedLimit, row.IsActive, row.Revision };
        row.WarehouseId = request.WarehouseId; row.ZoneCode = code; row.ZoneName = name; row.ZoneType = request.Type; row.SpeedLimit = request.SpeedLimit; row.IsActive = request.IsActive; row.UpdatedAt = DateTime.UtcNow;
        if (id.HasValue) row.Revision++; else { row.CreatedAt = DateTime.UtcNow; db.Zones.Add(row); }
        await db.SaveChangesAsync(ct); Audit(actor, "Zone", row.ZoneId, before, request, id.HasValue ? "UPDATE" : "CREATE");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row.ZoneId;
    }

    public async Task<int> SaveStation(int? id, StationWrite request, int actor, CancellationToken ct)
    {
        var code = Required(request.Code, "Station code", 30).ToUpperInvariant(); var name = Required(request.Name, "Station name", 100);
        if (!StationTypes.Contains(request.Type)) throw new ArgumentException("Choose PICKUP, DELIVERY, TRANSFER (pickup and delivery), WAITING or CHARGING.");
        if (request.Capacity != 1) throw new ArgumentException("Initial physical capacity is exactly one robot per checkpoint.");
        if (request.CoordX is < -999999 or > 999999 || request.CoordY is < -999999 or > 999999 || request.CoordZ is < -999999 or > 999999 || request.YawAngle is < -180 or > 180) throw new ArgumentException("Invalid metric coordinates or yaw.");
        var payload = request.QrCodePayload?.Trim();
        if (payload?.Length > 150 || request.IsActive && request.Type is "PICKUP" or "DELIVERY" or "TRANSFER" && string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Active pickup/delivery requires a station verification QR payload (max 150 characters).");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (config != null && !await MapSwitchGate.AllowsDispatch(db, config, ct))
            throw new InvalidOperationException("Map change in progress. Wait before changing station configuration.");
        var zone = await db.Zones.Include(z => z.Warehouse).SingleOrDefaultAsync(z => z.ZoneId == request.ZoneId, ct) ?? throw new ArgumentException("Zone does not exist.");
        if (request.IsActive && (!zone.IsActive || !zone.Warehouse.IsActive)) throw new ArgumentException("An active station requires an active zone and warehouse.");
        await Guard(zone.WarehouseId, ct);
        var row = id.HasValue ? await db.Stations.Include(s => s.Zone).Include(s => s.Checkpoint).SingleOrDefaultAsync(s => s.StationId == id, ct) ?? throw new KeyNotFoundException() : new Station();
        if (id.HasValue) {
            Revision(row.Revision, request.Revision); await Guard(row.Zone.WarehouseId, ct);
            if (row.Zone.WarehouseId != zone.WarehouseId) throw new InvalidOperationException("Station cannot move between warehouses; preserve historical references.");
            if (row.IsOccupied || await db.StationBerths.AnyAsync(b => b.StationId == id, ct)) throw new InvalidOperationException("Release the occupied/reserved station before editing it.");
            if (row.Zone.WarehouseId == navigation.WarehouseId && row.Checkpoint != null && navigation.GetCheckpointOccupants().Values.Any(ids => ids.Contains(row.Checkpoint.ArucoId)) &&
                (request.ArucoId != row.Checkpoint.ArucoId || request.Type != row.StationType || !request.IsActive || row.ZoneId != request.ZoneId ||
                    row.CoordX != request.CoordX || row.CoordY != request.CoordY || row.CoordZ != request.CoordZ || row.YawAngle != request.YawAngle))
                throw new InvalidOperationException("A robot occupies this checkpoint/corridor. Clear it before changing placement, type or active state.");
        }
        if (await db.Stations.AnyAsync(s => s.StationId != (id ?? 0) && s.StationCode.ToUpper() == code, ct)) throw new InvalidOperationException("Duplicate station code.");
        if (!string.IsNullOrEmpty(payload) && await db.Stations.AnyAsync(s => s.StationId != (id ?? 0) && s.QrCodePayload == payload, ct)) throw new InvalidOperationException("Station verification QR payload must be unique.");
        if (request.IsActive && request.ArucoId == null) throw new ArgumentException("Active operational stations require an explicit ArUco checkpoint mapping.");
        if (request.ArucoId is int qr) {
            var savedMap = await db.MapConfigurations.AsNoTracking().SingleOrDefaultAsync(m => m.WarehouseId == zone.WarehouseId && m.IsActive, ct);
            var map = navigation.GetMap();
            if (savedMap != null && !string.IsNullOrEmpty(savedMap.MapMetadataJson)) {
                using var doc = JsonDocument.Parse(savedMap.MapMetadataJson);
                if (doc.RootElement.TryGetProperty("navigationMap", out var stored)) {
                    var current = stored.Deserialize<WarehouseMapDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    if (navigation.WarehouseId == zone.WarehouseId && current.Revision != map.Revision) throw new InvalidOperationException("Database map changed. Reload the navigation map before station commissioning.");
                    map = current;
                }
            }
            if (savedMap == null && navigation.WarehouseId != zone.WarehouseId) throw new ArgumentException("Configure this warehouse map before mapping stations.");
            var markers = map.Cells.SelectMany(r => r).Where(c => c.Type == "qr" && c.ArucoId == qr).ToList();
            if (markers.Count != 1 || markers[0].IsIntersection || markers[0].MarkerYawDeg == null || markers[0].MarkerSizeM == null) throw new ArgumentException("Map to a unique, commissioned QR outside reserved intersections.");
            if (map.DirectedLinks && markers[0].OperationalRole != request.Type) throw new ArgumentException("A lane scene station must match its commissioned physical bay role.");
            if (await db.StationCheckpoints.AnyAsync(m => m.WarehouseId == zone.WarehouseId && m.ArucoId == qr && m.StationId != (id ?? 0), ct)) throw new InvalidOperationException("Checkpoint is already mapped to another station (including inactive stations).");
        }
        var before = new { row.StationCode, row.StationName, row.StationType, row.ZoneId, row.CoordX, row.CoordY, row.CoordZ, row.YawAngle, row.QrCodePayload, arucoId = row.Checkpoint?.ArucoId, row.IsActive, row.Revision };
        row.ZoneId = request.ZoneId; row.StationCode = code; row.StationName = name; row.StationType = request.Type; row.CoordX = request.CoordX; row.CoordY = request.CoordY; row.CoordZ = request.CoordZ; row.YawAngle = request.YawAngle;
        row.QrCodePayload = string.IsNullOrEmpty(payload) ? null : payload; row.Capacity = 1; row.IsActive = request.IsActive; row.UpdatedAt = DateTime.UtcNow;
        if (id.HasValue) row.Revision++; else { row.CreatedAt = DateTime.UtcNow; db.Stations.Add(row); }
        if (request.ArucoId is int markerId) { row.Checkpoint ??= new StationCheckpoint { Station = row }; row.Checkpoint.WarehouseId = zone.WarehouseId; row.Checkpoint.ArucoId = markerId; }
        else if (row.Checkpoint != null) { db.StationCheckpoints.Remove(row.Checkpoint); row.Checkpoint = null; }
        await db.SaveChangesAsync(ct); Audit(actor, "Station", row.StationId, before, request, id.HasValue ? "UPDATE" : "CREATE");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row.StationId;
    }

    public async Task Berth(int id, BerthWrite request, int actor, CancellationToken ct)
    {
        Required(request.Reason, "Reason", 400);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (config != null && !await MapSwitchGate.AllowsDispatch(db, config, ct))
            throw new InvalidOperationException("Map change in progress. Wait before changing station configuration.");
        var station = await db.Stations.Include(s => s.Zone).ThenInclude(z => z.Warehouse).Include(s => s.Checkpoint).SingleOrDefaultAsync(s => s.StationId == id, ct) ?? throw new KeyNotFoundException();
        if (station.StationType is not ("WAITING" or "CHARGING")) throw new ArgumentException("Berth reservations apply only to WAITING or CHARGING stations.");
        var robot = await db.Robots.SingleOrDefaultAsync(r => r.RobotId == request.RobotId && r.IsActive, ct) ?? throw new ArgumentException("Robot not found or inactive.");
        if (await db.ChargingJobs.AnyAsync(j => j.RobotId == robot.RobotId && j.State != "COMPLETED", ct))
            throw new InvalidOperationException("Berth is managed by an active charging cycle; use charging safety recovery controls.");
        if (robot.WarehouseId != station.Zone.WarehouseId) throw new ArgumentException("Robot and station must share a warehouse.");
        if (await db.TaskAssignments.AnyAsync(a => a.RobotId == robot.RobotId && a.AssignmentStatus == "ACTIVE", ct)) throw new InvalidOperationException("Resolve the robot's active assignment before changing a berth reservation.");
        var existing = await db.StationBerths.SingleOrDefaultAsync(b => b.StationId == id, ct);
        if (request.Release) {
            if (!request.PhysicalAreaConfirmedClear || existing?.RobotId != request.RobotId) throw new InvalidOperationException("Release requires the reserved robot and physical clearance confirmation.");
            if (station.Checkpoint != null && navigation.GetCheckpointOccupants().Values.Any(p => p.Contains(station.Checkpoint.ArucoId))) throw new InvalidOperationException("Camera occupancy still covers this station; move/relocalize the robot first.");
            db.StationBerths.Remove(existing); station.IsOccupied = false;
        } else {
            if (!station.IsActive || !station.Zone.IsActive || !station.Zone.Warehouse.IsActive || station.Checkpoint == null) throw new InvalidOperationException("Choose an active mapped station.");
            if (existing != null || station.IsOccupied || navigation.GetCheckpointOccupants().Any(p => p.Key != robot.RobotCode && p.Value.Contains(station.Checkpoint.ArucoId))) throw new InvalidOperationException("Station capacity is exhausted.");
            if (await db.StationBerths.AnyAsync(b => b.RobotId == robot.RobotId, ct)) throw new InvalidOperationException("Robot already has a berth reservation.");
            db.StationBerths.Add(new() { StationId = id, RobotId = request.RobotId, ReservedAt = DateTime.UtcNow });
        }
        station.Revision++; station.UpdatedAt = DateTime.UtcNow; Audit(actor, "Station", id, new { reservedRobotId = existing?.RobotId }, request, request.Release ? "RELEASE_BERTH" : "RESERVE_BERTH");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
