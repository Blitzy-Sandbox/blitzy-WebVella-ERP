using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.Service.Reporting.Domain.Services;
using WebVella.Erp.SharedKernel;

namespace WebVella.Erp.Service.Reporting
{
    /// <summary>
    /// Entry point for the Reporting microservice using .NET 10 Minimal Hosting API.
    ///
    /// This service provides aggregation and report generation capabilities,
    /// consuming event-sourced projections from other services (particularly Project and CRM)
    /// and generating business intelligence reports.
    ///
    /// <para><b>Architecture Pattern:</b> CQRS (light) — AAP 0.4.3</para>
    /// <list type="bullet">
    ///   <item>Reads from event-sourced projections populated by MassTransit event consumers</item>
    ///   <item>No gRPC endpoints — REST only (per AAP structure — no Grpc/ folder listed for Reporting)</item>
    ///   <item>No background jobs (unlike Core/Mail/Project — no Jobs/ folder listed)</item>
    ///   <item>Domain events consumed via MassTransit subscribers in Events/ folder</item>
    ///   <item>Report execution exposed via REST API in Controllers/</item>
    /// </list>
    ///
    /// <para><b>Transformation from Monolith:</b></para>
    /// Extracted from <c>WebVella.Erp.Plugins.Project/Services/ReportService.cs</c> into a
    /// standalone independently deployable microservice. The old WebHost + Startup pattern
    /// from <c>WebVella.Erp.Site.Project/</c> is replaced with .NET 10 Minimal Hosting API.
    ///
    /// <para><b>Authentication:</b> JWT-only (no cookies — cookies are Gateway/BFF only per AAP 0.8.3)</para>
    /// <para><b>Serialization:</b> Newtonsoft.Json with ErpDateTimeJsonConverter (NOT System.Text.Json)</para>
    /// <para><b>Database:</b> Database-per-service — owns <c>erp_reporting</c> PostgreSQL schema exclusively</para>
    /// <para><b>Caching:</b> Redis distributed cache replacing monolith IMemoryCache</para>
    /// <para><b>Messaging:</b> MassTransit with RabbitMQ (Docker) / Amazon SQS (LocalStack) transport</para>
    /// </summary>
    public class Program
    {
        public static void Main(string[] args)
        {
            // ================================================================
            // NPGSQL LEGACY TIMESTAMP SWITCH
            // Preserved from monolith Site Startup.cs (source line 34):
            //   AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
            // Required for backward-compatible DateTime handling with PostgreSQL
            // timestamp columns until system tables are migrated to timestamptz.
            // ================================================================
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            var builder = WebApplication.CreateBuilder(args);

            // ================================================================
            // SHARED KERNEL CONFIGURATION INITIALIZATION
            // Initializes ErpSettings static properties from appsettings.json.
            // Must be called before any code that uses ErpSettings (e.g.,
            // ErpDateTimeJsonConverter uses ErpSettings.TimeZoneName and
            // ErpSettings.JsonDateTimeFormat).
            // ================================================================
            ErpSettings.Initialize(builder.Configuration);

            // ================================================================
            // RESPONSE COMPRESSION (matching monolith Startup.cs lines 40-41)
            // ================================================================
            builder.Services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Fastest);
            builder.Services.AddResponseCompression(options =>
            {
                options.Providers.Add<GzipCompressionProvider>();
            });

            // ================================================================
            // ROUTING
            // ================================================================
            builder.Services.AddRouting(options =>
            {
                options.LowercaseUrls = true;
            });

