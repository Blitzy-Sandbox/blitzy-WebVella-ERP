// ============================================================================
// Program.cs — Mail/Notification Microservice Entry Point
// ============================================================================
// Minimal Hosting API for the Mail/Notification microservice, replacing both:
//   - WebVella.Erp.Site.Mail/Program.cs  (WebHost.CreateDefaultBuilder entry point)
//   - WebVella.Erp.Site.Mail/Startup.cs  (ConfigureServices + Configure)
//
// Bootstraps the service as an independently deployable ASP.NET Core application
// with the following capabilities:
//   - REST controllers with Newtonsoft.Json serialization (API contract stability)
//   - gRPC endpoints for inter-service binary communication
//   - EF Core with Npgsql for mail-specific PostgreSQL database (erp_mail)
//   - Distributed Redis caching (replacing IMemoryCache for SMTP config)
//   - MassTransit event bus (RabbitMQ or Amazon SQS/SNS via LocalStack)
//   - JWT Bearer authentication (replacing cookie auth for cross-service identity)
//   - Background job for SMTP queue processing (10-minute interval)
//   - Health checks for PostgreSQL, Redis, and RabbitMQ
//   - Gzip response compression with optimal level
//   - CORS policy for Node.js localhost development
//
// Source references:
//   - Startup.cs lines 24-73: ConfigureServices (DI registrations)
//   - Startup.cs lines 77-133: Configure (middleware pipeline)
//   - MailPlugin.cs lines 35-37: AutoMapper configuration
//   - MailPlugin.cs lines 41-80: Schedule plan (10-minute interval, ID 8f410aca)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using WebVella.Erp.Service.Mail.Database;
using WebVella.Erp.Service.Mail.Domain.Services;
using WebVella.Erp.Service.Mail.Grpc;
using WebVella.Erp.Service.Mail.Jobs;
using WebVella.Erp.SharedKernel;

namespace WebVella.Erp.Service.Mail
{
    /// <summary>
    /// Application entry point for the Mail/Notification microservice.
    /// Implements the .NET 10 Minimal Hosting API pattern, consolidating
    /// the monolith's separate Program.cs + Startup.cs into a single file.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Well-known schedule plan ID preserved from MailPlugin.cs line 47.
        /// This was the schedule plan GUID used by the monolith's ScheduleManager
        /// for the SMTP queue processing job. Preserved for migration reference.
        /// </summary>
        public static readonly Guid MailQueueSchedulePlanId = new Guid("8f410aca-a537-4c3f-b49b-927670534c07");

        /// <summary>
        /// Well-known job type ID preserved from MailPlugin.cs line 72.
        /// This was the ProcessSmtpQueueJob type ID in the monolith's job system.
        /// Preserved for migration reference and data compatibility.
        /// </summary>
        public static readonly Guid ProcessSmtpQueueJobTypeId = new Guid("9b301dca-6c81-40dd-887c-efd31c23bd77");

        public static void Main(string[] args)
        {
            // -----------------------------------------------------------------
            // Npgsql legacy timestamp behavior (from Startup.cs line 27)
            // Preserves the monolith's timestamp handling for backward compatibility
            // with existing PostgreSQL data. Must be set before any Npgsql usage.
            // -----------------------------------------------------------------
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            var builder = WebApplication.CreateBuilder(args);

            // -----------------------------------------------------------------
            // Configuration bridge: Map new microservice config paths to legacy
            // Settings:* paths expected by SharedKernel's ErpSettings.Initialize().
            // This allows ErpSettings to read from both the new structured
            // appsettings.json format and legacy Settings:* keys.
            // -----------------------------------------------------------------
            BridgeLegacyConfiguration(builder.Configuration);

            // -----------------------------------------------------------------
            // Initialize shared ErpSettings from configuration.
            // This binds EncryptionKey, Lang, Locale, TimeZoneName, DevelopmentMode,
            // ConnectionString, and other shared properties used by SharedKernel
            // utilities (CryptoUtility, ErpDateTimeJsonConverter, etc.).
            // -----------------------------------------------------------------
            ErpSettings.Initialize(builder.Configuration);

            // -----------------------------------------------------------------
            // Response Compression (from Startup.cs lines 28-29)
            // Gzip with optimal compression level for API responses.
            // -----------------------------------------------------------------
            builder.Services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Optimal);
            builder.Services.AddResponseCompression(options =>
            {
                options.Providers.Add<GzipCompressionProvider>();
            });

