using System.Text.Json;
using SmartFleetBE.Services;

namespace SmartFleetBE.Navigation;

public static class NavigationMqttDispatch
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task PublishAsync(
        MqttService mqtt,
        IEnumerable<NavigationDirective> directives,
        CancellationToken cancellationToken = default)
    {
        foreach (var directive in directives)
        {
            // Deliberately separate from the existing mission EXECUTE_TASK topic.
            await mqtt.PublishAsync(
                $"smartfleet/robot/{directive.RobotCode}/nav-control",
                JsonSerializer.Serialize(directive, JsonOptions),
                cancellationToken);
        }
    }
}
