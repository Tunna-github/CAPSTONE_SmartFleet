using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

public sealed class MqttRobotPoseWorker(IServiceScopeFactory scopes,IConfiguration config,NavigationCoordinator navigation,
    SimulationScene scene,RobotPoseStore poses,FleetRealtimePublisher realtime,ILogger<MqttRobotPoseWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string,(int Id,int Warehouse)> robots=new();
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using(var scope=scopes.CreateScope())
        {
            foreach(var robot in await scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>().Robots.AsNoTracking()
                .Where(r=>r.IsActive).Select(r=>new {r.RobotId,r.RobotCode,r.WarehouseId}).ToListAsync(ct))
                robots[robot.RobotCode]=(robot.RobotId,robot.WarehouseId);
        }
        using var client=new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync+=async args=>
        {
            try
            {
                if(args.ApplicationMessage.Payload.Length>2048) return;
                var parts=args.ApplicationMessage.Topic.Split('/');
                if(parts.Length!=4 || parts[0]!="smartfleet" || parts[1]!="robot" || parts[3]!="pose" ||
                    !robots.TryGetValue(parts[2],out var robot) || robot.Warehouse!=scene.WarehouseId) return;
                var pose=JsonSerializer.Deserialize<RobotPose>(args.ApplicationMessage.ConvertPayloadToString(),Json);
                var map=navigation.GetMap();
                if(pose==null || pose.RobotCode!=parts[2] || !scene.Matches(map) || !poses.TryAccept(pose,map)) return;
                await realtime.PoseUpdatedAsync(pose,ct);
            }
            catch(Exception ex) when(ex is not OperationCanceledException) {logger.LogWarning(ex,"Invalid visualization pose");}
        };
        var options=MqttService.CreateOptions(config,"SmartFleet-pose-"+Guid.NewGuid().ToString("N"));
        while(!ct.IsCancellationRequested)
        {
            try
            {
                if(!client.IsConnected)
                {
                    await client.ConnectAsync(options,ct);
                    await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f=>f.WithTopic("smartfleet/robot/+/pose")
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)).Build(),ct);
                }
                await Task.Delay(2000,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
            catch(Exception ex) {logger.LogWarning(ex,"Pose subscriber reconnecting");await Task.Delay(2000,ct);}
        }
    }
}
