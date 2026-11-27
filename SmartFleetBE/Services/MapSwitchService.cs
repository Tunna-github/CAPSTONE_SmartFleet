using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace SmartFleetBE.Services;

public sealed record MapSwitchWrite(Guid RequestId, string TargetKey, string ExpectedActiveKey, string Reason, bool Confirmed);

public sealed class MapSwitchService(SmartFleetDbContext db, IConfiguration config)
{
    // Physical geometry/manifest validation is repeated on the host before backup
    // and inside the stopped-world transaction before any activation.
    private const string BlockedSql = """
        SELECT CASE
        WHEN EXISTS(SELECT 1 FROM "TaskAssignments" WHERE "AssignmentStatus"='ACTIVE')
          OR EXISTS(SELECT 1 FROM "Missions" WHERE "MissionStatus" NOT IN ('COMPLETED','FAILED','ABORTED'))
          THEN 'A robot has an active or interrupted mission. Finish or safely recover it first.'
        WHEN EXISTS(SELECT 1 FROM "TaskInterventions" WHERE "State" IN ('CANCEL_STOP_PENDING','RETURNING_HOME','CANCEL_REQUESTED')
          OR ("CommandId" IS NOT NULL AND NOT "CommandAcknowledged" AND "State"<>'NORMAL'))
          THEN 'A robot safety command is still awaiting acknowledgement.'
        WHEN EXISTS(SELECT 1 FROM "Robots" r WHERE r."RobotType"<>'SIMULATION' OR NOT EXISTS(
          SELECT 1 FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE m."WarehouseID"=r."WarehouseID"))
          THEN 'Map switching requires robots registered to the configured simulation warehouses.'
        WHEN EXISTS(SELECT 1 FROM "ChargingJobs" WHERE "State" NOT IN ('READY','PAUSED','COMPLETED'))
          THEN 'A robot is returning to charge or needs charging recovery. Wait until it is safely docked.'
        WHEN EXISTS(SELECT 1 FROM "StationBerths" b WHERE NOT EXISTS(SELECT 1 FROM "ChargingJobs" j
          WHERE j."RobotId"=b."RobotId" AND j."StationId"=b."StationId" AND j."State" IN ('READY','PAUSED')))
          THEN 'A reserved charging berth needs recovery.'
        ELSE NULL END
        """;

    private bool Enabled => config.GetValue<bool>("SharedDatabase:Enabled");
    private Guid HostId => Guid.TryParse(config["MapSwitch:HostId"], out var id) ? id : Guid.Empty;
    public static void Validate(MapSwitchWrite value)
    {
        if (value.RequestId == Guid.Empty || value.TargetKey is not ("map1" or "map2") ||
            value.ExpectedActiveKey is not ("map1" or "map2") || !value.Confirmed ||
            string.IsNullOrWhiteSpace(value.Reason) || value.Reason.Trim().Length > 500)
            throw new ArgumentException("Choose Map 1 or Map 2, provide a reason and confirm the world change.");
    }

    private NpgsqlConnection Connection => (NpgsqlConnection)db.Database.GetDbConnection();
    private NpgsqlCommand Command(string text, params (string, object?)[] values)
    {
        var command = new NpgsqlCommand(text, Connection, (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private async Task<bool> WorkerOnline(CancellationToken ct)
    {
        await using var cmd = Command("SELECT EXISTS(SELECT 1 FROM \"MapSwitchWorkers\" WHERE \"HostId\"=@host AND \"LastSeenAt\">now()-interval '25 seconds')", ("host", HostId));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    private async Task<object?> Latest(CancellationToken ct)
    {
        await using var cmd = Command("""
            SELECT jsonb_build_object('id',"Id",'targetKey',"TargetKey",'state',"State",'message',"Message",
            'backupReady',"BackupSha256" IS NOT NULL,'requestedAt',"RequestedAt",'updatedAt',"UpdatedAt")::text
            FROM "MapSwitchRequests" ORDER BY "RequestedAt" DESC LIMIT 1
            """);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json == null ? null : JsonSerializer.Deserialize<JsonElement>(json);
    }
    public async Task<object> Read(CancellationToken ct)
    {
        if (!Enabled) return new { available = false };
        await db.Database.OpenConnectionAsync(ct);
        var online = HostId != Guid.Empty && await WorkerOnline(ct);
        await using var gate = Command("SELECT \"ActiveRequestId\" FROM \"MapSwitchControl\" WHERE \"Id\"=1");
        var active = await gate.ExecuteScalarAsync(ct);
        await using var blocked = Command(BlockedSql);
        var reason = await blocked.ExecuteScalarAsync(ct) as string;
        return new { available = online, blockedReason = active is Guid ? "A map change or recovery hold is already in progress." :
            !online ? "The simulation host map manager is offline. Ask the host administrator to start it." : reason,
            operation = await Latest(ct) };
    }

    public async Task<object> Request(MapSwitchWrite value, int actor, CancellationToken ct)
    {
        Validate(value);
        if (!Enabled || HostId == Guid.Empty) throw new InvalidOperationException("Web map switching is not configured on this host.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var gate = Command("SELECT \"ActiveRequestId\" FROM \"MapSwitchControl\" WHERE \"Id\"=1 FOR UPDATE");
        var active = await gate.ExecuteScalarAsync(ct);
        if (active == null) throw new InvalidOperationException("Map switch gate is not initialized.");
        await using var previous = Command("SELECT \"TargetKey\" FROM \"MapSwitchRequests\" WHERE \"Id\"=@id", ("id", value.RequestId));
        if (await previous.ExecuteScalarAsync(ct) is string target)
        {
            if (target != value.TargetKey) throw new InvalidOperationException("This request ID was already used for another map.");
            await tx.CommitAsync(ct); return new { requestId = value.RequestId };
        }
        if (active is Guid) throw new InvalidOperationException("Another map change or recovery hold is already in progress.");
        if (!await WorkerOnline(ct)) throw new InvalidOperationException("The simulation host map manager is offline.");
        await using var profile = Command("""
            SELECT p."Key" FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE p."IsSelected" AND m."IsActive"
            """);
        var current = await profile.ExecuteScalarAsync(ct) as string;
        if (current != value.ExpectedActiveKey) throw new InvalidOperationException("The active map changed. Reload before selecting again.");
        if (current == value.TargetKey) throw new InvalidOperationException("This map is already active.");
        await using var blocked = Command(BlockedSql);
        if (await blocked.ExecuteScalarAsync(ct) is string reason) throw new InvalidOperationException(reason);
        await using var save = Command("""
            INSERT INTO "MapSwitchRequests" ("Id","HostId","SourceKey","TargetKey","RequestedBy","Reason")
            VALUES (@id,@host,@source,@target,@actor,@reason);
            UPDATE "MapSwitchControl" SET "ActiveRequestId"=@id WHERE "Id"=1;
            INSERT INTO "SystemAuditLogs" ("UserID","ActionCategory","ActionName","EntityType","EntityID","Description")
            VALUES (@actor,'SIMULATION','REQUEST_MAP_SWITCH','MapProfile',@target,@reason);
            """, ("id", value.RequestId), ("host", HostId), ("source", current), ("target", value.TargetKey),
            ("actor", actor), ("reason", value.Reason.Trim()));
        await save.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return new { requestId = value.RequestId };
    }
}
