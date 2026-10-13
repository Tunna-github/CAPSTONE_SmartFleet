
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

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// 1. CONTROLLERS AND CORS
// =====================================================

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:8443")
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
            "REST API for SmartFleet Autonomous Warehouse Fleet Management System"
    });

    options.AddSecurityDefinition(
        "bearer",
        new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "Enter the JWT access token returned by /api/v1/auth/login."
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
    builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Missing ConnectionStrings:DefaultConnection. " +
        "Set the Aiven PostgreSQL connection string.");
}

// Detect old SQL Server connection strings.
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

// IMPORTANT: PostgreSQL, not SQL Server.
builder.Services.AddDbContext<SmartFleetDbContext>(options =>
    options.UseNpgsql(connectionString));

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

builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddScoped<
    ITransportTaskService,
    TransportTaskService>();

builder.Services.AddScoped<
    IMaintenanceRecordService,
    MaintenanceRecordService>();

builder.Services.AddScoped<
    ITaskDispatchService,
    TaskDispatchService>();

builder.Services.AddScoped<
    IMqttRobotService,
    MqttRobotService>();

builder.Services.AddSingleton<
    IJwtTokenService,
    JwtTokenService>();

// =====================================================
// 6. MQTT SERVICES
// =====================================================

builder.Services.AddSingleton<MqttService>();

builder.Services.AddScoped<MissionLifecycleService>();

// Keep workers disabled while testing Aiven.
// Set Mqtt:EnableWorkers = true when ready.
if (builder.Configuration.GetValue<bool>("Mqtt:EnableWorkers"))
{
    builder.Services.AddHostedService<MqttMissionStatusWorker>();
    builder.Services.AddHostedService<RobotCommandOutboxWorker>();
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
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
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
// 9. BUILD APPLICATION
// =====================================================

var app = builder.Build();

// =====================================================
// 10. SWAGGER IN DEVELOPMENT
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

app.UseHttpsRedirection();

app.UseCors("AllowFrontend");

app.UseAuthentication();

app.UseAuthorization();

// =====================================================
// 12. CONTROLLER ROUTES
// =====================================================

app.MapControllers();

// =====================================================
// 13. TEMPORARY AIVEN DATABASE CONNECTION CHECK
// =====================================================

if (app.Environment.IsDevelopment())
{
    app.MapGet("/dev/db-check",
        async (SmartFleetDbContext db) =>
        {
            var provider =
                db.Database.ProviderName ?? "Unknown";

            try
            {
                // Get actual connection details.
                var connection =
                    db.Database.GetDbConnection();

                // Execute real SQL queries through EF Core.
                var totalUsers =
                    await db.Users.CountAsync();

                var totalTasks =
                    await db.TransportTasks.CountAsync();

                return Results.Ok(new
                {
                    connected = true,

                    provider = provider,

                    server = connection.DataSource,

                    database = connection.Database,

                    totalUsers = totalUsers,

                    totalTasks = totalTasks,

                    message =
                        "SmartFleet successfully queried Aiven PostgreSQL"
                });
            }
            catch (Exception ex)
            {
                // Full error goes to backend logs,
                // not to the browser response.
                app.Logger.LogError(
                    ex,
                    "Aiven PostgreSQL database check failed");

                return Results.Json(new
                {
                    connected = false,

                    provider = provider,

                    error = ex.GetType().Name,

                    message =
                        "Database check failed. " +
                        "View Visual Studio Output for details."
                },
                statusCode: 503);
            }
        });
}

// =====================================================
// 14. START APPLICATION
// =====================================================

app.Run();
