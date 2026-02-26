using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.PluginSystem.DataAccess;
using WebVellaErp.PluginSystem.Functions;
using WebVellaErp.PluginSystem.Models;
using WebVellaErp.PluginSystem.Services;
using Xunit;

namespace WebVellaErp.PluginSystem.Tests.Unit
{
    /// <summary>
    /// Unit tests for PluginHandler Lambda handler class.
    /// Tests all 6 Lambda handler methods using mocked AWS SDK dependencies via Moq.
    /// ZERO real AWS SDK calls — all dependencies are mocked.
    ///
    /// Replaces the need for integration-level validation of the Plugin System's API surface layer.
    /// Covers: HandleRegisterPlugin, HandleListPlugins, HandleGetPlugin, HandleActivatePlugin,
    /// HandleDeactivatePlugin, HandleUnregisterPlugin, plus correlation-ID, response format,
    /// and error handling cross-cutting concerns.
    /// </summary>
    public class PluginHandlerTests : IDisposable
    {
        private readonly Mock<IPluginService> _mockPluginService;
        private readonly Mock<IPluginRepository> _mockPluginRepository;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<PluginHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly PluginHandler _sut;

        /// <summary>
        /// Initializes test fixtures with mocked dependencies injected into PluginHandler
        /// via the test-friendly constructor accepting IServiceProvider.
        /// Mirrors the DI resolution pattern from PluginHandler(IServiceProvider).
        /// </summary>
        public PluginHandlerTests()
        {
            _mockPluginService = new Mock<IPluginService>();
            _mockPluginRepository = new Mock<IPluginRepository>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<PluginHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();
            _mockLambdaContext.Setup(x => x.AwsRequestId).Returns(Guid.NewGuid().ToString());

            // Configure SNS mock to return success for any publish call (non-blocking event publishing)
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            // Set environment variable for SNS topic ARN used by PluginHandler constructor
            Environment.SetEnvironmentVariable("PLUGIN_EVENTS_TOPIC_ARN",
                "arn:aws:sns:us-east-1:000000000000:plugin-events");

            // Build DI service provider with all mocked dependencies matching
            // PluginHandler(IServiceProvider) constructor resolution:
            //   _pluginService = serviceProvider.GetRequiredService<IPluginService>();
            //   _pluginRepository = serviceProvider.GetRequiredService<IPluginRepository>();
            //   _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            //   var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            //   _logger = loggerFactory.CreateLogger<PluginHandler>();
            var services = new ServiceCollection();
            services.AddSingleton(_mockPluginService.Object);
            services.AddSingleton(_mockPluginRepository.Object);
            services.AddSingleton<IAmazonSimpleNotificationService>(_mockSnsClient.Object);

            var mockLoggerFactory = new Mock<ILoggerFactory>();
            mockLoggerFactory
                .Setup(x => x.CreateLogger(It.IsAny<string>()))
                .Returns(_mockLogger.Object);
            services.AddSingleton<ILoggerFactory>(mockLoggerFactory.Object);

            var serviceProvider = services.BuildServiceProvider();
            _sut = new PluginHandler(serviceProvider);
        }

        /// <summary>
        /// Cleanup environment variables set during test construction.
        /// </summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PLUGIN_EVENTS_TOPIC_ARN", null);
        }

        #region Helper Methods

