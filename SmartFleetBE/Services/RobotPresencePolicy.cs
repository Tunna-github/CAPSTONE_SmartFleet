namespace SmartFleetBE.Services;

public static class RobotPresencePolicy
{
    // DB ONLINE is historical; require recent samples from the robot as well.
    public static int TimeoutSeconds(IConfiguration config) =>
        Math.Clamp(config.GetValue("Dispatch:PresenceTimeoutSeconds", 45), 15, 300);

    public static bool IsFresh(Robot robot, DateTime now, IConfiguration config)
    {
        var cutoff = now.AddSeconds(-TimeoutSeconds(config));
        var future = now.AddSeconds(5);
        return robot.LastHeartbeatTime >= cutoff && robot.LastHeartbeatTime <= future &&
            robot.LastTelemetryTime >= cutoff && robot.LastTelemetryTime <= future;
    }
}
