// =============================================================================
// WebVella ERP — Core Platform Service Test Infrastructure
// CoreServiceWebApplicationFactory.cs
// =============================================================================
// Custom WebApplicationFactory<Program> that overrides the Core Platform
// service's production configuration for integration test isolation.
//
// This is the central test host that:
//   - Replaces the production PostgreSQL connection with a Testcontainer string
//   - Replaces Redis distributed cache with an in-memory distributed cache
//   - Replaces MassTransit (RabbitMQ/SQS) with InMemoryTestHarness
//   - Configures JWT authentication matching the monolith's Config.json values
//   - Disables background job hosted services for deterministic test execution
//   - Sets the Npgsql legacy timestamp behavior switch (Startup.cs line 40)
//
// Key source references:
//   - WebVella.Erp.Site/Startup.cs (lines 37-132): Original DI registrations
//   - WebVella.Erp.Site/Config.json (lines 1-37): Settings, JWT, EncryptionKey
//   - WebVella.Erp/Database/DbContext.cs (line 111): CreateContext(connString)
//   - WebVella.Erp/ERPService.cs (lines 18-527): System entity initialization
//   - WebVella.Erp/ErpSettings.cs: Static configuration binder
//   - src/Services/WebVella.Erp.Service.Core/Program.cs: Service entry point
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using WebVella.Erp.Service.Core;

namespace WebVella.Erp.Tests.Core.Fixtures
{
	/// <summary>
	/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that creates an
	/// in-memory test server hosting the Core Platform microservice with all
	/// external dependencies replaced by test-safe alternatives.
	///
	/// <para>
	/// <strong>PostgreSQL</strong>: The production connection string is replaced
	/// with a Testcontainer PostgreSQL instance connection string, ensuring each
	/// test run operates against a clean, isolated database.
	/// </para>
	///
	/// <para>
	/// <strong>Redis</strong>: The production Redis distributed cache is replaced
	/// with <c>AddDistributedMemoryCache()</c>, an in-memory implementation that
	/// requires no external Redis instance.
	/// </para>
	///
	/// <para>
	/// <strong>MassTransit</strong>: The production RabbitMQ/SQS transport is
	/// replaced with the MassTransit InMemory test harness, allowing tests to
	/// verify event publishing and consuming without external message brokers.
	/// </para>
	///
	/// <para>
	/// <strong>JWT Authentication</strong>: JWT Bearer options are configured
	/// with the same key, issuer, and audience values as the monolith's
	/// <c>Config.json</c> (lines 24-28), ensuring token compatibility.
	/// </para>
	///
	/// <para>
	/// <strong>Background Jobs</strong>: All job-related hosted services are
	/// removed from the DI container to prevent background processing during
	/// tests, ensuring deterministic test execution.
	/// </para>
	/// </summary>
	/// <remarks>
	/// Usage in an xUnit test fixture:
	/// <code>
	/// var factory = new CoreServiceWebApplicationFactory(postgresContainer.GetConnectionString());
	/// var client = factory.CreateClient();
	/// // Use client for HTTP integration tests
	/// // Or resolve services: factory.Services.GetRequiredService&lt;EntityManager&gt;()
	/// </code>
	/// </remarks>
	public class CoreServiceWebApplicationFactory : WebApplicationFactory<Program>
	{
		/// <summary>
		/// Connection string to the PostgreSQL Testcontainer instance.
		/// This connection string is injected into the Core service's configuration
		/// at <c>ConnectionStrings:Default</c>, replacing the production database.
		/// </summary>
		private readonly string _postgresConnectionString;

		/// <summary>
		/// JWT signing key matching the monolith's <c>Config.json</c> line 25
		/// (<c>Settings:Jwt:Key</c>). Used for both configuration override and
		/// JWT Bearer token validation parameter configuration.
		/// </summary>
		private const string TestJwtKey =
			"ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

		/// <summary>
		/// JWT issuer matching the monolith's <c>Config.json</c> line 26
		/// (<c>Settings:Jwt:Issuer</c>).
		/// </summary>
		private const string TestJwtIssuer = "webvella-erp";

		/// <summary>
		/// JWT audience matching the monolith's <c>Config.json</c> line 27
		/// (<c>Settings:Jwt:Audience</c>).
		/// </summary>
		private const string TestJwtAudience = "webvella-erp";

		/// <summary>
		/// Encryption key matching the monolith's <c>Config.json</c> line 5
		/// (<c>Settings:EncryptionKey</c>). Required for password hashing
		/// compatibility with the monolith's credential validation logic.
		/// </summary>
		private const string TestEncryptionKey =
			"BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658";

