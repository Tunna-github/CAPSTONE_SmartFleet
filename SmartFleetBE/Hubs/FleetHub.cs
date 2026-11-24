using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SmartFleetBE.Constants;
using System.Security.Claims;

namespace SmartFleetBE.Hubs;

[Authorize(Roles = AppRoles.Admin + "," + AppRoles.WarehouseOperator + "," + AppRoles.MaintenanceTechnician)]
public sealed class FleetHub(FleetConnectionRegistry connections) : Hub
{
    // Server-to-client notification hub. No untrusted client commands.
    public override Task OnConnectedAsync()
    {
        if (int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            connections.Add(Context.ConnectionId, userId, Context.Abort);
        return base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
