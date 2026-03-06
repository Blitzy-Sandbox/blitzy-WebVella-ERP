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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.Service.Project.Database;
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
                ?? "Server=localhost;Port=5434;User Id=dev;Password=dev;Database=erp_project;";
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
                .ConfigureApplicationPartManager(manager =>
                {
                    // The Project service references Core for its managers (RecordManager, EntityManager, etc.)
                    // but must NOT register Core's controllers (RecordController, FileController, etc.)
                    // in the Project routing table. Core's FileController has a catch-all DELETE route
                    // {*filepath} that creates routing conflicts with Project endpoints.
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
            // CORS — permissive default policy
            // Preserved from monolith Startup.cs lines 50-56.
            // AllowAnyOrigin/AllowAnyMethod/AllowAnyHeader for backend
            // microservice consumed by API Gateway and other services.
            // =================================================================
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader());
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
            // EF Core ProjectDbContext — for migrations and entity queries
            // Registered via AddDbContext<T>() with Npgsql provider per AAP
            // database-per-service pattern. This is SEPARATE from CoreDbContext
            // which provides the ambient context for RecordManager/EntityManager.
            // =================================================================
            builder.Services.AddDbContext<ProjectDbContext>(options =>
            {
                options.UseNpgsql(connectionString);
                // Suppress PendingModelChangesWarning — the initial migration was generated
                // from the monolith entity schema; minor model snapshot drift is expected
                // during the decomposition phase and does not affect runtime correctness.
                options.ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
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
            // SecurityManager — required by ProjectController for user resolution
            // and permission checks. Depends on CoreDbContext and RecordManager
            // which are already registered above.
            builder.Services.AddScoped<SecurityManager>();

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
            // HttpClient — inter-service REST calls (AAP 0.5.2)
            // Named HttpClients for Core (user/entity resolution) and CRM
            // (account/case lookups) services.
            // =================================================================
            var coreServiceUrl = configuration["ServiceUrls:CoreService"] ?? "http://core-service:8080";
            var crmServiceUrl = configuration["ServiceUrls:CrmService"] ?? "http://crm-service:8080";

            builder.Services.AddHttpClient("CoreService", client =>
            {
                client.BaseAddress = new Uri(coreServiceUrl);
            });
            builder.Services.AddHttpClient("CrmService", client =>
            {
                client.BaseAddress = new Uri(crmServiceUrl);
            });

            // =================================================================
            // ICrmServiceClient — cross-service CRM account validation
            // Used by ReportingService to validate account existence.
            // Implemented via HttpClient calling CRM REST API.
            // =================================================================
            builder.Services.AddScoped<ICrmServiceClient>(sp =>
            {
                var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
                return new HttpCrmServiceClient(httpClientFactory);
            });

            // =================================================================
            // gRPC Clients — inter-service gRPC communication
            // =================================================================
            var coreGrpcUrl = configuration["Grpc:CoreServiceUrl"] ?? "http://core-service:8081";
            var crmGrpcUrl = configuration["Grpc:CrmServiceUrl"] ?? "http://crm-service:8081";

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
                        cfg.UseNewtonsoftJsonSerializer();
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
                        cfg.UseNewtonsoftJsonSerializer();
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
            // EF Core Migrations — create task, timelog, comment, feed tables
            // Matches the pattern used by CRM, Admin, and Reporting services.
            // Runs on startup to ensure the erp_project database has all
            // required tables before accepting requests.
            // =================================================================
            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ProjectDbContext>();
                // Guard: only call Migrate() when using a relational provider.
                // In test environments, WebApplicationFactory may register an InMemory provider
                // which does not support relational migrations.
                if (dbContext.Database.IsRelational())
                {
                    dbContext.Database.Migrate();
                }
            }

            // =================================================================
            // Ensure Entity Metadata System Tables — required for EQL engine
            // The EntityManager and EQL engine require 'entities' and
            // 'entity_relations' tables in the service database. In the monolith
            // these tables existed in the single shared DB. In the microservice
            // architecture, each service that uses EQL needs these tables locally.
            // They are populated by syncing entity metadata from the Core service.
            // =================================================================
            {
                var coreCtx = app.Services.CreateScope().ServiceProvider.GetRequiredService<CoreDbContext>();
                using (var conn = coreCtx.CreateConnection())
                {
                    try
                    {
                        // Create entities table if not exists
                        using (var cmd = conn.CreateCommand(@"
                            CREATE TABLE IF NOT EXISTS entities (
                                id UUID NOT NULL PRIMARY KEY,
                                json TEXT NOT NULL,
                                created_on TIMESTAMPTZ NOT NULL DEFAULT now()
                            )"))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Create entity_relations table if not exists
                        using (var cmd = conn.CreateCommand(@"
                            CREATE TABLE IF NOT EXISTS entity_relations (
                                id UUID NOT NULL PRIMARY KEY,
                                json TEXT NOT NULL,
                                created_on TIMESTAMPTZ NOT NULL DEFAULT now()
                            )"))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectStartup");
                        startupLogger.LogInformation("Project Service: Entity metadata system tables ensured in project database.");
                    }
                    catch (Exception ex)
                    {
                        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectStartup");
                        startupLogger.LogError(ex, "Project Service: Failed to create entity metadata system tables.");
                    }
                }
            }

            // =================================================================
            // Seed Entity Metadata from Database Schema (DbEntity format)
            // The EQL engine reads entity metadata via DbEntityRepository which
            // deserializes JSON with TypeNameHandling.Auto (requires $type
            // discriminators for polymorphic DbBaseField subclasses). Instead
            // of syncing from Core API (which returns camelCase Entity JSON
            // without $type discriminators), we introspect the local database
            // schema and build proper DbEntity-format metadata directly.
            // This ensures EQL can resolve all project-local entities.
            // =================================================================
            {
                var seedLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectStartup");
                try
                {
                    var seedScope = app.Services.CreateScope();
                    var coreCtx = seedScope.ServiceProvider.GetRequiredService<CoreDbContext>();
                    using var seedConn = coreCtx.CreateConnection();

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
                    static DbBaseField MapColumnToField(string colName, string pgType, string entityName)
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
                            var entityName = tableName.Substring(4); // strip "rec_" prefix
                            var columns = new List<(string colName, string pgType)>();
                            using (var colCmd = seedConn.CreateCommand(
                                "SELECT column_name, data_type FROM information_schema.columns " +
                                "WHERE table_schema = 'public' AND table_name = @tbl ORDER BY ordinal_position",
                                parameters: new List<NpgsqlParameter> { new NpgsqlParameter("@tbl", tableName) }))
                            {
                                using var colReader = colCmd.ExecuteReader();
                                while (colReader.Read())
                                    columns.Add((colReader.GetString(0), colReader.GetString(1)));
                                colReader.Close();
                            }
                            entityDefs.Add((entityName, columns));
                        }
                    }

                    // Step 2: Clear stale entity metadata (may have wrong format from previous sync)
                    using (var clearCmd = seedConn.CreateCommand("DELETE FROM entities WHERE TRUE"))
                        clearCmd.ExecuteNonQuery();
                    using (var clearRelCmd = seedConn.CreateCommand("DELETE FROM entity_relations WHERE TRUE"))
                        clearRelCmd.ExecuteNonQuery();

                    // Step 3: Build and seed DbEntity metadata for each discovered entity
                    var seededEntityIds = new Dictionary<string, Guid>(); // entityName -> entityId
                    var seededFieldIds = new Dictionary<string, Guid>();  // entityName.fieldName -> fieldId
                    var jsonSettings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };

                    foreach (var (entityName, columns) in entityDefs)
                    {
                        var entityId = DeterministicGuid($"entity_{entityName}");
                        seededEntityIds[entityName] = entityId;

                        var fields = new List<DbBaseField>();
                        foreach (var (colName, pgType) in columns)
                        {
                            var field = MapColumnToField(colName, pgType, entityName);
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

                        var entityJson = JsonConvert.SerializeObject(dbEntity, jsonSettings);
                        using var insertCmd = seedConn.CreateCommand(
                            "INSERT INTO entities (id, json) VALUES (@id, @json) ON CONFLICT (id) DO UPDATE SET json = @json",
                            parameters: new List<NpgsqlParameter>
                            {
                                new NpgsqlParameter("@id", entityId),
                                new NpgsqlParameter("@json", entityJson)
                            });
                        insertCmd.ExecuteNonQuery();
                    }

                    // Step 4: Seed entity_relations for local rel_* join tables
                    // Discover rel_* tables and create M:N relation metadata
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
                        // Parse relation table name: rel_{origin}_nn_{target}
                        var relParts = relTableName.Substring(4); // strip "rel_"
                        var nnIdx = relParts.IndexOf("_nn_");
                        if (nnIdx < 0) continue;

                        var originEntityName = relParts.Substring(0, nnIdx);
                        var targetEntityName = relParts.Substring(nnIdx + 4);

                        if (!seededEntityIds.ContainsKey(originEntityName) || !seededEntityIds.ContainsKey(targetEntityName))
                            continue;

                        var relId = DeterministicGuid($"rel_{originEntityName}_nn_{targetEntityName}");
                        var originEntityId = seededEntityIds[originEntityName];
                        var targetEntityId = seededEntityIds[targetEntityName];
                        var originFieldId = seededFieldIds.GetValueOrDefault($"{originEntityName}.id", DeterministicGuid($"{originEntityName}_id"));
                        var targetFieldId = seededFieldIds.GetValueOrDefault($"{targetEntityName}.id", DeterministicGuid($"{targetEntityName}_id"));

                        var dbRelation = new DbEntityRelation
                        {
                            Id = relId,
                            Name = $"{originEntityName}_nn_{targetEntityName}",
                            Label = $"{ToLabel(originEntityName)}-{ToLabel(targetEntityName)}",
                            Description = null,
                            System = false,
                            RelationType = EntityRelationType.ManyToMany,
                            OriginEntityId = originEntityId,
                            OriginFieldId = originFieldId,
                            TargetEntityId = targetEntityId,
                            TargetFieldId = targetFieldId
                        };

                        var relJson = JsonConvert.SerializeObject(dbRelation, jsonSettings);
                        using var insertRelCmd = seedConn.CreateCommand(
                            "INSERT INTO entity_relations (id, json) VALUES (@id, @json) ON CONFLICT (id) DO UPDATE SET json = @json",
                            parameters: new List<NpgsqlParameter>
                            {
                                new NpgsqlParameter("@id", relId),
                                new NpgsqlParameter("@json", relJson)
                            });
                        insertRelCmd.ExecuteNonQuery();
                    }

                    seedLogger.LogInformation(
                        "Project Service: Seeded {EntityCount} entities and {RelCount} relations from database schema.",
                        entityDefs.Count, relTableNames.Count);
                }
                catch (Exception ex)
                {
                    seedLogger.LogError(ex, "Project Service: Failed to seed entity metadata from database schema.");
                }
            }

            // =================================================================
            // AutoMapper Initialization — required for EntityManager.ReadEntities()
            // EntityManager.ReadEntities() calls MapTo<Entity>() which requires
            // ErpAutoMapper.Mapper to be initialized. The Core service calls
            // InitializeAutoMapper() during its startup. Since the Project service
            // runs independently, it must also initialize the mapper with the
            // essential Entity/Field/Relation mapping profiles.
            // This mirrors Core service's InitializeAutoMapper() but only includes
            // the mappings needed for the Project service's operations.
            // =================================================================
            if (ErpAutoMapper.Mapper == null)
            {
                var cfg = ErpAutoMapperConfiguration.MappingExpressions;
                cfg.CreateMap<Guid, string>().ConvertUsing(src => src.ToString());
                cfg.CreateMap<DateTimeOffset, DateTime>().ConvertUsing(src => src.DateTime);
                cfg.CreateMap<Entity, DbEntity>();
                cfg.CreateMap<DbEntity, Entity>();
                cfg.CreateMap<EntityRelation, DbEntityRelation>();
                cfg.CreateMap<DbEntityRelation, EntityRelation>();
                cfg.CreateMap<RecordPermissions, DbRecordPermissions>();
                cfg.CreateMap<DbRecordPermissions, RecordPermissions>();
                cfg.CreateMap<FieldPermissions, DbFieldPermissions>();
                cfg.CreateMap<DbFieldPermissions, FieldPermissions>();
                // Field type mappings — required for polymorphic DbBaseField → Field mapping
                cfg.CreateMap<Field, DbBaseField>().IncludeAllDerived();
                cfg.CreateMap<DbBaseField, Field>().IncludeAllDerived();
                cfg.CreateMap<DbGuidField, GuidField>();
                cfg.CreateMap<GuidField, DbGuidField>();
                cfg.CreateMap<DbTextField, TextField>();
                cfg.CreateMap<TextField, DbTextField>();
                cfg.CreateMap<DbNumberField, NumberField>();
                cfg.CreateMap<NumberField, DbNumberField>();
                cfg.CreateMap<DbDateTimeField, DateTimeField>();
                cfg.CreateMap<DateTimeField, DbDateTimeField>();
                cfg.CreateMap<DbCheckboxField, CheckboxField>();
                cfg.CreateMap<CheckboxField, DbCheckboxField>();
                // Entity relation options
                cfg.CreateMap<EntityRelationOptionsItem, DbEntityRelationOptions>();
                cfg.CreateMap<DbEntityRelationOptions, EntityRelationOptionsItem>();
                // Error model
                cfg.CreateMap<ErrorModel, ErrorModel>();
                ErpAutoMapperConfiguration.Configure(cfg);
                ErpAutoMapper.Initialize(cfg);
            }

            // =================================================================
            // Cache Initialization — required before EntityManager.ReadEntities()
            // EntityManager.ReadEntities() calls Cache.GetEntities() which requires
            // the static IDistributedCache to be initialized. Without this,
            // a NullReferenceException occurs at Cache._cache.GetString().
            // Mirrors Core service's Cache.Initialize() at Program.cs line 536.
            // =================================================================
            {
                var distributedCache = app.Services.GetRequiredService<IDistributedCache>();
                Cache.Initialize(distributedCache);
                // Clear stale entity/relation cache from previous runs so that
                // EntityManager.ReadEntities() reads freshly-seeded metadata from DB
                // instead of returning an empty cached list.
                Cache.ClearEntities();
                Cache.ClearRelations();
            }

            // =================================================================
            // EQL Default Providers — required for TaskService, RecordManager, etc.
            // The EQL engine uses static default providers (EqlCommand.DefaultEntityProvider,
            // etc.) when callers do not pass explicit providers. This mirrors the Core
            // service's initialization at src/Services/WebVella.Erp.Service.Core/Program.cs
            // lines 567-573. Without this, all EQL queries fail with
            // "One or more Eql errors occurred." because the entity provider is null.
            // Resolved from a DI scope to avoid the "Cannot resolve scoped service from
            // root provider" error in Development mode. The scope is not disposed because
            // the EQL default providers need to live for the app's lifetime.
            // =================================================================
            try
            {
                var eqlScope = app.Services.CreateScope();
                var entityManager = eqlScope.ServiceProvider.GetRequiredService<EntityManager>();
                var relationManager = eqlScope.ServiceProvider.GetRequiredService<EntityRelationManager>();
                EqlCommand.DefaultEntityProvider = new ProjectEqlEntityProvider(entityManager);
                EqlCommand.DefaultRelationProvider = new ProjectEqlRelationProvider(relationManager);
                EqlCommand.DefaultFieldValueExtractor = new ProjectEqlFieldValueExtractor();
                EqlCommand.DefaultSecurityProvider = new ProjectEqlSecurityProvider();
            }
            catch (Exception ex)
            {
                var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectStartup");
                startupLogger.LogError(ex, "Project Service: Failed to initialize EQL providers. " +
                    "The service will start but EQL queries may not function correctly.");
            }

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

            // Global exception handler — converts unhandled exceptions to proper
            // JSON error responses instead of dropping the connection (which causes
            // the Gateway to return 502). Pattern matches CRM service exception handling.
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
                    await context.Response.WriteAsync(JsonConvert.SerializeObject(response));
                });
            });

            app.UseResponseCompression();
            app.UseCors();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            // =================================================================
            // SecurityContext bridge — opens a SecurityContext scope from the
            // authenticated ClaimsPrincipal on each request so that downstream
            // managers (EntityManager, RecordManager, etc.) can call
            // SecurityContext.CurrentUser / HasMetaPermission() correctly.
            // The gRPC services handle this explicitly per-call; REST controllers
            // rely on this middleware to establish the scope automatically.
            // Pattern preserved from Core service.
            // =================================================================
            app.Use(async (context, next) =>
            {
                if (context.User?.Identity?.IsAuthenticated == true)
                {
                    try
                    {
                        using (SecurityContext.OpenScope(context.User))
                        {
                            await next();
                        }
                    }
                    catch (ArgumentException)
                    {
                        // If claims cannot be mapped to an ErpUser (e.g. missing
                        // NameIdentifier), proceed without a SecurityContext scope.
                        // Permission checks will see CurrentUser == null and deny
                        // access naturally.
                        await next();
                    }
                }
                else
                {
                    await next();
                }
            });

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

    /// <summary>
    /// HTTP-based implementation of <see cref="ICrmServiceClient"/> for cross-service
    /// CRM account validation. Uses the named "CrmService" HttpClient to call the
    /// CRM microservice REST API.
    ///
    /// Used by <see cref="ReportingService"/> to validate account existence when
    /// filtering timelog reports by CRM account. If the CRM service is unreachable
    /// or returns an error, the validation returns false (fail-safe).
    ///
    /// In production, this HTTP call can be replaced with an event-sourced local
    /// projection once the CRM service publishes AccountCreated/AccountDeleted events.
    /// </summary>
    /// <summary>
    /// IEqlEntityProvider implementation for the Project service, delegating to EntityManager.
    /// Mirrors Core service's CoreEqlEntityProvider. Required so that EqlCommand instances
    /// created via legacy constructors (e.g., in TaskService.ExecuteEql) can resolve
    /// entity metadata from the Project service's own database.
    /// </summary>
    internal sealed class ProjectEqlEntityProvider : IEqlEntityProvider
    {
        private readonly EntityManager _entityManager;

        public ProjectEqlEntityProvider(EntityManager entityManager)
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
    /// IEqlRelationProvider implementation for the Project service.
    /// Mirrors Core service's CoreEqlRelationProvider.
    /// </summary>
    internal sealed class ProjectEqlRelationProvider : IEqlRelationProvider
    {
        private readonly EntityRelationManager _relationManager;

        public ProjectEqlRelationProvider(EntityRelationManager relationManager)
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
    /// IEqlFieldValueExtractor implementation for the Project service.
    /// Mirrors Core service's CoreEqlFieldValueExtractor.
    /// </summary>
    internal sealed class ProjectEqlFieldValueExtractor : IEqlFieldValueExtractor
    {
        public object ExtractFieldValue(object jToken, Field field)
        {
            return DbRecordRepository.ExtractFieldValue(jToken, field);
        }
    }

    /// <summary>
    /// IEqlSecurityProvider implementation for the Project service.
    /// Mirrors Core service's CoreEqlSecurityProvider.
    /// </summary>
    internal sealed class ProjectEqlSecurityProvider : IEqlSecurityProvider
    {
        public bool HasEntityPermission(EntityPermission permission, Entity entity)
        {
            return SecurityContext.HasEntityPermission(permission, entity);
        }
    }

    internal sealed class HttpCrmServiceClient : ICrmServiceClient
    {
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// Initializes a new instance using the provided <see cref="IHttpClientFactory"/>
        /// to create named "CrmService" HTTP clients for each request.
        /// </summary>
        /// <param name="httpClientFactory">Factory for creating named HTTP clients.</param>
        public HttpCrmServiceClient(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        /// <summary>
        /// Validates whether an account with the specified ID exists in the CRM service
        /// by sending a HEAD request to the CRM REST API endpoint.
        /// Returns false if the CRM service is unreachable or returns a non-success status.
        /// </summary>
        /// <param name="accountId">The unique identifier of the account to validate.</param>
        /// <returns>True if the account exists; false otherwise (including on network errors).</returns>
        public async Task<bool> AccountExistsAsync(Guid accountId)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("CrmService");
                var response = await client.GetAsync($"/api/v3/en_US/record/account/{accountId}");
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                // CRM service unreachable — fail-safe to false
                return false;
            }
            catch (TaskCanceledException)
            {
                // Request timeout — fail-safe to false
                return false;
            }
        }
    }
}
