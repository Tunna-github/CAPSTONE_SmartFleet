using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace SmartFleetBE.Services;

public static class MapSwitchGate
{
    // First read in the assignment transaction. FOR UPDATE also forces a
    // Serializable retry if a switch changed this row after the snapshot began.
    // Request creation takes the same row lock before checking active missions.
    public static async Task<bool> AllowsDispatch(SmartFleetDbContext db, IConfiguration config, CancellationToken ct)
    {
        if (!config.GetValue<bool>("SharedDatabase:Enabled")) return true;
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        cmd.CommandText = "SELECT \"ActiveRequestId\" FROM \"MapSwitchControl\" WHERE \"Id\"=1 FOR UPDATE";
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value == null) throw new InvalidOperationException("Shared map dispatch gate is not initialized.");
        return value == DBNull.Value;
    }
}
