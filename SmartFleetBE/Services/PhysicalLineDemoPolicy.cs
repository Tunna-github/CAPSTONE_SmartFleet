namespace SmartFleetBE.Services;

// Explicit, per-device marker-event demo. Does not claim metric pose/heading.
public static class PhysicalLineDemoPolicy
{
    public const string Source = "ARUCO_LINE_DEMO";
    public static bool Allows(IConfiguration config,string code) =>
        config.GetValue<bool>("PhysicalRobots:Enabled") && config.GetValue<bool>("DemoRoute:Enabled") &&
        !config.GetValue<bool>("Charging:Enabled") &&
        (config.GetSection("PhysicalRobots:LineDemoRobotCodes").Get<string[]>() ?? [])
            .Contains(code,StringComparer.OrdinalIgnoreCase);
    public static bool SupervisedStraight(IConfiguration config,string code) => Allows(config,code) &&
        (config.GetSection("PhysicalRobots:SupervisedStraightLineCodes").Get<string[]>() ?? [])
            .Contains(code,StringComparer.OrdinalIgnoreCase);
}
