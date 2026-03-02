using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using WebVella.Erp.Service.Reporting.Database;

namespace WebVella.Erp.Service.Reporting
{
    /// <summary>
    /// Entry point for the Reporting microservice using .NET 10 Minimal Hosting API.
    /// This service provides aggregation and report generation capabilities,
    /// consuming event-sourced projections from other services (particularly Project and CRM)
    /// and generating business intelligence reports.
    ///
    /// Follows the CQRS (light) pattern (AAP 0.4.3) — reads from event-sourced projections
    /// populated by MassTransit event consumers. No gRPC endpoints (REST only).
    /// </summary>
    public class Program
    {
        public static void Main(string[] args)
        {
            // Enable legacy timestamp behavior to match monolith patterns
            System.AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            var builder = WebApplication.CreateBuilder(args);

            // Register ReportingDbContext with Npgsql (database-per-service: erp_reporting)
            builder.Services.AddDbContext<ReportingDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

            // MVC Controllers with Newtonsoft.Json serialization
            builder.Services.AddControllers();

            // Health checks
            builder.Services.AddHealthChecks();

            var app = builder.Build();

            app.UseRouting();
            app.MapHealthChecks("/health");
            app.MapControllers();

            app.Run();
        }
    }
}