		/// <summary>
		/// Constructs a new <see cref="CoreServiceWebApplicationFactory"/> with
		/// the specified PostgreSQL Testcontainer connection string.
		/// </summary>
		/// <param name="postgresConnectionString">
		/// Connection string to the PostgreSQL Testcontainer instance.
		/// Must not be null or empty. Typically obtained from
		/// <c>PostgreSqlContainer.GetConnectionString()</c>.
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="postgresConnectionString"/> is null.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="postgresConnectionString"/> is empty
		/// or whitespace.
		/// </exception>
		public CoreServiceWebApplicationFactory(string postgresConnectionString)
		{
			if (postgresConnectionString == null)
			{
				throw new ArgumentNullException(nameof(postgresConnectionString));
			}

			if (string.IsNullOrWhiteSpace(postgresConnectionString))
			{
				throw new ArgumentException(
					"PostgreSQL connection string must not be empty or whitespace.",
					nameof(postgresConnectionString));
			}

			_postgresConnectionString = postgresConnectionString;
		}

		/// <summary>
		/// Configures the web host for the Core Platform service with test-specific
		/// overrides. This method is called by the <see cref="WebApplicationFactory{T}"/>
		/// base class during test server creation.
		///
		/// <para>
		/// The configuration is applied in the following order:
		/// <list type="number">
		///   <item>Environment set to "Testing"</item>
		///   <item>In-memory configuration overrides (DB connection, JWT, settings)</item>
		///   <item>Service registration overrides (cache, MassTransit, JWT, jobs)</item>
		/// </list>
		/// </para>
		/// </summary>
		/// <param name="builder">
		/// The <see cref="IWebHostBuilder"/> provided by the factory base class.
		/// </param>
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			// =================================================================
			// Environment — "Testing"
			// Ensures test-specific configuration files (appsettings.Testing.json)
			// are loaded and environment-conditional code paths execute correctly.
			// =================================================================
			builder.UseEnvironment("Testing");

			// =================================================================
			// Configuration Overrides
			// Replace production appsettings.json values with test-specific
			// values. In-memory collection has the highest priority and
			// overrides all other configuration sources.
			// =================================================================
			builder.ConfigureAppConfiguration((context, config) =>
			{
				config.AddInMemoryCollection(new Dictionary<string, string>
				{
					// PostgreSQL connection — Testcontainer instance
					["ConnectionStrings:Default"] = _postgresConnectionString,

					// JWT settings — matching monolith Config.json lines 24-28
					// These values MUST match exactly for backward compatibility
					// with existing JWT tokens and credential validation.
					["Jwt:Key"] = TestJwtKey,
					["Jwt:Issuer"] = TestJwtIssuer,
					["Jwt:Audience"] = TestJwtAudience,

					// ErpSettings overrides — matching monolith Config.json
					// DevelopmentMode=true enables diagnostic features
					["Settings:DevelopmentMode"] = "true",

					// Background jobs disabled for test isolation
					["Settings:EnableBackgroundJobs"] = "false",

					// File system storage disabled — tests do not write to disk
					["Settings:EnableFileSystemStorage"] = "false",

					// Encryption key — matching monolith Config.json line 5
					// Required for password hashing compatibility (SecurityManager,
					// ErpSettings.EncryptionKey used by CryptoUtility)
					["Settings:EncryptionKey"] = TestEncryptionKey,

					// Localization defaults
					["Settings:Lang"] = "en",
					["Settings:Locale"] = "en-US",

					// Per-service job system disabled
					["Jobs:Enabled"] = "false",

					// Redis not used in tests — in-memory cache replaces it
					["Redis:ConnectionString"] = "not-used-in-tests",

					// MassTransit transport — InMemory for test harness
					["Messaging:Transport"] = "InMemory"
				});
			});

