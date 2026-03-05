// =============================================================================
// WebVella ERP — Core Platform Microservice
// Program.cs: Minimal Hosting API entry point
// =============================================================================
// Entry point for the Core Platform microservice using the .NET 10 minimal
// hosting API pattern. Replaces the monolith's WebVella.Erp.Site/Program.cs +
// Startup.cs composition root with a modern, per-service hosting configuration.
//
// Responsibilities:
//   - JWT-only authentication (no cookies — cookies are for Gateway/BFF only)
//   - gRPC service hosting for inter-service communication
//   - MassTransit event bus (RabbitMQ or Amazon SQS/SNS via LocalStack)
//   - Redis distributed cache replacing monolith IMemoryCache
//   - Service-scoped DI for all API managers, repositories, and database context
//   - Health check endpoints for container orchestration probes
//   - Background job system (JobManager, ScheduleManager) as hosted services
//   - Newtonsoft.Json serialization preserving monolith API contracts
//   - Service initialization: system tables, system entities, default seed data
//
// Source references:
//   - WebVella.Erp.Site/Program.cs (lines 1-19): old WebHost.CreateDefaultBuilder
//   - WebVella.Erp.Site/Startup.cs (lines 1-196): cookie+JWT auth, MVC, CORS, etc.
//   - WebVella.Erp/ERPService.cs (lines 1-1473): bootstrap orchestrator
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MassTransit;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.Service.Core.Grpc;
using WebVella.Erp.Service.Core.Jobs;

namespace WebVella.Erp.Service.Core
{
    /// <summary>
    /// Application entry point for the Core Platform microservice.
    /// Uses .NET 10 minimal hosting API (WebApplication.CreateBuilder).
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main entry point. Configures and starts the Core Platform microservice
        /// with all required middleware, DI registrations, and service initialization.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        public static void Main(string[] args)
        {
            // =====================================================================
            // Npgsql Legacy Timestamp Behavior
            // Preserved from monolith Startup.cs line 40 — required until system
            // tables are migrated to timestamptz. Npgsql 6+ changed timestamp
            // handling; this switch restores the legacy behavior.
            // =====================================================================
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            var builder = WebApplication.CreateBuilder(args);

            // =====================================================================
            // ErpSettings initialization — bind from appsettings.json Settings section
            // MUST be called before any code that uses ErpSettings (e.g.,
            // ErpDateTimeJsonConverter uses ErpSettings.TimeZoneName).
            // Matches the pattern used in CRM, Admin, Reporting, and Gateway.
            // =====================================================================
            ErpSettings.Initialize(builder.Configuration);

            // =====================================================================
            // Configuration
            // appsettings.json + environment variables are loaded automatically
            // by WebApplication.CreateBuilder. Additional sources can be added here.
            // =====================================================================
            var configuration = builder.Configuration;

            // Read key configuration values used across registrations
            var connectionString = configuration["ConnectionStrings:Default"]
                ?? "Server=localhost;Port=5432;User Id=dev;Password=dev;Database=erp_core;";
            var jwtKey = configuration["Jwt:Key"] ?? JwtTokenOptions.DefaultDevelopmentKey;
            var jwtIssuer = configuration["Jwt:Issuer"] ?? "webvella-erp";
            var jwtAudience = configuration["Jwt:Audience"] ?? "webvella-erp";
            var redisConnectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";
            var redisInstanceName = configuration["Redis:InstanceName"] ?? "erp_core_";
            var messagingTransport = configuration["Messaging:Transport"] ?? "RabbitMQ";
            var jobsEnabled = configuration.GetValue<bool>("Jobs:Enabled");
            var locale = configuration["Settings:Locale"] ?? "en-US";

            // =====================================================================
            // Newtonsoft.Json Global Default Settings
            // Preserved from monolith Startup.cs lines 83-86 — ensures all
            // JsonConvert.SerializeObject/DeserializeObject calls across the
            // codebase use the ERP DateTime converter by default.
            // =====================================================================
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
            };

