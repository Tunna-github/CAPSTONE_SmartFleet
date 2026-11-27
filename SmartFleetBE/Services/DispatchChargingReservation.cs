using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Services;

internal static class DispatchChargingReservation
{
    // Acquire before reading the dispatch snapshot. Dock reports update both rows
    // frequently; on a remote DB those writes otherwise starve Serializable
    // assignment transactions. Locks last until commit/rollback. All eligibility,
    // camera freshness and berth checks still run inside the same transaction.
    public static async Task LockReadyAsync(SmartFleetDbContext db, long taskId, int? robotId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT j."Id" FROM "ChargingJobs" j
            JOIN "Robots" r ON r."RobotID"=j."RobotId"
            WHERE j."State"='READY' AND r."OperationalStatus"='AVAILABLE'
              AND r."WarehouseID"=(SELECT "WarehouseID" FROM "TransportTasks" WHERE "TaskID"={taskId})
              AND ({robotId}::integer IS NULL OR r."RobotID"={robotId})
            ORDER BY j."RobotId" FOR UPDATE OF j,r
            """, ct);
    }
}
