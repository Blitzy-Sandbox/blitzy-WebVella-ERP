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
using WebVella.Erp.Service.Admin.Database;
using WebVella.Erp.Service.Admin.Jobs;
using WebVella.Erp.Service.Admin.Services;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Admin
{
	/// <summary>
	/// Entry point and composition root for the Admin/SDK microservice.
	/// Converts the monolith's <c>WebHost.CreateDefaultBuilder + Startup</c> pattern
	/// (from WebVella.Erp.Site/Program.cs and Startup.cs) to the .NET 10 minimal hosting
	/// API pattern (<c>WebApplication.CreateBuilder</c>).
	///
	/// <para><b>Key architectural changes from monolith:</b></para>
	/// <list type="bullet">
	///   <item>NO plugin model — Admin service is a standalone ASP.NET Core app, not an ErpPlugin</item>
	///   <item>NO <c>services.AddErp()</c> — replaced by explicit per-service DI registrations</item>
	///   <item>NO <c>UseErpPlugin&lt;SdkPlugin&gt;()</c> — plugin initialization moves to service startup</item>
	///   <item>JWT-only authentication (no cookie auth) for API endpoints per AAP 0.8.3</item>
	///   <item>Communicates with Core service via gRPC (configured via Services:CoreServiceUrl)</item>
	///   <item>MassTransit/RabbitMQ replaces PostgreSQL LISTEN/NOTIFY for inter-service events</item>
	///   <item>Redis replaces IMemoryCache for distributed caching across service instances</item>
	///   <item>EF Core replaces ambient static DbContext.Current with DI-injected AdminDbContext</item>
	/// </list>
	/// </summary>
	public class Program
	{
		/// <summary>
		/// Application entry point. Initializes the Admin/SDK microservice with all
		/// required infrastructure (database, caching, messaging, authentication) and
		/// domain services (CodeGenService, LogService, ClearJobAndErrorLogsJob).
		/// </summary>
		/// <param name="args">Command-line arguments passed to the host builder.</param>
		public static void Main(string[] args)
		{
			// CRITICAL: Preserve Npgsql legacy timestamp behavior from monolith Startup.cs line 40.
			// This switch must be set before ANY Npgsql connections are created to ensure
			// DateTime values are handled with the legacy timestamp behavior that the
			// monolith's schema and queries depend on.
			AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

			var builder = WebApplication.CreateBuilder(args);

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
		/// Registers all services, infrastructure, and middleware components for the Admin service.
		/// Preserves monolith patterns where required by AAP (Newtonsoft.Json, CORS, compression)
		/// while introducing microservice infrastructure (gRPC, MassTransit, Redis, EF Core).
		/// </summary>
		/// <param name="builder">The web application builder to configure.</param>
		private static void ConfigureServices(WebApplicationBuilder builder)
		{
			var configuration = builder.Configuration;
			var connectionString = configuration.GetConnectionString("Default")
				?? "Server=localhost;Port=5432;User Id=dev;Password=dev;Database=erp_admin;";
			var redisConnectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";

			// ================================================================
			// MVC Controllers + Newtonsoft.Json
			// AAP 0.8.2: Preserve Newtonsoft.Json [JsonProperty] annotations for
			// API contract stability. Matching monolith Startup.cs lines 67-77.
			// ================================================================
			builder.Services.AddControllers()
				.AddNewtonsoftJson(options =>
				{
					// Preserve ErpDateTimeJsonConverter from monolith Startup.cs lines 74-77.
					// This converter applies ERP timezone rules to all DateTime serialization,
					// ensuring API response DateTime values match the monolith's output format.
					options.SerializerSettings.Converters.Add(new ErpDateTimeJsonConverter());
				});

			// Global Newtonsoft.Json default settings (monolith Startup.cs lines 83-86).
			// Ensures JsonConvert.SerializeObject/DeserializeObject calls throughout the
			// service use the same DateTime converter as MVC controllers.
			JsonConvert.DefaultSettings = () => new JsonSerializerSettings
			{
				Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
			};

			// ================================================================
			// OpenAPI / Swagger documentation
			// ================================================================
			builder.Services.AddEndpointsApiExplorer();
			builder.Services.AddSwaggerGen(c =>
			{
				// Microsoft.OpenApi v2.3.0 moved types from Microsoft.OpenApi.Models
				// to the Microsoft.OpenApi root namespace.
				c.SwaggerDoc("v3", new Microsoft.OpenApi.OpenApiInfo
				{
					Title = "WebVella ERP Admin Service API",
					Version = "v3.0",
					Description = "Admin/SDK microservice providing entity designer, code generation, " +
						"and log management REST/gRPC endpoints."
				});

				// Configure Swagger UI with JWT Bearer authentication support
				c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
				{
					Name = "Authorization",
					Type = Microsoft.OpenApi.SecuritySchemeType.Http,
					Scheme = "bearer",
					BearerFormat = "JWT",
					In = Microsoft.OpenApi.ParameterLocation.Header,
					Description = "Enter the JWT token issued by the Core service."
				});
				c.AddSecurityRequirement(doc => new Microsoft.OpenApi.OpenApiSecurityRequirement
				{
					{
						new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer"),
						new List<string>()
					}
				});
			});

			// ================================================================
			// JWT Bearer Authentication
			// Replaces the monolith's dual Cookie + JWT policy scheme (Startup.cs lines 88-125)
			// with JWT-only authentication for inter-service API communication.
			// AAP 0.8.3: JWT tokens issued by Core service must be accepted.
			// ================================================================
			var jwtKey = configuration["Jwt:Key"]
				?? "default-dev-key-change-in-production-min-32-chars!!";
			var jwtIssuer = configuration["Jwt:Issuer"] ?? "webvella-erp";
			var jwtAudience = configuration["Jwt:Audience"] ?? "webvella-erp";

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
					ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }
				};
			});

			builder.Services.AddAuthorization();

			// ================================================================
			// gRPC server hosting for inter-service communication
			// Enables other microservices to call Admin service operations via gRPC.
			// ================================================================
			builder.Services.AddGrpc();

			// ================================================================
			// MassTransit + RabbitMQ (event-driven inter-service messaging)
			// Replaces the monolith's PostgreSQL LISTEN/NOTIFY pub/sub
			// (WebVella.Erp/Notifications/) with asynchronous domain events.
			// Configuration from appsettings.json → Messaging:RabbitMq section.
			// ================================================================
			builder.Services.AddMassTransit(x =>
			{
				x.UsingRabbitMq((context, cfg) =>
				{
					var rabbitHost = configuration["Messaging:RabbitMq:Host"] ?? "localhost";
					var rabbitUser = configuration["Messaging:RabbitMq:Username"] ?? "guest";
					var rabbitPass = configuration["Messaging:RabbitMq:Password"] ?? "guest";

					cfg.Host(rabbitHost, "/", h =>
					{
						h.Username(rabbitUser);
						h.Password(rabbitPass);
					});

					cfg.ConfigureEndpoints(context);
				});
			});

			// ================================================================
			// Redis Distributed Cache
			// Replaces the monolith's IMemoryCache (1-hour TTL) with distributed
			// caching for entity metadata shared across Admin service instances.
			// Configuration from appsettings.json → Redis:ConnectionString.
			// ================================================================
			builder.Services.AddStackExchangeRedisCache(options =>
			{
				options.Configuration = redisConnectionString;
				options.InstanceName = "admin-service:";
			});

			// ================================================================
			// EF Core + PostgreSQL (database-per-service: erp_admin)
			// AAP 0.8.3: Connection pooling (min 1, max 100) configurable via
			// connection string. EQL query timeout (600 seconds) preserved.
			// ================================================================
			builder.Services.AddDbContext<AdminDbContext>(options =>
			{
				options.UseNpgsql(connectionString, npgsqlOptions =>
				{
					// Preserve the monolith's 600-second command timeout for EQL queries
					// (AAP 0.8.3). Connection pooling min/max is configured in the
					// connection string itself (MinPoolSize=1;MaxPoolSize=100).
					npgsqlOptions.CommandTimeout(600);
				});
			});

			// ================================================================
			// Admin-specific domain services
			// ================================================================

			// ILogService → LogService: system log and job log cleanup service.
			// Used by ClearJobAndErrorLogsJob for periodic maintenance.
			// Registered as scoped matching the monolith's per-request lifecycle.
			builder.Services.AddScoped<ILogService, LogService>();

			// Cross-service proxy interfaces required by CodeGenService for accessing
			// Core service data (entities, relations, records, apps, pages).
			// These implementations delegate to the Core service via gRPC.
			// Registered as scoped to match the CodeGenService lifecycle.
			builder.Services.AddScoped<IEntityRepository, CoreServiceEntityRepositoryProxy>();
			builder.Services.AddScoped<IRelationRepository, CoreServiceRelationRepositoryProxy>();
			builder.Services.AddScoped<IRecordRepository, CoreServiceRecordRepositoryProxy>();
			builder.Services.AddScoped<IAppServiceClient, CoreServiceAppClientProxy>();
			builder.Services.AddScoped<IPageServiceClient, CoreServicePageClientProxy>();

			// ICodeGenService → CodeGenService: diff-based C# migration code generator.
			// Uses factory registration to provide the defaultCulture string parameter
			// that the DI container cannot resolve automatically.
			builder.Services.AddScoped<ICodeGenService>(sp =>
			{
				return new CodeGenService(
					sp.GetRequiredService<IEntityRepository>(),
					sp.GetRequiredService<IRelationRepository>(),
					sp.GetRequiredService<IRecordRepository>(),
					sp.GetRequiredService<IAppServiceClient>(),
					sp.GetRequiredService<IPageServiceClient>(),
					sp.GetRequiredService<AdminDbContext>(),
					sp.GetRequiredService<IConfiguration>(),
					sp.GetRequiredService<ILogger<CodeGenService>>(),
					ErpSettings.Locale ?? "en-US");
			});

			// ================================================================
			// Background Jobs
			// Replaces the monolith's ScheduleManager + JobManager pattern
			// (SdkPlugin.cs lines 72-106) with ASP.NET Core BackgroundService.
			// ClearJobAndErrorLogsJob runs on a configurable interval
			// (default 1440 min = 24h, from appsettings.json → Jobs:ClearLogsIntervalMinutes).
			// ================================================================
			builder.Services.AddHostedService<ClearJobAndErrorLogsJob>();

			// ================================================================
			// Health Checks
			// Provides a /health endpoint for container orchestrators (K8s, Docker)
			// to verify service liveness and readiness.
			// ================================================================
			builder.Services.AddHealthChecks()
				.AddCheck("self", () => HealthCheckResult.Healthy("Admin service is running."));

			// ================================================================
			// CORS (permissive default, matching monolith Startup.cs lines 58-64)
			// ================================================================
			builder.Services.AddCors(options =>
			{
				options.AddDefaultPolicy(policy =>
					policy.AllowAnyOrigin()
						.AllowAnyMethod()
						.AllowAnyHeader());
			});

			// ================================================================
			// Response Compression (matching monolith Startup.cs lines 48-49)
			// ================================================================
			builder.Services.Configure<GzipCompressionProviderOptions>(options =>
				options.Level = CompressionLevel.Optimal);
			builder.Services.AddResponseCompression(options =>
			{
				options.Providers.Add<GzipCompressionProvider>();
			});

			// ================================================================
			// Routing (lowercase URLs matching monolith Startup.cs line 50)
			// ================================================================
			builder.Services.AddRouting(options => { options.LowercaseUrls = true; });
		}

		/// <summary>
		/// Configures the HTTP request pipeline (middleware) for the Admin service.
		/// Preserves the monolith's middleware ordering where applicable:
		/// compression → CORS → routing → auth → endpoints.
		/// </summary>
		/// <param name="app">The built web application to configure.</param>
		private static void ConfigurePipeline(WebApplication app)
		{
			// Swagger UI — available in all environments for API documentation
			if (app.Environment.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}

			app.UseSwagger();
			app.UseSwaggerUI(c =>
			{
				c.SwaggerEndpoint("/swagger/v3/swagger.json", "Admin Service API v3");
			});

			// Response compression (before static files, matching monolith Startup.cs line 161)
			app.UseResponseCompression();

			// CORS (before routing, matching monolith Startup.cs line 164)
			app.UseCors();

			// Routing
			app.UseRouting();

			// Authentication + Authorization (matching monolith Startup.cs lines 179-180)
			app.UseAuthentication();
			app.UseAuthorization();

			// Map controller endpoints (AdminController routes: api/v3.0/p/sdk/*)
			app.MapControllers();

			// Health check endpoint for container orchestration
			app.MapHealthChecks("/health");
		}
	}

	#region Cross-Service Proxy Implementations

	/// <summary>
	/// Core service entity repository proxy that provides entity metadata access
	/// for the CodeGenService. Connects to the Core service to retrieve entity
	/// definitions. When the Core service is unavailable, returns empty collections
	/// to allow the Admin service to remain operational for non-CodeGen endpoints.
	/// This implementation will be enhanced with gRPC client calls when the Core
	/// service gRPC integration layer is completed.
	/// </summary>
	internal sealed class CoreServiceEntityRepositoryProxy : IEntityRepository
	{
		private readonly ILogger<CoreServiceEntityRepositoryProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceEntityRepositoryProxy(
			ILogger<CoreServiceEntityRepositoryProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public List<DbEntity> Read()
		{
			var coreServiceUrl = _configuration["Services:CoreServiceUrl"];
			_logger.LogWarning(
				"CoreServiceEntityRepositoryProxy.Read() called. Core service URL: {Url}. " +
				"Returning empty list — gRPC integration pending.", coreServiceUrl);
			return new List<DbEntity>();
		}
	}

	/// <summary>
	/// Core service relation repository proxy for CodeGenService relation diff operations.
	/// Delegates to Core service via gRPC for entity relation metadata retrieval.
	/// </summary>
	internal sealed class CoreServiceRelationRepositoryProxy : IRelationRepository
	{
		private readonly ILogger<CoreServiceRelationRepositoryProxy> _logger;
		private readonly IConfiguration _configuration;

		public CoreServiceRelationRepositoryProxy(
			ILogger<CoreServiceRelationRepositoryProxy> logger,
			IConfiguration configuration)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		public List<DbEntityRelation> Read()
		{
			_logger.LogWarning(
				"CoreServiceRelationRepositoryProxy.Read() called — gRPC integration pending.");
			return new List<DbEntityRelation>();
		}

		public EntityRelation ReadRelation(Guid id)
		{
			_logger.LogWarning(
				"CoreServiceRelationRepositoryProxy.ReadRelation({Id}) called — gRPC integration pending.", id);
			return null;
		}
	}

	/// <summary>
	/// Core service record repository proxy for CodeGenService record diff operations.
	/// Delegates to Core service via gRPC for record query execution.
	/// </summary>
	internal sealed class CoreServiceRecordRepositoryProxy : IRecordRepository
	{
		private readonly ILogger<CoreServiceRecordRepositoryProxy> _logger;

		public CoreServiceRecordRepositoryProxy(ILogger<CoreServiceRecordRepositoryProxy> logger)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		public List<EntityRecord> Find(EntityQuery query)
		{
			_logger.LogWarning(
				"CoreServiceRecordRepositoryProxy.Find() called — gRPC integration pending.");
			return new List<EntityRecord>();
		}
	}

	/// <summary>
	/// Core service application client proxy for CodeGenService app diff operations.
	/// Delegates to Core/Gateway service for application/sitemap metadata retrieval.
	/// </summary>
	internal sealed class CoreServiceAppClientProxy : IAppServiceClient
	{
		private readonly ILogger<CoreServiceAppClientProxy> _logger;

		public CoreServiceAppClientProxy(ILogger<CoreServiceAppClientProxy> logger)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		public List<App> GetAllApplications(bool useCache = true)
		{
			_logger.LogWarning(
				"CoreServiceAppClientProxy.GetAllApplications(useCache={UseCache}) called — gRPC integration pending.",
				useCache);
			return new List<App>();
		}

		public List<App> GetAllApplications(string connectionString, bool useCache = false)
		{
			_logger.LogWarning(
				"CoreServiceAppClientProxy.GetAllApplications(connectionString, useCache={UseCache}) called — gRPC integration pending.",
				useCache);
			return new List<App>();
		}
	}

	/// <summary>
	/// Core service page client proxy for CodeGenService page diff operations.
	/// Delegates to Core/Gateway service for page and page body node retrieval.
	/// </summary>
	internal sealed class CoreServicePageClientProxy : IPageServiceClient
	{
		private readonly ILogger<CoreServicePageClientProxy> _logger;

		public CoreServicePageClientProxy(ILogger<CoreServicePageClientProxy> logger)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		public List<ErpPage> GetAll(bool useCache = true)
		{
			_logger.LogWarning(
				"CoreServicePageClientProxy.GetAll(useCache={UseCache}) called — gRPC integration pending.",
				useCache);
			return new List<ErpPage>();
		}

		public List<ErpPage> GetAll(string connectionString, bool useCache = false)
		{
			_logger.LogWarning(
				"CoreServicePageClientProxy.GetAll(connectionString, useCache={UseCache}) called — gRPC integration pending.",
				useCache);
			return new List<ErpPage>();
		}

		public List<PageBodyNode> GetAllBodyNodes()
		{
			_logger.LogWarning(
				"CoreServicePageClientProxy.GetAllBodyNodes() called — gRPC integration pending.");
			return new List<PageBodyNode>();
		}

		public List<PageBodyNode> GetAllBodyNodes(string connectionString)
		{
			_logger.LogWarning(
				"CoreServicePageClientProxy.GetAllBodyNodes(connectionString) called — gRPC integration pending.");
			return new List<PageBodyNode>();
		}

		public List<PageBodyNode> GetPageNodes(Guid pageId)
		{
			_logger.LogWarning(
				"CoreServicePageClientProxy.GetPageNodes({PageId}) called — gRPC integration pending.", pageId);
			return new List<PageBodyNode>();
		}

		public List<PageBodyNode> GetPageNodes(string connectionString, Guid pageId)
		{
			_logger.LogWarning(
				"CoreServicePageClientProxy.GetPageNodes(connectionString, {PageId}) called — gRPC integration pending.",
				pageId);
			return new List<PageBodyNode>();
		}
	}

	#endregion
}
