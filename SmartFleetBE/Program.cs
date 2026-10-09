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
// Controllers
// =====================================================

builder.Services.AddControllers();
// =====================================================
// CORS Configuration (CHÈN VÀO ĐÂY)
// =====================================================

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:8443") // Cổng HTTPS của Frontend bên bạn
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});


// =====================================================
// Swagger / OpenAPI
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

    // -------------------------------------------------
    // JWT Bearer Authentication for Swagger
    // -------------------------------------------------

    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
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
// Database
// =====================================================

builder.Services.AddDbContext<SmartFleetDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString(
            "DefaultConnection"
        )
    )
);


// =====================================================
// Repositories
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
builder.Services.AddScoped<MissionLifecycleService>();
builder.Services.AddHostedService<MqttMissionStatusWorker>();
builder.Services.AddHostedService<RobotCommandOutboxWorker>();


// =====================================================
// JWT Configuration
// =====================================================

var jwtKey =
    builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException(
        "Jwt:Key is missing from configuration."
    );

var jwtIssuer =
    builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException(
        "Jwt:Issuer is missing from configuration."
    );

var jwtAudience =
    builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException(
        "Jwt:Audience is missing from configuration."
    );


// =====================================================
// Authentication
// =====================================================

builder.Services
    .AddAuthentication(
        JwtBearerDefaults.AuthenticationScheme
    )
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                // --------------------------------------
                // Validate token issuer
                // --------------------------------------

                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,

                // --------------------------------------
                // Validate token audience
                // --------------------------------------

                ValidateAudience = true,
                ValidAudience = jwtAudience,

                // --------------------------------------
                // Validate JWT signature
                // --------------------------------------

                ValidateIssuerSigningKey = true,

                IssuerSigningKey =
                    new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtKey)
                    ),

                // --------------------------------------
                // Validate token expiration
                // --------------------------------------

                ValidateLifetime = true,

                ClockSkew =
                    TimeSpan.FromSeconds(30)
            };
    });


// =====================================================
// Authorization
// =====================================================

builder.Services.AddAuthorization();


// =====================================================
// Build Application
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

        options.DocumentTitle =
            "SmartFleet API Documentation";
    });
}


// =====================================================
// Middleware
// =====================================================

app.UseHttpsRedirection();
// Kích hoạt CORS (BẮT BUỘC PHẢI ĐẶT TRƯỚC AUTHENTICATION)
app.UseCors("AllowFrontend");

// Authentication MUST come before Authorization
app.UseAuthentication();

app.UseAuthorization();


// =====================================================
// Controllers
// =====================================================

app.MapControllers();


// =====================================================
// Run
// =====================================================

app.Run();