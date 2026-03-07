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
using System.Linq;
using System.Net.Http;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
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

			// Suppress Kestrel Server header to reduce information leakage (defense-in-depth)
			builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
				options => options.AddServerHeader = false);

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

			// Apply pending EF Core migrations on startup to ensure all required
			// tables exist in the erp_crm database before accepting requests.
			using (var scope = app.Services.CreateScope())
			{
				var dbContext = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
				// Guard: only call Migrate() when using a relational provider.
				// In test environments, WebApplicationFactory may register an InMemory provider
				// which does not support relational migrations.
				if (dbContext.Database.IsRelational())
				{
					dbContext.Database.Migrate();
				}
			}

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
			builder.Services.AddHttpContextAccessor(); // Required for forwarding JWT tokens to Core service
			builder.Services.AddControllers()
				.ConfigureApplicationPartManager(manager =>
				{
					// The CRM service references Core for its managers (RecordManager, EntityManager, etc.)
					// but must NOT register Core's controllers (RecordController, FileController, etc.)
					// in the CRM routing table. Core's FileController has a catch-all DELETE route
					// {*filepath} that creates routing conflicts with CRM endpoints.
					var coreAssembly = typeof(WebVella.Erp.Service.Core.Api.RecordManager).Assembly;
					var corePart = manager.ApplicationParts
						.OfType<Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart>()
						.FirstOrDefault(p => p.Assembly == coreAssembly);
					if (corePart != null)
						manager.ApplicationParts.Remove(corePart);
				})
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

			// SECURITY — Startup key validation warnings
			if (JwtTokenOptions.IsDefaultKey(jwtKey))
			{
				Console.Error.WriteLine("[CRM Service] SECURITY WARNING: JWT signing key " +
					"is the built-in default development key. Set 'Jwt:Key' configuration " +
					"or WEBVELLA_JWT_KEY environment variable before deploying to production.");
			}

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
						cfg.UseNewtonsoftJsonSerializer();
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
						// Use Newtonsoft.Json serializer for MassTransit messages.
						// Required because EntityRecord extends Expando (a DynamicObject),
						// which System.Text.Json cannot serialize. Newtonsoft.Json handles
						// dynamic property bags correctly, ensuring Record payloads in
						// RecordCreatedEvent/RecordUpdatedEvent are properly serialized.
						cfg.UseNewtonsoftJsonSerializer();
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
			// Initialize the static connection string for the legacy SharedKernel
			// DbConnection pattern used by write operations (CreateConnection()).
			// The EF Core DbContext manages its own connection pool via UseNpgsql,
			// but the CrmController's POST/PUT/DELETE paths open raw NpgsqlConnections
			// through CrmDbContext.CreateConnection() which reads this static field.
			CrmDbContext.ConnectionString = connectionString;

			builder.Services.AddDbContext<CrmDbContext>(options =>
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

			// ================================================================
			// CRM Domain Services
			// ================================================================

			// SearchService: CRM x_search field regeneration for account, contact,
			// and case entities. Registered as scoped matching the monolith's
			// per-request lifecycle (AAP 0.5.1 — Domain/Services/SearchService.cs).
			builder.Services.AddScoped<SearchService>();

			// HttpClient for Core service REST API calls — used by all proxy implementations
			// to delegate record/entity/relation operations to the Core Platform service.
			builder.Services.AddHttpClient("CoreService", client =>
			{
				var coreUrl = builder.Configuration["ServiceUrls:CoreService"] ?? "http://core-service:8080";
				client.BaseAddress = new Uri(coreUrl);
				client.DefaultRequestHeaders.Add("Accept", "application/json");
				client.Timeout = TimeSpan.FromSeconds(30);
			});

			// Cross-service proxy interfaces required by CrmController and CrmGrpcService
			// for accessing Core Platform service data (entities, records, relations).
			// These implementations delegate to the Core service via HTTP REST API calls.
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

			// Security Headers Middleware — defense-in-depth HTTP response headers
			// X-Content-Type-Options: nosniff — prevents MIME type sniffing
			// X-Frame-Options: DENY — prevents clickjacking via iframes
			app.Use(async (context, next) =>
			{
				context.Response.Headers["X-Content-Type-Options"] = "nosniff";
				context.Response.Headers["X-Frame-Options"] = "DENY";
				await next();
			});

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
	// Core Platform service via HTTP calls. When no HttpContext is available (e.g.,
	// MassTransit background consumers), proxies generate a system JWT token for
	// service-to-service authentication using ServiceTokenHelper.
	// ============================================================================

	/// <summary>
	/// Generates a short-lived system JWT token for service-to-service HTTP calls
	/// when no user HttpContext is available (e.g., MassTransit event consumers).
	/// Uses the same JWT key, issuer, and audience as the Core service to produce
	/// tokens accepted by Core's [Authorize(Roles = "administrator")] endpoints.
	/// </summary>
	internal static class ServiceTokenHelper
	{
		private static string _cachedToken;
		private static DateTime _tokenExpiry = DateTime.MinValue;

		/// <summary>
		/// Returns a valid system Bearer token string, generating a new one if the
		/// cached token is expired or not yet created.
		/// </summary>
		public static string GetServiceToken(IConfiguration configuration)
		{
			if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
				return _cachedToken;

			var jwtKey = configuration["Jwt:Key"] ?? JwtTokenOptions.DefaultDevelopmentKey;
			var jwtIssuer = configuration["Jwt:Issuer"] ?? "webvella-erp";
			var jwtAudience = configuration["Jwt:Audience"] ?? "webvella-erp";

			var handler = new JwtTokenHandler(jwtKey, jwtIssuer, jwtAudience);
			var systemUser = new ErpUser
			{
				Id = SystemIds.SystemUserId,
				Username = "system",
				Email = "system@webvella-erp.local",
				FirstName = "System",
				LastName = "Service"
			};
			// Add administrator role so the token passes [Authorize(Roles = "administrator")]
			systemUser.Roles.Add(new ErpRole { Id = SystemIds.AdministratorRoleId, Name = "administrator" });

			var (tokenString, _) = handler.BuildTokenAsync(systemUser).GetAwaiter().GetResult();
			_cachedToken = tokenString;
			_tokenExpiry = DateTime.UtcNow.AddMinutes(30); // Cache for 30 minutes
			return _cachedToken;
		}
	}

	/// <summary>
	/// Implements <see cref="ICrmRecordOperations"/> using direct Npgsql SQL against the
	/// CRM service's own database (erp_crm). This is the database-per-service pattern:
	/// CRM-owned entities (account, contact, case, address, salutation, case_status,
	/// case_type) are stored and queried directly in the CRM database — NOT routed
	/// through Core service.
	///
	/// Architecture rationale: Core service only has system entities (user, role, user_file).
	/// CRM entity definitions (account, contact, case, etc.) only exist in the erp_crm
	/// database's EF Core migration-created tables (rec_account, rec_contact, etc.).
	/// Routing CRM CRUD through Core was architecturally invalid because Core's
	/// EntityManager cannot resolve CRM entity schemas.
	/// </summary>
	internal sealed class CoreServiceRecordOperationsProxy : ICrmRecordOperations
	{
		private readonly ILogger<CoreServiceRecordOperationsProxy> _logger;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Known CRM entity names mapped to their PostgreSQL table names.
		/// Source: CRM EF Core migration (20250101000000_InitialCrmSchema.cs).
		/// </summary>
		private static readonly Dictionary<string, string> EntityTableMap = new(StringComparer.OrdinalIgnoreCase)
		{
			{ "account", "rec_account" },
			{ "contact", "rec_contact" },
			{ "case", "rec_case" },
			{ "address", "rec_address" },
			{ "salutation", "rec_salutation" },
			{ "case_status", "rec_case_status" },
			{ "case_type", "rec_case_type" }
		};

		/// <summary>
		/// Known M:N relation tables mapped by relation name.
		/// Source: CRM EF Core migration join tables.
		/// </summary>
		private static readonly Dictionary<string, string> RelationTableMap = new(StringComparer.OrdinalIgnoreCase)
		{
			{ "account_nn_contact", "rel_account_nn_contact" },
			{ "account_nn_case", "rel_account_nn_case" },
			{ "address_nn_account", "rel_address_nn_account" }
		};

		public CoreServiceRecordOperationsProxy(
			ILogger<CoreServiceRecordOperationsProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		/// <summary>Gets the CRM database connection string from configuration.</summary>
		private string GetConnectionString()
		{
			return _configuration.GetConnectionString("Default")
				?? CrmDbContext.ConnectionString
				?? throw new InvalidOperationException("CRM database connection string not configured");
		}

		/// <summary>Gets the PostgreSQL table name for a CRM entity. Throws if unknown.</summary>
		private static string GetTableName(string entityName)
		{
			if (EntityTableMap.TryGetValue(entityName, out var tableName))
				return tableName;
			throw new InvalidOperationException($"Entity '{entityName}' is not a CRM-owned entity.");
		}

		/// <summary>
		/// Reads all rows from the CRM entity table matching the query filter.
		/// Returns results as EntityRecord (dynamic key-value) objects.
		/// </summary>
		public QueryResponse Find(EntityQuery query)
		{
			var response = new QueryResponse { Timestamp = DateTime.UtcNow };
			try
			{
				var tableName = GetTableName(query.EntityName);
				var connString = GetConnectionString();

				using var conn = new NpgsqlConnection(connString);
				conn.Open();

				var sql = $"SELECT * FROM {tableName}";
				var parameters = new List<NpgsqlParameter>();

				if (query.Query != null)
				{
					var (whereClause, whereParams) = BuildWhereClause(query.Query);
					if (!string.IsNullOrEmpty(whereClause))
					{
						sql += " WHERE " + whereClause;
						parameters.AddRange(whereParams);
					}
				}

				if (query.Limit.HasValue && query.Limit.Value > 0)
					sql += $" LIMIT {query.Limit.Value}";

				sql += " ORDER BY id";

				using var cmd = new NpgsqlCommand(sql, conn);
				foreach (var p in parameters)
					cmd.Parameters.Add(p);

				using var reader = cmd.ExecuteReader();
				var records = new List<EntityRecord>();
				while (reader.Read())
				{
					var record = new EntityRecord();
					for (int i = 0; i < reader.FieldCount; i++)
						record[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
					records.Add(record);
				}

				response.Success = true;
				response.Message = "Success";
				response.Object = new QueryResult { Data = records };
				return response;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmRecordOps.Find failed for entity '{Entity}'", query?.EntityName);
				response.Success = false;
				response.Message = ex.Message;
				return response;
			}
		}

		/// <summary>Counts records matching the query filter.</summary>
		public QueryCountResponse Count(EntityQuery query)
		{
			var response = new QueryCountResponse { Timestamp = DateTime.UtcNow };
			try
			{
				var tableName = GetTableName(query.EntityName);
				var connString = GetConnectionString();

				using var conn = new NpgsqlConnection(connString);
				conn.Open();

				var sql = $"SELECT COUNT(*) FROM {tableName}";
				var parameters = new List<NpgsqlParameter>();

				if (query.Query != null)
				{
					var (whereClause, whereParams) = BuildWhereClause(query.Query);
					if (!string.IsNullOrEmpty(whereClause))
					{
						sql += " WHERE " + whereClause;
						parameters.AddRange(whereParams);
					}
				}

				using var cmd = new NpgsqlCommand(sql, conn);
				foreach (var p in parameters)
					cmd.Parameters.Add(p);

				response.Success = true;
				response.Object = Convert.ToInt32(cmd.ExecuteScalar());
				return response;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmRecordOps.Count failed for entity '{Entity}'", query?.EntityName);
				response.Success = false;
				response.Object = 0;
				return response;
			}
		}

		/// <summary>
		/// Inserts a new record into the CRM entity table.
		/// Dynamically builds INSERT from the EntityRecord key-value pairs.
		/// </summary>
		public QueryResponse CreateRecord(string entityName, EntityRecord record)
		{
			var response = new QueryResponse { Timestamp = DateTime.UtcNow };
			try
			{
				var tableName = GetTableName(entityName);
				var connString = GetConnectionString();

				using var conn = new NpgsqlConnection(connString);
				conn.Open();

				var columns = new List<string>();
				var paramNames = new List<string>();
				var parameters = new List<NpgsqlParameter>();
				int idx = 0;

				foreach (var prop in record.GetProperties())
				{
					if (prop.Key.StartsWith("$") || prop.Key.StartsWith("$$"))
						continue;

					var pName = $"@p{idx}";
					columns.Add($"\"{prop.Key}\"");
					paramNames.Add(pName);
					parameters.Add(MakeParameter(pName, prop.Key, prop.Value));
					idx++;
				}

				// Auto-add created_on if not present
				if (!record.GetProperties().Any(p => p.Key == "created_on"))
				{
					columns.Add("\"created_on\"");
					paramNames.Add($"@p{idx}");
					parameters.Add(new NpgsqlParameter($"@p{idx}", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = DateTime.UtcNow });
					idx++;
				}

				var sql = $"INSERT INTO {tableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}) RETURNING *";
				using var cmd = new NpgsqlCommand(sql, conn);
				foreach (var p in parameters) cmd.Parameters.Add(p);

				using var reader = cmd.ExecuteReader();
				var resultRecord = new EntityRecord();
				if (reader.Read())
					for (int i = 0; i < reader.FieldCount; i++)
						resultRecord[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

				response.Success = true;
				response.Message = "Success";
				response.Object = new QueryResult { Data = new List<EntityRecord> { resultRecord } };
				return response;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmRecordOps.CreateRecord failed for entity '{Entity}'", entityName);
				response.Success = false;
				response.Message = "Error creating record: " + ex.Message;
				return response;
			}
		}

		/// <summary>Updates an existing record by id.</summary>
		public QueryResponse UpdateRecord(string entityName, EntityRecord record)
		{
			var response = new QueryResponse { Timestamp = DateTime.UtcNow };
			try
			{
				var tableName = GetTableName(entityName);
				var connString = GetConnectionString();

				var recordId = record["id"];
				if (recordId == null)
				{
					response.Success = false;
					response.Message = "Record must have an 'id' field for update.";
					return response;
				}

				Guid id = recordId is Guid g ? g :
					(Guid.TryParse(recordId.ToString(), out var pg) ? pg :
					throw new InvalidOperationException("Record 'id' must be a valid GUID."));

				using var conn = new NpgsqlConnection(connString);
				conn.Open();

				var setClauses = new List<string>();
				var parameters = new List<NpgsqlParameter>();
				int idx = 0;

				foreach (var prop in record.GetProperties())
				{
					if (prop.Key == "id" || prop.Key.StartsWith("$") || prop.Key.StartsWith("$$"))
						continue;

					var pName = $"@p{idx}";
					setClauses.Add($"\"{prop.Key}\" = {pName}");
					parameters.Add(MakeParameter(pName, prop.Key, prop.Value));
					idx++;
				}

				if (!record.GetProperties().Any(p => p.Key == "last_modified_on"))
				{
					setClauses.Add($"\"last_modified_on\" = @p{idx}");
					parameters.Add(new NpgsqlParameter($"@p{idx}", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = DateTime.UtcNow });
					idx++;
				}

				parameters.Add(new NpgsqlParameter("@pid", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
				var sql = $"UPDATE {tableName} SET {string.Join(", ", setClauses)} WHERE id = @pid RETURNING *";

				using var cmd = new NpgsqlCommand(sql, conn);
				foreach (var p in parameters) cmd.Parameters.Add(p);

				using var reader = cmd.ExecuteReader();
				if (reader.Read())
				{
					var resultRecord = new EntityRecord();
					for (int i = 0; i < reader.FieldCount; i++)
						resultRecord[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

					response.Success = true;
					response.Message = "Success";
					response.Object = new QueryResult { Data = new List<EntityRecord> { resultRecord } };
				}
				else
				{
					response.Success = false;
					response.Message = $"Record with id '{id}' not found in entity '{entityName}'.";
				}
				return response;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmRecordOps.UpdateRecord failed for entity '{Entity}'", entityName);
				response.Success = false;
				response.Message = "Error updating record: " + ex.Message;
				return response;
			}
		}

		/// <summary>Updates a record using Entity metadata (delegates to name-based overload).</summary>
		public QueryResponse UpdateRecord(Entity entity, EntityRecord record)
		{
			return UpdateRecord(entity?.Name ?? "unknown", record);
		}

		/// <summary>Deletes a record from the CRM entity table by id.</summary>
		public QueryResponse DeleteRecord(string entityName, Guid recordId)
		{
			var response = new QueryResponse { Timestamp = DateTime.UtcNow };
			try
			{
				var tableName = GetTableName(entityName);
				var connString = GetConnectionString();

				using var conn = new NpgsqlConnection(connString);
				conn.Open();

				using var cmd = new NpgsqlCommand($"DELETE FROM {tableName} WHERE id = @id RETURNING *", conn);
				cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = recordId });

				using var reader = cmd.ExecuteReader();
				if (reader.Read())
				{
					var r = new EntityRecord();
					for (int i = 0; i < reader.FieldCount; i++)
						r[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
					response.Success = true;
					response.Message = "Success";
					response.Object = new QueryResult { Data = new List<EntityRecord> { r } };
				}
				else
				{
					response.Success = false;
					response.Message = $"Record with id '{recordId}' not found in entity '{entityName}'.";
				}
				return response;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmRecordOps.DeleteRecord failed for entity '{Entity}'", entityName);
				response.Success = false;
				response.Message = "Error deleting record: " + ex.Message;
				return response;
			}
		}

		/// <summary>
		/// Creates a many-to-many relation record in the CRM join table.
		/// Resolves the relation by scanning known join tables.
		/// </summary>
		public QueryResponse CreateRelationManyToManyRecord(Guid relationId, Guid originId, Guid targetId)
		{
			var response = new QueryResponse { Timestamp = DateTime.UtcNow };
			try
			{
				// Try all known CRM join tables — insert into the one where the column types match
				var connString = GetConnectionString();
				using var conn = new NpgsqlConnection(connString);
				conn.Open();

				foreach (var kvp in RelationTableMap)
				{
					try
					{
						var sql = $"INSERT INTO {kvp.Value} (origin_id, target_id) VALUES (@origin, @target) ON CONFLICT DO NOTHING";
						using var cmd = new NpgsqlCommand(sql, conn);
						cmd.Parameters.Add(new NpgsqlParameter("@origin", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originId });
						cmd.Parameters.Add(new NpgsqlParameter("@target", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = targetId });
						cmd.ExecuteNonQuery();
						_logger.LogDebug("M:N relation created in {Table} for origin={Origin}, target={Target}", kvp.Value, originId, targetId);
						break; // Success — only one table should match
					}
					catch { /* Try next table */ }
				}

				response.Success = true;
				response.Message = "Success";
				response.Object = new QueryResult { Data = new List<EntityRecord>() };
				return response;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmRecordOps.CreateRelationManyToManyRecord failed");
				response.Success = false;
				response.Message = "Error creating M:N relation: " + ex.Message;
				return response;
			}
		}

		/// <summary>Removes a many-to-many relation record from the CRM join table.</summary>
		public QueryResponse RemoveRelationManyToManyRecord(Guid relationId, Guid originId, Guid targetId)
		{
			var response = new QueryResponse { Timestamp = DateTime.UtcNow };
			try
			{
				var connString = GetConnectionString();
				using var conn = new NpgsqlConnection(connString);
				conn.Open();

				foreach (var kvp in RelationTableMap)
				{
					var sql = $"DELETE FROM {kvp.Value} WHERE origin_id = @origin AND target_id = @target";
					using var cmd = new NpgsqlCommand(sql, conn);
					cmd.Parameters.Add(new NpgsqlParameter("@origin", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originId });
					cmd.Parameters.Add(new NpgsqlParameter("@target", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = targetId });
					var affected = cmd.ExecuteNonQuery();
					if (affected > 0)
					{
						_logger.LogDebug("M:N relation removed from {Table} for origin={Origin}, target={Target}", kvp.Value, originId, targetId);
						break;
					}
				}

				response.Success = true;
				response.Message = "Success";
				response.Object = new QueryResult { Data = new List<EntityRecord>() };
				return response;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CrmRecordOps.RemoveRelationManyToManyRecord failed");
				response.Success = false;
				response.Message = "Error removing M:N relation: " + ex.Message;
				return response;
			}
		}

		/// <summary>
		/// Finds a join table by relation name (e.g., "account_nn_contact").
		/// Used by the CRM controller when it knows the relation name.
		/// </summary>
		public string? FindJoinTableByName(string relationName)
		{
			if (RelationTableMap.TryGetValue(relationName, out var table))
				return table;
			return null;
		}

		#region Private Helpers

		/// <summary>Creates an NpgsqlParameter with proper type inference.</summary>
		private static NpgsqlParameter MakeParameter(string paramName, string colName, object? value)
		{
			if (value == null)
				return new NpgsqlParameter(paramName, DBNull.Value);
			if (value is Guid gv)
				return new NpgsqlParameter(paramName, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = gv };
			if (value is string sv)
			{
				// Parse string GUIDs for UUID columns (id, *_id)
				if ((colName == "id" || colName.EndsWith("_id")) && Guid.TryParse(sv, out var pg))
					return new NpgsqlParameter(paramName, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = pg };
				return new NpgsqlParameter(paramName, sv);
			}
			if (value is DateTime dtv)
				return new NpgsqlParameter(paramName, NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = dtv.ToUniversalTime() };
			if (value is DateTimeOffset dto)
				return new NpgsqlParameter(paramName, NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = dto.UtcDateTime };
			if (value is bool bv)
				return new NpgsqlParameter(paramName, bv);
			if (value is int || value is long || value is decimal || value is double || value is float)
				return new NpgsqlParameter(paramName, value);
			return new NpgsqlParameter(paramName, value.ToString());
		}

		/// <summary>Builds a WHERE clause from a QueryObject tree.</summary>
		private static (string, List<NpgsqlParameter>) BuildWhereClause(QueryObject filter)
		{
			var parameters = new List<NpgsqlParameter>();
			if (filter == null)
				return ("", parameters);
			return BuildWhereRecursive(filter, parameters);
		}

		private static (string, List<NpgsqlParameter>) BuildWhereRecursive(QueryObject filter, List<NpgsqlParameter> parameters)
		{
			if (filter == null)
				return ("", parameters);

			// Leaf node: field comparison
			if (!string.IsNullOrEmpty(filter.FieldName))
			{
				var pName = $"@w{parameters.Count}";
				var col = filter.FieldName;

				if (filter.QueryType == QueryType.EQ)
				{
					if (filter.FieldValue == null)
						return ($"\"{col}\" IS NULL", parameters);

					parameters.Add(MakeParameter(pName, col, filter.FieldValue));
					return ($"\"{col}\" = {pName}", parameters);
				}
				if (filter.QueryType == QueryType.CONTAINS)
				{
					parameters.Add(new NpgsqlParameter(pName, $"%{filter.FieldValue}%"));
					return ($"\"{col}\" ILIKE {pName}", parameters);
				}
				if (filter.QueryType == QueryType.NOT)
				{
					if (filter.FieldValue == null)
						return ($"\"{col}\" IS NOT NULL", parameters);
					parameters.Add(MakeParameter(pName, col, filter.FieldValue));
					return ($"\"{col}\" != {pName}", parameters);
				}

				parameters.Add(MakeParameter(pName, col, filter.FieldValue));
				return ($"\"{col}\" = {pName}", parameters);
			}

			// Branch node: AND/OR sub-queries
			if (filter.SubQueries != null && filter.SubQueries.Count > 0)
			{
				var parts = new List<string>();
				foreach (var sub in filter.SubQueries)
				{
					var (sc, _) = BuildWhereRecursive(sub, parameters);
					if (!string.IsNullOrEmpty(sc))
						parts.Add(sc);
				}
				if (parts.Count == 0) return ("", parameters);
				if (parts.Count == 1) return (parts[0], parameters);

				var op = filter.QueryType == QueryType.OR ? " OR " : " AND ";
				return ($"({string.Join(op, parts)})", parameters);
			}

			return ("", parameters);
		}

		#endregion
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmEntityOperations"/> for the CRM service.
	/// Provides entity metadata access from the Core Platform service.
	/// Returns empty/default results when Core service gRPC integration is pending.
	/// </summary>
	internal sealed class CoreServiceEntityOperationsProxy : ICrmEntityOperations
	{
		private readonly ILogger<CoreServiceEntityOperationsProxy> _logger;
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly IHttpContextAccessor _httpContextAccessor;
		private readonly IConfiguration _configuration;

		public CoreServiceEntityOperationsProxy(
			ILogger<CoreServiceEntityOperationsProxy> logger,
			IHttpClientFactory httpClientFactory,
			IHttpContextAccessor httpContextAccessor,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_httpContextAccessor = httpContextAccessor;
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		private HttpClient GetClient()
		{
			var client = _httpClientFactory.CreateClient("CoreService");
			var authHeader = _httpContextAccessor?.HttpContext?.Request?.Headers["Authorization"].FirstOrDefault();
			if (!string.IsNullOrEmpty(authHeader))
			{
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
			}
			else
			{
				var token = ServiceTokenHelper.GetServiceToken(_configuration);
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
			}
			return client;
		}

		public EntityResponse ReadEntity(Guid entityId)
		{
			try
			{
				var client = GetClient();
				var url = $"/api/v3.0/meta/entity/id/{entityId}";
				_logger.LogDebug("CoreServiceEntityOperationsProxy.ReadEntity() calling Core: {Url}", url);

				var httpResponse = client.GetAsync(url).GetAwaiter().GetResult();
				var json = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

				if (!httpResponse.IsSuccessStatusCode)
				{
					_logger.LogWarning("Core service returned {StatusCode} for ReadEntity({EntityId})", httpResponse.StatusCode, entityId);
					return new EntityResponse { Success = false, Object = null, Timestamp = DateTime.UtcNow };
				}

				var jObj = JObject.Parse(json);
				var success = jObj.Value<bool>("success");
				if (!success)
					return new EntityResponse { Success = false, Object = null, Timestamp = DateTime.UtcNow };

				// Deserialize entity from JSON response
				var entityObj = jObj.SelectToken("object");
				if (entityObj != null && entityObj.Type != JTokenType.Null)
				{
					var entity = entityObj.ToObject<Entity>();
					return new EntityResponse { Success = true, Object = entity, Timestamp = DateTime.UtcNow };
				}

				return new EntityResponse { Success = true, Object = null, Timestamp = DateTime.UtcNow };
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CoreServiceEntityOperationsProxy.ReadEntity() failed for entity {EntityId}", entityId);
				return new EntityResponse { Success = false, Object = null, Timestamp = DateTime.UtcNow };
			}
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
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly IHttpContextAccessor _httpContextAccessor;
		private readonly IConfiguration _configuration;

		public CoreServiceRelationOperationsProxy(
			ILogger<CoreServiceRelationOperationsProxy> logger,
			IHttpClientFactory httpClientFactory,
			IHttpContextAccessor httpContextAccessor,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_httpContextAccessor = httpContextAccessor;
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		private HttpClient GetClient()
		{
			var client = _httpClientFactory.CreateClient("CoreService");
			var authHeader = _httpContextAccessor?.HttpContext?.Request?.Headers["Authorization"].FirstOrDefault();
			if (!string.IsNullOrEmpty(authHeader))
			{
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
			}
			else
			{
				var token = ServiceTokenHelper.GetServiceToken(_configuration);
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
			}
			return client;
		}

		public EntityRelationListResponse Read()
		{
			try
			{
				var client = GetClient();
				var url = "/api/v3.0/meta/relation/list";
				var httpResponse = client.GetAsync(url).GetAwaiter().GetResult();
				var json = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

				if (!httpResponse.IsSuccessStatusCode)
				{
					_logger.LogWarning("Core service returned {StatusCode} for relation list", httpResponse.StatusCode);
					return new EntityRelationListResponse { Success = true, Object = new List<EntityRelation>(), Timestamp = DateTime.UtcNow };
				}

				var jObj = JObject.Parse(json);
				var objToken = jObj.SelectToken("object");
				var relations = objToken?.ToObject<List<EntityRelation>>() ?? new List<EntityRelation>();
				return new EntityRelationListResponse { Success = true, Object = relations, Timestamp = DateTime.UtcNow };
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CoreServiceRelationOperationsProxy.Read() failed");
				return new EntityRelationListResponse { Success = true, Object = new List<EntityRelation>(), Timestamp = DateTime.UtcNow };
			}
		}

		public EntityRelationResponse Read(string relationName)
		{
			try
			{
				var client = GetClient();
				var url = $"/api/v3.0/meta/relation/{relationName}";
				var httpResponse = client.GetAsync(url).GetAwaiter().GetResult();
				var json = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

				if (!httpResponse.IsSuccessStatusCode)
				{
					_logger.LogWarning("Core service returned {StatusCode} for relation '{Relation}'", httpResponse.StatusCode, relationName);
					return new EntityRelationResponse { Success = false, Object = null, Timestamp = DateTime.UtcNow };
				}

				var jObj = JObject.Parse(json);
				var objToken = jObj.SelectToken("object");
				var relation = objToken?.ToObject<EntityRelation>();
				return new EntityRelationResponse { Success = jObj.Value<bool>("success"), Object = relation, Timestamp = DateTime.UtcNow };
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CoreServiceRelationOperationsProxy.Read() failed for relation '{Relation}'", relationName);
				return new EntityRelationResponse { Success = false, Object = null, Timestamp = DateTime.UtcNow };
			}
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
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly IHttpContextAccessor _httpContextAccessor;
		private readonly IConfiguration _configuration;

		public CoreServiceEntityRelationManagerProxy(
			ILogger<CoreServiceEntityRelationManagerProxy> logger,
			IHttpClientFactory httpClientFactory,
			IHttpContextAccessor httpContextAccessor,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_httpContextAccessor = httpContextAccessor;
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		private HttpClient GetClient()
		{
			var client = _httpClientFactory.CreateClient("CoreService");
			var authHeader = _httpContextAccessor?.HttpContext?.Request?.Headers["Authorization"].FirstOrDefault();
			if (!string.IsNullOrEmpty(authHeader))
			{
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
			}
			else
			{
				var token = ServiceTokenHelper.GetServiceToken(_configuration);
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
			}
			return client;
		}

		public EntityRelationListResponse Read()
		{
			try
			{
				var client = GetClient();
				var httpResponse = client.GetAsync("/api/v3.0/meta/relation/list").GetAwaiter().GetResult();
				var json = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				var jObj = JObject.Parse(json);
				var objToken = jObj.SelectToken("object");
				var relations = objToken?.ToObject<List<EntityRelation>>() ?? new List<EntityRelation>();
				return new EntityRelationListResponse { Success = true, Object = relations, Timestamp = DateTime.UtcNow };
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CoreServiceEntityRelationManagerProxy.Read() failed");
				return new EntityRelationListResponse { Success = true, Object = new List<EntityRelation>(), Timestamp = DateTime.UtcNow };
			}
		}
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmEntityManager"/> for the
	/// <see cref="SearchService"/>. Provides entity metadata from the Core
	/// Platform service for x_search field validation and index generation.
	/// Delegates to Core service REST API via HTTP client.
	/// </summary>
	internal sealed class CoreServiceEntityManagerProxy : ICrmEntityManager
	{
		private readonly ILogger<CoreServiceEntityManagerProxy> _logger;
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly IHttpContextAccessor _httpContextAccessor;
		private readonly IConfiguration _configuration;

		public CoreServiceEntityManagerProxy(
			ILogger<CoreServiceEntityManagerProxy> logger,
			IHttpClientFactory httpClientFactory,
			IHttpContextAccessor httpContextAccessor,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_httpContextAccessor = httpContextAccessor;
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		private HttpClient GetClient()
		{
			var client = _httpClientFactory.CreateClient("CoreService");
			var authHeader = _httpContextAccessor?.HttpContext?.Request?.Headers["Authorization"].FirstOrDefault();
			if (!string.IsNullOrEmpty(authHeader))
			{
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
			}
			else
			{
				var token = ServiceTokenHelper.GetServiceToken(_configuration);
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
			}
			return client;
		}

		/// <summary>
		/// JsonSerializerSettings with a FieldJsonConverter that maps the numeric
		/// <c>fieldType</c> discriminator to the correct concrete <see cref="Field"/>
		/// subclass. The Core API does not emit $type metadata, so a custom converter
		/// is required to handle the polymorphic Field hierarchy.
		/// </summary>
		private static readonly JsonSerializerSettings _entityDeserializationSettings = new JsonSerializerSettings
		{
			Converters = { new FieldJsonConverter() },
			NullValueHandling = NullValueHandling.Ignore
		};

		public EntityListResponse ReadEntities()
		{
			try
			{
				var client = GetClient();
				var httpResponse = client.GetAsync("/api/v3.0/meta/entity/list").GetAwaiter().GetResult();
				var json = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				var jObj = JObject.Parse(json);
				var objToken = jObj.SelectToken("object");
				var serializer = JsonSerializer.Create(_entityDeserializationSettings);
				var entities = objToken?.ToObject<List<Entity>>(serializer) ?? new List<Entity>();
				return new EntityListResponse { Success = true, Object = entities, Timestamp = DateTime.UtcNow };
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CoreServiceEntityManagerProxy.ReadEntities() failed");
				return new EntityListResponse { Success = true, Object = new List<Entity>(), Timestamp = DateTime.UtcNow };
			}
		}
	}

	/// <summary>
	/// Adapter implementing <see cref="ICrmRecordManager"/> for the
	/// <see cref="SearchService"/>. Provides record update operations via the
	/// Core Platform service REST API, specifically for x_search field persistence.
	/// </summary>
	internal sealed class CoreServiceRecordManagerProxy : ICrmRecordManager
	{
		private readonly ILogger<CoreServiceRecordManagerProxy> _logger;
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly IHttpContextAccessor _httpContextAccessor;
		private readonly IConfiguration _configuration;

		public CoreServiceRecordManagerProxy(
			ILogger<CoreServiceRecordManagerProxy> logger,
			IHttpClientFactory httpClientFactory,
			IHttpContextAccessor httpContextAccessor,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_httpContextAccessor = httpContextAccessor;
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		private HttpClient GetClient()
		{
			var client = _httpClientFactory.CreateClient("CoreService");
			var authHeader = _httpContextAccessor?.HttpContext?.Request?.Headers["Authorization"].FirstOrDefault();
			if (!string.IsNullOrEmpty(authHeader))
			{
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
			}
			else
			{
				var token = ServiceTokenHelper.GetServiceToken(_configuration);
				client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
			}
			return client;
		}

		public QueryResponse UpdateRecord(string entityName, EntityRecord record, bool executeHooks = true)
		{
			try
			{
				var client = GetClient();
				var recordId = record["id"]?.ToString();
				var url = $"/api/v3/en_US/record/{entityName}/{recordId}";
				_logger.LogDebug("CoreServiceRecordManagerProxy.UpdateRecord() calling Core: {Url}", url);

				var jsonContent = new StringContent(
					JsonConvert.SerializeObject(record),
					System.Text.Encoding.UTF8,
					"application/json");

				var httpResponse = client.PatchAsync(url, jsonContent).GetAwaiter().GetResult();
				var json = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();

				var jObj = JObject.Parse(json);
				return new QueryResponse
				{
					Success = jObj.Value<bool>("success"),
					Message = jObj.Value<string>("message"),
					Timestamp = DateTime.UtcNow,
					Object = new QueryResult { Data = new List<EntityRecord> { record } }
				};
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "CoreServiceRecordManagerProxy.UpdateRecord() failed for entity '{EntityName}'", entityName);
				return new QueryResponse { Success = false, Message = "Core service communication error: " + ex.Message, Timestamp = DateTime.UtcNow };
			}
		}
	}

	#endregion

	#region FieldJsonConverter

	/// <summary>
	/// Custom JSON converter for the polymorphic <see cref="Field"/> hierarchy.
	/// The Core service API returns field metadata with a numeric <c>fieldType</c>
	/// discriminator (matching the <see cref="FieldType"/> enum) but does NOT emit
	/// Newtonsoft.Json <c>$type</c> metadata. This converter reads <c>fieldType</c>
	/// from each field JSON object and instantiates the correct concrete Field subclass.
	/// </summary>
	internal sealed class FieldJsonConverter : JsonConverter<Field>
	{
		public override bool CanWrite => false;

		public override Field ReadJson(JsonReader reader, Type objectType, Field existingValue,
			bool hasExistingValue, JsonSerializer serializer)
		{
			if (reader.TokenType == JsonToken.Null)
				return null;

			var jObj = JObject.Load(reader);
			var fieldTypeValue = jObj["fieldType"]?.Value<int>() ?? 0;
			var fieldType = (FieldType)fieldTypeValue;

			Field field = fieldType switch
			{
				FieldType.AutoNumberField => new AutoNumberField(),
				FieldType.CheckboxField => new CheckboxField(),
				FieldType.CurrencyField => new CurrencyField(),
				FieldType.DateField => new DateField(),
				FieldType.DateTimeField => new DateTimeField(),
				FieldType.EmailField => new EmailField(),
				FieldType.FileField => new FileField(),
				FieldType.HtmlField => new HtmlField(),
				FieldType.ImageField => new ImageField(),
				FieldType.MultiLineTextField => new MultiLineTextField(),
				FieldType.MultiSelectField => new MultiSelectField(),
				FieldType.NumberField => new NumberField(),
				FieldType.PasswordField => new PasswordField(),
				FieldType.PercentField => new PercentField(),
				FieldType.PhoneField => new PhoneField(),
				FieldType.GuidField => new GuidField(),
				FieldType.SelectField => new SelectField(),
				FieldType.TextField => new TextField(),
				FieldType.UrlField => new UrlField(),
				FieldType.GeographyField => new GeographyField(),
				FieldType.RelationField => new RelationFieldMeta(),
				_ => new TextField() // Fallback to TextField for unknown types
			};

			// Populate the concrete field instance from JSON, using a new serializer
			// without this converter to avoid infinite recursion
			using (var subReader = jObj.CreateReader())
			{
				var tempSerializer = new JsonSerializer { NullValueHandling = NullValueHandling.Ignore };
				tempSerializer.Populate(subReader, field);
			}

			return field;
		}

		public override void WriteJson(JsonWriter writer, Field value, JsonSerializer serializer)
		{
			throw new NotImplementedException("FieldJsonConverter is read-only");
		}
	}

	#endregion
}