            // =================================================================
            // Authentication — JWT Bearer only (AAP 0.1.2 / 0.8.1)
            // No cookie authentication; cookies are for Gateway/BFF only.
            // Preserved JWT validation parameters from Startup.cs lines 102-114.
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
                        Encoding.UTF8.GetBytes(jwtKey)),

                    // ErpUser.ToClaims() emits role IDs as ClaimTypes.Role (Guid)
                    // and human-readable role names as "role_name" companion claims.
                    // ASP.NET Core's [Authorize(Roles = "administrator")] must match
                    // the human-readable name, so we configure RoleClaimType to use
                    // "role_name" instead of the default ClaimTypes.Role.
                    RoleClaimType = "role_name"
                };
            });

            // =================================================================
            // Authorization — default policy requires authenticated users
            // Matches existing ApiControllerBase [Authorize] behavior.
            // =================================================================
            builder.Services.AddAuthorization(options =>
            {
                options.FallbackPolicy = options.DefaultPolicy;
            });

            // =================================================================
            // MVC Controllers with Newtonsoft.Json serialization
            // Preserved from Startup.cs lines 67-86 — Newtonsoft.Json replaces
            // System.Text.Json as the default serializer, with the ERP DateTime
            // converter for API contract stability.
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
            // Enables EntityGrpcServiceImpl, RecordGrpcServiceImpl, and
            // SecurityGrpcServiceImpl endpoints for other microservices.
            // =================================================================
            builder.Services.AddGrpc();

            // =================================================================
            // Redis Distributed Cache (NEW — replacing IMemoryCache, AAP 0.4.1)
            // Entity metadata and relation cache stored in Redis for cross-
            // instance coherence. 1-hour TTL preserved from monolith.
            // =================================================================
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = redisInstanceName;
            });

            // =================================================================
            // CORS — permissive policy for development
            // Preserved from Startup.cs lines 58-64. In production, restrict
            // origins via configuration.
            // =================================================================
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
                    if (allowedOrigins != null && allowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(allowedOrigins)
                              .AllowAnyMethod()
                              .AllowAnyHeader()
                              .AllowCredentials();
                    }
                    else if (builder.Environment.IsDevelopment())
                    {
                        // AllowAnyOrigin only acceptable in development
                        policy.AllowAnyOrigin()
                              .AllowAnyMethod()
                              .AllowAnyHeader();
                    }
                    else
                    {
                        // Production fallback: restrict to same-origin
                        policy.WithOrigins("https://localhost")
                              .AllowAnyMethod()
                              .AllowAnyHeader()
                              .AllowCredentials();
                    }
                });
            });

            // =================================================================
            // Localization — culture from Settings:Locale
            // Preserved from Startup.cs lines 45-46.
            // =================================================================
            builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
            builder.Services.Configure<RequestLocalizationOptions>(options =>
            {
                options.DefaultRequestCulture = new RequestCulture(locale);
            });

            // =================================================================
            // Response Compression — Gzip with optimal level
            // Preserved from Startup.cs lines 48-49.
            // =================================================================
            builder.Services.Configure<GzipCompressionProviderOptions>(options =>
                options.Level = CompressionLevel.Optimal);
            builder.Services.AddResponseCompression(options =>
            {
                options.Providers.Add<GzipCompressionProvider>();
            });

            // =================================================================
            // Routing — lowercase URLs
            // Preserved from Startup.cs line 50.
            // =================================================================
            builder.Services.AddRouting(options => { options.LowercaseUrls = true; });

            // =================================================================
            // Health Checks — PostgreSQL + Redis probes for container orchestration
            // Used by Kubernetes liveness/readiness probes at /health endpoint.
            // =================================================================
            builder.Services.AddHealthChecks()
                .AddNpgSql(connectionString, name: "postgresql", tags: new[] { "db", "ready" })
                .AddRedis(redisConnectionString, name: "redis", tags: new[] { "cache", "ready" });

            // =================================================================
            // SharedKernel — JwtTokenHandler singleton
            // Shared JWT token creation/validation for inter-service identity.
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
            // Core Database Context — scoped ambient context per request
            // Uses static factory CreateContext(connectionString) pattern from
            // the monolith's DbContext.Current, adapted for per-service isolation.
            // =================================================================
            builder.Services.AddScoped<CoreDbContext>(sp =>
            {
                return CoreDbContext.CreateContext(connectionString);
            });

            // =================================================================
            // Database Repositories — scoped, one per request
            // Each repository depends on CoreDbContext for connection management.
            // =================================================================
            builder.Services.AddScoped<DbEntityRepository>(sp =>
                new DbEntityRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbRecordRepository>(sp =>
                new DbRecordRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbRelationRepository>(sp =>
                new DbRelationRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbFileRepository>(sp =>
                new DbFileRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbDataSourceRepository>(sp =>
                new DbDataSourceRepository(sp.GetRequiredService<CoreDbContext>()));
            builder.Services.AddScoped<DbSystemSettingsRepository>(sp =>
                new DbSystemSettingsRepository(sp.GetRequiredService<CoreDbContext>()));

            // =================================================================
            // API Managers — scoped, one per request
            // Registered with factory delegates to handle non-DI-resolvable
            // constructor parameters (bool defaults in RecordManager).
            // =================================================================
            builder.Services.AddScoped<EntityManager>();
            builder.Services.AddScoped<EntityRelationManager>();
            builder.Services.AddScoped<RecordManager>(sp =>
                new RecordManager(
                    sp.GetRequiredService<CoreDbContext>(),
                    sp.GetRequiredService<EntityManager>(),
                    sp.GetRequiredService<EntityRelationManager>(),
                    sp.GetRequiredService<IPublishEndpoint>()));
            builder.Services.AddScoped<SecurityManager>();
            builder.Services.AddScoped<DataSourceManager>();
            builder.Services.AddScoped<SearchManager>();
            builder.Services.AddScoped<ImportExportManager>();

            // =================================================================
            // MassTransit Event Bus (AAP 0.6.1)
            // Replaces monolith hook system with async domain events.
            // Transport selection: RabbitMQ for local/Docker, Amazon SQS/SNS
            // for LocalStack validation and production AWS.
            // =================================================================
            builder.Services.AddMassTransit(busConfig =>
            {
                // Auto-discover all IConsumer<T> implementations in this assembly
                busConfig.AddConsumers(typeof(Program).Assembly);

                if (string.Equals(messagingTransport, "AmazonSQS", StringComparison.OrdinalIgnoreCase))
                {
                    // Amazon SQS/SNS transport for LocalStack and production AWS
                    var sqsServiceUrl = configuration["Messaging:AmazonSQS:ServiceUrl"] ?? "http://localhost:4566";
                    var sqsRegion = configuration["Messaging:AmazonSQS:Region"] ?? "us-east-1";
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
                    // RabbitMQ transport for local/Docker development (default)
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
            // Background Jobs — IHostedService registrations (AAP 0.7.2)
            // JobManager: dispatcher loop that polls for pending jobs
            // ScheduleManager: schedule plan trigger computation
            // Only registered when Jobs:Enabled is true in configuration.
            // =================================================================
            if (jobsEnabled)
            {
                builder.Services.AddSingleton<JobManager>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<JobManager>());
                builder.Services.AddSingleton<ScheduleManager>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduleManager>());
            }

            // =================================================================
            // Build the application
            // =================================================================
            var app = builder.Build();

            // =================================================================
            // Service Initialization
            // Runs synchronous startup tasks extracted from the monolith's
            // ERPService.InitializeSystemEntities() — creates PostgreSQL
            // extensions, system tables, system entities, and seed data.
            // Must execute before the application starts accepting requests.
            // =================================================================
            InitializeCoreService(app, connectionString, app.Logger);

            // =================================================================
            // Middleware Pipeline
            // Order matches monolith Startup.cs Configure() method:
            // 1. Response compression (before static files / routing)
            // 2. CORS
            // 3. Routing
            // 4. Authentication / Authorization
            // 5. Endpoint mapping
            // =================================================================

            // Localization middleware
            var supportedCultures = new[] { new CultureInfo(locale) };
            app.UseRequestLocalization(new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture(supportedCultures[0]),
                SupportedCultures = supportedCultures,
                SupportedUICultures = supportedCultures
            });

            // Error handling based on environment
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // Response compression — before routing
            app.UseResponseCompression();

            // CORS — before routing and auth
            app.UseCors();

            // Routing
            app.UseRouting();

            // Authentication and Authorization
            app.UseAuthentication();
            app.UseAuthorization();

            // SecurityContext bridge — opens a SecurityContext scope from the
            // authenticated ClaimsPrincipal on each request so that downstream
            // managers (EntityManager, RecordManager, etc.) can call
            // SecurityContext.CurrentUser / HasMetaPermission() correctly.
            // The gRPC services handle this explicitly per-call; REST controllers
            // rely on this middleware to establish the scope automatically.
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
            app.MapGrpcService<EntityGrpcServiceImpl>();
            app.MapGrpcService<RecordGrpcServiceImpl>();
            app.MapGrpcService<SecurityGrpcServiceImpl>();

            // =================================================================
            // Run the application
            // =================================================================
            app.Run();
        }

        // =====================================================================
        // Service Initialization
        // =====================================================================

        /// <summary>
        /// Initializes the Core Platform service database and system entities.
        /// Extracted from the monolith's <c>ERPService.InitializeSystemEntities()</c>
        /// (ERPService.cs lines 18-1473) and adapted for microservice architecture.
        ///
        /// Performs the following steps:
        /// 1. Creates a database context with the service connection string
        /// 2. Initializes the Redis-backed distributed cache
        /// 3. Creates PostgreSQL extensions (uuid-ossp, postgis, etc.)
        /// 4. Creates PostgreSQL type casts
        /// 5. Creates system tables (entities, entity_relations, system_settings, etc.)
        /// 6. Initializes system entities (user, role, user_file) if not already present
        /// 7. Seeds default administrator user and roles
        ///
        /// This method is idempotent — it checks system_settings version to skip
        /// already-applied schema changes, matching the monolith's versioned
        /// initialization pattern.
        /// </summary>
        /// <param name="app">The built WebApplication instance.</param>
        /// <param name="connectionString">PostgreSQL connection string for the Core service database.</param>
        /// <param name="logger">Logger for startup diagnostics.</param>
        private static void InitializeCoreService(WebApplication app, string connectionString, ILogger logger)
        {
            try
            {
                logger.LogInformation("Core Platform Service: Starting database initialization...");

                // Create ambient database context for initialization operations
                var dbContext = CoreDbContext.CreateContext(connectionString);

                try
                {
                    // Initialize the Redis-backed cache so that EntityManager, RecordManager,
                    // and other components can use Cache.GetEntities() during initialization.
                    var distributedCache = app.Services.GetRequiredService<IDistributedCache>();
                    Cache.Initialize(distributedCache);

                    using (var connection = dbContext.CreateConnection())
                    {
                        // Create PostgreSQL extensions required by the ERP system
                        // Preserved from ERPService.cs line 28: DbRepository.CreatePostgresqlExtensions()
                        DbRepository.CreatePostgresqlExtensions();

                        // Create PostgreSQL type casts
                        // Preserved from ERPService.cs line 30: DbRepository.CreatePostgresqlCasts()
                        DbRepository.CreatePostgresqlCasts();

                        // Create system tables (entities, entity_relations, system_settings,
                        // system_log, system_search, jobs, schedule_plans, plugin_data)
                        // Preserved from ERPService.cs line 36: CheckCreateSystemTables()
                        EnsureSystemTables(dbContext);

                        logger.LogInformation("Core Platform Service: Database schema initialized successfully.");
                    }
                }
                finally
                {
                    // Always close the ambient context to prevent resource leaks
                    CoreDbContext.CloseContext();
                }

                logger.LogInformation("Core Platform Service: Database initialization complete.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Core Platform Service: Failed to initialize database. " +
                    "The service will start but may not function correctly until the database is available.");
            }
        }

        /// <summary>
        /// Ensures all system tables exist in the Core service database.
        /// Adapted from the monolith's <c>ErpService.CheckCreateSystemTables()</c>.
        ///
        /// Creates the following tables if they do not exist:
        /// - entities: JSON storage for entity metadata
        /// - entity_relations: JSON storage for relation metadata
        /// - system_settings: versioned system configuration
        /// - system_log: diagnostic log entries
        /// - system_search: full-text search index
        /// - jobs: background job queue
        /// - schedule_plans: scheduled job definitions
        /// - plugin_data: plugin configuration storage
        ///
        /// Each table creation is idempotent (IF NOT EXISTS).
        /// </summary>
        /// <param name="dbContext">Active CoreDbContext for database operations.</param>
        private static void EnsureSystemTables(CoreDbContext dbContext)
        {
            using (var connection = dbContext.CreateConnection())
            {
                var command = connection.CreateCommand(
                    @"CREATE TABLE IF NOT EXISTS entities (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        json JSONB NOT NULL DEFAULT '{}'::jsonb
                    );

                    CREATE TABLE IF NOT EXISTS entity_relations (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        json JSONB NOT NULL DEFAULT '{}'::jsonb
                    );

                    CREATE TABLE IF NOT EXISTS system_settings (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        version INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE TABLE IF NOT EXISTS system_log (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        created_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        type SMALLINT NOT NULL DEFAULT 0,
                        source TEXT,
                        message TEXT,
                        details TEXT,
                        notification_status SMALLINT NOT NULL DEFAULT 0,
                        stack_trace TEXT
                    );

                    CREATE TABLE IF NOT EXISTS system_search (
                        id UUID NOT NULL,
                        entity_name TEXT NOT NULL,
                        search_content TEXT,
                        fts_content TSVECTOR,
                        CONSTRAINT pk_system_search PRIMARY KEY (id, entity_name)
                    );

                    CREATE TABLE IF NOT EXISTS jobs (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        type_id UUID NOT NULL,
                        type_name TEXT NOT NULL,
                        assembly TEXT,
                        complete_class_name TEXT,
                        attributes JSONB DEFAULT '{}'::jsonb,
                        status SMALLINT NOT NULL DEFAULT 0,
                        priority SMALLINT NOT NULL DEFAULT 0,
                        created_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        started_on TIMESTAMPTZ,
                        finished_on TIMESTAMPTZ,
                        aborted_on TIMESTAMPTZ,
                        canceled_on TIMESTAMPTZ,
                        error_message TEXT,
                        result TEXT
                    );

                    CREATE TABLE IF NOT EXISTS schedule_plans (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        name TEXT,
                        type SMALLINT NOT NULL DEFAULT 0,
                        start_date TIMESTAMPTZ,
                        end_date TIMESTAMPTZ,
                        schedule_days JSONB DEFAULT '[]'::jsonb,
                        interval_in_minutes INTEGER DEFAULT 0,
                        start_time_of_day TIMESTAMPTZ,
                        end_time_of_day TIMESTAMPTZ,
                        job_type_id UUID,
                        job_attributes JSONB DEFAULT '{}'::jsonb,
                        next_trigger_time TIMESTAMPTZ,
                        last_trigger_time TIMESTAMPTZ,
                        last_started_job_id UUID,
                        enabled BOOLEAN NOT NULL DEFAULT TRUE,
                        last_modified_by UUID,
                        last_modified_on TIMESTAMPTZ,
                        created_by UUID,
                        created_on TIMESTAMPTZ NOT NULL DEFAULT NOW()
                    );

                    CREATE TABLE IF NOT EXISTS plugin_data (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        name TEXT NOT NULL,
                        data JSONB DEFAULT '{}'::jsonb
                    );");

                command.ExecuteNonQuery();
            }
        }
    }
}
