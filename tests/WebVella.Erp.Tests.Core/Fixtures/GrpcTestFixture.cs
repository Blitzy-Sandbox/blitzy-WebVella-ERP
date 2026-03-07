// =============================================================================
// WebVella ERP — Core Platform Service gRPC Test Infrastructure
// GrpcTestFixture.cs
// =============================================================================
// Provides gRPC test infrastructure for testing the Core Platform service's
// gRPC endpoints (EntityGrpcService, RecordGrpcService, SecurityGrpcService).
//
// This fixture wraps the CoreServiceWebApplicationFactory to:
//   - Create a GrpcChannel connected to the in-process test server
//   - Enable typed gRPC client creation via CreateGrpcClient<T>()
//   - Support JWT-authenticated gRPC calls via CreateAuthenticatedChannel()
//   - Manage gRPC channel lifecycle through xUnit's IAsyncLifetime
//
// All gRPC communication is routed through the test server's HttpClient,
// ensuring no real network connections are made during tests.
//
// Key source references:
//   - WebVella.Erp.Site/Startup.cs (lines 88-114): JWT Bearer config pattern
//   - WebVella.Erp/Api/SecurityContext.cs: OpenSystemScope() replaced by JWT
//   - WebVella.Erp/Api/Definitions.cs: SystemIds for JWT claims
//   - CoreServiceWebApplicationFactory.cs: Test server hosting and DI overrides
// =============================================================================

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;

namespace WebVella.Erp.Tests.Core.Fixtures
{
	/// <summary>
	/// gRPC test infrastructure fixture for Core Platform service integration tests.
	///
	/// <para>
	/// This fixture implements <see cref="IAsyncLifetime"/> to manage the lifecycle
	/// of a <see cref="GrpcChannel"/> connected to the in-process test server created
	/// by <see cref="CoreServiceWebApplicationFactory"/>. All gRPC communication is
	/// routed through the factory's <see cref="HttpClient"/>, ensuring no real network
	/// connections are established during test execution.
	/// </para>
	///
	/// <para>
	/// <strong>Channel Creation</strong>: The channel is created via
	/// <c>GrpcChannel.ForAddress(baseAddress, new GrpcChannelOptions { HttpClient = httpClient })</c>,
	/// which connects the gRPC channel directly to the in-memory test server.
	/// </para>
	///
	/// <para>
	/// <strong>Typed Client Creation</strong>: <see cref="CreateGrpcClient{T}"/> uses
	/// <see cref="Activator.CreateInstance(Type, object[])"/> to instantiate proto-generated
	/// gRPC client types (e.g., <c>EntityGrpcService.EntityGrpcServiceClient</c>) with
	/// the test channel.
	/// </para>
	///
	/// <para>
	/// <strong>Authenticated Calls</strong>: <see cref="CreateAuthenticatedChannel(string)"/>
	/// and <see cref="CreateAuthenticatedHttpClient(string)"/> support JWT-authenticated
	/// gRPC integration testing. JWT tokens must be generated using the same
	/// key/issuer/audience values configured in <see cref="CoreServiceWebApplicationFactory"/>
	/// (matching monolith <c>Config.json</c> lines 24-28 and <c>Startup.cs</c> lines 102-114).
	/// </para>
	///
	/// <para>
	/// <strong>Disposal</strong>: The fixture disposes the <see cref="GrpcChannel"/>
	/// and <see cref="HttpClient"/> in <see cref="DisposeAsync"/>, but does NOT dispose
	/// the <see cref="CoreServiceWebApplicationFactory"/> — that is owned by the xUnit
	/// collection fixture and has its own lifecycle.
	/// </para>
	/// </summary>
	/// <remarks>
	/// Usage in an xUnit test class:
	/// <code>
	/// public class EntityGrpcServiceTests : IClassFixture&lt;GrpcTestFixture&gt;
	/// {
	///     private readonly GrpcTestFixture _fixture;
	///
	///     public EntityGrpcServiceTests(GrpcTestFixture fixture)
	///     {
	///         _fixture = fixture;
	///     }
	///
	///     [Fact]
	///     public void CanCreateGrpcClient()
	///     {
	///         var client = _fixture.CreateGrpcClient&lt;EntityGrpcService.EntityGrpcServiceClient&gt;();
	///         Assert.NotNull(client);
	///     }
	/// }
	/// </code>
	/// </remarks>
	public class GrpcTestFixture : IAsyncLifetime
	{
		/// <summary>
		/// The underlying <see cref="CoreServiceWebApplicationFactory"/> that provides the
		/// in-process test server hosting the Core Platform microservice. This factory is
		/// injected via xUnit constructor injection and is NOT owned by this fixture —
		/// its lifecycle is managed by the xUnit collection fixture infrastructure.
		/// </summary>
		private readonly CoreServiceWebApplicationFactory _factory;

