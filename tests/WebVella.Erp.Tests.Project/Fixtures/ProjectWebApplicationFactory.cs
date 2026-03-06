// =============================================================================
// WebVella ERP — Project/Task Service Test Infrastructure
// ProjectWebApplicationFactory.cs
// =============================================================================
// Custom WebApplicationFactory<Program> that overrides the Project/Task
// service's production configuration for integration test isolation.
//
// This is the central test host that:
//   - Replaces the production PostgreSQL connection with a Testcontainer string
//   - Replaces Redis distributed cache with an in-memory distributed cache
//   - Replaces MassTransit (RabbitMQ/SQS) with InMemoryTestHarness
//   - Configures JWT authentication matching the monolith's Config.json values
//   - Disables background job hosted services for deterministic test execution
//   - Sets the Npgsql legacy timestamp behavior switch (Startup.cs line 34)
//   - Provides configured HttpClient for REST controller integration tests
//   - Provides configured GrpcChannel for gRPC inter-service integration tests
//
// Key source references:
//   - WebVella.Erp.Site.Project/Startup.cs (lines 33-117): Original DI setup
//   - WebVella.Erp.Site.Project/Config.json (lines 1-32): Settings, JWT, EncryptionKey
//   - WebVella.Erp/Database/DbContext.cs (line 60): Static connection string usage
//   - WebVella.Erp/Api/SecurityContext.cs: AsyncLocal user stack (JWT replaces in tests)
//   - WebVella.Erp/ErpSettings.cs: Static configuration binder
//   - src/Services/WebVella.Erp.Service.Project/Program.cs: Service entry point
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Grpc.Net.Client;
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
using WebVella.Erp.Service.Project;

namespace WebVella.Erp.Tests.Project.Fixtures
{
	/// <summary>
	/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that creates an
	/// in-memory test server hosting the Project/Task microservice with all
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
	/// <c>Config.json</c> (lines 19-23), ensuring token compatibility.
	/// </para>
	///
	/// <para>
	/// <strong>Background Jobs</strong>: All job-related hosted services are
	/// removed from the DI container to prevent background processing during
	/// tests, ensuring deterministic test execution.
	/// </para>
	///
	/// <para>
	/// <strong>gRPC</strong>: Provides <see cref="GrpcChannel"/> instances for
	/// testing the Project service's gRPC endpoints (ProjectGrpcService) used
	/// for inter-service communication with CRM and Core services.
	/// </para>
	/// </summary>
	/// <remarks>
	/// Usage in an xUnit test fixture:
	/// <code>
	/// var factory = new ProjectWebApplicationFactory(postgresContainer.GetConnectionString());
	/// var client = factory.CreateClient();
	/// // Use client for REST integration tests
	/// var authClient = factory.CreateAuthenticatedClient(jwtToken);
	/// // Use authClient for authenticated REST integration tests
	/// var grpcChannel = factory.CreateGrpcChannel();
	/// // Use grpcChannel for gRPC integration tests
	/// </code>
	/// </remarks>
	public class ProjectWebApplicationFactory : WebApplicationFactory<Program>
	{
		/// <summary>
		/// Connection string to the PostgreSQL Testcontainer instance.
		/// This connection string is injected into the Project service's configuration
		/// at <c>ConnectionStrings:Default</c>, replacing the production database.
		/// </summary>
		private readonly string _postgresConnectionString;

		/// <summary>
		/// JWT signing key matching the monolith's <c>Config.json</c> line 20
		/// (<c>Settings:Jwt:Key</c>). Used for both configuration override and
		/// JWT Bearer token validation parameter configuration.
		/// </summary>
		private const string TestJwtKey =
			"ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

		/// <summary>
		/// JWT issuer matching the monolith's <c>Config.json</c> line 21
		/// (<c>Settings:Jwt:Issuer</c>).
		/// </summary>
		private const string TestJwtIssuer = "webvella-erp";

		/// <summary>
		/// JWT audience matching the monolith's <c>Config.json</c> line 22
		/// (<c>Settings:Jwt:Audience</c>).
		/// </summary>
		private const string TestJwtAudience = "webvella-erp";

		/// <summary>
		/// Encryption key matching the monolith's <c>Config.json</c> line 4
		/// (<c>Settings:EncryptionKey</c>). Required for password hashing
		/// compatibility with the monolith's credential validation logic
		/// (CryptoUtility in SharedKernel).
		/// </summary>
		private const string TestEncryptionKey =
			"BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658";

		/// <summary>
		/// Internal gRPC channel instance, lazily created on first access via
		/// <see cref="CreateGrpcChannel"/>. Disposed when the factory is disposed.
		/// </summary>
		private GrpcChannel _grpcChannel;

		/// <summary>
		/// Gets the gRPC channel for inter-service test calls against the Project
		/// service's gRPC endpoints (ProjectGrpcService). The channel is lazily
		/// created on first access using <see cref="CreateGrpcChannel"/> and
		/// disposed when the factory is disposed.
		/// </summary>
		/// <remarks>
		/// For authenticated gRPC calls, use <see cref="CreateAuthenticatedGrpcChannel"/>
		/// instead, which creates a new channel with Bearer token authentication.
		/// </remarks>
		public GrpcChannel GrpcChannel
		{
			get
			{
				if (_grpcChannel == null)
				{
					_grpcChannel = CreateGrpcChannel();
				}
				return _grpcChannel;
			}
			private set
			{
				_grpcChannel = value;
			}
		}