            // ================================================================
            // CORS — Permissive default policy for development
            // Matching monolith Startup.cs pattern (source lines 50-56):
            //   services.AddCors(options => {
            //       options.AddDefaultPolicy(policy =>
            //           policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            //   });
            // ================================================================
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
            });

            // ================================================================
            // MVC CONTROLLERS WITH NEWTONSOFT.JSON SERIALIZATION
            // CRITICAL: Newtonsoft.Json is the serializer, NOT System.Text.Json.
            // This preserves [JsonProperty] annotation compatibility with the
            // monolith REST API v3 response envelope (BaseResponseModel).
            // ErpDateTimeJsonConverter is added for timezone-aware DateTime
            // serialization matching the monolith's format.
            // (Source: Startup.cs lines 59-69)
            // ================================================================
            builder.Services.AddControllers()
                .AddNewtonsoftJson(options =>
                {
                    options.SerializerSettings.Converters.Add(new ErpDateTimeJsonConverter());
                    options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                });

            // ================================================================
            // GLOBAL NEWTONSOFT.JSON DEFAULT SETTINGS
            // Preserved from monolith Startup.cs (source lines 75-78):
            //   JsonConvert.DefaultSettings = () => new JsonSerializerSettings {
            //       Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
            //   };
            // Ensures all explicit JsonConvert.SerializeObject/DeserializeObject
            // calls also use the ERP DateTime converter.
            // ================================================================
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
            };

            // ================================================================
            // AUTHENTICATION — JWT-ONLY
            // Per AAP 0.1.2, 0.8.1, 0.8.3: Microservices use JWT-only auth.
            // No cookies — cookies are Gateway/BFF only.
            // Replaces the monolith's hybrid JWT_OR_COOKIE scheme
            // (source Startup.cs lines 80-117).
            // JWT tokens are issued by the Core service and validated here.
            // ================================================================
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = builder.Configuration["Jwt:Issuer"],
                    ValidAudience = builder.Configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? "DefaultDevKeyReplace"))
                };
            });

            // ================================================================
            // AUTHORIZATION
            // Default policy requires authenticated users on all controllers
            // marked with [Authorize] (per AAP 0.8.2).
            // ================================================================
            builder.Services.AddAuthorization();

            // ================================================================
            // EF CORE — REPORTING DATABASE CONTEXT
            // Database-per-service: erp_reporting PostgreSQL database.
            // No other service reads from or writes to this database (AAP 0.8.2).
            // Connection string from appsettings.json → ConnectionStrings:Default.
            // ================================================================
            builder.Services.AddDbContext<ReportingDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

            // ================================================================
            // REDIS DISTRIBUTED CACHE
            // NEW — replacing monolith IMemoryCache with 1-hour TTL (AAP 0.1.1).
            // Enables per-service local cache with event-driven invalidation
            // for report projection caching across Reporting service instances.
            // ================================================================
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = builder.Configuration["Redis:ConnectionString"];
                options.InstanceName = builder.Configuration["Redis:InstanceName"];
            });

            // ================================================================
            // MASSTRANSIT EVENT BUS — CORE TO REPORTING SERVICE
            // CQRS light pattern (AAP 0.4.3): The Reporting service consumes
            // domain events from Core, CRM, Project, and Mail services to build
            // materialized report projections (timelog, task, project).
            //
            // Transport switching:
            //   - "RabbitMQ" for local/Docker development
            //   - "AmazonSQS" for LocalStack deployment validation (AAP 0.7.4)
            //
            // All consumers in the Events/ folder are auto-registered via
            // assembly scanning (TimelogCreatedConsumer, TimelogDeletedConsumer,
            // TaskUpdatedConsumer, ProjectUpdatedConsumer).
            // ================================================================
            builder.Services.AddMassTransit(busConfig =>
            {
                busConfig.AddConsumers(typeof(Program).Assembly);

                var transport = builder.Configuration["Messaging:Transport"];
                if (string.Equals(transport, "RabbitMQ", StringComparison.OrdinalIgnoreCase))
                {
                    busConfig.UsingRabbitMq((context, cfg) =>
                    {
                        cfg.Host(builder.Configuration["Messaging:RabbitMQ:Host"], h =>
                        {
                            h.Username(builder.Configuration["Messaging:RabbitMQ:Username"] ?? "guest");
                            h.Password(builder.Configuration["Messaging:RabbitMQ:Password"] ?? "guest");
                        });
                        cfg.ConfigureEndpoints(context);
                    });
                }
                else
                {
                    busConfig.UsingAmazonSqs((context, cfg) =>
                    {
                        cfg.Host(builder.Configuration["Messaging:AmazonSQS:Region"] ?? "us-east-1", h =>
                        {
                            h.Scope("reporting", true);
                            h.AccessKey(builder.Configuration["Messaging:AmazonSQS:AccessKey"] ?? "test");
                            h.SecretKey(builder.Configuration["Messaging:AmazonSQS:SecretKey"] ?? "test");
                        });
                        cfg.ConfigureEndpoints(context);
                    });
                }
            });

            // ================================================================
            // DOMAIN SERVICE REGISTRATION
            // ReportAggregationService provides monthly timelog aggregation and
            // report generation capabilities. Registered as scoped to align with
            // the scoped ReportingDbContext lifetime.
            // ================================================================
            builder.Services.AddScoped<ReportAggregationService>();

            // ================================================================
            // HEALTH CHECKS
            // Basic health check endpoint at /health for container orchestration
            // (Docker HEALTHCHECK, Kubernetes readiness/liveness probes).
            // ================================================================
            builder.Services.AddHealthChecks();

            // ================================================================
            // BUILD THE APPLICATION
            // ================================================================
            var app = builder.Build();

            // ================================================================
            // MIDDLEWARE PIPELINE
            // Order follows ASP.NET Core conventions and monolith patterns:
            // 1. Response Compression (before static files — source line 147)
            // 2. CORS (before routing — source line 149)
            // 3. Routing
            // 4. Authentication
            // 5. Authorization
            // 6. Endpoint mapping
            // ================================================================
            app.UseResponseCompression();

            app.UseCors();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            // Map health check endpoint for container orchestration
            app.MapHealthChecks("/health");

            // Map REST API controllers
            app.MapControllers();

            // ================================================================
            // RUN THE APPLICATION
            // ================================================================
            app.Run();
        }
    }
}