		/// <summary>
		/// HTTP client created from the factory via <see cref="CoreServiceWebApplicationFactory.CreateDefaultClient()"/>.
		/// This client is connected to the in-memory test server and serves as the transport
		/// layer for the gRPC channel. Its <see cref="HttpClient.BaseAddress"/> provides the
		/// URI for <see cref="GrpcChannel.ForAddress(Uri, GrpcChannelOptions)"/>.
		/// </summary>
		private HttpClient _httpClient;

		/// <summary>
		/// Gets the gRPC channel connected to the in-process test server.
		/// This channel is created during <see cref="InitializeAsync"/> using the factory's
		/// HTTP client as the transport layer, enabling gRPC communication without real
		/// network connections.
		///
		/// <para>
		/// Use this channel with <see cref="CreateGrpcClient{T}"/> to create typed gRPC
		/// clients, or pass it directly to proto-generated client constructors.
		/// </para>
		/// </summary>
		public GrpcChannel Channel { get; private set; }

		/// <summary>
		/// Gets the underlying <see cref="CoreServiceWebApplicationFactory"/> for additional
		/// service access. Tests can use this to resolve services from the DI container via
		/// <c>Factory.Services.GetRequiredService&lt;T&gt;()</c> for test setup, verification,
		/// or direct service invocation alongside gRPC integration tests.
		/// </summary>
		public CoreServiceWebApplicationFactory Factory => _factory;

		/// <summary>
		/// Constructs a new <see cref="GrpcTestFixture"/> wrapping the specified factory.
		/// The factory is expected to be injected by xUnit's fixture infrastructure
		/// (e.g., via <c>IClassFixture&lt;GrpcTestFixture&gt;</c> or collection fixture).
		/// </summary>
		/// <param name="factory">
		/// The <see cref="CoreServiceWebApplicationFactory"/> that provides the in-process
		/// test server. Must not be null. The factory is NOT disposed by this fixture —
		/// it is owned by the xUnit collection fixture.
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="factory"/> is null.
		/// </exception>
		public GrpcTestFixture(CoreServiceWebApplicationFactory factory)
		{
			_factory = factory ?? throw new ArgumentNullException(nameof(factory));
		}

		/// <summary>
		/// Initializes the gRPC test infrastructure by creating an HTTP client from the
		/// factory and establishing a gRPC channel connected to the in-process test server.
		///
		/// <para>
		/// This method performs the following steps:
		/// <list type="number">
		///   <item>Creates an HTTP client via <see cref="CoreServiceWebApplicationFactory.CreateDefaultClient()"/>
		///         which is connected to the in-memory test server.</item>
		///   <item>Creates a <see cref="GrpcChannel"/> using <see cref="GrpcChannel.ForAddress(Uri, GrpcChannelOptions)"/>
		///         with the HTTP client's <see cref="HttpClient.BaseAddress"/> as the target URI and
		///         the HTTP client as the transport layer via <see cref="GrpcChannelOptions.HttpClient"/>.</item>
		/// </list>
		/// </para>
		///
		/// <para>
		/// After this method completes, the <see cref="Channel"/> property is ready for use
		/// in creating typed gRPC clients via <see cref="CreateGrpcClient{T}"/>.
		/// </para>
		/// </summary>
		/// <returns>A completed task indicating initialization is finished.</returns>
		public Task InitializeAsync()
		{
			// Create an HTTP client connected to the in-process test server.
			// CreateDefaultClient() returns a client pre-configured with the test server's
			// base address and message handler, enabling HTTP communication without real
			// network connections.
			_httpClient = _factory.CreateDefaultClient();

			// Create a gRPC channel using the test server's HTTP client as the transport.
			// GrpcChannel.ForAddress with GrpcChannelOptions.HttpClient routes all gRPC
			// calls through the in-memory test server, avoiding real network connections.
			// The HttpClient.BaseAddress provides the URI that GrpcChannel.ForAddress
			// requires for channel creation.
			Channel = GrpcChannel.ForAddress(_httpClient.BaseAddress, new GrpcChannelOptions
			{
				HttpClient = _httpClient
			});

			return Task.CompletedTask;
		}

