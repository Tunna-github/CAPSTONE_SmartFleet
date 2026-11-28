using Npgsql;

namespace SmartFleetBE.Services;

// Navigation ownership is process-local. A shared DB must never start a second
// controller backend on another machine. This is a singleton guard, not HA failover.
public sealed class SharedDatabaseLease(IConfiguration config, IHostApplicationLifetime lifetime, SimulationScene scene,
    ILogger<SharedDatabaseLease> logger) : BackgroundService
{
    private NpgsqlConnection? connection;
    public async Task AcquireAsync(CancellationToken ct)
    {
        if (connection != null) return;
        var builder = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection")) {
            // Allow a brief WAN delay, but fail before the server's 30-second
            // abandoned-owner timeout can release the coordination lock.
            Pooling = false, KeepAlive = 5, CommandTimeout = 15, ApplicationName = "SmartFleet-coordinator-lease"
        };
        connection = new(builder.ConnectionString);
        try {
            await connection.OpenAsync(ct);
            // A broken network can leave this server session alive after the
            // backend has stopped. The two-second heartbeat keeps an active
            // owner alive; an abandoned session expires and releases its lock.
            await new NpgsqlCommand("SET idle_session_timeout = '30s'", connection).ExecuteNonQueryAsync(ct);
            var locked = await new NpgsqlCommand("SELECT pg_try_advisory_lock(490,20261128)", connection).ExecuteScalarAsync(ct);
            if (locked is not true) throw new InvalidOperationException("Another SmartFleet backend already controls this shared database. Use its web address; do not start another robot simulation.");
            await using var cmd = new NpgsqlCommand("""
                SELECT p."Key",p."SceneId",p."ChargingEnabled",m."WarehouseID"
                FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID"
                WHERE p."IsSelected" AND m."IsActive"
                """, connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != config["Deployment:MapKey"] || reader.GetString(1) != scene.SceneId ||
                reader.GetBoolean(2) != config.GetValue<bool>("Charging:Enabled") || reader.GetInt32(3) != config.GetValue<int>("Navigation:WarehouseId"))
                throw new InvalidOperationException("The selected Docker map differs from the active shared database profile. Use tools/select_aiven_map.py before starting.");
            logger.LogInformation("Acquired exclusive coordinator ownership for {MapKey}", reader.GetString(0));
        } catch { await connection.DisposeAsync(); connection = null; throw; }
    }
    public override async Task StartAsync(CancellationToken ct) { await AcquireAsync(ct); await base.StartAsync(ct); }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
                await new NpgsqlCommand("SELECT 1", connection).ExecuteScalarAsync(ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) {
            logger.LogCritical(ex, "Shared database coordinator connection was lost; stopping this backend and retaining robot safety holds.");
            lifetime.StopApplication();
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (connection != null) { await connection.DisposeAsync(); connection = null; }
    }
    public override void Dispose() { connection?.Dispose(); base.Dispose(); }
}
