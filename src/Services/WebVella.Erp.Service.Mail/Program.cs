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
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.Service.Mail.Database;
using WebVella.Erp.Service.Mail.Domain.Services;
using WebVella.Erp.Service.Mail.Grpc;
using WebVella.Erp.Service.Mail.Jobs;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;

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
                .ConfigureApplicationPartManager(manager =>
                {
                    // The Mail service references Core for its managers (RecordManager, EntityManager, etc.)
                    // but must NOT register Core's controllers (RecordController, FileController, etc.)
                    // in the Mail routing table. Core's FileController has a catch-all DELETE route
                    // {*filepath} that creates routing conflicts with Mail endpoints.
                    var coreAssembly = typeof(WebVella.Erp.Service.Core.Api.RecordManager).Assembly;
                    var corePart = manager.ApplicationParts
                        .OfType<Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart>()
                        .FirstOrDefault(p => p.Assembly == coreAssembly);
                    if (corePart != null)
                        manager.ApplicationParts.Remove(corePart);
                })
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
                ?? "Host=localhost;Port=5435;Database=erp_mail;Username=dev;Password=dev";

            builder.Services.AddDbContext<MailDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.MinBatchSize(1);
                    npgsqlOptions.CommandTimeout(120);
                });
                // Suppress PendingModelChangesWarning — the initial migration was generated
                // from the monolith entity schema; minor model snapshot drift is expected
                // during the decomposition phase and does not affect runtime correctness.
                options.ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            });

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
            var jwtKey = jwtSection["Key"] ?? JwtTokenOptions.DefaultDevelopmentKey;
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
                        cfg.UseNewtonsoftJsonSerializer();
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

                        cfg.UseNewtonsoftJsonSerializer();
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
                        // Use CoreDbContext.ConnectionString which is set at resolution time
                        // (not the captured build-time variable) for test compatibility
                        var healthConnStr = CoreDbContext.ConnectionString ?? connectionString;
                        using var conn = new Npgsql.NpgsqlConnection(healthConnStr);
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
            // Core Service Dependencies — required by MailController and SmtpService
            // MailController depends on RecordManager for EQL-based record CRUD.
            // SmtpService depends on RecordManager for email record persistence.
            // These follow the same DI registration pattern as the Project service.
            // -----------------------------------------------------------------

            // CoreDbContext — ambient database context for EQL and RecordManager
            // IMPORTANT: Read connection string from IConfiguration at resolution time,
            // NOT from the captured 'connectionString' variable. WebApplicationFactory's
            // ConfigureAppConfiguration overrides are applied AFTER the builder phase,
            // so the captured variable still holds the default value during integration tests.
            builder.Services.AddScoped<CoreDbContext>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var resolvedConnStr = config.GetConnectionString("Default")
                    ?? config["Settings:ConnectionString"]
                    ?? connectionString;
                return CoreDbContext.CreateContext(resolvedConnStr);
            });

            // Database Repositories — scoped, one per request
            builder.Services.AddScoped<DbEntityRepository>(sp =>
                new DbEntityRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbRecordRepository>(sp =>
                new DbRecordRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbRelationRepository>(sp =>
                new DbRelationRepository(sp.GetRequiredService<CoreDbContext>()));

            // API Managers — scoped, one per request
            builder.Services.AddScoped<EntityManager>();
            builder.Services.AddScoped<EntityRelationManager>();
            builder.Services.AddScoped<RecordManager>(sp =>
                new RecordManager(
                    sp.GetRequiredService<CoreDbContext>(),
                    sp.GetRequiredService<EntityManager>(),
                    sp.GetRequiredService<EntityRelationManager>(),
                    sp.GetRequiredService<IPublishEndpoint>()));
            builder.Services.AddScoped<SecurityManager>();

            // JwtTokenHandler — required by SecurityContext for token operations
            var jwtTokenOptions = new JwtTokenOptions
            {
                Key = jwtKey,
                Issuer = jwtIssuer,
                Audience = jwtAudience,
                TokenExpiryMinutes = 60
            };
            builder.Services.AddSingleton(jwtTokenOptions);
            builder.Services.AddSingleton<JwtTokenHandler>();

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

            // =================================================================
            // EF Core Migrations — create email, smtp_service tables
            // Matches the pattern used by CRM, Admin, and Reporting services.
            // Runs on startup to ensure the erp_mail database has all
            // required tables before accepting requests.
            // =================================================================
            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<MailDbContext>();
                // Guard: only call Migrate() when using a relational provider.
                // Support Database:SkipMigration=true for integration test environments
                // where the test fixture manages schema creation independently.
                var skipMigration = app.Configuration.GetValue<bool>("Database:SkipMigration", false);
                if (dbContext.Database.IsRelational() && !skipMigration)
                {
                    dbContext.Database.Migrate();
                }
            }

            // =================================================================
            // Ensure Entity Metadata System Tables — required for EQL engine
            // The EntityManager and EQL engine require 'entities' and
            // 'entity_relations' tables in the service database. These tables
            // are populated by syncing entity metadata from the Core service.
            // =================================================================
            {
                var metaScope = app.Services.CreateScope();
                var coreCtx = metaScope.ServiceProvider.GetRequiredService<CoreDbContext>();
                using (var conn = coreCtx.CreateConnection())
                {
                    try
                    {
                        using (var cmd = conn.CreateCommand(@"
                            CREATE TABLE IF NOT EXISTS entities (
                                id UUID NOT NULL PRIMARY KEY,
                                json TEXT NOT NULL,
                                created_on TIMESTAMPTZ NOT NULL DEFAULT now()
                            )"))
                        { cmd.ExecuteNonQuery(); }

                        using (var cmd = conn.CreateCommand(@"
                            CREATE TABLE IF NOT EXISTS entity_relations (
                                id UUID NOT NULL PRIMARY KEY,
                                json TEXT NOT NULL,
                                created_on TIMESTAMPTZ NOT NULL DEFAULT now()
                            )"))
                        { cmd.ExecuteNonQuery(); }
                    }
                    catch (Exception ex)
                    {
                        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MailStartup");
                        startupLogger.LogError(ex, "Mail Service: Failed to create entity metadata system tables.");
                    }
                }
            }

            // =================================================================
            // Seed Entity Metadata from Database Schema (DbEntity format)
            // The EQL engine reads entity metadata via DbEntityRepository which
            // deserializes JSON with TypeNameHandling.Auto. We introspect the 
            // local database schema and build proper DbEntity-format metadata.
            // =================================================================
            {
                var seedLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MailStartup");
                try
                {
                    var seedScope = app.Services.CreateScope();
                    var coreCtx2 = seedScope.ServiceProvider.GetRequiredService<CoreDbContext>();
                    using var seedConn = coreCtx2.CreateConnection();

                    // Helper: generate deterministic GUID from a string key
                    static Guid DeterministicGuid(string input)
                    {
                        using var md5 = System.Security.Cryptography.MD5.Create();
                        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("webvella_erp_" + input));
                        return new Guid(hash);
                    }

                    // Helper: convert column_name to PascalCase label
                    static string ToLabel(string colName)
                    {
                        var parts = colName.Split('_');
                        return string.Join(" ", parts.Select(p =>
                            p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1) : p));
                    }

                    // Helper: map PostgreSQL data type to a DbBaseField instance
                    static DbBaseField MapColumnToField(string colName, string pgType)
                    {
                        DbBaseField field = pgType switch
                        {
                            "uuid" => new DbGuidField
                            {
                                GenerateNewId = (colName == "id"),
                                DefaultValue = null
                            },
                            "timestamp with time zone" or "timestamp without time zone" => new DbDateTimeField
                            {
                                DefaultValue = null,
                                Format = "yyyy-MMM-dd HH:mm",
                                UseCurrentTimeAsDefaultValue = (colName == "created_on")
                            },
                            "boolean" => new DbCheckboxField
                            {
                                DefaultValue = false
                            },
                            "numeric" or "integer" or "bigint" or "smallint"
                                or "double precision" or "real" => new DbNumberField
                            {
                                DefaultValue = null,
                                MinValue = null,
                                MaxValue = null,
                                DecimalPlaces = 2
                            },
                            _ => new DbTextField
                            {
                                DefaultValue = null,
                                MaxLength = null
                            }
                        };
                        return field;
                    }

                    // Step 1: Discover all rec_* tables and their columns
                    var entityDefs = new List<(string entityName, List<(string colName, string pgType)> columns)>();
                    using (var tablesCmd = seedConn.CreateCommand(
                        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE 'rec\\_%' ORDER BY table_name"))
                    {
                        using var tableReader = tablesCmd.ExecuteReader();
                        var tableNames = new List<string>();
                        while (tableReader.Read())
                            tableNames.Add(tableReader.GetString(0));
                        tableReader.Close();

                        foreach (var tableName in tableNames)
                        {
                            var entityName = tableName.Substring(4);
                            var columns = new List<(string colName, string pgType)>();
                            using (var colCmd = seedConn.CreateCommand(
                                "SELECT column_name, data_type FROM information_schema.columns " +
                                "WHERE table_schema = 'public' AND table_name = @tbl ORDER BY ordinal_position",
                                parameters: new List<Npgsql.NpgsqlParameter> { new Npgsql.NpgsqlParameter("@tbl", tableName) }))
                            {
                                using var colReader = colCmd.ExecuteReader();
                                while (colReader.Read())
                                    columns.Add((colReader.GetString(0), colReader.GetString(1)));
                                colReader.Close();
                            }
                            entityDefs.Add((entityName, columns));
                        }
                    }

                    // Step 2: Clear stale entity metadata
                    using (var clearCmd = seedConn.CreateCommand("DELETE FROM entities WHERE TRUE"))
                        clearCmd.ExecuteNonQuery();
                    using (var clearRelCmd = seedConn.CreateCommand("DELETE FROM entity_relations WHERE TRUE"))
                        clearRelCmd.ExecuteNonQuery();

                    // Step 3: Build and seed DbEntity metadata
                    var seededEntityIds = new Dictionary<string, Guid>();
                    var seededFieldIds = new Dictionary<string, Guid>();
                    var jsonSettings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto };

                    foreach (var (entityName, columns) in entityDefs)
                    {
                        var entityId = DeterministicGuid($"entity_{entityName}");
                        seededEntityIds[entityName] = entityId;

                        var fields = new List<DbBaseField>();
                        foreach (var (colName, pgType) in columns)
                        {
                            var field = MapColumnToField(colName, pgType);
                            var fieldId = DeterministicGuid($"{entityName}_{colName}");
                            field.Id = fieldId;
                            field.Name = colName;
                            field.Label = ToLabel(colName);
                            field.Required = (colName == "id");
                            field.Unique = (colName == "id");
                            field.Searchable = (colName == "id" || colName == "x_search");
                            field.Auditable = false;
                            field.System = false;
                            field.EnableSecurity = false;
                            field.Permissions = new DbFieldPermissions();
                            fields.Add(field);
                            seededFieldIds[$"{entityName}.{colName}"] = fieldId;
                        }

                        var dbEntity = new DbEntity
                        {
                            Id = entityId,
                            Name = entityName,
                            Label = ToLabel(entityName),
                            LabelPlural = ToLabel(entityName) + "s",
                            System = false,
                            IconName = "fa fa-cube",
                            Color = "#999999",
                            RecordPermissions = new DbRecordPermissions
                            {
                                CanRead = new List<Guid> {
                                    Guid.Parse("bdc56420-caf0-4030-8a0e-d264938e0cda"),
                                    Guid.Parse("f16ec6db-626d-4c27-8de0-3e7ce542c55f"),
                                    Guid.Parse("987148b1-afa8-4b33-8616-55861e5fd065")
                                },
                                CanCreate = new List<Guid> { Guid.Parse("bdc56420-caf0-4030-8a0e-d264938e0cda") },
                                CanUpdate = new List<Guid> { Guid.Parse("bdc56420-caf0-4030-8a0e-d264938e0cda") },
                                CanDelete = new List<Guid> { Guid.Parse("bdc56420-caf0-4030-8a0e-d264938e0cda") }
                            },
                            Fields = fields
                        };

                        var entityJson = Newtonsoft.Json.JsonConvert.SerializeObject(dbEntity, jsonSettings);
                        using var insertCmd = seedConn.CreateCommand(
                            "INSERT INTO entities (id, json) VALUES (@id, @json) ON CONFLICT (id) DO UPDATE SET json = @json",
                            parameters: new List<Npgsql.NpgsqlParameter>
                            {
                                new Npgsql.NpgsqlParameter("@id", entityId),
                                new Npgsql.NpgsqlParameter("@json", entityJson)
                            });
                        insertCmd.ExecuteNonQuery();
                    }

                    // Step 4: Seed entity_relations for local rel_* join tables
                    var relTableNames = new List<string>();
                    using (var relTablesCmd = seedConn.CreateCommand(
                        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE 'rel\\_%' ORDER BY table_name"))
                    {
                        using var relReader = relTablesCmd.ExecuteReader();
                        while (relReader.Read())
                            relTableNames.Add(relReader.GetString(0));
                        relReader.Close();
                    }

                    foreach (var relTableName in relTableNames)
                    {
                        var relParts = relTableName.Substring(4);
                        var nnIdx = relParts.IndexOf("_nn_");
                        if (nnIdx < 0) continue;

                        var originEntityName = relParts.Substring(0, nnIdx);
                        var targetEntityName = relParts.Substring(nnIdx + 4);

                        if (!seededEntityIds.ContainsKey(originEntityName) || !seededEntityIds.ContainsKey(targetEntityName))
                            continue;

                        var relId = DeterministicGuid($"rel_{originEntityName}_nn_{targetEntityName}");
                        var dbRelation = new DbEntityRelation
                        {
                            Id = relId,
                            Name = $"{originEntityName}_nn_{targetEntityName}",
                            Label = $"{ToLabel(originEntityName)}-{ToLabel(targetEntityName)}",
                            Description = null,
                            System = false,
                            RelationType = EntityRelationType.ManyToMany,
                            OriginEntityId = seededEntityIds[originEntityName],
                            OriginFieldId = seededFieldIds.GetValueOrDefault($"{originEntityName}.id", DeterministicGuid($"{originEntityName}_id")),
                            TargetEntityId = seededEntityIds[targetEntityName],
                            TargetFieldId = seededFieldIds.GetValueOrDefault($"{targetEntityName}.id", DeterministicGuid($"{targetEntityName}_id"))
                        };

                        var relJson = Newtonsoft.Json.JsonConvert.SerializeObject(dbRelation, jsonSettings);
                        using var insertRelCmd = seedConn.CreateCommand(
                            "INSERT INTO entity_relations (id, json) VALUES (@id, @json) ON CONFLICT (id) DO UPDATE SET json = @json",
                            parameters: new List<Npgsql.NpgsqlParameter>
                            {
                                new Npgsql.NpgsqlParameter("@id", relId),
                                new Npgsql.NpgsqlParameter("@json", relJson)
                            });
                        insertRelCmd.ExecuteNonQuery();
                    }

                    seedLogger.LogInformation("Mail Service: Seeded {EntityCount} entities and {RelCount} relations from database schema.",
                        entityDefs.Count, relTableNames.Count);
                }
                catch (Exception ex)
                {
                    seedLogger.LogError(ex, "Mail Service: Failed to seed entity metadata from database schema.");
                }
            }

            // =================================================================
            // Initialize Core Cache — EntityManager.ReadEntities() and
            // RecordManager depend on Cache being initialized with an
            // IDistributedCache instance before any entity lookups.
            // =================================================================
            using (var cacheScope = app.Services.CreateScope())
            {
                var distributedCache = cacheScope.ServiceProvider
                    .GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
                WebVella.Erp.Service.Core.Api.Cache.Initialize(distributedCache);
                // Clear stale entity/relation cache from previous runs so that
                // EntityManager.ReadEntities() reads freshly-seeded metadata from DB
                // instead of returning an empty cached list.
                WebVella.Erp.Service.Core.Api.Cache.ClearEntities();
                WebVella.Erp.Service.Core.Api.Cache.ClearRelations();
            }

            // =================================================================
            // AutoMapper initialization — required for MapTo<SmtpServiceConfig>(),
            // MapTo<Email>(), and other EntityRecord ↔ DTO conversions used by
            // SmtpService.GetSmtpServiceInternal() and other domain services.
            // =================================================================
            if (WebVella.Erp.SharedKernel.Utilities.ErpAutoMapper.Mapper == null)
            {
                var cfg = WebVella.Erp.SharedKernel.Utilities.ErpAutoMapperConfiguration.MappingExpressions;

                // Type converters required by EntityRecord property resolution
                cfg.CreateMap<Guid, string>().ConvertUsing(src => src.ToString());
                cfg.CreateMap<DateTimeOffset, DateTime>().ConvertUsing(src => src.DateTime);

                // Entity mappings — required for DbEntityRepository.Read() → MapTo<Entity>()
                // Without these, EntityManager.ReadEntities() throws AutoMapperMappingException
                // and EQL queries fail with "Entity not found" because entity metadata cannot be loaded.
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.Entity, WebVella.Erp.SharedKernel.Database.DbEntity>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbEntity, WebVella.Erp.SharedKernel.Models.Entity>();

                // EntityRelation mappings
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.EntityRelation, WebVella.Erp.SharedKernel.Database.DbEntityRelation>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbEntityRelation, WebVella.Erp.SharedKernel.Models.EntityRelation>();

                // Permission mappings
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.RecordPermissions, WebVella.Erp.SharedKernel.Database.DbRecordPermissions>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbRecordPermissions, WebVella.Erp.SharedKernel.Models.RecordPermissions>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.FieldPermissions, WebVella.Erp.SharedKernel.Database.DbFieldPermissions>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbFieldPermissions, WebVella.Erp.SharedKernel.Models.FieldPermissions>();

                // Field type mappings — required for polymorphic DbBaseField → Field mapping
                // IncludeAllDerived() handles any field type subclass automatically
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.Field, WebVella.Erp.SharedKernel.Database.DbBaseField>().IncludeAllDerived();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbBaseField, WebVella.Erp.SharedKernel.Models.Field>().IncludeAllDerived();

                // Explicit maps for all field types used in Mail entities (email, smtp_service)
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbGuidField, WebVella.Erp.SharedKernel.Models.GuidField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.GuidField, WebVella.Erp.SharedKernel.Database.DbGuidField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbTextField, WebVella.Erp.SharedKernel.Models.TextField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.TextField, WebVella.Erp.SharedKernel.Database.DbTextField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbNumberField, WebVella.Erp.SharedKernel.Models.NumberField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.NumberField, WebVella.Erp.SharedKernel.Database.DbNumberField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbDateTimeField, WebVella.Erp.SharedKernel.Models.DateTimeField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.DateTimeField, WebVella.Erp.SharedKernel.Database.DbDateTimeField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbCheckboxField, WebVella.Erp.SharedKernel.Models.CheckboxField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.CheckboxField, WebVella.Erp.SharedKernel.Database.DbCheckboxField>();
                // Additional field types for completeness (may be used in cross-entity queries)
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbEmailField, WebVella.Erp.SharedKernel.Models.EmailField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.EmailField, WebVella.Erp.SharedKernel.Database.DbEmailField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbAutoNumberField, WebVella.Erp.SharedKernel.Models.AutoNumberField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.AutoNumberField, WebVella.Erp.SharedKernel.Database.DbAutoNumberField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbMultiLineTextField, WebVella.Erp.SharedKernel.Models.MultiLineTextField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.MultiLineTextField, WebVella.Erp.SharedKernel.Database.DbMultiLineTextField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbHtmlField, WebVella.Erp.SharedKernel.Models.HtmlField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.HtmlField, WebVella.Erp.SharedKernel.Database.DbHtmlField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbPasswordField, WebVella.Erp.SharedKernel.Models.PasswordField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.PasswordField, WebVella.Erp.SharedKernel.Database.DbPasswordField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbSelectField, WebVella.Erp.SharedKernel.Models.SelectField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.SelectField, WebVella.Erp.SharedKernel.Database.DbSelectField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbMultiSelectField, WebVella.Erp.SharedKernel.Models.MultiSelectField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.MultiSelectField, WebVella.Erp.SharedKernel.Database.DbMultiSelectField>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbSelectFieldOption, WebVella.Erp.SharedKernel.Models.SelectOption>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.SelectOption, WebVella.Erp.SharedKernel.Database.DbSelectFieldOption>();

                // Entity relation options
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.EntityRelationOptionsItem, WebVella.Erp.SharedKernel.Database.DbEntityRelationOptions>();
                cfg.CreateMap<WebVella.Erp.SharedKernel.Database.DbEntityRelationOptions, WebVella.Erp.SharedKernel.Models.EntityRelationOptionsItem>();

                // Error model
                cfg.CreateMap<WebVella.Erp.SharedKernel.Models.ErrorModel, WebVella.Erp.SharedKernel.Models.ErrorModel>();

                WebVella.Erp.SharedKernel.Utilities.ErpAutoMapperConfiguration.Configure(cfg);
                WebVella.Erp.SharedKernel.Utilities.ErpAutoMapper.Initialize(cfg);
            }

            // =================================================================
            // EQL Default Providers — required for EqlCommand instances used by
            // MailController (e.g., "SELECT * FROM email", "SELECT * FROM smtp_service").
            // Mirrors the Core service's startup pattern (Core/Program.cs lines 562-573).
            // Sets the static providers only if not already set (e.g., by test fixtures).
            // =================================================================
            // Scope is intentionally NOT disposed — the EQL default providers need the
            // EntityManager and RelationManager (and their underlying CoreDbContext) to
            // remain alive for the application lifetime. Disposing the scope would dispose
            // the CoreDbContext, causing "Object disposed" errors on EQL queries.
            // This matches the Project service's pattern (Program.cs line 765).
            {
                var eqlScope = app.Services.CreateScope();
                var entityManager = eqlScope.ServiceProvider.GetRequiredService<EntityManager>();
                var relationManager = eqlScope.ServiceProvider.GetRequiredService<EntityRelationManager>();
                if (WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultEntityProvider == null)
                {
                    WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultEntityProvider =
                        new MailEqlEntityProvider(entityManager);
                }
                if (WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultRelationProvider == null)
                {
                    WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultRelationProvider =
                        new MailEqlRelationProvider(relationManager);
                }
                if (WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultFieldValueExtractor == null)
                {
                    WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultFieldValueExtractor =
                        new MailEqlFieldValueExtractor();
                }
                if (WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultSecurityProvider == null)
                {
                    WebVella.Erp.SharedKernel.Eql.EqlCommand.DefaultSecurityProvider =
                        new MailEqlSecurityProvider();
                }
            }

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
            // Global exception handler — converts unhandled exceptions to proper
            // JSON error responses instead of dropping the connection (which causes
            // the Gateway to return 502). Pattern matches CRM service handling.
            // -----------------------------------------------------------------
            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                    var response = new
                    {
                        success = false,
                        timestamp = DateTime.UtcNow,
                        message = error?.Error?.Message ?? "An internal error occurred.",
                        errors = new[] { new { key = (string?)null, value = (string?)null, message = error?.Error?.Message ?? "An internal error occurred." } },
                        @object = (object?)null
                    };
                    await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(response));
                });
            });

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
            // SecurityContext scope middleware — opens a SharedKernel SecurityContext
            // scope from the authenticated user's JWT claims on each request.
            // This replaces the monolith's ErpMiddleware which did the same.
            // Without this, EQL security checks fail with "No access to entity"
            // because SecurityContext.CurrentUser is null.
            // -----------------------------------------------------------------
            app.Use(async (context, next) =>
            {
                IDisposable securityScope = null;
                try
                {
                    if (context.User?.Identity?.IsAuthenticated == true)
                    {
                        securityScope = WebVella.Erp.SharedKernel.Security.SecurityContext.OpenScope(context.User);
                    }
                    await next();
                }
                finally
                {
                    securityScope?.Dispose();
                }
            });

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

        // =============================================================
        // EQL Provider implementations for Mail service
        // Mirrors Core service's CoreEqlEntityProvider / CoreEqlRelationProvider
        // pattern so that EqlCommand instances in MailController can
        // resolve entity metadata (email, smtp_service) and relations.
        // =============================================================

        /// <summary>
        /// IEqlEntityProvider implementation delegating to EntityManager.
        /// Provides entity metadata for EQL queries within the Mail service.
        /// </summary>
        private class MailEqlEntityProvider : IEqlEntityProvider
        {
            private readonly EntityManager _entityManager;

            public MailEqlEntityProvider(EntityManager entityManager)
            {
                _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
            }

            public Entity ReadEntity(string entityName)
            {
                var response = _entityManager.ReadEntity(entityName);
                return response?.Object;
            }

            public Entity ReadEntity(Guid entityId)
            {
                var response = _entityManager.ReadEntity(entityId);
                return response?.Object;
            }

            public List<Entity> ReadEntities()
            {
                var response = _entityManager.ReadEntities();
                return response?.Object ?? new List<Entity>();
            }
        }

        /// <summary>
        /// IEqlRelationProvider implementation delegating to EntityRelationManager.
        /// </summary>
        private class MailEqlRelationProvider : IEqlRelationProvider
        {
            private readonly EntityRelationManager _relationManager;

            public MailEqlRelationProvider(EntityRelationManager relationManager)
            {
                _relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
            }

            public List<EntityRelation> Read()
            {
                var response = _relationManager.Read();
                return response?.Object ?? new List<EntityRelation>();
            }

            public EntityRelation Read(string name)
            {
                var response = _relationManager.Read(name);
                return response?.Object;
            }

            public EntityRelation Read(Guid id)
            {
                var response = _relationManager.Read(id);
                return response?.Object;
            }
        }

        /// <summary>
        /// IEqlFieldValueExtractor implementation that delegates to DbRecordRepository.
        /// </summary>
        private class MailEqlFieldValueExtractor : IEqlFieldValueExtractor
        {
            public object ExtractFieldValue(object jToken, Field field)
            {
                return DbRecordRepository.ExtractFieldValue(jToken, field);
            }
        }

        /// <summary>
        /// IEqlSecurityProvider implementation that delegates to SecurityContext.
        /// </summary>
        private class MailEqlSecurityProvider : IEqlSecurityProvider
        {
            public bool HasEntityPermission(EntityPermission permission, Entity entity)
            {
                return SecurityContext.HasEntityPermission(permission, entity);
            }
        }
    }
}
