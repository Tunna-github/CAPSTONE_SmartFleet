namespace SmartFleetBE.Services;

// Listing, manual confirmation and AUTO dispatch must use the same robot type.
public static class DispatchRobotTypePolicy
{
    // Explicit demo opt-in. Defaults and all presence/warehouse/safety gates stay intact.
    public static string[] AllowedTypes(IConfiguration configuration) =>
        configuration.GetValue<bool>("Dispatch:AllowMixedRobotTypes") && configuration.GetValue<bool>("Simulation:Enabled")
            ? ["PHYSICAL", "SIMULATION"] : [RequiredType(configuration)];

    public static bool Allows(IConfiguration configuration, string type) =>
        AllowedTypes(configuration).Contains(type, StringComparer.OrdinalIgnoreCase);

    public static string RequiredType(IConfiguration configuration) =>
        configuration.GetValue<bool>("Simulation:Enabled") ? "SIMULATION" : "PHYSICAL";
}
