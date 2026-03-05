// =============================================================================
// Program.cs — Project/Task Microservice Entry Point (Composition Root)
// =============================================================================
// Minimal hosting API entry point for the Project/Task microservice, replacing
// the monolith's WebVella.Erp.Site.Project/Startup.cs and Program.cs with a
// modern .NET 10 minimal hosting pattern using WebApplication.CreateBuilder.
//
// Key architectural changes from monolith:
//   - NO plugin model — Project service is a standalone ASP.NET Core app
//   - NO services.AddErp() — replaced by explicit per-service DI registrations
//   - JWT-only authentication (no cookie auth) for API endpoints (AAP 0.8.3)
//   - MassTransit/RabbitMQ replaces PostgreSQL LISTEN/NOTIFY for inter-service events
//   - Redis replaces IMemoryCache for distributed caching across instances
//   - gRPC server endpoints for inter-service task/timelog resolution
//   - Background jobs use IHostedService pattern instead of monolith JobManager
//
// Source references:
//   - WebVella.Erp.Site.Project/Startup.cs (original host composition)
//   - WebVella.Erp.Site.Project/Program.cs (original WebHost builder)
//   - WebVella.Erp.Site.Project/Config.json (original configuration)
//   - WebVella.Erp.Plugins.Project/ProjectPlugin.cs (domain bootstrap)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.Service.Project.Controllers;
using WebVella.Erp.Service.Project.Domain.Services;
using WebVella.Erp.Service.Project.Grpc;
using WebVella.Erp.Service.Project.Jobs;

namespace WebVella.Erp.Service.Project
{
    /// <summary>
    /// Application entry point for the Project/Task microservice.
    /// Uses .NET 10 minimal hosting API (WebApplication.CreateBuilder).
    /// Configures JWT authentication, gRPC, MassTransit, Redis, domain services,
    /// background jobs, and health check endpoints.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main entry point. Configures and starts the Project/Task microservice
        /// with all required middleware, DI registrations, and service initialization.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        public static void Main(string[] args)
        {
            // =====================================================================
            // Npgsql Legacy Timestamp Behavior
            // Preserved from monolith Startup.cs line 34 — required until system
            // tables are migrated to timestamptz.
            // =====================================================================
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            var builder = WebApplication.CreateBuilder(args);

            // =====================================================================
            // ErpSettings initialization — bind from appsettings.json
            // MUST be called before any code that uses ErpSettings (e.g.,
            // ErpDateTimeJsonConverter uses ErpSettings.TimeZoneName).
            // =====================================================================
            ErpSettings.Initialize(builder.Configuration);

            var configuration = builder.Configuration;

            // Read key configuration values
            var connectionString = configuration["ConnectionStrings:Default"]
                ?? "Server=localhost;Port=5432;User Id=dev;Password=dev;Database=erp_project;";
            var jwtKey = configuration["Jwt:Key"] ?? JwtTokenOptions.DefaultDevelopmentKey;
            var jwtIssuer = configuration["Jwt:Issuer"] ?? "webvella-erp";
            var jwtAudience = configuration["Jwt:Audience"] ?? "webvella-erp";
            var redisConnectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";
            var messagingTransport = configuration["Messaging:Transport"] ?? "RabbitMQ";
            var jobsEnabled = configuration.GetValue<bool>("Jobs:Enabled");
            var locale = configuration["Locale"] ?? configuration["Settings:Locale"] ?? "en-US";

            // =====================================================================
            // Newtonsoft.Json Global Default Settings
            // Preserved from monolith Startup.cs lines 75-78.
            // =====================================================================
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
            };

