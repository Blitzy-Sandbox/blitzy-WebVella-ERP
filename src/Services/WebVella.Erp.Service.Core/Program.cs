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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.RateLimiting;
using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
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
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
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
            // Kestrel Server Header Suppression
            // Prevents disclosure of server technology stack in HTTP responses.
            // Reduces information leakage that aids attacker reconnaissance.
            // =====================================================================
            builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
                options => options.AddServerHeader = false);

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

            // =================================================================
            // SECURITY — Startup key validation warnings
            // Detect when hardcoded default keys are in use and emit prominent
            // warnings. In a production deployment environment variables MUST
            // supply unique keys; default constants exist only to keep the
            // developer inner-loop functional.
            // =================================================================
            if (JwtTokenOptions.IsDefaultKey(jwtKey))
            {
                var msg = "SECURITY WARNING: JWT signing key is the built-in " +
                          "default development key. Set the 'Jwt:Key' " +
                          "configuration value or WEBVELLA_JWT_KEY environment " +
                          "variable before deploying to production.";
                Console.Error.WriteLine($"[Core Service] {msg}");
                builder.Services.AddSingleton<string>(sp => msg); // discoverable marker
            }
            if (CryptoUtility.IsUsingDefaultKey)
            {
                var msg = "SECURITY WARNING: AES encryption key is the built-in " +
                          "default. Set the 'WEBVELLA_ENCRYPTION_KEY' environment " +
                          "variable before deploying to production.";
                Console.Error.WriteLine($"[Core Service] {msg}");
            }
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
                Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() },
                // Limit deserialization depth to prevent stack overflow / DoS from
                // deeply nested JSON payloads. 64 levels is generous for legitimate
                // ERP data while blocking adversarial inputs (e.g., 200+ levels).
                MaxDepth = 64
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

                // =============================================================
                // Token Revocation Check via Distributed Cache
                // After a token is refreshed, the old token's identifier is
                // stored in Redis with a TTL matching its remaining lifetime.
                // This event handler rejects blacklisted tokens on every request.
                // =============================================================
                options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var cache = context.HttpContext.RequestServices
                            .GetService<IDistributedCache>();
                        if (cache == null) return;

                        // Extract raw token from Authorization header to compute
                        // the blacklist key — compatible with both JwtSecurityToken
                        // and JsonWebToken (default in .NET 10+).
                        var authHeader = context.HttpContext.Request.Headers["Authorization"]
                            .FirstOrDefault();
                        if (string.IsNullOrEmpty(authHeader)) return;

                        var rawToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? authHeader.Substring(7)
                            : authHeader;

                        // Derive the blacklist key using the same logic as
                        // JwtTokenHandler.BlacklistTokenAsync: SHA256 hash of the raw
                        // token string (fallback when no JTI claim is present).
                        var key = Convert.ToBase64String(
                            System.Security.Cryptography.SHA256.HashData(
                                Encoding.UTF8.GetBytes(rawToken)));

                        var blacklisted = await cache.GetStringAsync(
                            $"jwt_blacklist:{key}");
                        if (blacklisted != null)
                        {
                            context.Fail("Token has been revoked.");
                        }
                    }
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
                    // Limit deserialization depth to prevent stack overflow / DoS from
                    // deeply nested JSON payloads sent to REST API endpoints.
                    options.SerializerSettings.MaxDepth = 64;
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
                        cfg.UseNewtonsoftJsonSerializer();
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
                        // Use Newtonsoft.Json serializer for MassTransit messages.
                        // Required because EntityRecord extends Expando (a DynamicObject),
                        // which System.Text.Json cannot serialize. Newtonsoft.Json handles
                        // dynamic property bags correctly, ensuring Record payloads in
                        // domain events are properly serialized across service boundaries.
                        cfg.UseNewtonsoftJsonSerializer();
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
            // Rate Limiting — brute force protection for login endpoint
            // Configured as a fixed-window limiter: 5 requests per minute per
            // IP address on the "login" policy. Returns HTTP 429 Too Many
            // Requests when exceeded. Applied via [EnableRateLimiting("login")]
            // attribute on the SecurityController login action.
            // =================================================================
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddFixedWindowLimiter("login", opt =>
                {
                    opt.PermitLimit = 5;
                    opt.Window = TimeSpan.FromMinutes(1);
                    opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    opt.QueueLimit = 0;
                });
            });

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

            // =================================================================
            // JSON Payload Depth Guard Middleware
            // Inspects the raw request body of JSON-typed requests using
            // System.Text.Json's Utf8JsonReader (which has reliable built-in
            // MaxDepth enforcement). Returns HTTP 400 if the JSON nesting
            // depth exceeds 64 levels. This prevents DoS via deeply nested
            // payloads that can cause stack overflows or excessive memory use
            // in the downstream Newtonsoft.Json model binder.
            //
            // The body stream is buffered and rewound so that downstream
            // middleware and MVC can re-read it normally.
            // =================================================================
            app.Use(async (context, next) =>
            {
                const int maxJsonDepth = 64;

                if (context.Request.ContentType != null &&
                    context.Request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
                    context.Request.ContentLength > 0 &&
                    (context.Request.Method == "POST" || context.Request.Method == "PUT" || context.Request.Method == "PATCH"))
                {
                    context.Request.EnableBuffering();
                    var body = context.Request.Body;
                    var memoryStream = new MemoryStream();
                    await body.CopyToAsync(memoryStream);
                    var bytes = memoryStream.ToArray();

                    // Validate JSON depth using System.Text.Json (fast, streaming)
                    try
                    {
                        var readerOptions = new System.Text.Json.JsonReaderOptions
                        {
                            MaxDepth = maxJsonDepth,
                            AllowTrailingCommas = true,
                            CommentHandling = System.Text.Json.JsonCommentHandling.Skip
                        };
                        var reader = new System.Text.Json.Utf8JsonReader(bytes, readerOptions);
                        while (reader.Read()) { /* exhaust all tokens to trigger depth check */ }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.ContentType = "application/json";
                        var errorResponse = new
                        {
                            success = false,
                            timestamp = DateTime.UtcNow,
                            message = $"Invalid JSON payload: the JSON payload exceeds the maximum allowed nesting depth of {maxJsonDepth}.",
                            errors = new[] { new { key = (string?)null, value = (string?)null, message = $"JSON nesting depth exceeds {maxJsonDepth}" } },
                            @object = (object?)null
                        };
                        await context.Response.WriteAsync(
                            JsonConvert.SerializeObject(errorResponse));
                        return; // short-circuit — do not call next()
                    }

                    // Rewind the body stream so MVC can re-read it
                    context.Request.Body = new MemoryStream(bytes);
                }

                await next();
            });

            // Response compression — before routing
            app.UseResponseCompression();

            // =================================================================
            // Security Headers Middleware
            // Adds defense-in-depth HTTP response headers to ALL responses:
            // - X-Content-Type-Options: nosniff — prevents MIME type sniffing
            // - X-Frame-Options: DENY — prevents clickjacking via iframes
            // Placed early in the pipeline so headers are added even for error
            // responses and health check endpoints.
            // =================================================================
            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                await next();
            });

            // CORS — before routing and auth
            app.UseCors();

            // Routing
            app.UseRouting();

            // Authentication and Authorization
            app.UseAuthentication();
            app.UseAuthorization();

            // Rate limiting — after auth so IP and user context are available
            app.UseRateLimiter();

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

                // ============================================================
                // AutoMapper initialization — ported from monolith ErpMvcExtensions.cs
                // lines 68-76 and Api/Models/AutoMapper/AutoMapperConfiguration.cs.
                // Registers all type mapping profiles required by EntityManager,
                // EntityRelationManager, RecordManager and other Core API components.
                // Must execute before any MapTo<T>() calls.
                // ============================================================
                InitializeAutoMapper(logger);

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

                    // Seed system entities, relations, and default records
                    // Ported from monolith ERPService.InitializeSystemEntities() (lines 51-830).
                    // Uses version-gated initialization so entities are created only once.
                    SeedSystemEntities(dbContext, app.Configuration, app.Services, logger);

                    // ============================================================
                    // Register EQL default providers so that EqlCommand instances
                    // created via the legacy List<EqlParameter> constructors
                    // (used by SecurityManager.GetUser, RecordManager, etc.)
                    // can resolve entity metadata, relation metadata, field values,
                    // and entity permissions.
                    // Resolved from a DI scope to avoid the "Cannot resolve scoped
                    // service from root provider" error in Development/test mode.
                    // The scope is intentionally not disposed because the EQL default
                    // providers need to live for the application's lifetime.
                    // ============================================================
                    var eqlScope = app.Services.CreateScope();
                    var entityManager = eqlScope.ServiceProvider.GetRequiredService<EntityManager>();
                    var relationManager = eqlScope.ServiceProvider.GetRequiredService<EntityRelationManager>();
                    EqlCommand.DefaultEntityProvider = new CoreEqlEntityProvider(entityManager);
                    EqlCommand.DefaultRelationProvider = new CoreEqlRelationProvider(relationManager);
                    EqlCommand.DefaultFieldValueExtractor = new CoreEqlFieldValueExtractor();
                    EqlCommand.DefaultSecurityProvider = new CoreEqlSecurityProvider();
                    logger.LogInformation("Core Platform Service: EQL default providers initialized.");
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
        /// Seeds system entities (user, role, user_file), the user_role relation,
        /// and default records (admin/regular/guest roles, system user, first user).
        /// Ported from monolith ERPService.InitializeSystemEntities() lines 51-830.
        ///
        /// Uses the system_settings.version column as an idempotency guard:
        /// - version 0 (or no row): first run → create everything, set version to 1
        /// - version >= 1: already initialized → skip
        ///
        /// All entity definitions, field schemas, permissions, and seed data are
        /// preserved exactly from the monolith for backward compatibility (AAP 0.8.1).
        /// </summary>
        private static void SeedSystemEntities(
            CoreDbContext dbContext,
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            ILogger logger)
        {
            try
            {
                using (var connection = dbContext.CreateConnection())
                {
                    // Check current version from system_settings to implement idempotency
                    int currentVersion = 0;
                    var systemSettingsId = new Guid("F3223177-B2FF-43F5-9A4B-FF16FC67D186");

                    using (var checkCmd = connection.CreateCommand(
                        "SELECT version FROM system_settings LIMIT 1"))
                    {
                        var result = checkCmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            currentVersion = Convert.ToInt32(result);
                        }
                    }

                    if (currentVersion >= 1)
                    {
                        logger.LogInformation(
                            "Core Platform Service: System entities already initialized (version={Version}). Skipping seed.",
                            currentVersion);
                        return;
                    }

                    logger.LogInformation("Core Platform Service: Seeding system entities (version 0 → 1)...");

                    // Open system security scope for the entire seeding process.
                    // Preserved from monolith ERPService.InitializeSystemEntities() which
                    // runs with system-level permissions (no user context during startup).
                    using var systemScope = SecurityContext.OpenSystemScope();

                    // Create managers for entity/relation/record operations.
                    // These are created directly (not via DI) because we are in the
                    // startup initialization phase before the app is accepting requests.
                    var entMan = new EntityManager(dbContext, configuration);
                    var relMan = new EntityRelationManager(dbContext, configuration);
                    // RecordManager needs IPublishEndpoint — resolve from DI for MassTransit
                    var publishEndpoint = serviceProvider.GetRequiredService<IPublishEndpoint>();
                    var recMan = new RecordManager(
                        dbContext, entMan, relMan, publishEndpoint,
                        ignoreSecurity: true, publishEvents: false);

                    connection.BeginTransaction();

                    try
                    {
                        // ============================================================
                        // Create User Entity
                        // Preserved from ERPService.cs lines 58-341
                        // ============================================================
                        {
                            var userEntity = new InputEntity();
                            userEntity.Id = SystemIds.UserEntityId;
                            userEntity.Name = "user";
                            userEntity.Label = "User";
                            userEntity.LabelPlural = "Users";
                            userEntity.System = true;
                            userEntity.Color = "#f44336";
                            userEntity.IconName = "fa fa-user";
                            userEntity.RecordPermissions = new RecordPermissions();
                            userEntity.RecordPermissions.CanCreate = new List<Guid>();
                            userEntity.RecordPermissions.CanRead = new List<Guid>();
                            userEntity.RecordPermissions.CanUpdate = new List<Guid>();
                            userEntity.RecordPermissions.CanDelete = new List<Guid>();
                            userEntity.RecordPermissions.CanCreate.Add(SystemIds.GuestRoleId);
                            userEntity.RecordPermissions.CanCreate.Add(SystemIds.AdministratorRoleId);
                            userEntity.RecordPermissions.CanRead.Add(SystemIds.GuestRoleId);
                            userEntity.RecordPermissions.CanRead.Add(SystemIds.RegularRoleId);
                            userEntity.RecordPermissions.CanRead.Add(SystemIds.AdministratorRoleId);
                            userEntity.RecordPermissions.CanUpdate.Add(SystemIds.AdministratorRoleId);
                            userEntity.RecordPermissions.CanDelete.Add(SystemIds.AdministratorRoleId);
                            var response = entMan.CreateEntity(userEntity, createOnlyIdField: true, checkPermissions: false);
                            logger.LogInformation("Core Platform Service: CreateEntity(user) result: Success={Success}, Message={Message}, Object={HasObject}",
                                response.Success, response.Message, response.Object != null);
                            if (!response.Success)
                                throw new Exception("CREATE USER ENTITY:" + response.Message);
                            // Debug: check if entity was actually saved to DB and what JSON looks like
                            {
                                using (var debugCon = dbContext.CreateConnection())
                                {
                                    var debugCmd = debugCon.CreateCommand("SELECT left(json::text, 500) FROM entities WHERE id=@id");
                                    var p = debugCmd.CreateParameter() as Npgsql.NpgsqlParameter;
                                    p.ParameterName = "id";
                                    p.Value = SystemIds.UserEntityId;
                                    debugCmd.Parameters.Add(p);
                                    var jsonResult = debugCmd.ExecuteScalar();
                                    logger.LogInformation("Core Platform Service: User entity JSON in DB (first 500 chars): {Json}",
                                        jsonResult?.ToString() ?? "NULL");
                                }
                            }

                            // created_on field
                            {
                                var field = new InputDateTimeField();
                                field.Id = new Guid("6C054A68-9EDE-4940-A0A7-E8D2B4F1E05F");
                                field.Name = "created_on";
                                field.Label = "Created On";
                                field.Required = true;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = null;
                                field.Format = "yyyy-MMM-dd HH:mm";
                                field.UseCurrentTimeAsDefaultValue = true;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // first_name field
                            {
                                var field = new InputTextField();
                                field.Id = new Guid("DF211549-41CC-4D11-BB43-DACA4D89C4EB");
                                field.Name = "first_name";
                                field.Label = "First Name";
                                field.Required = true;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = "";
                                field.MaxLength = null;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // last_name field
                            {
                                var field = new InputTextField();
                                field.Id = new Guid("63E685B9-B2C3-4EC9-85B6-42A8C0D655B4");
                                field.Name = "last_name";
                                field.Label = "Last Name";
                                field.Required = true;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = "";
                                field.MaxLength = null;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // username field
                            {
                                var field = new InputTextField();
                                field.Id = new Guid("263c0b21-88c1-4c2b-80b4-db7402b0d2e2");
                                field.Name = "username";
                                field.Label = "Username";
                                field.Required = true;
                                field.Unique = true;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = "";
                                field.MaxLength = null;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // email field
                            {
                                var field = new InputEmailField();
                                field.Id = new Guid("9FC75C8F-CE80-4A64-81D7-E2BEFA5E4815");
                                field.Name = "email";
                                field.Label = "Email";
                                field.Required = true;
                                field.Unique = true;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = "";
                                field.MaxLength = null;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // password field
                            {
                                var field = new InputPasswordField();
                                field.Id = new Guid("4EDE88D9-217A-4462-9300-EA0D6AFCDCEA");
                                field.Name = "password";
                                field.Label = "Password";
                                field.Required = true;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.MinLength = null;
                                field.MaxLength = 24;
                                field.Encrypted = true;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // last_logged_in field
                            {
                                var field = new InputDateTimeField();
                                field.Id = new Guid("3C85CCEC-D010-4514-8F31-5AFF12760F8B");
                                field.Name = "last_logged_in";
                                field.Label = "Last Logged In";
                                field.Required = false;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = true;
                                field.System = true;
                                field.DefaultValue = null;
                                field.Format = "yyyy-MMM-dd HH:mm";
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // enabled field
                            {
                                var field = new InputCheckboxField();
                                field.Id = new Guid("C0C63650-7572-4A27-8CB4-3FFFE358214A");
                                field.Name = "enabled";
                                field.Label = "Enabled";
                                field.Required = true;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = true;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // verified field
                            {
                                var field = new InputCheckboxField();
                                field.Id = new Guid("F1BA5069-8CC9-4E66-BCC3-60E33AD8CF68");
                                field.Name = "verified";
                                field.Label = "Verified";
                                field.Required = true;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = false;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // preferences field
                            {
                                var field = new InputTextField();
                                field.Id = new Guid("0b54f803-67a6-4e26-9714-cd9bdbb95742");
                                field.Name = "preferences";
                                field.Label = "Preferences";
                                field.Required = false;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = null;
                                field.MaxLength = null;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }

                            // image field
                            {
                                var field = new InputImageField();
                                field.Id = new Guid("bf199b74-4448-4f58-93f5-6b86d888843b");
                                field.Name = "image";
                                field.Label = "Image";
                                field.Required = false;
                                field.Unique = false;
                                field.Searchable = false;
                                field.Auditable = false;
                                field.System = true;
                                field.DefaultValue = null;
                                entMan.CreateField(SystemIds.UserEntityId, field, false);
                            }
                        }

                        // ============================================================
                        // Create Role Entity
                        // Preserved from ERPService.cs lines 344-419
                        // ============================================================
                        {
                            var roleEntity = new InputEntity();
                            roleEntity.Id = SystemIds.RoleEntityId;
                            roleEntity.Name = "role";
                            roleEntity.Label = "Role";
                            roleEntity.LabelPlural = "Roles";
                            roleEntity.System = true;
                            roleEntity.Color = "#f44336";
                            roleEntity.IconName = "fa fa-key";
                            roleEntity.RecordPermissions = new RecordPermissions();
                            roleEntity.RecordPermissions.CanCreate = new List<Guid>();
                            roleEntity.RecordPermissions.CanRead = new List<Guid>();
                            roleEntity.RecordPermissions.CanUpdate = new List<Guid>();
                            roleEntity.RecordPermissions.CanDelete = new List<Guid>();
                            roleEntity.RecordPermissions.CanCreate.Add(SystemIds.GuestRoleId);
                            roleEntity.RecordPermissions.CanCreate.Add(SystemIds.AdministratorRoleId);
                            roleEntity.RecordPermissions.CanRead.Add(SystemIds.RegularRoleId);
                            roleEntity.RecordPermissions.CanRead.Add(SystemIds.GuestRoleId);
                            roleEntity.RecordPermissions.CanRead.Add(SystemIds.AdministratorRoleId);
                            roleEntity.RecordPermissions.CanUpdate.Add(SystemIds.AdministratorRoleId);
                            roleEntity.RecordPermissions.CanDelete.Add(SystemIds.AdministratorRoleId);
                            var response = entMan.CreateEntity(roleEntity, createOnlyIdField: true, checkPermissions: false);
                            if (!response.Success)
                                throw new Exception("CREATE ROLE ENTITY:" + response.Message);

                            // name field
                            {
                                var nameField = new InputTextField();
                                nameField.Id = new Guid("36F91EBD-5A02-4032-8498-B7F716F6A349");
                                nameField.Name = "name";
                                nameField.Label = "Name";
                                nameField.PlaceholderText = "";
                                nameField.Description = "The name of the role";
                                nameField.HelpText = "";
                                nameField.Required = true;
                                nameField.Unique = false;
                                nameField.Searchable = false;
                                nameField.Auditable = false;
                                nameField.System = true;
                                nameField.DefaultValue = "";
                                nameField.MaxLength = 200;
                                nameField.EnableSecurity = true;
                                nameField.Permissions = new FieldPermissions();
                                nameField.Permissions.CanRead = new List<Guid>();
                                nameField.Permissions.CanUpdate = new List<Guid>();
                                nameField.Permissions.CanRead.Add(SystemIds.AdministratorRoleId);
                                nameField.Permissions.CanRead.Add(SystemIds.RegularRoleId);
                                nameField.Permissions.CanUpdate.Add(SystemIds.AdministratorRoleId);
                                entMan.CreateField(roleEntity.Id.Value, nameField, false);
                            }

                            // description field
                            {
                                var descField = new InputTextField();
                                descField.Id = new Guid("4A8B9E0A-1C36-40C6-972B-B19E2B5D265B");
                                descField.Name = "description";
                                descField.Label = "Description";
                                descField.PlaceholderText = "";
                                descField.Description = "";
                                descField.HelpText = "";
                                descField.Required = true;
                                descField.Unique = false;
                                descField.Searchable = false;
                                descField.Auditable = false;
                                descField.System = true;
                                descField.DefaultValue = "";
                                descField.MaxLength = 200;
                                entMan.CreateField(roleEntity.Id.Value, descField, false);
                            }
                        }

                        // ============================================================
                        // Create User-Role Relation
                        // Preserved from ERPService.cs lines 421-442
                        // ============================================================
                        {
                            var userEntity = entMan.ReadEntity(SystemIds.UserEntityId).Object;
                            var roleEntity = entMan.ReadEntity(SystemIds.RoleEntityId).Object;

                            var userRoleRelation = new EntityRelation();
                            userRoleRelation.Id = SystemIds.UserRoleRelationId;
                            userRoleRelation.Name = "user_role";
                            userRoleRelation.Label = "User-Role";
                            userRoleRelation.System = true;
                            userRoleRelation.RelationType = EntityRelationType.ManyToMany;
                            userRoleRelation.TargetEntityId = userEntity.Id;
                            userRoleRelation.TargetFieldId = userEntity.Fields.Single(x => x.Name == "id").Id;
                            userRoleRelation.OriginEntityId = roleEntity.Id;
                            userRoleRelation.OriginFieldId = roleEntity.Fields.Single(x => x.Name == "id").Id;
                            var result = relMan.Create(userRoleRelation);
                            if (!result.Success)
                                throw new Exception("CREATE USER-ROLE RELATION:" + result.Message);
                        }

                        // ============================================================
                        // Seed System Records
                        // Preserved from ERPService.cs lines 444-527
                        // ============================================================

                        // System user
                        {
                            var user = new EntityRecord();
                            user["id"] = SystemIds.SystemUserId;
                            user["first_name"] = "Local";
                            user["last_name"] = "System";
                            user["password"] = Guid.NewGuid().ToString();
                            user["email"] = "system@webvella.com";
                            user["username"] = "system";
                            user["created_on"] = new DateTime(2010, 10, 10);
                            user["enabled"] = true;
                            var result = recMan.CreateRecord("user", user);
                            if (!result.Success)
                                throw new Exception("CREATE SYSTEM USER RECORD:" + result.Message);
                        }

                        // First (admin) user
                        {
                            var user = new EntityRecord();
                            user["id"] = SystemIds.FirstUserId;
                            user["first_name"] = "WebVella";
                            user["last_name"] = "Erp";
                            user["password"] = "erp";
                            user["email"] = "erp@webvella.com";
                            user["username"] = "administrator";
                            user["created_on"] = new DateTime(2010, 10, 10);
                            user["enabled"] = true;
                            var result = recMan.CreateRecord("user", user);
                            if (!result.Success)
                                throw new Exception("CREATE FIRST USER RECORD:" + result.Message);
                        }

                        // Administrator role
                        {
                            var adminRole = new EntityRecord();
                            adminRole["id"] = SystemIds.AdministratorRoleId;
                            adminRole["name"] = "administrator";
                            adminRole["description"] = "";
                            var result = recMan.CreateRecord("role", adminRole);
                            if (!result.Success)
                                throw new Exception("CREATE ADMINISTRATOR ROLE RECORD:" + result.Message);
                        }

                        // Regular role
                        {
                            var regularRole = new EntityRecord();
                            regularRole["id"] = SystemIds.RegularRoleId;
                            regularRole["name"] = "regular";
                            regularRole["description"] = "";
                            var result = recMan.CreateRecord("role", regularRole);
                            if (!result.Success)
                                throw new Exception("CREATE REGULAR ROLE RECORD:" + result.Message);
                        }

                        // Guest role
                        {
                            var guestRole = new EntityRecord();
                            guestRole["id"] = SystemIds.GuestRoleId;
                            guestRole["name"] = "guest";
                            guestRole["description"] = "";
                            var result = recMan.CreateRecord("role", guestRole);
                            if (!result.Success)
                                throw new Exception("CREATE GUEST ROLE RECORD:" + result.Message);
                        }

                        // System user → administrator role
                        {
                            var result = recMan.CreateRelationManyToManyRecord(
                                SystemIds.UserRoleRelationId, SystemIds.AdministratorRoleId, SystemIds.SystemUserId);
                            if (!result.Success)
                                throw new Exception("CREATE SYSTEM-USER <-> ADMINISTRATOR ROLE:" + result.Message);
                        }

                        // First user → administrator + regular roles
                        {
                            var result = recMan.CreateRelationManyToManyRecord(
                                SystemIds.UserRoleRelationId, SystemIds.AdministratorRoleId, SystemIds.FirstUserId);
                            if (!result.Success)
                                throw new Exception("CREATE FIRST-USER <-> ADMINISTRATOR ROLE:" + result.Message);

                            result = recMan.CreateRelationManyToManyRecord(
                                SystemIds.UserRoleRelationId, SystemIds.RegularRoleId, SystemIds.FirstUserId);
                            if (!result.Success)
                                throw new Exception("CREATE FIRST-USER <-> REGULAR ROLE:" + result.Message);
                        }

                        // ============================================================
                        // Update system_settings version to 1
                        // ============================================================
                        using (var versionCmd = connection.CreateCommand(
                            "INSERT INTO system_settings (id, version) VALUES (@id, 1) " +
                            "ON CONFLICT (id) DO UPDATE SET version = 1"))
                        {
                            versionCmd.Parameters.Add(new Npgsql.NpgsqlParameter("id", systemSettingsId));
                            versionCmd.ExecuteNonQuery();
                        }

                        connection.CommitTransaction();

                        logger.LogInformation(
                            "Core Platform Service: System entities seeded successfully. " +
                            "Created entities: user, role. Relations: user_role. " +
                            "Seed records: 2 users, 3 roles, 3 role assignments.");
                    }
                    catch
                    {
                        connection.RollbackTransaction();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Core Platform Service: Failed to seed system entities. " +
                    "Entity-based operations (EQL queries, record CRUD) will not function " +
                    "until system entities are manually created.");
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
                    );

                    CREATE TABLE IF NOT EXISTS files (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        object_id NUMERIC(18) NOT NULL DEFAULT 0,
                        filepath TEXT NOT NULL,
                        created_on TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        modified_on TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        created_by UUID,
                        modified_by UUID
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS idx_filepath ON files (filepath);

                    CREATE TABLE IF NOT EXISTS data_source (
                        id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                        name TEXT NOT NULL,
                        description TEXT,
                        weight INTEGER NOT NULL DEFAULT 10,
                        eql_text TEXT,
                        sql_text TEXT,
                        parameters_json JSONB DEFAULT '[]'::jsonb,
                        fields_json JSONB DEFAULT '[]'::jsonb,
                        CONSTRAINT ux_data_source_name UNIQUE (name)
                    );");

                command.ExecuteNonQuery();
            }
        }
        /// <summary>
        /// Initializes the global AutoMapper configuration required by EntityManager,
        /// EntityRelationManager, RecordManager, and other Core API components.
        /// Ported from monolith: WebVella.Erp/Api/Models/AutoMapper/AutoMapperConfiguration.cs
        /// and WebVella.Erp/Api/Models/AutoMapper/Profiles/*.cs
        /// </summary>
        private static void InitializeAutoMapper(ILogger logger)
        {
            if (ErpAutoMapper.Mapper != null)
            {
                logger.LogInformation("Core Platform Service: AutoMapper already initialized — skipping.");
                return;
            }

            logger.LogInformation("Core Platform Service: Initializing AutoMapper profiles...");

            var cfg = ErpAutoMapperConfiguration.MappingExpressions;

            // ================================================================
            // Type converters (from monolith AutoMapperConfiguration.cs)
            // ================================================================
            cfg.CreateMap<Guid, string>().ConvertUsing(src => src.ToString());
            cfg.CreateMap<DateTimeOffset, DateTime>().ConvertUsing(src => src.DateTime);

            // ================================================================
            // Entity mappings (from monolith EntityProfile.cs)
            // ================================================================
            cfg.CreateMap<Entity, InputEntity>();
            cfg.CreateMap<InputEntity, Entity>()
                .ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty))
                .ForMember(x => x.System, opt => opt.MapFrom(y => (y.System.HasValue) ? y.System.Value : false));
            cfg.CreateMap<Entity, DbEntity>();
            cfg.CreateMap<DbEntity, Entity>();

            // ================================================================
            // EntityRelation mappings (from monolith EntityRelationProfile.cs)
            // ================================================================
            cfg.CreateMap<EntityRelation, DbEntityRelation>();
            cfg.CreateMap<DbEntityRelation, EntityRelation>();

            // ================================================================
            // EntityRelationOptions mappings
            // ================================================================
            cfg.CreateMap<EntityRelationOptions, DbEntityRelationOptions>();
            cfg.CreateMap<DbEntityRelationOptions, EntityRelationOptions>();

            // ================================================================
            // Permission mappings (from monolith RecordPermissionsProfile.cs / FieldPermissionsProfile.cs)
            // ================================================================
            cfg.CreateMap<RecordPermissions, DbRecordPermissions>();
            cfg.CreateMap<DbRecordPermissions, RecordPermissions>();
            cfg.CreateMap<FieldPermissions, DbFieldPermissions>();
            cfg.CreateMap<DbFieldPermissions, FieldPermissions>();

            // ================================================================
            // Select option / currency type mappings
            // ================================================================
            cfg.CreateMap<SelectOption, DbSelectFieldOption>();
            cfg.CreateMap<DbSelectFieldOption, SelectOption>();
            cfg.CreateMap<CurrencyType, DbCurrencyType>();
            cfg.CreateMap<DbCurrencyType, CurrencyType>();

            // ================================================================
            // Field base type with polymorphic includes (from monolith FieldProfile.cs)
            // ================================================================
            cfg.CreateMap<Field, InputField>()
                .Include<AutoNumberField, InputAutoNumberField>()
                .Include<CheckboxField, InputCheckboxField>()
                .Include<CurrencyField, InputCurrencyField>()
                .Include<DateField, InputDateField>()
                .Include<DateTimeField, InputDateTimeField>()
                .Include<EmailField, InputEmailField>()
                .Include<FileField, InputFileField>()
                .Include<GeographyField, InputGeographyField>()
                .Include<GuidField, InputGuidField>()
                .Include<HtmlField, InputHtmlField>()
                .Include<ImageField, InputImageField>()
                .Include<MultiLineTextField, InputMultiLineTextField>()
                .Include<MultiSelectField, InputMultiSelectField>()
                .Include<NumberField, InputNumberField>()
                .Include<PasswordField, InputPasswordField>()
                .Include<PercentField, InputPercentField>()
                .Include<PhoneField, InputPhoneField>()
                .Include<SelectField, InputSelectField>()
                .Include<TextField, InputTextField>()
                .Include<UrlField, InputUrlField>();

            cfg.CreateMap<InputField, Field>()
                .Include<InputAutoNumberField, AutoNumberField>()
                .Include<InputCheckboxField, CheckboxField>()
                .Include<InputCurrencyField, CurrencyField>()
                .Include<InputDateField, DateField>()
                .Include<InputDateTimeField, DateTimeField>()
                .Include<InputEmailField, EmailField>()
                .Include<InputFileField, FileField>()
                .Include<InputGeographyField, GeographyField>()
                .Include<InputGuidField, GuidField>()
                .Include<InputHtmlField, HtmlField>()
                .Include<InputImageField, ImageField>()
                .Include<InputMultiLineTextField, MultiLineTextField>()
                .Include<InputMultiSelectField, MultiSelectField>()
                .Include<InputNumberField, NumberField>()
                .Include<InputPasswordField, PasswordField>()
                .Include<InputPercentField, PercentField>()
                .Include<InputPhoneField, PhoneField>()
                .Include<InputSelectField, SelectField>()
                .Include<InputTextField, TextField>()
                .Include<InputUrlField, UrlField>()
                .ForMember(x => x.Id, opt => opt.MapFrom(y => (y.Id.HasValue) ? y.Id.Value : Guid.Empty))
                .ForMember(x => x.System, opt => opt.MapFrom(y => (y.System.HasValue) ? y.System.Value : false))
                .ForMember(x => x.Required, opt => opt.MapFrom(y => (y.Required.HasValue) ? y.Required.Value : false))
                .ForMember(x => x.Unique, opt => opt.MapFrom(y => (y.Unique.HasValue) ? y.Unique.Value : false))
                .ForMember(x => x.Searchable, opt => opt.MapFrom(y => (y.Searchable.HasValue) ? y.Searchable.Value : false))
                .ForMember(x => x.Auditable, opt => opt.MapFrom(y => (y.Auditable.HasValue) ? y.Auditable.Value : false));

            cfg.CreateMap<Field, DbBaseField>()
                .Include<AutoNumberField, DbAutoNumberField>()
                .Include<CheckboxField, DbCheckboxField>()
                .Include<CurrencyField, DbCurrencyField>()
                .Include<DateField, DbDateField>()
                .Include<DateTimeField, DbDateTimeField>()
                .Include<EmailField, DbEmailField>()
                .Include<FileField, DbFileField>()
                .Include<GeographyField, DbGeographyField>()
                .Include<GuidField, DbGuidField>()
                .Include<HtmlField, DbHtmlField>()
                .Include<ImageField, DbImageField>()
                .Include<MultiLineTextField, DbMultiLineTextField>()
                .Include<MultiSelectField, DbMultiSelectField>()
                .Include<NumberField, DbNumberField>()
                .Include<PasswordField, DbPasswordField>()
                .Include<PercentField, DbPercentField>()
                .Include<PhoneField, DbPhoneField>()
                .Include<SelectField, DbSelectField>()
                .Include<TextField, DbTextField>()
                .Include<UrlField, DbUrlField>();

            cfg.CreateMap<DbBaseField, Field>()
                .Include<DbAutoNumberField, AutoNumberField>()
                .Include<DbCheckboxField, CheckboxField>()
                .Include<DbCurrencyField, CurrencyField>()
                .Include<DbDateField, DateField>()
                .Include<DbDateTimeField, DateTimeField>()
                .Include<DbEmailField, EmailField>()
                .Include<DbFileField, FileField>()
                .Include<DbGeographyField, GeographyField>()
                .Include<DbGuidField, GuidField>()
                .Include<DbHtmlField, HtmlField>()
                .Include<DbImageField, ImageField>()
                .Include<DbMultiLineTextField, MultiLineTextField>()
                .Include<DbMultiSelectField, MultiSelectField>()
                .Include<DbNumberField, NumberField>()
                .Include<DbPasswordField, PasswordField>()
                .Include<DbPercentField, PercentField>()
                .Include<DbPhoneField, PhoneField>()
                .Include<DbSelectField, SelectField>()
                .Include<DbTextField, TextField>()
                .Include<DbUrlField, UrlField>();

            // ================================================================
            // Individual field type concrete maps (all 21 field types × 4 directions)
            // ================================================================
            var fieldTypeTriples = new (Type field, Type input, Type db)[]
            {
                (typeof(AutoNumberField), typeof(InputAutoNumberField), typeof(DbAutoNumberField)),
                (typeof(CheckboxField), typeof(InputCheckboxField), typeof(DbCheckboxField)),
                (typeof(CurrencyField), typeof(InputCurrencyField), typeof(DbCurrencyField)),
                (typeof(DateField), typeof(InputDateField), typeof(DbDateField)),
                (typeof(DateTimeField), typeof(InputDateTimeField), typeof(DbDateTimeField)),
                (typeof(EmailField), typeof(InputEmailField), typeof(DbEmailField)),
                (typeof(FileField), typeof(InputFileField), typeof(DbFileField)),
                (typeof(GeographyField), typeof(InputGeographyField), typeof(DbGeographyField)),
                (typeof(GuidField), typeof(InputGuidField), typeof(DbGuidField)),
                (typeof(HtmlField), typeof(InputHtmlField), typeof(DbHtmlField)),
                (typeof(ImageField), typeof(InputImageField), typeof(DbImageField)),
                (typeof(MultiLineTextField), typeof(InputMultiLineTextField), typeof(DbMultiLineTextField)),
                (typeof(MultiSelectField), typeof(InputMultiSelectField), typeof(DbMultiSelectField)),
                (typeof(NumberField), typeof(InputNumberField), typeof(DbNumberField)),
                (typeof(PasswordField), typeof(InputPasswordField), typeof(DbPasswordField)),
                (typeof(PercentField), typeof(InputPercentField), typeof(DbPercentField)),
                (typeof(PhoneField), typeof(InputPhoneField), typeof(DbPhoneField)),
                (typeof(SelectField), typeof(InputSelectField), typeof(DbSelectField)),
                (typeof(TextField), typeof(InputTextField), typeof(DbTextField)),
                (typeof(UrlField), typeof(InputUrlField), typeof(DbUrlField)),
            };

            foreach (var (field, input, db) in fieldTypeTriples)
            {
                cfg.CreateMap(field, input);
                cfg.CreateMap(input, field);
                cfg.CreateMap(field, db);
                cfg.CreateMap(db, field);
            }

            // ================================================================
            // ErpUser / ErpRole converters (from monolith AutoMapperConfiguration.cs)
            // EntityRecord → ErpUser, ErpUser → EntityRecord, EntityRecord → ErpRole
            // ================================================================
            cfg.CreateMap<EntityRecord, ErpUser>().ConvertUsing(new ErpUserRecordConverter());
            cfg.CreateMap<ErpUser, EntityRecord>().ConvertUsing(new ErpUserToRecordConverter());
            cfg.CreateMap<EntityRecord, ErpRole>().ConvertUsing(new ErpRoleRecordConverter());

            // ================================================================
            // ErrorModel → ValidationError (from monolith ErrorModelProfile.cs)
            // ================================================================
            cfg.CreateMap<ErrorModel, ValidationError>().ConvertUsing(
                src => src == null ? null : new ValidationError(src.Key ?? "id", src.Message));

            // ================================================================
            // SearchResult mapping (from monolith SearchResultProfile.cs)
            // ================================================================
            cfg.CreateMap<System.Data.DataRow, SearchResult>().ConvertUsing(
                src => src == null ? null : new SearchResult());

            // ================================================================
            // Database NN Relation Record mapping
            // ================================================================
            cfg.CreateMap<System.Data.DataRow, DatabaseNNRelationRecord>().ConvertUsing(
                src => src == null ? null : new DatabaseNNRelationRecord());

            // ================================================================
            // DataSource mapping (from monolith DataSourceProfile.cs)
            // DataSource data is stored as DataRow in the DB; mapped to model on retrieval
            // ================================================================
            cfg.CreateMap<System.Data.DataRow, DatabaseDataSource>().ConvertUsing(new DataRowToDatabaseDataSourceConverter());

            // ================================================================
            // Configure and initialize
            // ================================================================
            ErpAutoMapperConfiguration.Configure(cfg);
            ErpAutoMapper.Initialize(cfg);

            logger.LogInformation("Core Platform Service: AutoMapper initialized successfully.");
        }

        /// <summary>
        /// Converts EntityRecord to ErpUser. Ported from monolith ErpUserConverter.
        /// </summary>
        private class ErpUserRecordConverter : ITypeConverter<EntityRecord, ErpUser>
        {
            public ErpUser Convert(EntityRecord src, ErpUser dest, ResolutionContext ctx)
            {
                if (src == null) return null;
                var user = new ErpUser();
                user.Id = (Guid)src["id"];
                try { user.Username = (string)src["username"]; } catch (KeyNotFoundException) { }
                try { user.Email = (string)src["email"]; } catch (KeyNotFoundException) { }
                try { user.FirstName = (string)src["first_name"]; } catch (KeyNotFoundException) { }
                try { user.LastName = (string)src["last_name"]; } catch (KeyNotFoundException) { }
                try { user.Image = (string)src["image"]; } catch (KeyNotFoundException) { }
                try { user.Password = (string)src["password"]; } catch (KeyNotFoundException) { user.Password = null; }
                return user;
            }
        }

        /// <summary>
        /// Converts ErpUser to EntityRecord. Ported from monolith ErpUserConverterOposite.
        /// </summary>
        private class ErpUserToRecordConverter : ITypeConverter<ErpUser, EntityRecord>
        {
            public EntityRecord Convert(ErpUser src, EntityRecord dest, ResolutionContext ctx)
            {
                if (src == null) return null;
                var rec = new EntityRecord();
                rec["id"] = src.Id;
                rec["username"] = src.Username;
                rec["email"] = src.Email;
                rec["first_name"] = src.FirstName;
                rec["last_name"] = src.LastName;
                rec["image"] = src.Image;
                return rec;
            }
        }

        /// <summary>
        /// Converts EntityRecord to ErpRole. Ported from monolith ErpRoleConverter.
        /// </summary>
        private class ErpRoleRecordConverter : ITypeConverter<EntityRecord, ErpRole>
        {
            public ErpRole Convert(EntityRecord src, ErpRole dest, ResolutionContext ctx)
            {
                if (src == null) return null;
                var role = new ErpRole();
                role.Id = (Guid)src["id"];
                try { role.Name = (string)src["name"]; } catch (KeyNotFoundException) { }
                try { role.Description = (string)src["description"]; } catch (KeyNotFoundException) { }
                return role;
            }
        }

        /// <summary>
        /// Converts DataRow to DatabaseDataSource. Ported from monolith DataSourceProfile.cs.
        /// </summary>
        private class DataRowToDatabaseDataSourceConverter : ITypeConverter<System.Data.DataRow, DatabaseDataSource>
        {
            public DatabaseDataSource Convert(System.Data.DataRow source, DatabaseDataSource destination, ResolutionContext context)
            {
                if (source == null) return null;
                var outputObj = new DatabaseDataSource();
                outputObj.Id = (Guid)source["id"];
                outputObj.Name = (string)source["name"];
                outputObj.Description = (string)source["description"];
                outputObj.Weight = (int)source["weight"];
                outputObj.ReturnTotal = (bool)source["return_total"];
                outputObj.EqlText = (string)source["eql_text"];
                outputObj.SqlText = (string)source["sql_text"];
                outputObj.Parameters.AddRange(
                    JsonConvert.DeserializeObject<List<DataSourceParameter>>((string)source["parameters_json"]).ToArray());
                outputObj.Fields.AddRange(
                    JsonConvert.DeserializeObject<List<DataSourceModelFieldMeta>>((string)source["fields_json"]).ToArray());
                outputObj.EntityName = (string)source["entity_name"];
                foreach (var par in outputObj.Parameters)
                    if (par.Name.StartsWith("@"))
                        par.Name = par.Name.Substring(1);
                return outputObj;
            }
        }

        /// <summary>
        /// IEqlEntityProvider implementation that delegates to EntityManager for entity
        /// metadata resolution.  Registered as a singleton and also assigned to
        /// <see cref="EqlCommand.DefaultEntityProvider"/> so that EQL queries created
        /// via the legacy <c>List&lt;EqlParameter&gt;</c> constructors (SecurityManager,
        /// RecordManager, etc.) automatically resolve entity metadata.
        /// </summary>
        private class CoreEqlEntityProvider : IEqlEntityProvider
        {
            private readonly EntityManager _entityManager;

            public CoreEqlEntityProvider(EntityManager entityManager)
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
        /// IEqlRelationProvider implementation that delegates to EntityRelationManager.
        /// Registered as a singleton and assigned to <see cref="EqlCommand.DefaultRelationProvider"/>.
        /// </summary>
        private class CoreEqlRelationProvider : IEqlRelationProvider
        {
            private readonly EntityRelationManager _relationManager;

            public CoreEqlRelationProvider(EntityRelationManager relationManager)
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
        /// IEqlFieldValueExtractor implementation that delegates to DbRecordRepository.ExtractFieldValue.
        /// Bridges the EQL engine's result materialization to the existing field value conversion logic.
        /// </summary>
        private class CoreEqlFieldValueExtractor : IEqlFieldValueExtractor
        {
            public object ExtractFieldValue(object jToken, Field field)
            {
                return DbRecordRepository.ExtractFieldValue(jToken, field);
            }
        }

        /// <summary>
        /// IEqlSecurityProvider implementation that delegates entity-level permission checks
        /// to SecurityContext.HasEntityPermission. SecurityContext internally handles system user
        /// (unlimited permissions), role-based checks for authenticated users, and guest fallback.
        /// </summary>
        private class CoreEqlSecurityProvider : IEqlSecurityProvider
        {
            public bool HasEntityPermission(EntityPermission permission, Entity entity)
            {
                return SecurityContext.HasEntityPermission(permission, entity);
            }
        }
    }
}