		/// <summary>
		/// Constructs a new <see cref="ProjectWebApplicationFactory"/> with
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
		public ProjectWebApplicationFactory(string postgresConnectionString)
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
		/// Configures the web host for the Project/Task service with test-specific
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

					// JWT settings — matching monolith Config.json lines 19-23
					// These values MUST match exactly for backward compatibility
					// with existing JWT tokens and credential validation.
					["Jwt:Key"] = TestJwtKey,
					["Jwt:Issuer"] = TestJwtIssuer,
					["Jwt:Audience"] = TestJwtAudience,

					// Background jobs disabled for test isolation
					// Prevents StartTasksOnStartDateJob from running during tests.
					["Jobs:Enabled"] = "false",

					// Redis not used in tests — in-memory cache replaces it
					["Redis:ConnectionString"] = "not-used-in-tests",

					// MassTransit transport — InMemory for test harness
					// Replaces production RabbitMQ/SQS transport.
					["Messaging:Transport"] = "InMemory",

					// Encryption key — matching monolith Config.json line 4
					// Required for password hashing compatibility (SecurityManager,
					// ErpSettings.EncryptionKey used by CryptoUtility)
					["Settings:EncryptionKey"] = TestEncryptionKey,

					// Localization defaults matching monolith Config.json lines 5-6
					["Settings:Lang"] = "en",
					["Settings:Locale"] = "en-US",

					// DevelopmentMode enabled for diagnostic features in tests
					["Settings:DevelopmentMode"] = "true",

					// Inter-service URLs set to localhost — mocked in tests.
					// The Project service uses these for cross-service REST calls
					// to Core (user/entity resolution) and CRM (account/case lookups).
					["ServiceUrls:CoreService"] = "http://localhost",
					["ServiceUrls:CrmService"] = "http://localhost"
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
				// Preserved from monolith Startup.cs line 34.
				// Required until system tables are migrated to timestamptz.
				// The Project service's Program.cs also sets this (line 78),
				// but we set it again here as a safety net for test execution
				// ordering since AppContext switches are process-global.
				// -------------------------------------------------------------
				AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

				// -------------------------------------------------------------
				// 2. Replace Redis Distributed Cache with In-Memory
				// The Project service registers StackExchange.Redis via
				// AddStackExchangeRedisCache() in Program.cs lines 174-178.
				// For tests, we remove that registration and replace it with
				// an in-memory distributed cache that requires no Redis server.
				// -------------------------------------------------------------
				services.RemoveAll<IDistributedCache>();
				services.AddDistributedMemoryCache();

				// -------------------------------------------------------------
				// 3. Replace MassTransit with InMemory Test Harness
				// The Project service registers MassTransit with RabbitMQ or
				// Amazon SQS/SNS transport (Program.cs lines 332-370).
				// The test harness replaces the production transport with an
				// in-memory bus, allowing tests to verify event publishing
				// and consuming without external message broker dependencies.
				// AddConsumers re-discovers all IConsumer<T> implementations
				// from the Project service assembly for in-process verification.
				// -------------------------------------------------------------
				services.AddMassTransitTestHarness(cfg =>
				{
					cfg.AddConsumers(typeof(Program).Assembly);
				});

				// -------------------------------------------------------------
				// 4. Remove Background Job Hosted Services
				// While Jobs:Enabled is set to false (which prevents job
				// registration in Program.cs lines 375-378), this provides an
				// additional safety net by removing any IHostedService
				// registrations related to job processing.
				// We selectively remove only job-related hosted services,
				// preserving the MassTransit bus hosted service.
				// -------------------------------------------------------------
				RemoveJobHostedServices(services);

				// -------------------------------------------------------------
				// 5. Override JWT Bearer Authentication
				// The Project service configures JWT Bearer authentication in
				// Program.cs lines 114-132 using configuration values.
				// PostConfigure runs AFTER the initial configuration, ensuring
				// the test JWT key, issuer, and audience are applied regardless
				// of any configuration loading order issues.
				// Values match monolith Config.json lines 19-23 exactly.
				// Source: Startup.cs lines 94-106 JWT Bearer options pattern.
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
		/// Creates an <see cref="HttpClient"/> with the specified JWT Bearer token
		/// pre-configured in the <c>Authorization</c> header. Used for testing
		/// authenticated REST API endpoints on the Project service.
		/// </summary>
		/// <param name="jwtToken">
		/// A valid JWT token string. Typically generated using
		/// <c>JwtTokenHandler.GenerateToken()</c> from the SharedKernel
		/// with the same key, issuer, and audience as the test factory.
		/// </param>
		/// <returns>
		/// An <see cref="HttpClient"/> configured to make authenticated requests
		/// to the Project service's REST API endpoints.
		/// </returns>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="jwtToken"/> is null.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="jwtToken"/> is empty or whitespace.
		/// </exception>
		public HttpClient CreateAuthenticatedClient(string jwtToken)
		{
			if (jwtToken == null)
			{
				throw new ArgumentNullException(nameof(jwtToken));
			}

			if (string.IsNullOrWhiteSpace(jwtToken))
			{
				throw new ArgumentException(
					"JWT token must not be empty or whitespace.",
					nameof(jwtToken));
			}

			var client = CreateClient();
			client.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);
			return client;
		}

