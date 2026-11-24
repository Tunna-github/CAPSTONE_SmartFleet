using System.Collections.Concurrent;

namespace SmartFleetBE.Hubs;

public sealed class FleetConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (int UserId, Action Abort)> connections = new();
    public void Add(string connectionId, int userId, Action abort) => connections[connectionId] = (userId, abort);
    public void Remove(string connectionId) => connections.TryRemove(connectionId, out _);
    public void RevokeUser(int userId)
    {
        foreach (var item in connections.Where(c => c.Value.UserId == userId))
            if (connections.TryRemove(item.Key, out var connection)) connection.Abort();
    }
}
