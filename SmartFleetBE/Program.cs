
using System;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

using SmartFleetBE;
using SmartFleetBE.Repositories;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services;
using SmartFleetBE.Services.Interfaces;
using SmartFleetBE.Hubs;
using SmartFleetBE.Navigation;

var builder = WebApplication.CreateBuilder(args);
// LOCAL WEBOTS PILOT only. This file is ignored by Git and is never deployed.
builder.Configuration.AddJsonFile("App_Data/demo-route-config.json", optional: true, reloadOnChange: true);
if (builder.Configuration.GetValue<bool>("DemoRoute:Enabled") && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Simulation"))
    throw new InvalidOperationException("DemoRoute requires Development or the explicit Simulation environment.");

// =====================================================
// 1. CONTROLLERS AND CORS
// =====================================================

builder.Services.AddControllers();
builder.Services.AddScoped<StationCatalogService>();
builder.Services.AddScoped<RobotRegistryService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<MapSwitchService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<FleetConnectionRegistry>();
builder.Services.AddSingleton<FleetRealtimePublisher>();
builder.Services.AddSingleton(sp=>SimulationScene.Load(builder.Configuration,builder.Environment));
builder.Services.AddSingleton<RobotPoseStore>();
builder.Services.AddSingleton<SimulationViewerSessions>();
builder.Services.AddHttpClient("simulation-assets",client=>client.Timeout=TimeSpan.FromSeconds(20));

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(builder.Configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? ["http://localhost:8443", "http://localhost:5173"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// =====================================================
// 2. SWAGGER / OPENAPI
// =====================================================

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SmartFleet API",
        Version = "v1",
        Description =
            "REST API for SmartFleet Autonomous " +
            "Warehouse Fleet Management System"
    });

    options.AddSecurityDefinition(
        "bearer",
        new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "Enter the JWT access token returned by " +
                "/api/v1/auth/login."
        });

    options.AddSecurityRequirement(document =>
        new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(
                "bearer",
                document
            )] = []
        });
});

// =====================================================
// 3. AIVEN POSTGRESQL DATABASE
// =====================================================

var connectionString =
    builder.Configuration.GetConnectionString(
        "DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Missing ConnectionStrings:DefaultConnection. " +
        "Set the Aiven PostgreSQL connection string.");
}

if (
    connectionString.Contains(
        "Initial Catalog=",
        StringComparison.OrdinalIgnoreCase)
    ||
    connectionString.Contains(
        "Integrated Security=",
        StringComparison.OrdinalIgnoreCase)
    ||
    connectionString.Contains(
        "Trusted_Connection=",
        StringComparison.OrdinalIgnoreCase)
)
{
    throw new InvalidOperationException(
        "An old SQL Server connection string is still active. " +
        "Check User Secrets, appsettings.Development.json " +
        "and environment variables.");
}

builder.Services.AddDbContext<SmartFleetDbContext>(
    options => options.UseNpgsql(connectionString));

// =====================================================
// 4. REPOSITORIES
// =====================================================

builder.Services.AddScoped<
    IAuthRepository,
    AuthRepository>();

builder.Services.AddScoped<
    IUserRepository,
    UserRepository>();

builder.Services.AddScoped<
    ITransportTaskRepository,
    TransportTaskRepository>();

builder.Services.AddScoped<
    IMaintenanceRecordRepository,
    MaintenanceRecordRepository>();

// =====================================================
// 5. BUSINESS SERVICES
// =====================================================

builder.Services.AddScoped<
    IAuthService,
    AuthService>();

builder.Services.AddScoped<
    IUserService,
    UserService>();

builder.Services.AddScoped<
    ITransportTaskService,
    TransportTaskService>();

builder.Services.AddScoped<
    IMaintenanceRecordService,
    MaintenanceRecordService>();

builder.Services.AddScoped<
    ITaskDispatchService,
    TaskDispatchService>();

// Existing automatic robot selector.
builder.Services.AddScoped<AutoDispatchService>();

builder.Services.AddScoped<
    IMqttRobotService,
    MqttRobotService>();

builder.Services.AddSingleton<
    IJwtTokenService,
    JwtTokenService>();

// =====================================================
// 6. MQTT AND SCHEDULED DISPATCH WORKERS
// =====================================================

// Keep the existing Fast ACK implementation unchanged.
if (builder.Configuration.GetValue<bool>("SharedDatabase:Enabled"))
{
    builder.Services.AddSingleton<SharedDatabaseLease>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SharedDatabaseLease>());
}
builder.Services.AddSingleton<MqttService>();
// QR graph A* / Webots progress state for one-robot pilot.
builder.Services.AddSingleton<QrDemoRouteService>();
// Shared, single-process navigation map and intersection coordinator.
builder.Services.AddSingleton<INavigationMapStore, DatabaseNavigationMapStore>();
builder.Services.AddSingleton<NavigationCoordinator>();

builder.Services.AddScoped<MissionLifecycleService>();
builder.Services.AddScoped<MissionExecutionService>();
builder.Services.AddScoped<ChargingService>();
builder.Services.AddHostedService<ChargingWorker>();
builder.Services.AddScoped<TaskInterventionService>();
builder.Services.AddScoped<CancelledTaskService>();
builder.Services.AddScoped<RobotHealthMonitor>();

bool enableMqttWorkers =
    builder.Configuration.GetValue<bool>(
        "Mqtt:EnableWorkers");