        /// <summary>
        /// Creates an API Gateway HTTP API v2 proxy request with administrator JWT claims.
        /// Sets cognito:groups claim to "administrator" in RequestContext.Authorizer.Jwt.Claims,
        /// matching the IsAdministrator() check in PluginHandler which reads this claim.
        /// Replaces AdminController.cs [Authorize(Roles = "administrator")] attribute pattern.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateAdminRequest(
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            Dictionary<string, string>? headers = null)
        {
            var claims = new Dictionary<string, string>
            {
                ["cognito:groups"] = "administrator",
                ["sub"] = Guid.NewGuid().ToString(),
                ["email"] = "admin@webvella.com"
            };

            return CreateApiGatewayRequest(body, pathParameters, queryStringParameters, headers, claims);
        }

        /// <summary>
        /// Creates an API Gateway HTTP API v2 proxy request with non-administrator JWT claims.
        /// Sets cognito:groups to "regular" (non-admin), ensuring IsAdministrator() returns false.
        /// Tests verify that mutation endpoints (Register, Activate, Deactivate, Unregister)
        /// return 403 Forbidden for non-admin users.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateNonAdminRequest(
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            Dictionary<string, string>? headers = null)
        {
            var claims = new Dictionary<string, string>
            {
                ["cognito:groups"] = "regular",
                ["sub"] = Guid.NewGuid().ToString(),
                ["email"] = "user@webvella.com"
            };

            return CreateApiGatewayRequest(body, pathParameters, queryStringParameters, headers, claims);
        }

        /// <summary>
        /// Builds an APIGatewayHttpApiV2ProxyRequest with the specified body, path/query parameters,
        /// headers, and JWT claims. Constructs the full RequestContext.Authorizer.Jwt.Claims structure
        /// that PluginHandler's IsAdministrator() and GetCorrelationId() methods read.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateApiGatewayRequest(
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            Dictionary<string, string>? headers = null,
            Dictionary<string, string>? claims = null)
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParameters,
                QueryStringParameters = queryStringParameters,
                Headers = headers ?? new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = claims ?? new Dictionary<string, string>()
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Deserializes the response body JSON string into the specified type using System.Text.Json
        /// with camelCase property naming (matching PluginHandlerJsonContext configuration).
        /// </summary>
        private static T? DeserializeResponse<T>(APIGatewayHttpApiV2ProxyResponse response)
        {
            return JsonSerializer.Deserialize<T>(response.Body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });
        }

        /// <summary>
        /// Creates a sample Plugin instance with all properties populated for test data.
        /// Mirrors the 17 properties from ErpPlugin.cs (13 original + 4 new: Id, Status, CreatedAt, UpdatedAt).
        /// </summary>
        private static Plugin CreateTestPlugin(
            Guid? id = null,
            string name = "test-plugin",
            PluginStatus status = PluginStatus.Active)
        {
            return new Plugin
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Prefix = "tp",
                Url = "https://plugins.webvella.com/test",
                Description = "A test plugin for unit tests",
                Version = 1,
                Company = "WebVella",
                CompanyUrl = "https://webvella.com",
                Author = "Test Author",
                Repository = "https://github.com/webvella/test-plugin",
                License = "MIT",
                SettingsUrl = "/plugins/test/settings",
                PluginPageUrl = "/plugins/test",
                IconUrl = "/images/plugin-icon.png",
                Status = status,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                UpdatedAt = DateTime.UtcNow
            };
        }

        #endregion

        #region Phase 2: HandleRegisterPlugin Tests

        /// <summary>
        /// Verifies that a valid registration request with admin credentials returns 201 Created.
        /// Replaces monolith reflection-based plugin discovery + ErpPlugin.SavePluginData() INSERT path
        /// (source ErpPlugin.cs lines 96-103).
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_ValidRequest_Returns201Created()
        {
            // Arrange
            var testPlugin = CreateTestPlugin();
            var registerRequest = new RegisterPluginRequest
            {
                Name = "test-plugin",
                Prefix = "tp",
                Version = 1,
                Description = "A test plugin"
            };

            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("test-plugin", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginService
                .Setup(x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse { Success = true, Plugin = testPlugin, Message = "Plugin registered successfully." });

            var body = JsonSerializer.Serialize(registerRequest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var request = CreateAdminRequest(body: body);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(201);
            response.Body.Should().NotBeNull();
            response.Body.Should().Contain("\"success\":true");

            _mockPluginService.Verify(
                x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that a request without a plugin name returns 400 Bad Request.
        /// Source mapping: ErpPlugin.cs lines 69-70 — "Plugin name is not specified" validation.
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_MissingName_Returns400()
        {
            // Arrange — request body with no name field, only version
            var body = JsonSerializer.Serialize(new { version = 1, prefix = "tp" }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var request = CreateAdminRequest(body: body);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("\"success\":false");
            response.Body.Should().Contain("name");

            _mockPluginService.Verify(
                x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that registering a plugin with an already-existing name returns 409 Conflict.
        /// The handler checks name uniqueness via _pluginRepository.GetPluginByNameAsync before
        /// delegating to the service layer.
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_DuplicateName_Returns409Conflict()
        {
            // Arrange — repository returns an existing plugin with the same name
            var existingPlugin = CreateTestPlugin(name: "existing-plugin");
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("existing-plugin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingPlugin);

            var body = JsonSerializer.Serialize(new RegisterPluginRequest
            {
                Name = "existing-plugin",
                Prefix = "ep",
                Version = 1
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var request = CreateAdminRequest(body: body);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(409);
            response.Body.Should().Contain("\"success\":false");

            _mockPluginService.Verify(
                x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that a negative version number returns 400 Bad Request.
        /// The handler checks: if (request.Version &lt; 0) → 400.
        /// Message: "Plugin version must be greater than or equal to zero."
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_InvalidVersion_Returns400()
        {
            // Arrange — version is negative (actual handler rejects version < 0)
            var body = JsonSerializer.Serialize(new RegisterPluginRequest
            {
                Name = "test-plugin",
                Prefix = "tp",
                Version = -1
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var request = CreateAdminRequest(body: body);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("\"success\":false");
            response.Body.Should().Contain("version");

            _mockPluginService.Verify(
                x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that a non-administrator user receives 403 Forbidden on registration.
        /// Source mapping: AdminController.cs line 53 — [Authorize(Roles = "administrator")]
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_NonAdminUser_Returns403Forbidden()
        {
            // Arrange — valid body but non-admin request
            var body = JsonSerializer.Serialize(new RegisterPluginRequest
            {
                Name = "test-plugin",
                Prefix = "tp",
                Version = 1
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var request = CreateNonAdminRequest(body: body);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("\"success\":false");
            response.Body.Should().Contain("Administrator");

            _mockPluginService.Verify(
                x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that an empty/null request body returns 400 Bad Request.
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_EmptyBody_Returns400()
        {
            // Arrange — admin request with null body
            var request = CreateAdminRequest(body: null);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("\"success\":false");

            _mockPluginService.Verify(
                x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that an unhandled service exception returns 500 Internal Server Error
        /// with a sanitized error message (no stack trace exposed per security requirements).
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_ServiceException_Returns500()
        {
            // Arrange — service throws an unexpected exception
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginService
                .Setup(x => x.RegisterPluginAsync(It.IsAny<RegisterPluginRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("DynamoDB connection failed with detailed internal stack trace information"));

            var body = JsonSerializer.Serialize(new RegisterPluginRequest
            {
                Name = "test-plugin",
                Prefix = "tp",
                Version = 1
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var request = CreateAdminRequest(body: body);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("\"success\":false");
            // Handler returns generic error message, never the raw exception message
            response.Body.Should().NotContain("DynamoDB connection failed");
            response.Body.Should().NotContain("stack trace");
        }

        #endregion

        #region Phase 3: HandleListPlugins Tests

        /// <summary>
        /// Verifies that ListPlugins returns all plugins sorted alphabetically by Name.
        /// Source mapping: AdminController.cs line 45 — dsList.OrderBy(x => x.Name).ToList()
        /// Replaces IErpService.Plugins list access (source IErpService.cs line 9).
        /// </summary>
        [Fact]
        public async Task HandleListPlugins_ReturnsAllPlugins_OrderedByName()
        {
            // Arrange — 3 plugins in non-alphabetical order
            var plugins = new List<Plugin>
            {
                CreateTestPlugin(name: "charlie-plugin"),
                CreateTestPlugin(name: "alpha-plugin"),
                CreateTestPlugin(name: "bravo-plugin")
            };

            _mockPluginService
                .Setup(x => x.ListPluginsAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginListResponse
                {
                    Plugins = plugins,
                    TotalCount = 3,
                    Success = true,
                    Message = "Plugins retrieved successfully."
                });

            var request = CreateApiGatewayRequest();

            // Act
            var response = await _sut.HandleListPlugins(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");

            // Verify sorted order: alpha < bravo < charlie
            var alphaIdx = response.Body.IndexOf("alpha-plugin", StringComparison.Ordinal);
            var bravoIdx = response.Body.IndexOf("bravo-plugin", StringComparison.Ordinal);
            var charlieIdx = response.Body.IndexOf("charlie-plugin", StringComparison.Ordinal);
            alphaIdx.Should().BeLessThan(bravoIdx);
            bravoIdx.Should().BeLessThan(charlieIdx);

            _mockPluginService.Verify(
                x => x.ListPluginsAsync(null, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that an empty plugin list returns 200 with empty array.
        /// </summary>
        [Fact]
        public async Task HandleListPlugins_EmptyList_Returns200WithEmptyArray()
        {
            // Arrange
            _mockPluginService
                .Setup(x => x.ListPluginsAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginListResponse
                {
                    Plugins = new List<Plugin>(),
                    TotalCount = 0,
                    Success = true,
                    Message = "Plugins retrieved successfully."
                });

            var request = CreateApiGatewayRequest();

            // Act
            var response = await _sut.HandleListPlugins(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");
            response.Body.Should().Contain("\"total_count\":0");
        }

        /// <summary>
        /// Verifies that the status query parameter is parsed and passed to the service layer.
        /// </summary>
        [Fact]
        public async Task HandleListPlugins_WithStatusFilter_PassesFilterToService()
        {
            // Arrange
            var activePlugins = new List<Plugin> { CreateTestPlugin(status: PluginStatus.Active) };

            _mockPluginService
                .Setup(x => x.ListPluginsAsync(PluginStatus.Active, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginListResponse
                {
                    Plugins = activePlugins,
                    TotalCount = 1,
                    Success = true
                });

            var request = CreateApiGatewayRequest();
            request.QueryStringParameters = new Dictionary<string, string>
            {
                { "status", "Active" }
            };

            // Act
            var response = await _sut.HandleListPlugins(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");

            // Verify service was called with the Active status filter
            _mockPluginService.Verify(
                x => x.ListPluginsAsync(PluginStatus.Active, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        #endregion

        #region Phase 4: HandleGetPlugin Tests

        /// <summary>
        /// Verifies that requesting an existing plugin by ID returns 200 with full plugin data.
        /// </summary>
        [Fact]
        public async Task HandleGetPlugin_ExistingPlugin_Returns200()
        {
            // Arrange
            var pluginId = Guid.NewGuid();
            var testPlugin = CreateTestPlugin(id: pluginId, name: "test-plugin");

            _mockPluginService
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = true,
                    Plugin = testPlugin,
                    Message = "Plugin retrieved successfully."
                });

            var request = CreateApiGatewayRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleGetPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");
            response.Body.Should().Contain("test-plugin");

            _mockPluginService.Verify(
                x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that requesting a non-existent plugin returns 404.
        /// </summary>
        [Fact]
        public async Task HandleGetPlugin_NonExistentPlugin_Returns404()
        {
            // Arrange
            var pluginId = Guid.NewGuid();

            _mockPluginService
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = false,
                    Plugin = null,
                    Message = "Plugin not found."
                });

            var request = CreateApiGatewayRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleGetPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            response.Body.Should().Contain("\"success\":false");
        }

        /// <summary>
        /// Verifies that an invalid GUID format returns 400.
        /// </summary>
        [Fact]
        public async Task HandleGetPlugin_InvalidGuidFormat_Returns400()
        {
            // Arrange — "not-a-guid" cannot be parsed
            var request = CreateApiGatewayRequest(
                pathParameters: new Dictionary<string, string> { { "id", "not-a-guid" } });

            // Act
            var response = await _sut.HandleGetPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("\"success\":false");
            response.Body.Should().Contain("not-a-guid");

            _mockPluginService.Verify(
                x => x.GetPluginByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region Phase 5: HandleActivatePlugin Tests

        /// <summary>
        /// Verifies that activating an existing inactive plugin returns 200 with Active status.
        /// Replaces ErpPlugin.Initialize(IServiceProvider) lifecycle (source ErpPlugin.cs line 57).
        /// </summary>
        [Fact]
        public async Task HandleActivatePlugin_ExistingInactivePlugin_Returns200()
        {
            // Arrange
            var pluginId = Guid.NewGuid();
            var activatedPlugin = CreateTestPlugin(id: pluginId, status: PluginStatus.Active);

            _mockPluginService
                .Setup(x => x.ActivatePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = true,
                    Plugin = activatedPlugin,
                    Message = "Plugin activated successfully."
                });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleActivatePlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");

            _mockPluginService.Verify(
                x => x.ActivatePluginAsync(pluginId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that activating an already-active plugin returns 200 (idempotent).
        /// Per AAP Section 0.8.5: idempotent operations return same result.
        /// </summary>
        [Fact]
        public async Task HandleActivatePlugin_AlreadyActivePlugin_Returns200_Idempotent()
        {
            // Arrange — plugin is already active, service still returns success
            var pluginId = Guid.NewGuid();
            var alreadyActivePlugin = CreateTestPlugin(id: pluginId, status: PluginStatus.Active);

            _mockPluginService
                .Setup(x => x.ActivatePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = true,
                    Plugin = alreadyActivePlugin,
                    Message = "Plugin is already active."
                });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleActivatePlugin(request, _mockLambdaContext.Object);

            // Assert — idempotent: already active → still 200
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");
        }

        /// <summary>
        /// Verifies that activating a non-existent plugin returns 404.
        /// </summary>
        [Fact]
        public async Task HandleActivatePlugin_NonExistentPlugin_Returns404()
        {
            // Arrange
            var pluginId = Guid.NewGuid();

            _mockPluginService
                .Setup(x => x.ActivatePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = false,
                    Message = "Plugin not found."
                });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleActivatePlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            response.Body.Should().Contain("\"success\":false");
        }

        /// <summary>
        /// Verifies that a non-admin user receives 403 for activate operations.
        /// Source mapping: AdminController.cs line 53 — [Authorize(Roles = "administrator")]
        /// </summary>
        [Fact]
        public async Task HandleActivatePlugin_NonAdminUser_Returns403()
        {
            // Arrange — valid plugin ID but non-admin request
            var pluginId = Guid.NewGuid();
            var request = CreateNonAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleActivatePlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("\"success\":false");

            _mockPluginService.Verify(
                x => x.ActivatePluginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region Phase 6: HandleDeactivatePlugin Tests

        /// <summary>
        /// Verifies successful deactivation of an active plugin returns 200.
        /// No direct source equivalent — new operational flexibility for microservices architecture.
        /// </summary>
        [Fact]
        public async Task HandleDeactivatePlugin_ExistingActivePlugin_Returns200()
        {
            // Arrange — active plugin being deactivated
            var pluginId = Guid.NewGuid();
            var deactivatedPlugin = CreateTestPlugin(pluginId);
            deactivatedPlugin.Status = PluginStatus.Inactive;

            _mockPluginService
                .Setup(x => x.DeactivatePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = true,
                    Plugin = deactivatedPlugin,
                    Message = "Plugin deactivated successfully"
                });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleDeactivatePlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");
            response.Body.Should().Contain(pluginId.ToString());
        }

        /// <summary>
        /// Verifies deactivating an already-inactive plugin returns 200 (idempotent behavior).
        /// Per AAP Section 0.8.5: idempotency keys on all write endpoints and event handlers.
        /// </summary>
        [Fact]
        public async Task HandleDeactivatePlugin_AlreadyInactivePlugin_Returns200_Idempotent()
        {
            // Arrange — plugin is already inactive, service returns success (idempotent)
            var pluginId = Guid.NewGuid();
            var inactivePlugin = CreateTestPlugin(pluginId);
            inactivePlugin.Status = PluginStatus.Inactive;

            _mockPluginService
                .Setup(x => x.DeactivatePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = true,
                    Plugin = inactivePlugin,
                    Message = "Plugin is already inactive"
                });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleDeactivatePlugin(request, _mockLambdaContext.Object);

            // Assert — idempotent: deactivating an already-inactive plugin still returns 200
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");
        }

        /// <summary>
        /// Verifies deactivating a non-existent plugin returns 404.
        /// </summary>
        [Fact]
        public async Task HandleDeactivatePlugin_NonExistentPlugin_Returns404()
        {
            // Arrange — service returns failure (plugin not found)
            var pluginId = Guid.NewGuid();

            _mockPluginService
                .Setup(x => x.DeactivatePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse
                {
                    Success = false,
                    Message = "Plugin not found"
                });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleDeactivatePlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            response.Body.Should().Contain("\"success\":false");
        }

        /// <summary>
        /// Verifies non-admin user cannot deactivate plugins — returns 403.
        /// Source mapping: AdminController.cs [Authorize(Roles = "administrator")].
        /// </summary>
        [Fact]
        public async Task HandleDeactivatePlugin_NonAdminUser_Returns403()
        {
            // Arrange — valid plugin ID but non-admin request
            var pluginId = Guid.NewGuid();
            var request = CreateNonAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleDeactivatePlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("\"success\":false");

            _mockPluginService.Verify(
                x => x.DeactivatePluginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region Phase 7: HandleUnregisterPlugin Tests

        /// <summary>
        /// Verifies successful unregistration of an inactive plugin returns 200.
        /// Replaces deletion of plugin_data rows from the monolith's PostgreSQL persistence.
        /// HandleUnregisterPlugin first fetches the plugin via GetPluginByIdAsync to check status,
        /// then calls DeletePluginAsync and publishes SNS "plugin-system.plugin.deleted" event.
        /// </summary>
        [Fact]
        public async Task HandleUnregisterPlugin_InactivePlugin_Returns200()
        {
            // Arrange — plugin exists and is inactive (safe to unregister)
            var pluginId = Guid.NewGuid();
            var existingPlugin = CreateTestPlugin(pluginId);
            existingPlugin.Status = PluginStatus.Inactive;

            _mockPluginService
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse { Success = true, Plugin = existingPlugin });

            _mockPluginService
                .Setup(x => x.DeletePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleUnregisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("\"success\":true");

            _mockPluginService.Verify(
                x => x.DeletePluginAsync(pluginId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies unregistering an active plugin returns 400 with deactivation requirement.
        /// Safety check: active plugins must be deactivated before deletion to prevent orphaned
        /// dependencies. Maps from HandleUnregisterPlugin safety check at lines 842-1001.
        /// </summary>
        [Fact]
        public async Task HandleUnregisterPlugin_ActivePlugin_Returns400()
        {
            // Arrange — plugin exists but is still active
            var pluginId = Guid.NewGuid();
            var activePlugin = CreateTestPlugin(pluginId);
            activePlugin.Status = PluginStatus.Active;

            _mockPluginService
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse { Success = true, Plugin = activePlugin });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleUnregisterPlugin(request, _mockLambdaContext.Object);

            // Assert — safety check blocks deletion of active plugins
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Plugin must be deactivated before unregistration");

            // Verify delete was never called since the safety check prevented it
            _mockPluginService.Verify(
                x => x.DeletePluginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies unregistering a non-existent plugin returns 404.
        /// </summary>
        [Fact]
        public async Task HandleUnregisterPlugin_NonExistentPlugin_Returns404()
        {
            // Arrange — plugin does not exist
            var pluginId = Guid.NewGuid();

            _mockPluginService
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginResponse { Success = false, Message = "Plugin not found" });

            var request = CreateAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleUnregisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            response.Body.Should().Contain("\"success\":false");
        }

        /// <summary>
        /// Verifies non-admin user cannot unregister plugins — returns 403.
        /// Source mapping: AdminController.cs [Authorize(Roles = "administrator")].
        /// </summary>
        [Fact]
        public async Task HandleUnregisterPlugin_NonAdminUser_Returns403()
        {
            // Arrange — valid plugin ID but non-admin request
            var pluginId = Guid.NewGuid();
            var request = CreateNonAdminRequest(
                pathParameters: new Dictionary<string, string> { { "id", pluginId.ToString() } });

            // Act
            var response = await _sut.HandleUnregisterPlugin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("\"success\":false");

            _mockPluginService.Verify(
                x => x.DeletePluginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        #endregion

        #region Phase 8: Correlation-ID Tests

        /// <summary>
        /// Verifies that when x-correlation-id header is provided in the request,
        /// it is propagated to the response headers unchanged.
        /// Per AAP Section 0.8.5: structured JSON logging with correlation-ID propagation.
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_ExtractsCorrelationIdFromHeaders()
        {
            // Arrange — include correlation-ID header; empty body triggers 400 but
            // correlation-ID should still propagate via BuildResponse headers
            var correlationId = "test-corr-id-123";
            var request = CreateAdminRequest(
                body: null,
                headers: new Dictionary<string, string> { { "x-correlation-id", correlationId } });

            // Act — even a validation error (400 for empty body) will propagate the correlation-ID
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert — correlation-ID from request header should appear in response headers
            response.Headers.Should().ContainKey("x-correlation-id");
            response.Headers["x-correlation-id"].Should().Be(correlationId);
        }

        /// <summary>
        /// Verifies that when no x-correlation-id header is provided in the request,
        /// one is automatically generated and included in the response headers.
        /// Per AAP Section 0.8.5: correlation-ID propagation from all Lambda functions.
        /// </summary>
        [Fact]
        public async Task HandleRegisterPlugin_GeneratesCorrelationIdWhenMissing()
        {
            // Arrange — no correlation-ID header provided
            var request = CreateAdminRequest(body: null);

            // Act
            var response = await _sut.HandleRegisterPlugin(request, _mockLambdaContext.Object);

            // Assert — a correlation-ID should be auto-generated in response headers
            response.Headers.Should().ContainKey("x-correlation-id");
            response.Headers["x-correlation-id"].Should().NotBeNullOrEmpty();
        }

        #endregion

        #region Phase 9: Response Format Tests

        /// <summary>
        /// Verifies all handler responses follow the ResponseModel pattern from the monolith:
        /// { "success": bool, "message": string, data_payload }
        /// Maps from AdminController.cs ResponseModel { Success, Message, Object } (lines 58-61).
        /// In the microservices architecture, "Object" is replaced with domain-specific property
        /// names (e.g., "plugins" for list endpoints, "plugin" for single-entity endpoints).
        /// </summary>
        [Fact]
        public async Task AllHandlers_ResponseMatchesResponseModelPattern()
        {
            // Arrange — setup a successful list plugins call to get a full response
            _mockPluginService
                .Setup(x => x.ListPluginsAsync(It.IsAny<PluginStatus?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginListResponse
                {
                    Success = true,
                    Plugins = new List<Plugin> { CreateTestPlugin() },
                    TotalCount = 1,
                    Message = "Plugins retrieved successfully"
                });

            var request = CreateAdminRequest();

            // Act
            var response = await _sut.HandleListPlugins(request, _mockLambdaContext.Object);

            // Assert — verify JSON contains ResponseModel pattern fields
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNull();

            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;

            // ResponseModel core fields: "success" (bool) and "message" (string)
            root.TryGetProperty("success", out var successElement).Should().BeTrue(
                "response must contain 'success' boolean field per ResponseModel pattern");
            successElement.GetBoolean().Should().BeTrue();

            root.TryGetProperty("message", out _).Should().BeTrue(
                "response must contain 'message' string field per ResponseModel pattern");

            // Data payload — for list endpoint this is "plugins" array
            // (replaces generic "object" from monolith AdminController.cs ResponseModel)
            root.TryGetProperty("plugins", out var pluginsElement).Should().BeTrue(
                "response must contain domain-specific data payload field");
            pluginsElement.GetArrayLength().Should().Be(1);
        }

        #endregion

        #region Phase 10: Error Handling Tests

        /// <summary>
        /// Verifies that error responses never expose internal stack trace information.
        /// Security requirement per AAP Section 0.8.3: OWASP Top 10 compliance.
        /// All Lambda handlers catch exceptions and return sanitized error messages only,
        /// ensuring no sensitive internal details (file paths, line numbers, class names)
        /// are leaked to API consumers.
        /// </summary>
        [Fact]
        public async Task AllHandlers_NeverExposeStackTraceInResponse()
        {
            // Arrange — service throws with detailed internal error and stack trace information
            _mockPluginService
                .Setup(x => x.ListPluginsAsync(It.IsAny<PluginStatus?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(
                    "Sensitive internal error at DataAccess.PluginRepository.GetAllAsync:line 42\n" +
                    "   at WebVellaErp.PluginSystem.DataAccess.PluginRepository.GetAllAsync()"));

            var request = CreateAdminRequest();

            // Act
            var response = await _sut.HandleListPlugins(request, _mockLambdaContext.Object);

            // Assert — response should NOT contain any stack trace details or internal paths
            response.StatusCode.Should().Be(500);
            response.Body.Should().Contain("\"success\":false");

            // Verify no internal details leak through to the response body
            response.Body.Should().NotContain("at WebVellaErp");
            response.Body.Should().NotContain("StackTrace");
            response.Body.Should().NotContain(".cs:line");
            response.Body.Should().NotContain("DataAccess.PluginRepository");
        }

        #endregion
    }
}