		/// <summary>
		/// Creates a <see cref="GrpcChannel"/> connected to the in-memory test
		/// server for testing the Project service's gRPC endpoints
		/// (ProjectGrpcService). The channel uses the test server's HTTP handler
		/// directly, bypassing network transport.
		/// </summary>
		/// <returns>
		/// A <see cref="GrpcChannel"/> that routes gRPC calls to the test server.
		/// Callers are responsible for disposing the returned channel when done.
		/// </returns>
		/// <remarks>
		/// The gRPC channel is created using <c>CreateDefaultClient()</c> which
		/// wraps the test server's handler. This allows gRPC messages to flow
		/// through the full ASP.NET Core middleware pipeline including
		/// authentication, authorization, and the SecurityContext bridge.
		/// </remarks>
		public GrpcChannel CreateGrpcChannel()
		{
			var client = CreateDefaultClient();
			var channel = Grpc.Net.Client.GrpcChannel.ForAddress(
				client.BaseAddress ?? new Uri("http://localhost"),
				new GrpcChannelOptions
				{
					HttpClient = client
				});
			return channel;
		}

		/// <summary>
		/// Creates a <see cref="GrpcChannel"/> with the specified JWT Bearer token
		/// pre-configured for authenticated gRPC calls against the Project service's
		/// gRPC endpoints (ProjectGrpcService).
		/// </summary>
		/// <param name="jwtToken">
		/// A valid JWT token string. The token is set as a Bearer token in the
		/// HTTP client's default request headers, which propagates to all gRPC
		/// calls made through the returned channel.
		/// </param>
		/// <returns>
		/// A <see cref="GrpcChannel"/> configured to make authenticated gRPC calls
		/// to the Project service. Callers are responsible for disposing the
		/// returned channel when done.
		/// </returns>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="jwtToken"/> is null.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="jwtToken"/> is empty or whitespace.
		/// </exception>
		public GrpcChannel CreateAuthenticatedGrpcChannel(string jwtToken)
		{
			if (jwtToken == null)
			{
				throw new ArgumentNullException(nameof(jwtToken));
			}

			if (string.IsNullOrWhiteSpace(jwtToken))
			{
				throw new ArgumentException(
					"JWT token must not be empty or whitespace.",
					nameof(jwtToken));
			}

			var client = CreateDefaultClient();
			client.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);

			var channel = Grpc.Net.Client.GrpcChannel.ForAddress(
				client.BaseAddress ?? new Uri("http://localhost"),
				new GrpcChannelOptions
				{
					HttpClient = client
				});
			return channel;
		}

		/// <summary>
		/// Removes background job hosted services from the service collection
		/// while preserving infrastructure hosted services (MassTransit bus,
		/// health checks, etc.).
		///
		/// The Project service registers <c>StartTasksOnStartDateJob</c> as a
		/// hosted service when <c>Jobs:Enabled</c> is true (Program.cs lines
		/// 375-378). Although test configuration sets this to false, this method
		/// provides defense-in-depth removal.
		///
		/// The filtering logic identifies job-related services by checking
		/// the implementation type name for job/schedule-related keywords,
		/// matching the pattern established by the Core service's
		/// <c>CoreServiceWebApplicationFactory</c>.
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
					// - StartTasksOnStartDateJob (WebVella.Erp.Service.Project.Jobs)
					// - Any other Job/Schedule-related hosted services
					// Keep MassTransit bus hosted service and any other
					// infrastructure services intact.
					if (typeName.Contains("Job", StringComparison.OrdinalIgnoreCase) ||
						typeName.Contains("ScheduleManager", StringComparison.OrdinalIgnoreCase) ||
						typeName.Contains("JobPool", StringComparison.OrdinalIgnoreCase) ||
						typeName.Contains("JobManager", StringComparison.OrdinalIgnoreCase))
					{
						services.Remove(descriptor);
					}
				}
			}
		}

		/// <summary>
		/// Releases resources used by the test server, gRPC channel, and the factory.
		/// Disposes the <see cref="GrpcChannel"/> if it was created, then calls
		/// <see cref="WebApplicationFactory{T}.Dispose(bool)"/> which stops the
		/// in-memory test server and disposes all scoped services.
		/// </summary>
		/// <param name="disposing">
		/// <c>true</c> when called from <see cref="IDisposable.Dispose"/>;
		/// <c>false</c> when called from a finalizer.
		/// </param>
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (_grpcChannel != null)
				{
					_grpcChannel.Dispose();
					_grpcChannel = null;
				}
			}
			base.Dispose(disposing);
		}
	}
}
