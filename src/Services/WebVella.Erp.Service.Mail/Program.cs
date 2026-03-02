// ============================================================================
// Program.cs — Mail/Notification Microservice Entry Point
// ============================================================================
// Minimal hosting API for the Mail/Notification microservice. Configures
// MailDbContext with Npgsql, registers services, and sets up the middleware
// pipeline. This file enables the service to compile and run as an
// independently deployable container.
// ============================================================================

using Microsoft.EntityFrameworkCore;
using WebVella.Erp.Service.Mail.Database;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Database — Register MailDbContext with Npgsql provider
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? builder.Configuration.GetConnectionString("MailDb")
    ?? "Host=localhost;Port=5432;Database=erp_mail;Username=postgres;Password=postgres";

builder.Services.AddDbContext<MailDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.MinBatchSize(1);
        npgsqlOptions.CommandTimeout(120);
    }));

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
// Build and configure the application
// ---------------------------------------------------------------------------
var app = builder.Build();

app.UseRouting();
app.MapControllers();

app.Run();
