using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;

namespace SmartFleetBE.Navigation;

/// <summary>Stores the editable QR grid in the active warehouse map's metadata.</summary>
public sealed class DatabaseNavigationMapStore(
    IServiceScopeFactory scopes, IConfiguration configuration) : INavigationMapStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int? _warehouseId;

    public WarehouseMapDocument LoadOrCreate(WarehouseMapDocument legacyMap)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        var warehouseId = WarehouseId(db);
        var row = db.MapConfigurations.SingleOrDefault(m => m.WarehouseId == warehouseId && m.IsActive);
        var metadata = ReadMetadata(row?.MapMetadataJson);
        if (metadata["navigationMap"] is { } saved)
            return saved.Deserialize<WarehouseMapDocument>(JsonOptions)
                ?? throw new InvalidDataException("Database navigationMap is empty.");

        // Import the old local map once. Existing SLAM/image metadata stays intact.
        if (row == null)
        {
            row = new MapConfiguration
            {
                WarehouseId = warehouseId, MapVersion = "nav-" + Guid.NewGuid().ToString("N")[..20],
                MapName = "Warehouse navigation map", MapSource = "MANUAL",
                CoordinateSystem = "LOCAL_METRIC", ResolutionMeterPerPixel = 0.05m,
                IsActive = true, CreatedAt = DateTime.UtcNow
            };
            db.MapConfigurations.Add(row);
        }
        SetMap(row, metadata, legacyMap);
        db.SaveChanges();
        return legacyMap;
    }

    public void Save(WarehouseMapDocument map, int expectedRevision)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        var warehouseId = WarehouseId(db);
        var row = db.MapConfigurations.SingleOrDefault(m => m.WarehouseId == warehouseId && m.IsActive)
            ?? throw new DbUpdateConcurrencyException("The active warehouse map changed. Reload the map.");
        var metadata = ReadMetadata(row.MapMetadataJson);
        var saved = metadata["navigationMap"]?.Deserialize<WarehouseMapDocument>(JsonOptions);
        if (saved?.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("Database map revision changed. Reload the map.");
        SetMap(row, metadata, map);
        // MapMetadataJson is a concurrency token: a concurrent update cannot be overwritten.
        db.SaveChanges();
    }

    private int WarehouseId(SmartFleetDbContext db)
    {
        if (_warehouseId.HasValue) return _warehouseId.Value;
        var configured = configuration.GetValue<int?>("Navigation:WarehouseId");
        var query = db.Warehouses.Where(w => w.IsActive);
        if (configured.HasValue) query = query.Where(w => w.WarehouseId == configured.Value);
        var ids = query.Select(w => w.WarehouseId).Take(2).ToArray();
        if (ids.Length != 1)
            throw new InvalidOperationException(
                "Navigation requires one active warehouse. Set Navigation:WarehouseId when multiple warehouses exist.");
        _warehouseId = ids[0];
        return ids[0];
    }

    private static JsonObject ReadMetadata(string? json) => string.IsNullOrWhiteSpace(json)
        ? new JsonObject()
        : JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("MapMetadataJson must be a JSON object; existing metadata was not replaced.");

    private static void SetMap(MapConfiguration row, JsonObject metadata, WarehouseMapDocument map)
    {
        metadata["navigationMap"] = JsonSerializer.SerializeToNode(map, JsonOptions);
        metadata["navigationMapUpdatedAtUtc"] = DateTime.UtcNow;
        row.MapMetadataJson = metadata.ToJsonString(JsonOptions);
    }
}