bool enableAutoDispatch =
    builder.Configuration.GetValue<bool>(
        "AutoDispatch:EnableOnCreate");

if (enableMqttWorkers)
{
    // Existing worker: receives mission status
    // and sends ACK to the robot.
    builder.Services
        .AddHostedService<MqttMissionStatusWorker>();

    builder.Services.AddHostedService<MqttRobotTelemetryWorker>();
    if (builder.Configuration.GetValue<bool>("Simulation:Enabled") || builder.Configuration.GetValue<bool>("PhysicalRobots:Enabled"))
        builder.Services.AddHostedService<MqttRobotPoseWorker>();
    builder.Services.AddHostedService<RobotHealthWorker>();
    if (builder.Configuration.GetValue<bool>("DemoRoute:Enabled"))
        builder.Services.AddHostedService<QrDemoProgressWorker>();

    // Disabled by default until the JetRacer understands NAV_CONTROL.
    // Exactly ONE live Backend must coordinate a physical intersection.
    if (builder.Configuration.GetValue<bool>("Navigation:Enabled"))
    {
        builder.Services.AddHostedService<MqttNavigationWorker>();
    }

    // Existing worker: sends committed commands
    // from RobotCommandOutbox through MQTT.
    builder.Services
        .AddHostedService<RobotCommandOutboxWorker>();

    // NEW:
    // Periodically checks scheduled AUTO tasks.
    // Only registered when both MQTT and AUTO
    // dispatch are enabled.
    if (enableAutoDispatch)
    {
        builder.Services
            .AddHostedService<ScheduledAutoDispatchWorker>();
    }
}

// =====================================================
// 7. JWT AUTHENTICATION
// =====================================================

var jwtKey = builder.Configuration["Jwt:Key"];

if (
    string.IsNullOrWhiteSpace(jwtKey)
    ||
    Encoding.UTF8.GetByteCount(jwtKey) < 32
)
{
    throw new InvalidOperationException(
        "Jwt:Key must contain at least 32 UTF-8 bytes.");
}

var jwtIssuer =
    builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException(
        "Jwt:Issuer is missing.");

var jwtAudience =
    builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException(
        "Jwt:Audience is missing.");

builder.Services
    .AddAuthentication(
        JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs/robots"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<SmartFleetDbContext>();
                if (!await CurrentAccountValidator.IsCurrentAsync(context.Principal, db,
                    context.HttpContext.RequestAborted))
                    context.Fail("Account is inactive or its permissions changed. Sign in again.");
            }
        };
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,

                ValidateAudience = true,
                ValidAudience = jwtAudience,

                ValidateIssuerSigningKey = true,

                IssuerSigningKey =
                    new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtKey)),

                ValidateLifetime = true,

                ClockSkew = TimeSpan.FromSeconds(30)
            };
    });

// =====================================================
// 8. AUTHORIZATION
// =====================================================

builder.Services.AddAuthorization();

// =====================================================
// 9. BUILD
// =====================================================

var app = builder.Build();
// Acquire before any MQTT/navigation worker is even constructed.
if (builder.Configuration.GetValue<bool>("SharedDatabase:Enabled"))
    await app.Services.GetRequiredService<SharedDatabaseLease>().AcquireAsync(CancellationToken.None);

// =====================================================
// 10. SWAGGER
// =====================================================

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "SmartFleet API v1");

        options.RoutePrefix = "swagger";

        options.DocumentTitle =
            "SmartFleet API Documentation";
    });
}

// =====================================================
// 11. MIDDLEWARE
// =====================================================

if (builder.Configuration.GetValue("Hosting:UseHttpsRedirection", true)) app.UseHttpsRedirection();
app.UseWebSockets();

app.UseCors("AllowFrontend");

app.UseAuthentication();

app.UseAuthorization();

// =====================================================
// 12. CONTROLLERS
// =====================================================

app.MapControllers();
app.MapGet("/healthz", async (SmartFleetDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status="ready" }) : Results.StatusCode(503)).AllowAnonymous();
app.MapHub<FleetHub>("/hubs/robots", options => options.CloseOnAuthenticationExpiration = true);

// =====================================================
// 13. DEVELOPMENT DATABASE CHECK
// =====================================================

if (app.Environment.IsDevelopment())
{
    app.MapGet(
        "/dev/db-check",
        async (SmartFleetDbContext db) =>
        {
            var provider =
                db.Database.ProviderName ?? "Unknown";

            try
            {
                var connection =
                    db.Database.GetDbConnection();

                var totalUsers =
                    await db.Users.CountAsync();

                var totalTasks =
                    await db.TransportTasks.CountAsync();

                return Results.Ok(new
                {
                    connected = true,

                    provider,

                    server = connection.DataSource,
                    database = connection.Database,

                    totalUsers,
                    totalTasks,

                    message =
                        "SmartFleet successfully queried " +
                        "Aiven PostgreSQL"
                });
            }
            catch (Exception ex)
            {
                app.Logger.LogError(
                    ex,
                    "Aiven PostgreSQL database check failed");

                return Results.Json(
                    new
                    {
                        connected = false,
                        provider,
                        error = ex.GetType().Name,
                        message =
                            "Database check failed. " +
                            "View backend logs for details."
                    },
                    statusCode: 503);
            }
        });
}

// =====================================================
// 14. START BACKEND
// =====================================================

app.Run();