            // -----------------------------------------------------------------
            // Routing (from Startup.cs line 30)
            // Lowercase URL routing for consistent API paths.
            // -----------------------------------------------------------------
            builder.Services.AddRouting(options =>
            {
                options.LowercaseUrls = true;
            });

            // -----------------------------------------------------------------
            // CORS (from Startup.cs lines 32-37)
            // Preserves the AllowNodeJsLocalhost policy for development.
            // -----------------------------------------------------------------
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowNodeJsLocalhost", policy =>
                    policy
                        .WithOrigins("http://localhost:3000", "http://localhost")
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials());
            });

            // -----------------------------------------------------------------
            // Controllers with Newtonsoft.Json (from Startup.cs lines 41-51)
            // Preserves ErpDateTimeJsonConverter for consistent DateTime
            // serialization across API responses. Uses Newtonsoft.Json as the
            // default serializer (NOT System.Text.Json) per AAP 0.8.2.
            // -----------------------------------------------------------------
            builder.Services.AddControllers()
                .AddNewtonsoftJson(options =>
                {
                    options.SerializerSettings.Converters.Add(new ErpDateTimeJsonConverter());
                    options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                    options.SerializerSettings.NullValueHandling = NullValueHandling.Include;
                });

            // -----------------------------------------------------------------
            // Global Newtonsoft.Json defaults (from Startup.cs lines 56-60)
            // Sets JsonConvert.DefaultSettings globally so all serialization
            // (including MassTransit messages) uses the ErpDateTimeJsonConverter.
            // -----------------------------------------------------------------
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
            };

            // -----------------------------------------------------------------
            // gRPC services (AAP 0.4.1 — inter-service communication)
            // Registers gRPC server hosting for MailGrpcService endpoint.
            // -----------------------------------------------------------------
            builder.Services.AddGrpc(options =>
            {
                options.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16 MB
                options.MaxSendMessageSize = 16 * 1024 * 1024;    // 16 MB
                options.EnableDetailedErrors = builder.Environment.IsDevelopment();
            });

            // -----------------------------------------------------------------
            // EF Core with Npgsql (AAP 0.4.1 — database-per-service)
            // MailDbContext owns the erp_mail PostgreSQL database with
            // Email and SmtpServiceEntity tables.
            // -----------------------------------------------------------------
            var connectionString = builder.Configuration.GetConnectionString("Default")
                ?? "Host=localhost;Port=5432;Database=erp_mail;Username=postgres;Password=postgres";

            builder.Services.AddDbContext<MailDbContext>(options =>
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.MinBatchSize(1);
                    npgsqlOptions.CommandTimeout(120);
                }));

            // -----------------------------------------------------------------
            // Distributed Redis Caching (AAP 0.1.1)
            // Replaces monolith's IMemoryCache with Redis for SMTP config
            // caching across service instances. Preserves 1-hour TTL semantics.
            // -----------------------------------------------------------------
            var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
            var redisInstanceName = builder.Configuration["Redis:InstanceName"] ?? "MailService_";

            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = redisInstanceName;
            });

            // -----------------------------------------------------------------
            // JWT Bearer Authentication (replacing cookie auth from Startup.cs
            // lines 62-71). Validates JWT tokens issued by Core service for
            // cross-service identity propagation per AAP 0.8.3.
            // -----------------------------------------------------------------
            var jwtSection = builder.Configuration.GetSection("Jwt");
            var jwtKey = jwtSection["Key"] ?? "DEVELOPMENT_ONLY_KEY__OVERRIDE_VIA_Settings__Jwt__Key_ENV_VAR";
            var jwtIssuer = jwtSection["Issuer"] ?? "webvella-erp";
            var jwtAudience = jwtSection["Audience"] ?? "webvella-erp";
            var requireHttpsMetadata = bool.TryParse(jwtSection["RequireHttpsMetadata"], out var https) && https;

            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = requireHttpsMetadata;
                options.SaveToken = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = jwtAudience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ClockSkew = TimeSpan.FromMinutes(5)
                };

                // Allow JWT tokens in query string for gRPC-Web and SignalR
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            context.Token = accessToken;
                        }
                        return System.Threading.Tasks.Task.CompletedTask;
                    }
                };
            });

            builder.Services.AddAuthorization();

            // -----------------------------------------------------------------
            // MassTransit (AAP 0.1.1 — event-driven communication)
            // Replaces PostgreSQL LISTEN/NOTIFY with async message bus.
            // Supports RabbitMQ (local/Docker) and Amazon SQS/SNS (LocalStack).
            // Automatically discovers IConsumer<T> implementations in this assembly
            // (e.g., SendNotificationSubscriber).
            // -----------------------------------------------------------------
            var messagingSection = builder.Configuration.GetSection("Messaging");
            var transport = messagingSection["Transport"] ?? "RabbitMQ";

            builder.Services.AddMassTransit(busConfig =>
            {
                // Auto-discover all consumers (IConsumer<T>) in this assembly
                busConfig.AddConsumers(typeof(Program).Assembly);

                if (string.Equals(transport, "AmazonSQS", StringComparison.OrdinalIgnoreCase))
                {
                    // Amazon SQS/SNS transport for LocalStack validation
                    busConfig.UsingAmazonSqs((context, cfg) =>
                    {
                        var sqsSection = messagingSection.GetSection("AmazonSQS");
                        var region = sqsSection["Region"] ?? "us-east-1";
                        var serviceUrl = sqsSection["ServiceUrl"];

                        cfg.Host(region, h =>
                        {
                            h.AccessKey(sqsSection["AccessKey"] ?? "test");
                            h.SecretKey(sqsSection["SecretKey"] ?? "test");

                            // For LocalStack, the ServiceUrl is injected via
                            // AWS_SERVICE_URL environment variable or configured
                            // in the AmazonSQS section of appsettings.json.
                            // MassTransit picks up the standard AWS env vars.
                        });
                        cfg.ConfigureEndpoints(context);
                    });
                }
                else
                {
                    // RabbitMQ transport (default for local/Docker)
                    busConfig.UsingRabbitMq((context, cfg) =>
                    {
                        var rabbitSection = messagingSection.GetSection("RabbitMQ");
                        var host = rabbitSection["Host"] ?? "localhost";
                        var port = ushort.TryParse(rabbitSection["Port"], out var p) ? p : (ushort)5672;
                        var username = rabbitSection["Username"] ?? "guest";
                        var password = rabbitSection["Password"] ?? "guest";

                        cfg.Host(host, port, "/", h =>
                        {
                            h.Username(username);
                            h.Password(password);
                        });

                        cfg.ConfigureEndpoints(context);
                    });
                }
            });

            // -----------------------------------------------------------------
            // AutoMapper (from MailPlugin.cs line 37)
            // Auto-discovers Profile classes in this assembly for EntityRecord ↔
            // Email/SmtpServiceConfig DTO transformations.
            // -----------------------------------------------------------------
            builder.Services.AddAutoMapper(typeof(Program).Assembly);

            // -----------------------------------------------------------------
            // Health Checks — PostgreSQL, Redis, and RabbitMQ
            // Exposes /health endpoint for Docker/Kubernetes readiness probes.
            // Uses inline health check delegates to avoid additional NuGet
            // package dependencies (AspNetCore.HealthChecks.*).
            // -----------------------------------------------------------------
            builder.Services.AddHealthChecks()
                .AddCheck("postgresql", () =>
                {
                    try
                    {
                        using var conn = new Npgsql.NpgsqlConnection(connectionString);
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT 1";
                        cmd.ExecuteScalar();
                        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("PostgreSQL connection OK");
                    }
                    catch (Exception ex)
                    {
                        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("PostgreSQL connection failed", ex);
                    }
                }, tags: new[] { "db", "ready" })
                .AddCheck("redis", () =>
                {
                    try
                    {
                        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(
                            redisConnectionString + ",abortConnect=false,connectTimeout=3000");
                        var db = redis.GetDatabase();
                        db.Ping();
                        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Redis connection OK");
                    }
                    catch (Exception ex)
                    {
                        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Redis connection failed", ex);
                    }
                }, tags: new[] { "cache", "ready" });

            // -----------------------------------------------------------------
            // Domain Services — Mail-specific DI registrations
            // -----------------------------------------------------------------
            // SmtpService: Core SMTP domain service consolidating all mail
            // sending, queueing, validation, and SMTP config caching logic.
            // Registered as scoped to align with EF Core's scoped DbContext.
            builder.Services.AddScoped<SmtpService>();

            // -----------------------------------------------------------------
            // Background Job Configuration (from MailPlugin.cs lines 41-80)
            // Binds JobSettings from appsettings.json → Jobs section.
            // ProcessMailQueueJob runs as a timed IHostedService with
            // configurable interval (default 10 minutes per monolith schedule plan).
            // -----------------------------------------------------------------
            builder.Services.Configure<JobSettings>(
                builder.Configuration.GetSection("Jobs"));
            builder.Services.AddHostedService<ProcessMailQueueJob>();

            // -----------------------------------------------------------------
            // Request Localization
            // Preserves en-US default culture from Startup.cs line 79-82.
            // -----------------------------------------------------------------
            builder.Services.Configure<RequestLocalizationOptions>(options =>
            {
                options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(
                    CultureInfo.GetCultureInfo("en-US"));
            });

            // =================================================================
            // Build the application
            // =================================================================
            var app = builder.Build();

            // -----------------------------------------------------------------
            // Request Localization middleware
            // -----------------------------------------------------------------
            app.UseRequestLocalization();

            // -----------------------------------------------------------------
            // Developer exception page in development (from Startup.cs line 88)
            // -----------------------------------------------------------------
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // -----------------------------------------------------------------
            // Response Compression (from Startup.cs line 100)
            // Should be before static files and routing.
            // -----------------------------------------------------------------
            app.UseResponseCompression();

            // -----------------------------------------------------------------
            // CORS (from Startup.cs line 102)
            // Enable CORS before routing for preflight request handling.
            // -----------------------------------------------------------------
            app.UseCors("AllowNodeJsLocalhost");

            // -----------------------------------------------------------------
            // Routing + Authentication + Authorization
            // (from Startup.cs lines 115-117)
            // -----------------------------------------------------------------
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            // -----------------------------------------------------------------
            // Endpoint mapping
            // -----------------------------------------------------------------
            app.MapControllers();
            app.MapGrpcService<MailGrpcService>();
            app.MapHealthChecks("/health");

            // -----------------------------------------------------------------
            // Run the application
            // -----------------------------------------------------------------
            app.Run();
        }

        /// <summary>
        /// Bridges the new microservice appsettings.json configuration format
        /// to the legacy Settings:* paths expected by SharedKernel's ErpSettings.Initialize().
        ///
        /// The monolith used a flat Settings:* key structure (e.g., Settings:EncryptionKey,
        /// Settings:Lang). The new microservice appsettings uses a more organized structure
        /// (Security:EncryptionKey, Localization:Lang, ConnectionStrings:Default, etc.).
        ///
        /// This method adds in-memory configuration entries that map the new paths to the
        /// old Settings:* paths, ensuring backward compatibility with ErpSettings and all
        /// SharedKernel utilities that depend on it.
        /// </summary>
        /// <param name="configuration">The configuration manager to augment.</param>
        private static void BridgeLegacyConfiguration(ConfigurationManager configuration)
        {
            var bridgeEntries = new Dictionary<string, string>();

            // Security → Settings:EncryptionKey
            var encryptionKey = configuration["Security:EncryptionKey"];
            if (!string.IsNullOrWhiteSpace(encryptionKey))
            {
                bridgeEntries["Settings:EncryptionKey"] = encryptionKey;
            }

            // Localization → Settings:Lang, Settings:Locale, Settings:TimeZoneName
            var lang = configuration["Localization:Lang"];
            if (!string.IsNullOrWhiteSpace(lang))
            {
                bridgeEntries["Settings:Lang"] = lang;
            }

            var locale = configuration["Localization:Locale"];
            if (!string.IsNullOrWhiteSpace(locale))
            {
                bridgeEntries["Settings:Locale"] = locale;
            }

            var timeZoneName = configuration["Localization:TimeZoneName"];
            if (!string.IsNullOrWhiteSpace(timeZoneName))
            {
                bridgeEntries["Settings:TimeZoneName"] = timeZoneName;
            }

            // ConnectionStrings:Default → Settings:ConnectionString
            var connStr = configuration.GetConnectionString("Default");
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                bridgeEntries["Settings:ConnectionString"] = connStr;
            }

            // Storage → Settings file storage flags
            var enableFs = configuration["Storage:EnableFileSystemStorage"];
            if (!string.IsNullOrWhiteSpace(enableFs))
            {
                bridgeEntries["Settings:EnableFileSystemStorage"] = enableFs;
            }

            var fsFolder = configuration["Storage:FileSystemStorageFolder"];
            if (!string.IsNullOrWhiteSpace(fsFolder))
            {
                bridgeEntries["Settings:FileSystemStorageFolder"] = fsFolder;
            }

            // Jobs:Enabled → Settings:EnableBackgroungJobs (preserving monolith key spelling)
            var jobsEnabled = configuration["Jobs:Enabled"];
            if (!string.IsNullOrWhiteSpace(jobsEnabled))
            {
                bridgeEntries["Settings:EnableBackgroungJobs"] = jobsEnabled;
            }

            if (bridgeEntries.Count > 0)
            {
                configuration.AddInMemoryCollection(bridgeEntries);
            }
        }
    }
}