		/// <summary>
		/// Creates a typed gRPC client instance connected to the test server's channel.
		///
		/// <para>
		/// This method uses <see cref="Activator.CreateInstance(Type, object[])"/> to
		/// instantiate the proto-generated gRPC client type <typeparamref name="T"/>
		/// with the test <see cref="Channel"/>. All proto-generated gRPC clients extend
		/// <see cref="ClientBase{T}"/> and provide a constructor accepting a
		/// <see cref="ChannelBase"/> parameter.
		/// </para>
		///
		/// <para>
		/// Example usage:
		/// <code>
		/// var entityClient = fixture.CreateGrpcClient&lt;EntityGrpcService.EntityGrpcServiceClient&gt;();
		/// var recordClient = fixture.CreateGrpcClient&lt;RecordGrpcService.RecordGrpcServiceClient&gt;();
		/// var securityClient = fixture.CreateGrpcClient&lt;SecurityGrpcService.SecurityGrpcServiceClient&gt;();
		/// </code>
		/// </para>
		/// </summary>
		/// <typeparam name="T">
		/// The proto-generated gRPC client type, constrained to <see cref="ClientBase{T}"/>
		/// to ensure only valid gRPC client types are instantiated.
		/// </typeparam>
		/// <returns>A new instance of <typeparamref name="T"/> connected to the test channel.</returns>
		/// <exception cref="InvalidOperationException">
		/// Thrown when the gRPC channel has not been initialized. Call <see cref="InitializeAsync"/>
		/// before creating clients.
		/// </exception>
		public T CreateGrpcClient<T>() where T : ClientBase<T>
		{
			if (Channel == null)
			{
				throw new InvalidOperationException(
					"GrpcChannel has not been initialized. Ensure InitializeAsync() has been called " +
					"before creating gRPC clients.");
			}

			// Activator.CreateInstance instantiates the proto-generated gRPC client type
			// using its constructor that accepts a ChannelBase parameter (GrpcChannel extends
			// ChannelBase). All proto-generated clients follow this constructor pattern:
			//   public EntityGrpcServiceClient(ChannelBase channel) : base(channel) { }
			return (T)Activator.CreateInstance(typeof(T), Channel);
		}

		/// <summary>
		/// Creates an HTTP client with JWT Bearer token authentication for authenticated
		/// gRPC integration tests.
		///
		/// <para>
		/// The returned client is connected to the in-process test server (same as the
		/// default client) but includes an <c>Authorization: Bearer {jwtToken}</c> header.
		/// This enables testing gRPC endpoints that require JWT authentication, matching
		/// the Core service's JWT Bearer validation configuration.
		/// </para>
		///
		/// <para>
		/// JWT tokens must be generated using the same key, issuer, and audience values
		/// configured in <see cref="CoreServiceWebApplicationFactory"/>:
		/// <list type="bullet">
		///   <item>Key: <c>ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey</c></item>
		///   <item>Issuer: <c>webvella-erp</c></item>
		///   <item>Audience: <c>webvella-erp</c></item>
		/// </list>
		/// These values match the monolith's <c>Config.json</c> lines 24-28 and
		/// <c>Startup.cs</c> lines 102-114 JWT Bearer configuration.
		/// </para>
		///
		/// <para>
		/// <strong>Important:</strong> The caller is responsible for disposing the returned
		/// <see cref="HttpClient"/> when it is no longer needed.
		/// </para>
		/// </summary>
		/// <param name="jwtToken">
		/// A valid JWT token string. Must be signed with the test JWT key and contain
		/// claims compatible with the Core service's authorization policies. Typically
		/// generated by <c>CoreServiceFixture.GenerateAdminJwtToken()</c> or similar
		/// helper methods.
		/// </param>
		/// <returns>
		/// An <see cref="HttpClient"/> configured with the Bearer token authorization
		/// header and connected to the in-process test server.
		/// </returns>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="jwtToken"/> is null.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="jwtToken"/> is empty or whitespace.
		/// </exception>
		public HttpClient CreateAuthenticatedHttpClient(string jwtToken)
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

			// Create a new HTTP client connected to the in-process test server.
			// Each call to CreateDefaultClient() returns a fresh client instance,
			// allowing different tests to use different authentication tokens
			// without interfering with each other.
			var httpClient = _factory.CreateDefaultClient();

