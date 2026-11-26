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

public sealed class StationCatalogService(SmartFleetDbContext db, NavigationCoordinator navigation)
{
    public static readonly string[] StationTypes = ["PICKUP", "DELIVERY", "WAITING", "CHARGING"];
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
        var stations = rows.Select(s => {
            var claim = berths.SingleOrDefault(b => b.StationId == s.StationId);
            var codes = s.Checkpoint != null && s.Checkpoint.WarehouseId == navigation.WarehouseId
                ? occupied.Where(p => p.Value.Contains(s.Checkpoint.ArucoId)).Select(p => p.Key).ToList() : [];
            if (claim != null && robots.SingleOrDefault(r => r.RobotId == claim.RobotId) is { } robot && !codes.Contains(robot.RobotCode)) codes.Add(robot.RobotCode);
            var marker = s.Checkpoint == null ? null : map.Cells.SelectMany(r => r).SingleOrDefault(c => c.Type == "qr" && c.ArucoId == s.Checkpoint.ArucoId);
            return new { s.StationId, s.ZoneId, s.Zone.WarehouseId, code = s.StationCode, name = s.StationName, type = s.StationType,
                s.CoordX, s.CoordY, s.CoordZ, s.YawAngle, coordinateFrame = "LOCAL_METRIC", s.QrCodePayload,
                arucoId = s.Checkpoint?.ArucoId, s.IsActive, s.Capacity, s.Revision,
                usable = s.IsActive && s.Zone.IsActive && s.Zone.Warehouse.IsActive && s.Checkpoint != null &&
                    s.Checkpoint.WarehouseId == navigation.WarehouseId && marker is { IsIntersection: false, MarkerYawDeg: not null, MarkerSizeM: not null },
                isOccupied = s.IsOccupied || codes.Count > 0, occupantRobotCodes = codes, reservedRobotId = claim?.RobotId };
        }).ToList();
        return new { warehouses, zones, stations, robots, navigationWarehouseId = navigation.WarehouseId,
            mapRevision = map.Revision, capacityPolicy = "One robot per marker; reservations and last camera occupancy persist until confirmed clear. No automatic docking or charging control." };
    }

    private static string Required(string? value, string name, int max)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > max) throw new ArgumentException($"{name} requires 1..{max} characters.");
        return value;
    }
    private async Task Guard(int warehouseId, CancellationToken ct)
    {
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
        if (!StationTypes.Contains(request.Type)) throw new ArgumentException("Choose PICKUP, DELIVERY, WAITING or CHARGING. Dual-use stations are not supported.");
        if (request.Capacity != 1) throw new ArgumentException("Initial physical capacity is exactly one robot per checkpoint.");
        if (request.CoordX is < -999999 or > 999999 || request.CoordY is < -999999 or > 999999 || request.CoordZ is < -999999 or > 999999 || request.YawAngle is < -180 or > 180) throw new ArgumentException("Invalid metric coordinates or yaw.");
        var payload = request.QrCodePayload?.Trim();
        if (payload?.Length > 150 || request.IsActive && request.Type is "PICKUP" or "DELIVERY" && string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Active pickup/delivery requires a station verification QR payload (max 150 characters).");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var zone = await db.Zones.Include(z => z.Warehouse).SingleOrDefaultAsync(z => z.ZoneId == request.ZoneId, ct) ?? throw new ArgumentException("Zone does not exist.");
        if (request.IsActive && (!zone.IsActive || !zone.Warehouse.IsActive)) throw new ArgumentException("An active station requires an active zone and warehouse.");
        await Guard(zone.WarehouseId, ct);
        var row = id.HasValue ? await db.Stations.Include(s => s.Zone).Include(s => s.Checkpoint).SingleOrDefaultAsync(s => s.StationId == id, ct) ?? throw new KeyNotFoundException() : new Station();
        if (id.HasValue) {
            Revision(row.Revision, request.Revision); await Guard(row.Zone.WarehouseId, ct);
            if (row.Zone.WarehouseId != zone.WarehouseId) throw new InvalidOperationException("Station cannot move between warehouses; preserve historical references.");
            if (row.IsOccupied || await db.StationBerths.AnyAsync(b => b.StationId == id, ct)) throw new InvalidOperationException("Release the occupied/reserved station before editing it.");
            if (row.Checkpoint != null && navigation.GetCheckpointOccupants().Values.Any(ids => ids.Contains(row.Checkpoint.ArucoId)) &&
                (request.ArucoId != row.Checkpoint.ArucoId || request.Type != row.StationType || !request.IsActive || row.ZoneId != request.ZoneId ||
                    row.CoordX != request.CoordX || row.CoordY != request.CoordY || row.CoordZ != request.CoordZ || row.YawAngle != request.YawAngle))
                throw new InvalidOperationException("A robot occupies this checkpoint/corridor. Clear it before changing placement, type or active state.");
        }
        if (await db.Stations.AnyAsync(s => s.StationId != (id ?? 0) && s.StationCode.ToUpper() == code, ct)) throw new InvalidOperationException("Duplicate station code.");
        if (!string.IsNullOrEmpty(payload) && await db.Stations.AnyAsync(s => s.StationId != (id ?? 0) && s.QrCodePayload == payload, ct)) throw new InvalidOperationException("Station verification QR payload must be unique.");
        if (request.IsActive && request.ArucoId == null) throw new ArgumentException("Active operational stations require an explicit ArUco checkpoint mapping.");
        if (request.ArucoId is int qr) {
            if (navigation.WarehouseId != zone.WarehouseId) throw new ArgumentException("Configure the navigation map for this warehouse before mapping stations.");
            var savedMap = await db.MapConfigurations.AsNoTracking().SingleOrDefaultAsync(m => m.WarehouseId == zone.WarehouseId && m.IsActive, ct);
            var map = navigation.GetMap();
            if (savedMap != null && !string.IsNullOrEmpty(savedMap.MapMetadataJson)) {
                using var doc = JsonDocument.Parse(savedMap.MapMetadataJson);
                if (doc.RootElement.TryGetProperty("navigationMap", out var stored)) {
                    var current = stored.Deserialize<WarehouseMapDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    if (current.Revision != map.Revision) throw new InvalidOperationException("Database map changed. Reload the navigation map before station commissioning.");
                    map = current;
                }
            }
            var markers = map.Cells.SelectMany(r => r).Where(c => c.Type == "qr" && c.ArucoId == qr).ToList();
            if (markers.Count != 1 || markers[0].IsIntersection || markers[0].MarkerYawDeg == null || markers[0].MarkerSizeM == null) throw new ArgumentException("Map to a unique, commissioned QR outside reserved intersections.");
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
        var station = await db.Stations.Include(s => s.Zone).ThenInclude(z => z.Warehouse).Include(s => s.Checkpoint).SingleOrDefaultAsync(s => s.StationId == id, ct) ?? throw new KeyNotFoundException();
        if (station.StationType is not ("WAITING" or "CHARGING")) throw new ArgumentException("Berth reservations apply only to WAITING or CHARGING stations.");
        var robot = await db.Robots.SingleOrDefaultAsync(r => r.RobotId == request.RobotId && r.IsActive, ct) ?? throw new ArgumentException("Robot not found or inactive.");
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
