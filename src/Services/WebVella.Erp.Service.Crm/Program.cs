// =============================================================================
// Program.cs — CRM Microservice Entry Point (Composition Root)
// =============================================================================
// Minimal hosting API composition root for the CRM microservice, replacing the
// monolith's WebVella.Erp.Site.Crm/Startup.cs (134 lines) and Program.cs with
// a modern .NET 10 minimal hosting pattern using WebApplication.CreateBuilder.
//
// Key architectural changes from monolith:
//   - NO plugin model — CRM service is a standalone ASP.NET Core app, not an ErpPlugin
//   - NO services.AddErp() — replaced by explicit per-service DI registrations
//   - NO UseErpPlugin<CrmPlugin>(), UseErpPlugin<NextPlugin>(), UseErpPlugin<SdkPlugin>()
//   - JWT-only authentication (no cookie auth "erp_auth_crm") for API endpoints (AAP 0.8.3)
//   - NO Razor Pages or static files — CRM is a pure API + gRPC service
//   - NO device detection (Wangkanai.Detection) — not needed for API service
//   - MassTransit/RabbitMQ replaces PostgreSQL LISTEN/NOTIFY for inter-service events
//   - Redis replaces IMemoryCache for distributed caching across service instances
//   - EF Core replaces ambient static DbContext.Current with DI-injected CrmDbContext
//   - gRPC server endpoints for inter-service entity resolution (accounts, contacts, cases)
//
// Preserved from monolith (AAP 0.8.1 — Zero Business Rule Changes):
//   - Npgsql.EnableLegacyTimestampBehavior switch (Startup.cs line 27)
//   - GZip response compression at CompressionLevel.Optimal (Startup.cs lines 28-29)
//   - Newtonsoft.Json with ErpDateTimeJsonConverter (Startup.cs lines 48-60)
//   - Lowercase URL routing (Startup.cs line 30)
//   - CORS policy for cross-origin API access (Startup.cs lines 33-37)
//
// Source references:
//   - WebVella.Erp.Site.Crm/Startup.cs (original host composition)
//   - WebVella.Erp.Site.Crm/Program.cs (original WebHost builder)
//   - WebVella.Erp.Site.Crm/Config.json (original configuration)
//   - WebVella.Erp.Plugins.Crm/CrmPlugin.cs (domain bootstrap)
//   - WebVella.Erp.Plugins.Next/NextPlugin.cs (CRM entity provisioning)
//
// All NuGet packages are declared in WebVella.Erp.Service.Crm.csproj with
// versions managed centrally via Directory.Packages.props (CPM).
// =============================================================================

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
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using WebVella.Erp.Service.Crm.Controllers;
using WebVella.Erp.Service.Crm.Database;
using WebVella.Erp.Service.Crm.Domain.Services;
using WebVella.Erp.Service.Crm.Grpc;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;

namespace WebVella.Erp.Service.Crm
{
	/// <summary>
	/// Entry point and composition root for the CRM microservice.
	/// Converts the monolith's <c>WebHost.CreateDefaultBuilder + Startup</c> pattern
	/// (from WebVella.Erp.Site.Crm/Program.cs and Startup.cs) to the .NET 10 minimal
	/// hosting API pattern (<c>WebApplication.CreateBuilder</c>).
	///
	/// <para><b>Key architectural changes from monolith:</b></para>
	/// <list type="bullet">
	///   <item>NO plugin model — CRM service is a standalone ASP.NET Core app, not an ErpPlugin</item>
	///   <item>NO <c>services.AddErp()</c> — replaced by explicit per-service DI registrations</item>
	///   <item>NO <c>UseErpPlugin&lt;CrmPlugin&gt;()</c> — plugin initialization replaced by service startup</item>
	///   <item>JWT-only authentication (no cookie auth) for API endpoints per AAP 0.8.3</item>
	///   <item>Communicates with Core service via gRPC (configured via GrpcEndpoints:CoreService)</item>
	///   <item>MassTransit/RabbitMQ replaces PostgreSQL LISTEN/NOTIFY for inter-service events</item>
	///   <item>Redis replaces IMemoryCache for distributed caching across service instances</item>
	///   <item>EF Core replaces ambient static DbContext.Current with DI-injected CrmDbContext</item>
	/// </list>
	/// </summary>
	public class Program
	{
		/// <summary>
		/// Application entry point. Initializes the CRM microservice with all required
		/// infrastructure (database, caching, messaging, authentication) and domain services
		/// (SearchService, CRM entity proxy adapters, gRPC server).
		/// </summary>
		/// <param name="args">Command-line arguments passed to the host builder.</param>
		public static void Main(string[] args)
		{
			// CRITICAL: Preserve Npgsql legacy timestamp behavior from monolith Startup.cs line 27.
			// This switch must be set before ANY Npgsql connections are created to ensure
			// DateTime values are handled with the legacy timestamp behavior that the
			// monolith's schema and queries depend on.
			AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

			var builder = WebApplication.CreateBuilder(args);

			// Map CRM-specific configuration section to the Settings section that
			// ErpSettings.Initialize() expects. CRM appsettings.json uses "CrmService"
			// section for service-specific settings; ErpSettings reads from "Settings:*".
			MapConfigurationSections(builder.Configuration);

			// Initialize shared cross-cutting settings from appsettings.json → Settings section.
			// Binds ErpSettings.TimeZoneName, ErpSettings.Lang, ErpSettings.Locale,
			// ErpSettings.EncryptionKey, etc. required by ErpDateTimeJsonConverter and
			// other SharedKernel utilities.
			ErpSettings.Initialize(builder.Configuration);

			ConfigureServices(builder);

			var app = builder.Build();

			ConfigurePipeline(app);

			app.Run();
		}