			// Set the Authorization header with the Bearer scheme and the provided
			// JWT token. This matches the JWT Bearer authentication pattern from
			// Startup.cs lines 102-114, where the Core service validates tokens
			// with the configured Issuer, Audience, and IssuerSigningKey.
			// The SecurityContext.OpenSystemScope() pattern from the monolith is
			// replaced by JWT tokens containing appropriate user/role claims.
			httpClient.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue("Bearer", jwtToken);

			return httpClient;
		}

		/// <summary>
		/// Creates a gRPC channel with JWT Bearer token authentication for testing
		/// authorized gRPC endpoints.
		///
		/// <para>
		/// This method combines <see cref="CreateAuthenticatedHttpClient(string)"/> with
		/// <see cref="GrpcChannel.ForAddress(Uri, GrpcChannelOptions)"/> to produce a
		/// gRPC channel that automatically sends the JWT token with every gRPC call.
		/// </para>
		///
		/// <para>
		/// Example usage:
		/// <code>
		/// var token = fixture.Factory.Services... // or CoreServiceFixture.GenerateAdminJwtToken()
		/// using var channel = fixture.CreateAuthenticatedChannel(token);
		/// var client = new EntityGrpcService.EntityGrpcServiceClient(channel);
		/// var response = await client.GetEntityAsync(request);
		/// </code>
		/// </para>
		///
		/// <para>
		/// <strong>Important:</strong> The caller is responsible for disposing the returned
		/// <see cref="GrpcChannel"/>. The underlying <see cref="HttpClient"/> is owned by
		/// the channel and will be disposed when the channel is disposed, as gRPC channels
		/// manage their HTTP client's lifecycle.
		/// </para>
		/// </summary>
		/// <param name="jwtToken">
		/// A valid JWT token string for authentication. Must be compatible with the
		/// Core service's JWT validation parameters.
		/// </param>
		/// <returns>
		/// A <see cref="GrpcChannel"/> configured with JWT authentication, connected
		/// to the in-process test server.
		/// </returns>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="jwtToken"/> is null.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="jwtToken"/> is empty or whitespace.
		/// </exception>
		public GrpcChannel CreateAuthenticatedChannel(string jwtToken)
		{
			// Create an HTTP client with the JWT Bearer authorization header.
			// The authenticated client is passed to GrpcChannelOptions.HttpClient,
			// causing all gRPC calls through this channel to include the Bearer token.
			var authenticatedHttpClient = CreateAuthenticatedHttpClient(jwtToken);

			// Create a gRPC channel using the authenticated HTTP client as transport.
			// The channel inherits the Authorization header from the HTTP client,
			// enabling JWT-authenticated gRPC communication with the test server.
			return GrpcChannel.ForAddress(authenticatedHttpClient.BaseAddress, new GrpcChannelOptions
			{
				HttpClient = authenticatedHttpClient
			});
		}

		/// <summary>
		/// Disposes gRPC channel and HTTP client resources after tests complete.
		///
		/// <para>
		/// This method disposes the <see cref="Channel"/> and the underlying
		/// <see cref="HttpClient"/> created during <see cref="InitializeAsync"/>.
		/// </para>
		///
		/// <para>
		/// <strong>Note:</strong> The <see cref="CoreServiceWebApplicationFactory"/> is
		/// NOT disposed here. It is owned by the xUnit collection fixture infrastructure
		/// (typically <c>CoreServiceFixture</c>) and has its own disposal lifecycle.
		/// Disposing the factory here would prematurely shut down the test server,
		/// breaking other test classes that share the same factory instance.
		/// </para>
		/// </summary>
		/// <returns>A completed task indicating disposal is finished.</returns>
		public Task DisposeAsync()
		{
			// Dispose the gRPC channel first, which releases channel-level resources
			// (connection pool, pending calls, etc.).
			Channel?.Dispose();

			// Dispose the HTTP client created from the factory during InitializeAsync.
			// This releases the underlying HttpMessageHandler and socket resources.
			_httpClient?.Dispose();

			// Do NOT dispose _factory here — it is owned by the xUnit collection fixture
			// (CoreServiceFixture) and shared across multiple test classes. Disposing it
			// here would prematurely stop the test server for other test classes.

			return Task.CompletedTask;
		}
	}
}