			// =================================================================
			// Service Registration Overrides
			// ConfigureTestServices runs AFTER the service's ConfigureServices,
			// allowing us to replace production registrations with test doubles.
			// =================================================================
			builder.ConfigureTestServices(services =>
			{
				// -------------------------------------------------------------
				// 1. Npgsql Legacy Timestamp Behavior
				// Preserved from monolith Startup.cs line 40.
				// Required until system tables are migrated to timestamptz.
				// The Core service's Program.cs also sets this, but we set it
				// again here as a safety net for test execution ordering.
				// -------------------------------------------------------------
				AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

				// -------------------------------------------------------------
				// 2. Replace Redis Distributed Cache with In-Memory
				// The Core service registers StackExchange.Redis via
				// AddStackExchangeRedisCache() in Program.cs lines 182-186.
				// For tests, we remove that registration and replace it with
				// an in-memory distributed cache that requires no Redis server.
				// -------------------------------------------------------------
				services.RemoveAll<IDistributedCache>();
				services.AddDistributedMemoryCache();

				// -------------------------------------------------------------
				// 3. Replace MassTransit with InMemory Test Harness
				// The Core service registers MassTransit with RabbitMQ or
				// Amazon SQS/SNS transport (Program.cs lines 323-365).
				// The test harness replaces the production transport with an
				// in-memory bus, allowing tests to verify event publishing
				// and consuming without external message broker dependencies.
				// AddConsumers re-discovers all IConsumer<T> implementations
				// from the Core service assembly for in-process verification.
				// -------------------------------------------------------------
				services.AddMassTransitTestHarness(cfg =>
				{
					cfg.AddConsumers(typeof(Program).Assembly);
				});

				// -------------------------------------------------------------
				// 4. Remove Background Job Hosted Services
				// While Jobs:Enabled is set to false (which prevents job
				// registration in Program.cs lines 373-379), this provides an
				// additional safety net by removing any IHostedService
				// registrations related to job processing.
				// We selectively remove only job-related hosted services,
				// preserving the MassTransit bus hosted service.
				// -------------------------------------------------------------
				RemoveJobHostedServices(services);

				// -------------------------------------------------------------
				// 5. Override JWT Bearer Authentication
				// The Core service configures JWT Bearer authentication in
				// Program.cs lines 120-145 using configuration values.
				// PostConfigure runs AFTER the initial configuration, ensuring
				// the test JWT key, issuer, and audience are applied regardless
				// of any configuration loading order issues.
				// Values match monolith Config.json lines 24-28 exactly.
				// Source: Startup.cs lines 102-114 JWT Bearer options pattern.
				// -------------------------------------------------------------
				services.PostConfigure<JwtBearerOptions>(
					JwtBearerDefaults.AuthenticationScheme,
					options =>
					{
						options.TokenValidationParameters = new TokenValidationParameters
						{
							ValidateIssuer = true,
							ValidateAudience = true,
							ValidateLifetime = true,
							ValidateIssuerSigningKey = true,
							ValidIssuer = TestJwtIssuer,
							ValidAudience = TestJwtAudience,
							IssuerSigningKey = new SymmetricSecurityKey(
								Encoding.UTF8.GetBytes(TestJwtKey))
						};
					});
			});
		}

		/// <summary>
		/// Removes background job hosted services from the service collection
		/// while preserving infrastructure hosted services (MassTransit bus,
		/// health checks, etc.).
		///
		/// The Core service registers <c>JobManager</c> and <c>ScheduleManager</c>
		/// as hosted services when <c>Jobs:Enabled</c> is true (Program.cs
		/// lines 373-379). Although test configuration sets this to false,
		/// this method provides defense-in-depth removal.
		///
		/// The filtering logic identifies job-related services by checking
		/// the implementation type name for job/schedule-related keywords.
		/// </summary>
		/// <param name="services">The service collection to modify.</param>
		private static void RemoveJobHostedServices(IServiceCollection services)
		{
			var hostedServiceDescriptors = services
				.Where(d => d.ServiceType == typeof(IHostedService))
				.ToList();

			foreach (var descriptor in hostedServiceDescriptors)
			{
				// Determine the actual implementation type from the descriptor.
				// Factory-based registrations (AddHostedService with lambda) do
				// not set ImplementationType directly — fall back to checking
				// ImplementationInstance type if available.
				var implType = descriptor.ImplementationType
					?? descriptor.ImplementationInstance?.GetType();

				if (implType != null)
				{
					var typeName = implType.FullName ?? implType.Name;

					// Remove job-related hosted services:
					// - JobManager (WebVella.Erp.Service.Core.Jobs.JobManager)
					// - ScheduleManager (WebVella.Erp.Service.Core.Jobs.ScheduleManager)
					// Keep MassTransit bus hosted service and any other infrastructure services.
					if (typeName.Contains("JobManager", StringComparison.OrdinalIgnoreCase) ||
						typeName.Contains("ScheduleManager", StringComparison.OrdinalIgnoreCase) ||
						typeName.Contains("JobPool", StringComparison.OrdinalIgnoreCase))
					{
						services.Remove(descriptor);
					}
				}
			}
		}

		/// <summary>
		/// Releases resources used by the test server and the factory.
		/// Calls <see cref="WebApplicationFactory{T}.Dispose(bool)"/> which
		/// stops the in-memory test server and disposes all scoped services.
		/// </summary>
		/// <param name="disposing">
		/// <c>true</c> when called from <see cref="IDisposable.Dispose"/>;
		/// <c>false</c> when called from a finalizer.
		/// </param>
		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
		}
	}
}
