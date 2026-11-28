using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFleetBE.Constants;
using SmartFleetBE.Hubs;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Controllers;

[ApiController,Route("api/v1/simulation")]
[Authorize(Roles=AppRoles.AdminOrWarehouseOperator+","+AppRoles.MaintenanceTechnician)]
public sealed class SimulationController(IConfiguration config,SimulationScene scene,NavigationCoordinator navigation,
    RobotPoseStore poses,SimulationViewerSessions sessions,SmartFleetDbContext db,FleetConnectionRegistry connections,
    IHttpClientFactory clients,ILogger<SimulationController> logger) : ControllerBase
{
    public sealed record BridgeRegistration(string RunId,string WorldSha256);
    private bool Enabled=>config.GetValue<bool>("Simulation:Enabled");
    private bool StreamReady=>Enabled && scene.Matches(navigation.GetMap()) && poses.Snapshot(navigation.GetMap().Revision).Any(p=>p.Source=="SIMULATION_GROUND_TRUTH");
    [HttpGet("map-template"),Authorize(Roles=AppRoles.Admin)]
    public IActionResult MapTemplate() => !Enabled || scene.Anchors.Count < 3 ? NotFound() : Ok(scene.MapTemplate(navigation.GetMap()));
    [HttpGet]
    public IActionResult Status()=>Ok(new {enabled=Enabled,scene.SceneId,mapRevision=navigation.GetMap().Revision,
        mapCompatible=Enabled && scene.Matches(navigation.GetMap()),streamReady=StreamReady,
        mode="w3d",poseSource="SIMULATION_GROUND_TRUTH"});

    [HttpGet("poses")]
    public IActionResult Poses()=>Ok(new {mapRevision=navigation.GetMap().Revision,
        poses=poses.Snapshot(navigation.GetMap().Revision).Where(p=>p.Source=="ARUCO_IMU_ODOMETRY" || Enabled && scene.Matches(navigation.GetMap())),serverTimeUtc=DateTime.UtcNow});

    [AllowAnonymous,HttpPost("bridge/register")]
    public IActionResult Register(BridgeRegistration request)
    {
        if(!Enabled || !SimulationScene.ValidBridgeKey(config,Request.Headers["X-Simulation-Key"].ToString())) return Unauthorized();
        if(!Guid.TryParse(request.RunId,out _) || request.WorldSha256!=scene.WorldSha256) return Conflict(new {message="Scene hash mismatch."});
        if(!scene.Matches(navigation.GetMap())) return Conflict(new {message="Saved map geometry does not match the simulation world. Recommission before running."});
        poses.BeginRun(request.RunId);
        return Ok(new {mapRevision=navigation.GetMap().Revision,scene.SceneId,scene.WarehouseId});
    }

    [HttpPost("session")]
    public IActionResult Session()
    {
        if(!Enabled) return StatusCode(503,new {message="Simulation streaming is not configured on this server."});
        if(!StreamReady) return StatusCode(503,new {message="Webots is not reporting fresh positions. The simulator may be starting or unresponsive. Check its logs; active missions remain held for recovery."});
        try
        {
            var session=sessions.Create(User);
            Response.Cookies.Append(SimulationViewerSessions.CookieName,session.Id,new CookieOptions {
                HttpOnly=true,Secure=config.GetValue<bool?>("Simulation:SecureCookies") ?? Request.IsHttps,SameSite=SameSiteMode.Strict,IsEssential=true,
                Path="/api/v1/simulation/view",Expires=session.ExpiresAtUtc});
            return Ok(new {viewerPath="/simulation-viewer.html",session.ExpiresAtUtc,scene.SceneId});
        }
        catch(InvalidOperationException ex) {return StatusCode(429,new {message=ex.Message});}
    }

    [AllowAnonymous,HttpGet("view/{**asset}")]
    public async Task View(string? asset,CancellationToken ct)
    {
        var session=sessions.Get(Request.Cookies[SimulationViewerSessions.CookieName]);
        if(!Enabled || session==null) {Response.StatusCode=401;return;}
        if(!Uri.TryCreate(config["Simulation:StreamUrl"],UriKind.Absolute,out var stream) || stream.Scheme is not ("ws" or "wss"))
        {Response.StatusCode=503;return;}
        if(HttpContext.WebSockets.IsWebSocketRequest)
        {
            if(!await CurrentAccountValidator.IsCurrentAsync(session.User,db,ct)) {Response.StatusCode=401;return;}
            await Relay(stream,session,ct);return;
        }
        if(string.IsNullOrEmpty(asset) || !scene.AllowsAsset(asset)) {Response.StatusCode=404;return;}
        // Webots resolves HTTP assets from the project root; world texture URLs are world-relative.
        var resource = asset.StartsWith("textures/",StringComparison.Ordinal) ? "worlds/"+asset : asset;
        var target=new UriBuilder(stream) {Scheme=stream.Scheme=="wss"?"https":"http",Path="/"+resource,Query=""}.Uri;
        try
        {
            using var response=await clients.CreateClient("simulation-assets").GetAsync(target,HttpCompletionOption.ResponseHeadersRead,ct);
            Response.StatusCode=(int)response.StatusCode;
            Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/octet-stream";
            Response.Headers.CacheControl="private, max-age=300";
            await response.Content.CopyToAsync(Response.Body,ct);
        }
        catch(HttpRequestException) {Response.StatusCode=502;}
    }

    private async Task Relay(Uri target,SimulationViewerSessions.Session session,CancellationToken ct)
    {
        using var upstream=new ClientWebSocket();
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(session.ExpiresAtUtc-DateTime.UtcNow);
        using var connectionTimeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try {await upstream.ConnectAsync(target,connectionTimeout.Token);}
        catch(Exception ex) when(ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {Response.StatusCode=503;return;}
        using var browser=await HttpContext.WebSockets.AcceptWebSocketAsync();
        var id="simulation:"+Guid.NewGuid().ToString("N");
        connections.Add(id,session.UserId,HttpContext.Abort);
        async Task ReceiveClient()
        {
            var buffer=new byte[1024];var initialized=false;
            while(!lifetime.IsCancellationRequested)
            {
                var message=await browser.ReceiveAsync(buffer,lifetime.Token);
                if(message.MessageType==WebSocketMessageType.Close) return;
                var text=Encoding.UTF8.GetString(buffer,0,message.Count);
                if(message.MessageType!=WebSocketMessageType.Text || !message.EndOfMessage) return;
                // The official R2025a viewer emits pause while loading a scene even in broadcast mode.
                // Discard all control traffic; keep the observation stream alive during asset loading.
                if(!SimulationViewerSessions.IsAllowedClientMessage(text,initialized))
                {if(!initialized) return;continue;}
                initialized=true;
                await upstream.SendAsync(buffer.AsMemory(0,message.Count),WebSocketMessageType.Text,true,lifetime.Token);
            }
        }
        async Task ReceiveServer()
        {
            var buffer=new byte[65536];
            var firstMessage=true;
            while(!lifetime.IsCancellationRequested)
            {
                using var initialTimeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if(firstMessage) initialTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var message=await upstream.ReceiveAsync(buffer,initialTimeout.Token);
                firstMessage=false;
                if(message.MessageType==WebSocketMessageType.Close) return;
                // Device editor windows are not part of this observe-only viewer.
                if(message.MessageType==WebSocketMessageType.Text && message.EndOfMessage &&
                    buffer.AsSpan(0,message.Count).StartsWith("robot window:"u8)) continue;
                await browser.SendAsync(buffer.AsMemory(0,message.Count),message.MessageType,message.EndOfMessage,lifetime.Token);
            }
        }
        var receivingClient=ReceiveClient();
        var receivingServer=ReceiveServer();
        try {await await Task.WhenAny(receivingClient,receivingServer);}
        catch(Exception ex) when(ex is WebSocketException or OperationCanceledException) {logger.LogDebug(ex,"Simulation viewer disconnected");}
        finally
        {
            lifetime.Cancel();connections.Remove(id);upstream.Abort();browser.Abort();
            try {await Task.WhenAll(receivingClient,receivingServer);}
            catch(Exception ex) when(ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {logger.LogDebug(ex,"Simulation relay closed");}
        }
    }
}
