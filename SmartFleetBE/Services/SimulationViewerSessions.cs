using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using SmartFleetBE.Hubs;

namespace SmartFleetBE.Services;

public sealed class SimulationViewerSessions
{
    public sealed record Session(string Id,ClaimsPrincipal User,int UserId,DateTime ExpiresAtUtc);
    private readonly ConcurrentDictionary<string,Session> sessions=new();
    public const string CookieName="smartfleet_simulation_view";
    public SimulationViewerSessions(FleetConnectionRegistry connections)
    {
        connections.UserRevoked+=id=> {foreach(var entry in sessions.Where(s=>s.Value.UserId==id)) sessions.TryRemove(entry.Key,out _);};
    }
    public Session Create(ClaimsPrincipal user)
    {
        foreach(var old in sessions.Where(s=>s.Value.ExpiresAtUtc<DateTime.UtcNow)) sessions.TryRemove(old.Key,out _);
        if(sessions.Count>=512) throw new InvalidOperationException("Too many simulation viewers. Retry later.");
        var expiry=DateTime.UtcNow.AddMinutes(15);
        if(long.TryParse(user.FindFirstValue("exp"),out var jwtExpiry)) expiry=new[] {expiry,DateTimeOffset.FromUnixTimeSeconds(jwtExpiry).UtcDateTime}.Min();
        var item=new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),user,
            int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),expiry);
        sessions[item.Id]=item;return item;
    }
    public Session? Get(string? id)=>id!=null && id.Length==64 && sessions.TryGetValue(id,out var session) && session.ExpiresAtUtc>DateTime.UtcNow ? session:null;

    // Observe-only W3D: the initial handshake is the only permitted browser command.
    public static bool IsAllowedClientMessage(string message,bool initialized)=>!initialized && message=="w3d;broadcast";
}
