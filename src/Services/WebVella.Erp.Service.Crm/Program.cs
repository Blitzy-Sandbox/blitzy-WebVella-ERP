// ============================================================================
// Program.cs — CRM Microservice Entry Point
// ============================================================================
// Minimal hosting API for the CRM microservice. Configures CrmDbContext with
// Npgsql, registers MassTransit for event-driven messaging, sets up gRPC
// server endpoints, Redis distributed caching, JWT Bearer authentication,
// and health checks.
//
// Source references:
//   - WebVella.Erp.Site.Crm/Startup.cs (original host composition)
//   - WebVella.Erp.Plugins.Crm/CrmPlugin.cs (domain bootstrap)
//
// All NuGet packages are declared in WebVella.Erp.Service.Crm.csproj with
// versions managed centrally via Directory.Packages.props (CPM).
// ============================================================================

using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

// Enable legacy timestamp behavior to match monolith Npgsql patterns where
// DateTime values are stored/read without UTC conversion.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Database — Register CrmDbContext with Npgsql provider
// ---------------------------------------------------------------------------
// Database-per-service model (AAP 0.4.1): CRM owns its own PostgreSQL
// database (erp_crm) with rec_account, rec_contact, rec_case, rec_address,
// rec_salutation, rec_case_status, rec_case_type, rec_industry tables and
// CRM-internal rel_* join tables.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=erp_crm;Username=postgres;Password=postgres";

builder.Services.AddDbContext<WebVella.Erp.Service.Crm.Database.CrmDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.MinBatchSize(1);
        npgsqlOptions.CommandTimeout(120);
    }));

// ---------------------------------------------------------------------------
// Authentication — JWT Bearer (AAP 0.8.3)
// ---------------------------------------------------------------------------
// Validates JWT tokens issued by the Core service for cross-service identity
// propagation. The CRM service does not issue tokens — it only validates them.
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"] ?? "DefaultDevKeyThatShouldBeReplacedInProduction_32chars!";
var jwtIssuer = jwtSection["Issuer"] ?? "webvella-erp";
var jwtAudience = jwtSection["Audience"] ?? "webvella-erp";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = bool.TryParse(jwtSection["RequireHttpsMetadata"], out var req) && req;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(2)
    };
});
builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// Redis — Distributed cache replacing monolith IMemoryCache (AAP 0.4.1)
// ---------------------------------------------------------------------------
// Per-service entity metadata caching with event-driven invalidation.
// Falls back to in-memory cache if Redis is unavailable during development.
var redisSection = builder.Configuration.GetSection("Redis");
var redisConnectionString = redisSection["ConnectionString"];
if (!string.IsNullOrEmpty(redisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = redisSection["InstanceName"] ?? "erp_crm_";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

// ---------------------------------------------------------------------------
// MassTransit — Event bus replacing monolith hook system (AAP 0.6.1)
// ---------------------------------------------------------------------------
// Publishes CRM domain events (AccountCreated, ContactUpdated, CaseDeleted)
// and subscribes to events from Core service (RecordCreated for user references).
var messagingSection = builder.Configuration.GetSection("Messaging");
var transport = messagingSection["Transport"] ?? "RabbitMQ";

builder.Services.AddMassTransit(busConfig =>
{
    // Register consumers from the CRM service assembly
    busConfig.AddConsumers(typeof(Program).Assembly);

    if (string.Equals(transport, "AmazonSQS", StringComparison.OrdinalIgnoreCase))
    {
        // Amazon SQS/SNS transport for LocalStack deployment validation
        var sqsSection = messagingSection.GetSection("AmazonSQS");
        busConfig.UsingAmazonSqs((context, cfg) =>
        {
            cfg.Host(sqsSection["Region"] ?? "us-east-1", h =>
            {
                h.AccessKey(sqsSection["AccessKey"] ?? "test");
                h.SecretKey(sqsSection["SecretKey"] ?? "test");
                var serviceUrl = sqsSection["ServiceUrl"];
                if (!string.IsNullOrEmpty(serviceUrl))
                {
                    h.Config(new Amazon.SQS.AmazonSQSConfig { ServiceURL = serviceUrl });
                    h.Config(new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl });
                }
            });
            cfg.ConfigureEndpoints(context);
        });
    }
    else
    {
        // RabbitMQ transport for local/Docker deployments (default)
        var rmqSection = messagingSection.GetSection("RabbitMQ");
        busConfig.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(rmqSection["Host"] ?? "rabbitmq",
                ushort.TryParse(rmqSection["Port"], out var port) ? port : (ushort)5672,
                rmqSection["VirtualHost"] ?? "/",
                h =>
                {
                    h.Username(rmqSection["Username"] ?? "guest");
                    h.Password(rmqSection["Password"] ?? "guest");
                });
            cfg.ConfigureEndpoints(context);
        });
    }
});

// ---------------------------------------------------------------------------
// gRPC — Inter-service communication server (AAP 0.6.1)
// ---------------------------------------------------------------------------
// CRM service exposes CrmGrpcService endpoints consumed by Project service
// (account/case-task relations), Mail service (contact resolution), and Gateway.
builder.Services.AddGrpc();

// ---------------------------------------------------------------------------
// MVC / API — Controllers with Newtonsoft.Json for API contract stability
// ---------------------------------------------------------------------------
builder.Services.AddControllers()
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ReferenceLoopHandling =
            Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        options.SerializerSettings.NullValueHandling =
            Newtonsoft.Json.NullValueHandling.Include;
    });

// ---------------------------------------------------------------------------
// Health Checks — Service readiness and liveness
// ---------------------------------------------------------------------------
builder.Services.AddHealthChecks();

// ---------------------------------------------------------------------------
// CORS — Cross-Origin Resource Sharing for API Gateway
// ---------------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// ---------------------------------------------------------------------------
// Build and configure the middleware pipeline
// ---------------------------------------------------------------------------
var app = builder.Build();

app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Map endpoints
app.MapControllers();
app.MapHealthChecks("/health");
app.MapGrpcService<WebVella.Erp.Service.Crm.Grpc.CrmGrpcService>();

app.Run();

// CrmDbContext is defined in Database/CrmDbContext.cs with full EF Core
// entity configuration, IDbContext implementation, and legacy compatibility.
