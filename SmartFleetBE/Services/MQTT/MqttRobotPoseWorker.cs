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
    private readonly Dictionary<string,(int Id,int Warehouse,string Type)> robots=new();
    private DateTime refreshedAt;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using(var scope=scopes.CreateScope())
        {
            foreach(var robot in await scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>().Robots.AsNoTracking()
                .Where(r=>r.IsActive).Select(r=>new {r.RobotId,r.RobotCode,r.WarehouseId,r.RobotType}).ToListAsync(ct))
                robots[robot.RobotCode]=(robot.RobotId,robot.WarehouseId,robot.RobotType);
            refreshedAt=DateTime.UtcNow;
        }
        using var client=new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync+=async args=>
        {
            try
            {
                if(args.ApplicationMessage.Payload.Length>2048) return;
                if(DateTime.UtcNow-refreshedAt>TimeSpan.FromSeconds(5)) {
                    using var scope=scopes.CreateScope();
                    var latest=await scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>().Robots.AsNoTracking()
                        .Where(r=>r.IsActive).Select(r=>new {r.RobotId,r.RobotCode,r.WarehouseId,r.RobotType}).ToListAsync(ct);
                    robots.Clear();foreach(var r in latest) robots[r.RobotCode]=(r.RobotId,r.WarehouseId,r.RobotType);
                    refreshedAt=DateTime.UtcNow;
                }
                var parts=args.ApplicationMessage.Topic.Split('/');
                if(parts.Length!=4 || parts[0]!="smartfleet" || parts[1]!="robot" || parts[3]!="pose" ||
                    !robots.TryGetValue(parts[2],out var robot)) return;
                var pose=JsonSerializer.Deserialize<RobotPose>(args.ApplicationMessage.ConvertPayloadToString(),Json);
                var map=navigation.GetMap();
                if(pose==null || pose.RobotCode!=parts[2]) return;
                if(robot.Type=="SIMULATION") {
                    if(!config.GetValue<bool>("Simulation:Enabled") || robot.Warehouse!=scene.WarehouseId || !scene.Matches(map) || !poses.TryAccept(pose,map)) return;
                } else if(robot.Type=="PHYSICAL") {
                    if(robot.Warehouse!=navigation.WarehouseId || !poses.TryAcceptPhysical(pose,map)) return;
                } else return;
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
