using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SmartFleetBE.Constants;

namespace SmartFleetBE.Hubs;

[Authorize(Roles = AppRoles.Admin + "," + AppRoles.WarehouseOperator + "," + AppRoles.MaintenanceTechnician)]
public sealed class FleetHub : Hub
{
    // Server-to-client notification hub. No untrusted client commands.
}