            // =================================================================
            // Authentication — JWT Bearer only (AAP 0.1.2 / 0.8.1)
            // =================================================================
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtIssuer,
                    ValidAudience = jwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtKey))
                };
            });

            // =================================================================
            // Authorization
            // =================================================================
            builder.Services.AddAuthorization(options =>
            {
                options.FallbackPolicy = options.DefaultPolicy;
            });

            // =================================================================
            // MVC Controllers with Newtonsoft.Json
            // =================================================================
            builder.Services.AddControllers()
                .AddNewtonsoftJson(options =>
                {
                    options.SerializerSettings.Converters.Add(new ErpDateTimeJsonConverter());
                    options.SerializerSettings.DateFormatString =
                        configuration["Settings:JsonDateTimeFormat"] ?? "yyyy-MM-ddTHH:mm:ss.fff";
                });

            // =================================================================
            // gRPC — Inter-service communication (AAP 0.4.1)
            // =================================================================
            builder.Services.AddGrpc();

            // =================================================================
            // Redis Distributed Cache (replacing IMemoryCache)
            // =================================================================
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "erp_project_";
            });

            // =================================================================
            // CORS — environment-aware configuration
            // In development, allows specified origins. In production, restrict
            // origins via appsettings CorsOrigins configuration.
            // =================================================================
            builder.Services.AddCors(options =>
            {
                var allowedOrigins = configuration.GetSection("CorsOrigins").Get<string[]>();
                options.AddDefaultPolicy(policy =>
                {
                    if (allowedOrigins != null && allowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(allowedOrigins)
                              .AllowAnyMethod()
                              .AllowAnyHeader()
                              .AllowCredentials();
                    }
                    else
                    {
                        // Development fallback — restrict to known local ports
                        policy.WithOrigins(
                                "http://localhost:5000",
                                "http://localhost:5090",
                                "http://localhost:5092",
                                "https://localhost:5001")
                              .AllowAnyMethod()
                              .AllowAnyHeader()
                              .AllowCredentials();
                    }
                });
            });

            // =================================================================
            // Localization — culture from configuration
            // =================================================================
            builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

            // =================================================================
            // Response Compression — Gzip with optimal level
            // =================================================================
            builder.Services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Optimal);
            builder.Services.AddResponseCompression(options =>
            {
                options.Providers.Add<GzipCompressionProvider>();
            });

            // =================================================================
            // Routing — lowercase URLs
            // =================================================================
            builder.Services.AddRouting(options => { options.LowercaseUrls = true; });

            // =================================================================
            // Health Checks
            // =================================================================
            builder.Services.AddHealthChecks();

            // =================================================================
            // SharedKernel — JwtTokenHandler singleton
            // =================================================================
            var jwtTokenOptions = new JwtTokenOptions
            {
                Key = jwtKey,
                Issuer = jwtIssuer,
                Audience = jwtAudience,
                TokenExpiryMinutes = configuration.GetValue<int?>("Jwt:ExpirationMinutes") ?? 60
            };
            builder.Services.AddSingleton(jwtTokenOptions);
            builder.Services.AddSingleton<JwtTokenHandler>();

            // =================================================================
            // Project Database Context — scoped ambient context per request
            // Uses the Project service's own database (database-per-service).
            // =================================================================
            builder.Services.AddScoped<CoreDbContext>(sp =>
            {
                return CoreDbContext.CreateContext(connectionString);
            });

            // =================================================================
            // Database Repositories — scoped, one per request
            // =================================================================
            builder.Services.AddScoped<DbEntityRepository>(sp =>
                new DbEntityRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbRecordRepository>(sp =>
                new DbRecordRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbRelationRepository>(sp =>
                new DbRelationRepository(sp.GetRequiredService<CoreDbContext>()));

            // =================================================================
            // API Managers — scoped, one per request
            // Project service uses these managers against its OWN database.
            // =================================================================
            builder.Services.AddScoped<EntityManager>();
            builder.Services.AddScoped<EntityRelationManager>();
            builder.Services.AddScoped<RecordManager>(sp =>
                new RecordManager(
                    sp.GetRequiredService<CoreDbContext>(),
                    sp.GetRequiredService<EntityManager>(),
                    sp.GetRequiredService<EntityRelationManager>(),
                    sp.GetRequiredService<IPublishEndpoint>()));

            // =================================================================
            // Domain Services — scoped, one per request
            // Replaces monolith's new TaskService() instantiation pattern with DI.
            // =================================================================
            builder.Services.AddScoped<FeedService>();
            builder.Services.AddScoped<CommentService>();
            builder.Services.AddScoped<TimelogService>();
            builder.Services.AddScoped<TaskService>();
            builder.Services.AddScoped<ReportingService>();

            // =================================================================
            // MassTransit Event Bus (AAP 0.6.1)
            // Replaces monolith hook system with async domain events.
            // =================================================================
            builder.Services.AddMassTransit(busConfig =>
            {
                busConfig.AddConsumers(typeof(Program).Assembly);

                if (string.Equals(messagingTransport, "AmazonSQS", StringComparison.OrdinalIgnoreCase))
                {
                    var sqsServiceUrl = configuration["Messaging:AmazonSQS:ServiceUrl"] ?? "http://localhost:4566";
                    var sqsAccessKey = configuration["Messaging:AmazonSQS:AccessKey"] ?? "test";
                    var sqsSecretKey = configuration["Messaging:AmazonSQS:SecretKey"] ?? "test";

                    busConfig.UsingAmazonSqs((context, cfg) =>
                    {
                        cfg.Host(sqsServiceUrl, h =>
                        {
                            h.AccessKey(sqsAccessKey);
                            h.SecretKey(sqsSecretKey);
                        });
                        cfg.ConfigureEndpoints(context);
                    });
                }
                else
                {
                    var rabbitHost = configuration["Messaging:RabbitMQ:Host"] ?? "localhost";
                    var rabbitPort = configuration.GetValue<ushort?>("Messaging:RabbitMQ:Port") ?? 5672;
                    var rabbitUser = configuration["Messaging:RabbitMQ:Username"] ?? "guest";
                    var rabbitPass = configuration["Messaging:RabbitMQ:Password"] ?? "guest";
                    var rabbitVHost = configuration["Messaging:RabbitMQ:VirtualHost"] ?? "/";

                    busConfig.UsingRabbitMq((context, cfg) =>
                    {
                        cfg.Host(rabbitHost, rabbitPort, rabbitVHost, h =>
                        {
                            h.Username(rabbitUser);
                            h.Password(rabbitPass);
                        });
                        cfg.ConfigureEndpoints(context);
                    });
                }
            });

            // =================================================================
            // Background Jobs — IHostedService registrations
            // =================================================================
            if (jobsEnabled)
            {
                builder.Services.AddHostedService<StartTasksOnStartDateJob>();
            }

            // =================================================================
            // Build the application
            // =================================================================
            var app = builder.Build();

            // =================================================================
            // Middleware Pipeline
            // =================================================================

            // Localization
            var supportedCultures = new[] { new CultureInfo(locale) };
            app.UseRequestLocalization(new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture(supportedCultures[0]),
                SupportedCultures = supportedCultures,
                SupportedUICultures = supportedCultures
            });

            // Error handling
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseResponseCompression();
            app.UseCors();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            // Health check endpoint — anonymous access for container probes
            app.MapHealthChecks("/health").AllowAnonymous();

            // REST API controllers
            app.MapControllers();

            // gRPC services for inter-service communication
            app.MapGrpcService<ProjectGrpcService>();

            // =================================================================
            // Run the application
            // =================================================================
            app.Run();
        }
    }
}
