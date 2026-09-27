using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartFleetBE;
using SmartFleetBE.Repositories;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services;
using SmartFleetBE.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// Controllers
// =====================================================
builder.Services.AddControllers();


// =====================================================
// Swagger
// =====================================================
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "SmartFleet API",
        Version = "v1",
        Description = "REST API for SmartFleet Autonomous Warehouse Fleet Management System"
    });
});


// =====================================================
// Database
// =====================================================
builder.Services.AddDbContext<SmartFleetDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")
    ));


// =====================================================
// Repositories
// =====================================================
builder.Services.AddScoped<IAuthRepository, AuthRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ITransportTaskRepository, TransportTaskRepository>();
builder.Services.AddScoped<IMaintenanceRecordRepository, MaintenanceRecordRepository>();


// =====================================================
// Services
// =====================================================
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ITransportTaskService, TransportTaskService>();
builder.Services.AddScoped<IMaintenanceRecordService, MaintenanceRecordService>();
builder.Services.AddScoped<ITaskDispatchService, TaskDispatchService>();
builder.Services.AddScoped<IMqttRobotService, MqttRobotService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();


// =====================================================
// MQTT
// =====================================================
builder.Services.AddSingleton<MqttService>();


// =====================================================
// JWT
// =====================================================
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException(
        "Jwt:Key is missing from configuration.");

var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException(
        "Jwt:Issuer is missing from configuration.");

var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException(
        "Jwt:Audience is missing from configuration.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
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

builder.Services.AddAuthorization();


// =====================================================
// Build
// =====================================================
var app = builder.Build();


// =====================================================
// Swagger
// =====================================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "SmartFleet API v1"
        );

        options.RoutePrefix = "swagger";
    });
}


// =====================================================
// Middleware
// =====================================================
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();