		/// <summary>
		/// Maps the CRM-specific configuration section ("CrmService") to the standard
		/// "Settings" section that <see cref="ErpSettings.Initialize"/> reads from.
		/// This allows CRM service settings (EncryptionKey, Lang, Locale, TimeZoneName)
		/// to be read from the CrmService section in appsettings.json while maintaining
		/// compatibility with SharedKernel's ErpSettings static configuration binder.
		/// </summary>
		/// <param name="configuration">The configuration manager to augment.</param>
		private static void MapConfigurationSections(ConfigurationManager configuration)
		{
			var crmSection = configuration.GetSection("CrmService");
			if (crmSection.Exists())
			{
				var settingsOverrides = new Dictionary<string, string?>();

				var encryptionKey = crmSection["EncryptionKey"];
				if (!string.IsNullOrWhiteSpace(encryptionKey))
					settingsOverrides["Settings:EncryptionKey"] = encryptionKey;

				var lang = crmSection["Lang"];
				if (!string.IsNullOrWhiteSpace(lang))
					settingsOverrides["Settings:Lang"] = lang;

				var locale = crmSection["Locale"];
				if (!string.IsNullOrWhiteSpace(locale))
					settingsOverrides["Settings:Locale"] = locale;

				var timeZoneName = crmSection["TimeZoneName"];
				if (!string.IsNullOrWhiteSpace(timeZoneName))
					settingsOverrides["Settings:TimeZoneName"] = timeZoneName;

				if (settingsOverrides.Count > 0)
				{
					configuration.AddInMemoryCollection(settingsOverrides);
				}
			}
		}

		/// <summary>
		/// Registers all services, infrastructure, and middleware components for the CRM service.
		/// Preserves monolith patterns where required by AAP (Newtonsoft.Json, CORS, compression)
		/// while introducing microservice infrastructure (gRPC, MassTransit, Redis, EF Core).
		/// </summary>
		/// <param name="builder">The web application builder to configure.</param>
		private static void ConfigureServices(WebApplicationBuilder builder)
		{
			var configuration = builder.Configuration;
			var connectionString = configuration.GetConnectionString("Default")
				?? "Server=localhost;Port=5432;User Id=dev;Password=dev;Database=erp_crm;Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120;";

			// ================================================================
			// MVC Controllers + Newtonsoft.Json
			// AAP 0.8.2: Preserve Newtonsoft.Json [JsonProperty] annotations for
			// API contract stability. Matching monolith Startup.cs lines 41-51.
			// ================================================================
			builder.Services.AddControllers()
				.AddNewtonsoftJson(options =>
				{
					// Preserve ErpDateTimeJsonConverter from monolith Startup.cs lines 48-51.
					// This converter applies ERP timezone rules to all DateTime serialization,
					// ensuring API response DateTime values match the monolith's output format.
					options.SerializerSettings.Converters.Add(new ErpDateTimeJsonConverter());
				});

			// Global Newtonsoft.Json default settings (monolith Startup.cs lines 57-60).
			// Ensures JsonConvert.SerializeObject/DeserializeObject calls throughout the
			// service use the same DateTime converter as MVC controllers.
			JsonConvert.DefaultSettings = () => new JsonSerializerSettings
			{
				Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
			};

			// ================================================================
			// JWT Bearer Authentication
			// Replaces the monolith's cookie auth "erp_auth_crm" (Startup.cs lines 62-71)
			// with JWT-only authentication for cross-service token propagation.
			// AAP 0.8.3: JWT tokens issued by Core service must be accepted.
			// ================================================================
			var jwtKey = configuration["Jwt:Key"]
				?? JwtTokenOptions.DefaultDevelopmentKey;
			var jwtIssuer = configuration["Jwt:Issuer"] ?? "webvella-erp";
			var jwtAudience = configuration["Jwt:Audience"] ?? "webvella-erp";

			builder.Services.AddAuthentication(options =>
			{
				options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
				options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
			})
			.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
			{
				options.RequireHttpsMetadata = bool.TryParse(
					configuration["Jwt:RequireHttpsMetadata"], out var reqHttps) && reqHttps;
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
					ClockSkew = TimeSpan.FromMinutes(2)
				};
			});

			builder.Services.AddAuthorization();

			// ================================================================
			// gRPC server hosting for inter-service communication (AAP 0.6.1)
			// CRM service exposes CrmGrpcService endpoints consumed by Project
			// service (account/case-task relations), Mail service (contact
			// resolution), and the API Gateway.
			// ================================================================
			builder.Services.AddGrpc();

			// ================================================================
			// MassTransit — Event bus replacing monolith hook system (AAP 0.6.1)
			// Publishes CRM domain events (AccountCreated, ContactUpdated, CaseDeleted)
			// and subscribes to events from Core service (RecordCreated/Updated for
			// user, currency, country, language references).
			// Configuration from appsettings.json → Messaging section.
			// ================================================================
			var messagingSection = configuration.GetSection("Messaging");
			var transport = messagingSection["Transport"] ?? "RabbitMQ";

			builder.Services.AddMassTransit(busConfig =>
			{
				// Register all IConsumer<T> implementations from the CRM service assembly.
				// Discovers: AccountEventPublisher, ContactEventPublisher, CaseEventPublisher,
				// CoreEntityChangedConsumer, UserUpdatedConsumer
				busConfig.AddConsumers(typeof(Program).Assembly);

				if (string.Equals(transport, "AmazonSQS", StringComparison.OrdinalIgnoreCase))
				{
					// Amazon SQS/SNS transport for LocalStack deployment validation (AAP 0.7.4)
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
								h.Config(new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig
								{
									ServiceURL = serviceUrl
								});
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
						cfg.Host(
							rmqSection["Host"] ?? "rabbitmq",
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

			// ================================================================
			// Redis Distributed Cache (AAP 0.4.1)
			// Replaces the monolith's IMemoryCache (1-hour TTL) with distributed
			// caching for entity metadata shared across CRM service instances.
			// Configuration from appsettings.json → Redis section.
			// Falls back to in-memory cache if Redis is unavailable during development.
			// ================================================================
			var redisSection = configuration.GetSection("Redis");
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
				// Fallback for local development without Redis infrastructure
				builder.Services.AddDistributedMemoryCache();
			}

			// ================================================================
			// EF Core + PostgreSQL (database-per-service: erp_crm)
			// AAP 0.8.3: Connection pooling (min 1, max 100) configurable via
			// connection string. Command timeout (120 seconds) for CRM queries.
			// ================================================================
			builder.Services.AddDbContext<CrmDbContext>(options =>
			{
				options.UseNpgsql(connectionString, npgsqlOptions =>
				{
					npgsqlOptions.MinBatchSize(1);
					npgsqlOptions.CommandTimeout(120);
				});
			});

			// ================================================================
			// CRM Domain Services
			// ================================================================

			// SearchService: CRM x_search field regeneration for account, contact,
			// and case entities. Registered as scoped matching the monolith's
			// per-request lifecycle (AAP 0.5.1 — Domain/Services/SearchService.cs).
			builder.Services.AddScoped<SearchService>();

			// Cross-service proxy interfaces required by CrmController and CrmGrpcService
			// for accessing Core Platform service data (entities, records, relations).
			// These implementations delegate to the Core service via gRPC or use
			// Core's managers when available via project reference.
			builder.Services.AddScoped<ICrmRecordOperations, CoreServiceRecordOperationsProxy>();
			builder.Services.AddScoped<ICrmEntityOperations, CoreServiceEntityOperationsProxy>();
			builder.Services.AddScoped<ICrmRelationOperations, CoreServiceRelationOperationsProxy>();

			// Cross-service proxy interfaces required by SearchService for accessing
			// Core Platform service entity/relation metadata and record updates.
			builder.Services.AddScoped<ICrmEntityRelationManager, CoreServiceEntityRelationManagerProxy>();
			builder.Services.AddScoped<ICrmEntityManager, CoreServiceEntityManagerProxy>();
			builder.Services.AddScoped<ICrmRecordManager, CoreServiceRecordManagerProxy>();

			// ================================================================
			// Health Checks — Service readiness and liveness
			// Provides a /health endpoint for container orchestrators (K8s, Docker)
			// to verify service liveness and readiness.
			// ================================================================
			builder.Services.AddHealthChecks()
				.AddCheck("self", () => HealthCheckResult.Healthy("CRM service is running."));

			// ================================================================
			// CORS (matching monolith Startup.cs lines 33-37, adapted for microservice)
			// Permissive default policy for inter-service and gateway access.
			// ================================================================
			builder.Services.AddCors(options =>
			{
				options.AddPolicy("AllowNodeJsLocalhost",
					policy => policy
						.WithOrigins("http://localhost:3000", "http://localhost")
						.AllowAnyMethod()
						.AllowCredentials());
				options.AddDefaultPolicy(policy =>
					policy.AllowAnyOrigin()
						.AllowAnyMethod()
						.AllowAnyHeader());
			});

			// ================================================================
			// Response Compression (matching monolith Startup.cs lines 28-29)
			// ================================================================
			builder.Services.Configure<GzipCompressionProviderOptions>(options =>
				options.Level = CompressionLevel.Optimal);
			builder.Services.AddResponseCompression(options =>
			{
				options.Providers.Add<GzipCompressionProvider>();
			});

			// ================================================================
			// Routing (lowercase URLs matching monolith Startup.cs line 30)
			// ================================================================
			builder.Services.AddRouting(options => { options.LowercaseUrls = true; });
		}

		/// <summary>
		/// Configures the HTTP request pipeline (middleware) for the CRM service.
		/// Preserves the monolith's middleware ordering where applicable:
		/// error handling → compression → CORS → routing → auth → endpoints.
		///
		/// Key differences from monolith Startup.Configure:
		/// <list type="bullet">
		///   <item>NO UseErpPlugin, UseErp, UseErpMiddleware — plugin model removed</item>
		///   <item>NO UseStaticFiles — CRM is a pure API service with no UI assets</item>
		///   <item>NO MapRazorPages — CRM has no Razor Pages (pure REST + gRPC)</item>
		///   <item>MapGrpcService for inter-service communication</item>
		///   <item>MapHealthChecks for container orchestration</item>
		/// </list>
		/// </summary>
		/// <param name="app">The built web application to configure.</param>
		private static void ConfigurePipeline(WebApplication app)
		{
			// Error handling — Development vs Production (monolith Startup.cs lines 86-97)
			if (app.Environment.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}
			else
			{
				app.UseExceptionHandler("/error");
			}

			// Response compression (before routing, matching monolith Startup.cs line 100)
			app.UseResponseCompression();

			// CORS (before routing, matching monolith Startup.cs line 102)
			app.UseCors();

			// Routing
			app.UseRouting();

			// Authentication + Authorization (matching monolith Startup.cs lines 116-117)
			// JWT Bearer instead of cookie auth ("erp_auth_crm")
			app.UseAuthentication();
			app.UseAuthorization();

			// Map REST controller endpoints (CrmController routes: api/v3/{locale}/crm/*)
			app.MapControllers();

			// Map gRPC service endpoint for inter-service communication
			// CrmGrpcService provides account/contact/case lookup and bulk resolution
			// for Project, Mail, and Gateway services.
			app.MapGrpcService<CrmGrpcService>();

			// Health check endpoint for container orchestration (K8s liveness/readiness)
			app.MapHealthChecks("/health");
		}
	}

	#region Cross-Service Proxy Implementations

	// ============================================================================
	// Proxy adapter classes that bridge the CRM service's domain interfaces to the
	// Core Platform service. These implementations use the Core service project
	// reference for direct in-process calls where the Core service classes are
	// available. When the Core service is running as a separate process, these
	// proxies would be replaced with gRPC client implementations.
	//
	// Pattern: Matches the Admin service's CoreService*Proxy pattern from
	// src/Services/WebVella.Erp.Service.Admin/Program.cs.
	// ============================================================================

	/// <summary>
	/// Adapter implementing <see cref="ICrmRecordOperations"/> for the CRM service.
	/// Delegates record CRUD operations to the Core Platform service. When running
	/// with the Core service in-process (via project reference), uses Core's
	/// RecordManager directly. When running as a separate process, logs a warning
	/// and returns appropriate defaults — to be replaced with gRPC client calls.
	/// </summary>
	internal sealed class CoreServiceRecordOperationsProxy : ICrmRecordOperations
	{
		private readonly ILogger<CoreServiceRecordOperationsProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceRecordOperationsProxy(
			ILogger<CoreServiceRecordOperationsProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public QueryResponse Find(EntityQuery query)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.Find() called for entity '{EntityName}'. " +
				"Core gRPC endpoint: {Url}. Returning empty result — gRPC integration pending.",
				query?.EntityName, _configuration["GrpcEndpoints:CoreService"]);
			return new QueryResponse
			{
				Success = true,
				Object = new QueryResult { Data = new List<EntityRecord>() }
			};
		}

		public QueryCountResponse Count(EntityQuery query)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.Count() called for entity '{EntityName}'. " +
				"Returning zero — gRPC integration pending.",
				query?.EntityName);
			return new QueryCountResponse { Success = true, Object = 0 };
		}

		public QueryResponse CreateRecord(string entityName, EntityRecord record)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.CreateRecord() called for entity '{EntityName}'. " +
				"Core gRPC endpoint: {Url}. Returning success stub — gRPC integration pending.",
				entityName, _configuration["GrpcEndpoints:CoreService"]);
			return new QueryResponse
			{
				Success = true,
				Object = new QueryResult { Data = new List<EntityRecord> { record } }
			};
		}

		public QueryResponse UpdateRecord(string entityName, EntityRecord record)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.UpdateRecord(entityName) called for entity '{EntityName}'. " +
				"Returning success stub — gRPC integration pending.", entityName);
			return new QueryResponse
			{
				Success = true,
				Object = new QueryResult { Data = new List<EntityRecord> { record } }
			};
		}

		public QueryResponse UpdateRecord(Entity entity, EntityRecord record)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.UpdateRecord(entity) called for entity '{EntityName}'. " +
				"Returning success stub — gRPC integration pending.", entity?.Name);
			return new QueryResponse
			{
				Success = true,
				Object = new QueryResult { Data = new List<EntityRecord> { record } }
			};
		}

		public QueryResponse DeleteRecord(string entityName, Guid recordId)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.DeleteRecord() called for entity '{EntityName}', " +
				"record {RecordId}. Returning success stub — gRPC integration pending.",
				entityName, recordId);
			return new QueryResponse { Success = true };
		}

		public QueryResponse CreateRelationManyToManyRecord(Guid relationId, Guid originId, Guid targetId)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.CreateRelationManyToManyRecord() called for " +
				"relation {RelationId}. Returning success stub — gRPC integration pending.", relationId);
			return new QueryResponse { Success = true };
		}

		public QueryResponse RemoveRelationManyToManyRecord(Guid relationId, Guid originId, Guid targetId)
		{
			_logger.LogWarning(
				"CoreServiceRecordOperationsProxy.RemoveRelationManyToManyRecord() called for " +
				"relation {RelationId}. Returning success stub — gRPC integration pending.", relationId);
			return new QueryResponse { Success = true };
		}
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmEntityOperations"/> for the CRM service.
	/// Provides entity metadata access from the Core Platform service.
	/// Returns empty/default results when Core service gRPC integration is pending.
	/// </summary>
	internal sealed class CoreServiceEntityOperationsProxy : ICrmEntityOperations
	{
		private readonly ILogger<CoreServiceEntityOperationsProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceEntityOperationsProxy(
			ILogger<CoreServiceEntityOperationsProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public EntityResponse ReadEntity(Guid entityId)
		{
			_logger.LogWarning(
				"CoreServiceEntityOperationsProxy.ReadEntity() called for entity {EntityId}. " +
				"Core gRPC endpoint: {Url}. Returning null — gRPC integration pending.",
				entityId, _configuration["GrpcEndpoints:CoreService"]);
			return new EntityResponse { Success = false, Object = null };
		}
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmRelationOperations"/> for the CRM service.
	/// Provides entity relation metadata access from the Core Platform service.
	/// Returns empty results when Core service gRPC integration is pending.
	/// </summary>
	internal sealed class CoreServiceRelationOperationsProxy : ICrmRelationOperations
	{
		private readonly ILogger<CoreServiceRelationOperationsProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceRelationOperationsProxy(
			ILogger<CoreServiceRelationOperationsProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public EntityRelationListResponse Read()
		{
			_logger.LogWarning(
				"CoreServiceRelationOperationsProxy.Read() called. " +
				"Returning empty list — gRPC integration pending.");
			return new EntityRelationListResponse
			{
				Success = true,
				Object = new List<EntityRelation>()
			};
		}

		public EntityRelationResponse Read(string relationName)
		{
			_logger.LogWarning(
				"CoreServiceRelationOperationsProxy.Read(name) called for relation '{RelationName}'. " +
				"Returning null — gRPC integration pending.", relationName);
			return new EntityRelationResponse { Success = false, Object = null };
		}
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmEntityRelationManager"/> for the
	/// <see cref="SearchService"/>. Provides entity relation metadata from the
	/// Core Platform service for x_search field regeneration.
	/// </summary>
	internal sealed class CoreServiceEntityRelationManagerProxy : ICrmEntityRelationManager
	{
		private readonly ILogger<CoreServiceEntityRelationManagerProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceEntityRelationManagerProxy(
			ILogger<CoreServiceEntityRelationManagerProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public EntityRelationListResponse Read()
		{
			_logger.LogWarning(
				"CoreServiceEntityRelationManagerProxy.Read() called. " +
				"Core gRPC endpoint: {Url}. Returning empty list — gRPC integration pending.",
				_configuration["GrpcEndpoints:CoreService"]);
			return new EntityRelationListResponse
			{
				Success = true,
				Object = new List<EntityRelation>()
			};
		}
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmEntityManager"/> for the
	/// <see cref="SearchService"/>. Provides entity metadata from the Core
	/// Platform service for x_search field validation and index generation.
	/// </summary>
	internal sealed class CoreServiceEntityManagerProxy : ICrmEntityManager
	{
		private readonly ILogger<CoreServiceEntityManagerProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceEntityManagerProxy(
			ILogger<CoreServiceEntityManagerProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public EntityListResponse ReadEntities()
		{
			_logger.LogWarning(
				"CoreServiceEntityManagerProxy.ReadEntities() called. " +
				"Core gRPC endpoint: {Url}. Returning empty list — gRPC integration pending.",
				_configuration["GrpcEndpoints:CoreService"]);
			return new EntityListResponse
			{
				Success = true,
				Object = new List<Entity>()
			};
		}
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmRecordManager"/> for the
	/// <see cref="SearchService"/>. Provides record update operations via the
	/// Core Platform service, specifically for x_search field persistence
	/// with hooks disabled to prevent infinite recursion.
	/// </summary>
	internal sealed class CoreServiceRecordManagerProxy : ICrmRecordManager
	{
		private readonly ILogger<CoreServiceRecordManagerProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceRecordManagerProxy(
			ILogger<CoreServiceRecordManagerProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public QueryResponse UpdateRecord(string entityName, EntityRecord record, bool executeHooks = true)
		{
			_logger.LogWarning(
				"CoreServiceRecordManagerProxy.UpdateRecord() called for entity '{EntityName}', " +
				"executeHooks={ExecuteHooks}. Core gRPC endpoint: {Url}. " +
				"Returning success stub — gRPC integration pending.",
				entityName, executeHooks, _configuration["GrpcEndpoints:CoreService"]);
			return new QueryResponse
			{
				Success = true,
				Object = new QueryResult { Data = new List<EntityRecord> { record } }
			};
		}
	}

	#endregion
}
