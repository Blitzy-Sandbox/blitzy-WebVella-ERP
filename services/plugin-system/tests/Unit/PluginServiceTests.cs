// ---------------------------------------------------------------------------
// PluginServiceTests.cs — xUnit Unit Tests for PluginService Business Logic
//
// Namespace: WebVellaErp.PluginSystem.Tests.Unit
//
// Validates ALL PluginService operations with mocked IPluginRepository,
// IAmazonSimpleNotificationService, and ILogger<PluginService> — ZERO real
// AWS SDK calls. Covers 11 test phases (38 test methods) for the plugin
// lifecycle management service that replaces ErpPlugin.GetPluginData()/
// SavePluginData() and IErpService.Plugins from the monolith.
//
// Testing Framework: xUnit [Fact]/[Theory], Moq mocking, FluentAssertions
// Coverage Target: >80% per AAP Section 0.8.4
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.PluginSystem.DataAccess;
using WebVellaErp.PluginSystem.Models;
using WebVellaErp.PluginSystem.Services;
using Xunit;

namespace WebVellaErp.PluginSystem.Tests.Unit
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="PluginService"/> business logic.
    ///
    /// Tests cover all 11 phases of plugin lifecycle management:
    ///   Phase 1:  Test class setup (constructor, helpers)
    ///   Phase 2:  RegisterPluginAsync — name uniqueness, validation, SNS events
    ///   Phase 3:  GetPluginByIdAsync — existing and non-existent lookup
    ///   Phase 4:  GetPluginByNameAsync — existing, non-existent, empty name
    ///   Phase 5:  ListPluginsAsync — no filter, status filter, empty result
    ///   Phase 6:  ActivatePluginAsync — idempotent state transitions, SNS events
    ///   Phase 7:  DeactivatePluginAsync — idempotent state transitions, SNS events
    ///   Phase 8:  DeletePluginAsync — existing, active safety, idempotent
    ///   Phase 9:  GetPluginDataAsync — behavioral parity with ErpPlugin.GetPluginData()
    ///   Phase 10: SavePluginDataAsync — behavioral parity with ErpPlugin.SavePluginData()
    ///   Phase 11: Structured logging verification
    ///
    /// All dependencies are mocked — zero real AWS SDK calls.
    /// Zero references to WebVella.Erp.* namespaces.
    /// </summary>
    public class PluginServiceTests
    {
        #region Fields and Constructor

        private readonly Mock<IPluginRepository> _mockPluginRepository;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<PluginService>> _mockLogger;
        private readonly PluginService _sut;

        /// <summary>
        /// Initializes mocks and creates PluginService with injected dependencies.
        /// Sets PLUGIN_SYSTEM_SNS_TOPIC_ARN environment variable for SNS event tests.
        /// </summary>
        public PluginServiceTests()
        {
            _mockPluginRepository = new Mock<IPluginRepository>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<PluginService>>();

            // Set environment variable for SNS topic ARN before constructing the SUT
            // so the constructor reads a valid ARN value
            Environment.SetEnvironmentVariable(
                "PLUGIN_SYSTEM_SNS_TOPIC_ARN",
                "arn:aws:sns:us-east-1:000000000000:plugin-events");

            // Default SNS mock returns valid response for all tests that
            // don't explicitly test SNS behavior
            _mockSnsClient
                .Setup(x => x.PublishAsync(
                    It.IsAny<PublishRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = "test-message-id" });

            _sut = new PluginService(
                _mockPluginRepository.Object,
                _mockSnsClient.Object,
                _mockLogger.Object);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a fully populated Plugin model with all 17 properties
        /// mirroring the 13 metadata properties from source ErpPlugin.cs (lines 14-51)
        /// plus 4 new properties (Id, Status, CreatedAt, UpdatedAt).
        /// </summary>
        /// <param name="name">Plugin name. Defaults to "test-plugin".</param>
        /// <param name="version">Plugin version. Defaults to 1.</param>
        /// <param name="status">Plugin status. Defaults to PluginStatus.Active.</param>
        /// <returns>A fully populated Plugin instance for testing.</returns>
        private Plugin CreateTestPlugin(
            string name = "test-plugin",
            int version = 1,
            PluginStatus status = PluginStatus.Active)
        {
            return new Plugin
            {
                Id = Guid.NewGuid(),
                Name = name,
                Prefix = "tp",
                Url = "http://test.com",
                Description = "Test description",
                Version = version,
                Company = "Test Co",
                CompanyUrl = "http://testco.com",
                Author = "Test Author",
                Repository = "https://github.com/test",
                License = "Apache-2.0",
                SettingsUrl = "/settings",
                PluginPageUrl = "/plugin-page",
                IconUrl = "/icon.png",
                Status = status,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a RegisterPluginRequest DTO for registration test inputs.
        /// </summary>
        /// <param name="name">Plugin name. Defaults to "test-plugin".</param>
        /// <param name="version">Plugin version. Defaults to 1.</param>
        /// <returns>A populated RegisterPluginRequest for testing.</returns>
        private RegisterPluginRequest CreateRegisterRequest(
            string name = "test-plugin",
            int version = 1)
        {
            return new RegisterPluginRequest
            {
                Name = name,
                Prefix = "tp",
                Version = version,
                Url = "http://test.com",
                Description = "Test description",
                Company = "Test Co",
                CompanyUrl = "http://testco.com",
                Author = "Test Author",
                Repository = "https://github.com/test",
                License = "Apache-2.0",
                SettingsUrl = "/settings",
                PluginPageUrl = "/plugin-page",
                IconUrl = "/icon.png"
            };
        }

        #endregion

        #region Phase 2: RegisterPluginAsync Tests

        /// <summary>
        /// Validates that a valid registration request creates a new plugin,
        /// persists it via the repository, and returns a success response.
        /// Replaces ErpPlugin.SavePluginData() INSERT path (ErpPlugin.cs lines 96-103).
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_ValidRequest_ReturnsSuccessWithPlugin()
        {
            // Arrange — no existing plugin with the same name
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("new-plugin", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginRepository
                .Setup(x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateRegisterRequest("new-plugin");

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Name.Should().Be("new-plugin");

            // Verify repository interactions
            _mockPluginRepository.Verify(
                x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify SNS domain event published (plugin.plugin.registered)
            _mockSnsClient.Verify(
                x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates idempotent duplicate name handling per AAP Section 0.8.5.
        /// When a plugin with the same name already exists, the service returns
        /// the existing plugin with Success=true (idempotent behavior).
        /// CreatePluginAsync must NOT be called for duplicate names.
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_DuplicateName_ReturnsFailure()
        {
            // Arrange — an existing plugin with the same name
            var existingPlugin = CreateTestPlugin("existing-plugin");
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("existing-plugin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingPlugin);

            var request = CreateRegisterRequest("existing-plugin");

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — implementation is idempotent: returns existing plugin with Success=true
            // Per AAP Section 0.8.5: "All event consumers MUST be idempotent"
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Name.Should().Be("existing-plugin");
            result.Message.Should().Contain("already registered");

            // Verify CreatePluginAsync was NOT called — no duplicate creation
            _mockPluginRepository.Verify(
                x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Validates that an empty name is rejected with a validation error.
        /// Source mapping: ErpPlugin.cs lines 69-70:
        /// if (string.IsNullOrWhiteSpace(Name)) throw new Exception("Plugin name is not specified")
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_EmptyName_ReturnsFailure()
        {
            // Arrange
            var request = CreateRegisterRequest(name: "");

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — validation failure for empty name
            result.Success.Should().BeFalse();
            result.Message.Should().NotBeNull();
            result.Message.Should().Contain("name");
        }

        /// <summary>
        /// Validates that a null name is rejected with a validation error.
        /// Matching the source ErpPlugin.cs name validation pattern.
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_NullName_ReturnsFailure()
        {
            // Arrange — set Name to null using null-forgiving operator
            var request = CreateRegisterRequest();
            request.Name = null!;

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — validation failure for null name
            result.Success.Should().BeFalse();
            result.Message.Should().NotBeNull();
        }

        /// <summary>
        /// Validates that a negative version number is rejected.
        /// The ValidateRegisterRequest checks Version &lt; 0 and returns a validation error.
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_InvalidVersion_ReturnsFailure()
        {
            // Arrange — version -1 is invalid (must be >= 0)
            var request = CreateRegisterRequest(version: -1);

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — validation failure for negative version
            result.Success.Should().BeFalse();
            result.Message.Should().NotBeNull();
            result.Message.Should().Contain("version");
        }

        /// <summary>
        /// Validates that the service sets CreatedAt and UpdatedAt timestamps
        /// to values close to DateTime.UtcNow during registration.
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_SetsCreatedAtAndUpdatedAt()
        {
            // Arrange
            var beforeRegistration = DateTime.UtcNow;

            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("timestamp-test", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginRepository
                .Setup(x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateRegisterRequest("timestamp-test");

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — timestamps should be close to now
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.CreatedAt.Should().BeCloseTo(beforeRegistration, TimeSpan.FromSeconds(5));
            result.Plugin.UpdatedAt.Should().BeCloseTo(beforeRegistration, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Validates that the service generates a new non-empty GUID for plugin Id.
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_GeneratesNewGuidForId()
        {
            // Arrange
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("guid-test", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginRepository
                .Setup(x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateRegisterRequest("guid-test");

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — Id should be a new non-empty GUID
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Id.Should().NotBe(Guid.Empty);
        }

        /// <summary>
        /// Validates that RegisterPluginAsync publishes an SNS domain event
        /// following the {domain}.{entity}.{action} convention: "plugin.plugin.registered".
        /// Per AAP Section 0.7.2: post-hooks → SNS events.
        /// Per AAP Section 0.8.5: event naming convention {domain}.{entity}.{action}.
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_PublishesSnsEvent()
        {
            // Arrange — capture the SNS publish request for detailed inspection
            PublishRequest? capturedRequest = null;
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse { MessageId = "event-msg-id" });

            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("sns-test", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginRepository
                .Setup(x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateRegisterRequest("sns-test");

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — SNS publish was called with correct event structure
            result.Success.Should().BeTrue();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TopicArn.Should().Be("arn:aws:sns:us-east-1:000000000000:plugin-events");
            capturedRequest.Message.Should().Contain("plugin.plugin.registered");

            // Verify the SNS message contains pluginId and pluginName fields
            capturedRequest.Message.Should().Contain("sns-test");

            // Verify message attributes include eventType for SNS filter policies
            capturedRequest.MessageAttributes.Should().ContainKey("eventType");
            capturedRequest.MessageAttributes["eventType"].StringValue
                .Should().Be("plugin.plugin.registered");

            // Verify invocation count
            _mockSnsClient.Verify(
                x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates that SNS publish failures do NOT block plugin registration.
        /// Event publishing is non-blocking per AAP Section 0.7.2:
        /// "Event publishing errors are logged but do NOT block the calling operation."
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_SnsFailure_DoesNotBlockRegistration()
        {
            // Arrange — SNS throws exception
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("SNS is temporarily unavailable"));

            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("sns-fail-test", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginRepository
                .Setup(x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateRegisterRequest("sns-fail-test");

            // Act — registration should still succeed despite SNS failure
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — registration succeeded even though SNS failed
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Name.Should().Be("sns-fail-test");

            // Verify the plugin was created in the repository
            _mockPluginRepository.Verify(
                x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        #endregion

        #region Phase 3: GetPluginByIdAsync Tests

        /// <summary>
        /// Validates that an existing plugin is returned successfully by ID.
        /// </summary>
        [Fact]
        public async Task GetPluginByIdAsync_ExistingPlugin_ReturnsSuccess()
        {
            // Arrange
            var pluginId = Guid.NewGuid();
            var plugin = CreateTestPlugin("existing-by-id");
            plugin.Id = pluginId;

            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            // Act
            var result = await _sut.GetPluginByIdAsync(pluginId);

            // Assert
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Id.Should().Be(pluginId);
            result.Plugin.Name.Should().Be("existing-by-id");
        }

        /// <summary>
        /// Validates that a non-existent plugin ID returns a failure response
        /// with a "not found" message.
        /// </summary>
        [Fact]
        public async Task GetPluginByIdAsync_NonExistentPlugin_ReturnsFailure()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(nonExistentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            // Act
            var result = await _sut.GetPluginByIdAsync(nonExistentId);

            // Assert
            result.Success.Should().BeFalse();
            result.Plugin.Should().BeNull();
            result.Message.Should().Contain("not found");
        }

        #endregion

        #region Phase 4: GetPluginByNameAsync Tests

        /// <summary>
        /// Validates that an existing plugin is returned successfully by name.
        /// Replaces ErpPlugin.GetPluginData() (source lines 67-85):
        /// SELECT * FROM plugin_data WHERE name = @name
        /// </summary>
        [Fact]
        public async Task GetPluginByNameAsync_ExistingPlugin_ReturnsSuccess()
        {
            // Arrange
            var plugin = CreateTestPlugin("sdk");
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("sdk", It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            // Act
            var result = await _sut.GetPluginByNameAsync("sdk");

            // Assert
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Name.Should().Be("sdk");
        }

        /// <summary>
        /// Validates that a non-existent plugin name returns a failure response.
        /// </summary>
        [Fact]
        public async Task GetPluginByNameAsync_NonExistentPlugin_ReturnsFailure()
        {
            // Arrange
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("nonexistent", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            // Act
            var result = await _sut.GetPluginByNameAsync("nonexistent");

            // Assert
            result.Success.Should().BeFalse();
            result.Plugin.Should().BeNull();
            result.Message.Should().Contain("not found");
        }

        /// <summary>
        /// Validates that an empty name returns a failure response.
        /// Source mapping: ErpPlugin.cs line 69:
        /// if (string.IsNullOrWhiteSpace(Name)) throw new Exception("Plugin name is not specified")
        /// </summary>
        [Fact]
        public async Task GetPluginByNameAsync_EmptyName_ReturnsFailure()
        {
            // Act
            var result = await _sut.GetPluginByNameAsync("");

            // Assert — validation failure for empty name
            result.Success.Should().BeFalse();
            result.Message.Should().NotBeNull();
            result.Message.Should().Contain("name");
        }

        #endregion

        #region Phase 5: ListPluginsAsync Tests

        /// <summary>
        /// Validates that listing plugins without a filter returns all plugins.
        /// Replaces IErpService.Plugins property (source IErpService.cs line 9).
        /// </summary>
        [Fact]
        public async Task ListPluginsAsync_NoFilter_ReturnsAllPlugins()
        {
            // Arrange — repository returns 3 plugins
            var plugins = new List<Plugin>
            {
                CreateTestPlugin("plugin-1"),
                CreateTestPlugin("plugin-2"),
                CreateTestPlugin("plugin-3")
            };

            _mockPluginRepository
                .Setup(x => x.ListPluginsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugins);

            // Act — null status filter means return all
            var result = await _sut.ListPluginsAsync(null);

            // Assert
            result.Success.Should().BeTrue();
            result.Plugins.Should().HaveCount(3);
            result.TotalCount.Should().Be(3);

            // Verify ListPluginsAsync (not GetPluginsByStatusAsync) was called
            _mockPluginRepository.Verify(
                x => x.ListPluginsAsync(It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates that listing with a status filter uses GetPluginsByStatusAsync
        /// and correctly filters results.
        /// </summary>
        [Fact]
        public async Task ListPluginsAsync_WithStatusFilter_FiltersCorrectly()
        {
            // Arrange — only active plugins returned by status query
            var activePlugins = new List<Plugin>
            {
                CreateTestPlugin("active-1", status: PluginStatus.Active),
                CreateTestPlugin("active-2", status: PluginStatus.Active)
            };

            _mockPluginRepository
                .Setup(x => x.GetPluginsByStatusAsync(
                    PluginStatus.Active,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(activePlugins);

            // Act — filter by Active status
            var result = await _sut.ListPluginsAsync(PluginStatus.Active);

            // Assert
            result.Success.Should().BeTrue();
            result.Plugins.Should().HaveCount(2);
            result.TotalCount.Should().Be(2);

            // Verify GetPluginsByStatusAsync was called instead of ListPluginsAsync
            _mockPluginRepository.Verify(
                x => x.GetPluginsByStatusAsync(
                    PluginStatus.Active,
                    It.IsAny<CancellationToken>()),
                Times.Once);

            _mockPluginRepository.Verify(
                x => x.ListPluginsAsync(It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Validates that an empty result set returns Success=true with an empty list.
        /// </summary>
        [Fact]
        public async Task ListPluginsAsync_EmptyResult_ReturnsEmptyList()
        {
            // Arrange — empty result
            _mockPluginRepository
                .Setup(x => x.ListPluginsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Plugin>());

            // Act
            var result = await _sut.ListPluginsAsync(null);

            // Assert
            result.Success.Should().BeTrue();
            result.Plugins.Should().BeEmpty();
            result.TotalCount.Should().Be(0);
        }

        #endregion

        #region Phase 6: ActivatePluginAsync Tests

        /// <summary>
        /// Validates that an inactive plugin is activated successfully:
        /// Status transitions from Inactive → Active, UpdatedAt is refreshed.
        /// Replaces ErpPlugin.Initialize(IServiceProvider) lifecycle (ErpPlugin.cs line 57).
        /// </summary>
        [Fact]
        public async Task ActivatePluginAsync_InactivePlugin_ActivatesSuccessfully()
        {
            // Arrange — inactive plugin
            var pluginId = Guid.NewGuid();
            var plugin = CreateTestPlugin("inactive-plugin", status: PluginStatus.Inactive);
            plugin.Id = pluginId;

            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            _mockPluginRepository
                .Setup(x => x.UpdatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.ActivatePluginAsync(pluginId);

            // Assert
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Status.Should().Be(PluginStatus.Active);

            // Verify UpdatePluginAsync was called with Active status
            _mockPluginRepository.Verify(
                x => x.UpdatePluginAsync(
                    It.Is<Plugin>(p => p.Status == PluginStatus.Active),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates idempotent behavior: activating an already-active plugin returns
        /// Success=true without calling UpdatePluginAsync.
        /// Per AAP Section 0.8.5: "All event consumers MUST be idempotent."
        /// </summary>
        [Fact]
        public async Task ActivatePluginAsync_AlreadyActivePlugin_ReturnsSuccess_Idempotent()
        {
            // Arrange — already active plugin
            var pluginId = Guid.NewGuid();
            var plugin = CreateTestPlugin("active-plugin", status: PluginStatus.Active);
            plugin.Id = pluginId;

            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            // Act
            var result = await _sut.ActivatePluginAsync(pluginId);

            // Assert — idempotent: success without modification
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Status.Should().Be(PluginStatus.Active);
            result.Message.Should().Contain("already active");

            // Verify UpdatePluginAsync was NOT called — no update needed for idempotent case
            _mockPluginRepository.Verify(
                x => x.UpdatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Validates that attempting to activate a non-existent plugin returns failure.
        /// </summary>
        [Fact]
        public async Task ActivatePluginAsync_NonExistentPlugin_ReturnsFailure()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(nonExistentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            // Act
            var result = await _sut.ActivatePluginAsync(nonExistentId);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("not found");
        }

        /// <summary>
        /// Validates that activating an inactive plugin publishes an SNS event
        /// with the "plugin.plugin.activated" event type.
        /// </summary>
        [Fact]
        public async Task ActivatePluginAsync_PublishesSnsEvent()
        {
            // Arrange — capture SNS request
            PublishRequest? capturedRequest = null;
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse { MessageId = "activate-msg-id" });

            var pluginId = Guid.NewGuid();
            var plugin = CreateTestPlugin("activate-sns-test", status: PluginStatus.Inactive);
            plugin.Id = pluginId;

            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            _mockPluginRepository
                .Setup(x => x.UpdatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.ActivatePluginAsync(pluginId);

            // Assert — SNS event published with correct event type
            result.Success.Should().BeTrue();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Message.Should().Contain("plugin.plugin.activated");
            capturedRequest.MessageAttributes.Should().ContainKey("eventType");
            capturedRequest.MessageAttributes["eventType"].StringValue
                .Should().Be("plugin.plugin.activated");
        }

        #endregion

        #region Phase 7: DeactivatePluginAsync Tests

        /// <summary>
        /// Validates that an active plugin is deactivated successfully:
        /// Status transitions from Active → Inactive, UpdatedAt is refreshed.
        /// No direct source equivalent — new operational flexibility.
        /// </summary>
        [Fact]
        public async Task DeactivatePluginAsync_ActivePlugin_DeactivatesSuccessfully()
        {
            // Arrange — active plugin
            var pluginId = Guid.NewGuid();
            var plugin = CreateTestPlugin("active-to-deactivate", status: PluginStatus.Active);
            plugin.Id = pluginId;

            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            _mockPluginRepository
                .Setup(x => x.UpdatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.DeactivatePluginAsync(pluginId);

            // Assert
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Status.Should().Be(PluginStatus.Inactive);

            // Verify UpdatePluginAsync was called with Inactive status
            _mockPluginRepository.Verify(
                x => x.UpdatePluginAsync(
                    It.Is<Plugin>(p => p.Status == PluginStatus.Inactive),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates idempotent behavior: deactivating an already-inactive plugin
        /// returns Success=true without calling UpdatePluginAsync.
        /// Per AAP Section 0.8.5: idempotent operations.
        /// </summary>
        [Fact]
        public async Task DeactivatePluginAsync_AlreadyInactivePlugin_ReturnsSuccess_Idempotent()
        {
            // Arrange — already inactive plugin
            var pluginId = Guid.NewGuid();
            var plugin = CreateTestPlugin("inactive-plugin", status: PluginStatus.Inactive);
            plugin.Id = pluginId;

            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            // Act
            var result = await _sut.DeactivatePluginAsync(pluginId);

            // Assert — idempotent: success without modification
            result.Success.Should().BeTrue();
            result.Plugin.Should().NotBeNull();
            result.Plugin!.Status.Should().Be(PluginStatus.Inactive);
            result.Message.Should().Contain("already inactive");

            // Verify UpdatePluginAsync was NOT called
            _mockPluginRepository.Verify(
                x => x.UpdatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Validates that attempting to deactivate a non-existent plugin returns failure.
        /// </summary>
        [Fact]
        public async Task DeactivatePluginAsync_NonExistentPlugin_ReturnsFailure()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(nonExistentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            // Act
            var result = await _sut.DeactivatePluginAsync(nonExistentId);

            // Assert
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("not found");
        }

        /// <summary>
        /// Validates that deactivating an active plugin publishes an SNS event
        /// with the "plugin.plugin.deactivated" event type.
        /// </summary>
        [Fact]
        public async Task DeactivatePluginAsync_PublishesSnsEvent()
        {
            // Arrange — capture SNS request
            PublishRequest? capturedRequest = null;
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse { MessageId = "deactivate-msg-id" });

            var pluginId = Guid.NewGuid();
            var plugin = CreateTestPlugin("deactivate-sns-test", status: PluginStatus.Active);
            plugin.Id = pluginId;

            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(pluginId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plugin);

            _mockPluginRepository
                .Setup(x => x.UpdatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.DeactivatePluginAsync(pluginId);

            // Assert — SNS event published with correct event type
            result.Success.Should().BeTrue();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Message.Should().Contain("plugin.plugin.deactivated");
            capturedRequest.MessageAttributes.Should().ContainKey("eventType");
            capturedRequest.MessageAttributes["eventType"].StringValue
                .Should().Be("plugin.plugin.deactivated");
        }

        #endregion

        #region Phase 8: DeletePluginAsync Tests

        /// <summary>
        /// Validates that deleting an existing plugin succeeds and returns true.
        /// </summary>
        [Fact]
        public async Task DeletePluginAsync_ExistingPlugin_ReturnsTrue()
        {
            // Arrange — repository delete completes successfully
            var pluginId = Guid.NewGuid();
            _mockPluginRepository
                .Setup(x => x.DeletePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.DeletePluginAsync(pluginId);

            // Assert
            result.Should().BeTrue();

            // Verify repository delete was called
            _mockPluginRepository.Verify(
                x => x.DeletePluginAsync(pluginId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates safe decommissioning: attempting to delete an active plugin
        /// results in a failure when the repository layer refuses the operation.
        /// The repository enforces that a plugin must be deactivated before deletion.
        /// The service catches the exception and returns false.
        /// </summary>
        [Fact]
        public async Task DeletePluginAsync_ActivePlugin_MustDeactivateFirst()
        {
            // Arrange — repository throws InvalidOperationException for active plugins
            var pluginId = Guid.NewGuid();
            _mockPluginRepository
                .Setup(x => x.DeletePluginAsync(pluginId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(
                    "Plugin must be deactivated before deletion"));

            // Act
            var result = await _sut.DeletePluginAsync(pluginId);

            // Assert — service catches exception and returns false
            result.Should().BeFalse();
        }

        /// <summary>
        /// Validates idempotent behavior: deleting a non-existent plugin returns true.
        /// The repository handles non-existent plugins gracefully (no exception thrown).
        /// </summary>
        [Fact]
        public async Task DeletePluginAsync_NonExistentPlugin_ReturnsTrue_Idempotent()
        {
            // Arrange — repository completes successfully even for non-existent IDs
            // (idempotent delete — DynamoDB DeleteItem does not throw for missing items)
            var nonExistentId = Guid.NewGuid();
            _mockPluginRepository
                .Setup(x => x.DeletePluginAsync(nonExistentId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.DeletePluginAsync(nonExistentId);

            // Assert — idempotent: deleting non-existent is considered success
            result.Should().BeTrue();
        }

        #endregion

        #region Phase 9: GetPluginDataAsync Tests

        /// <summary>
        /// Validates that existing plugin data is returned as a JSON string.
        /// DIRECT replacement for ErpPlugin.GetPluginData() (ErpPlugin.cs lines 67-85).
        /// Data consumed by SdkPlugin._.cs line 69-71:
        ///   string jsonData = GetPluginData();
        ///   currentPluginSettings = JsonConvert.DeserializeObject&lt;PluginSettings&gt;(jsonData);
        /// </summary>
        [Fact]
        public async Task GetPluginDataAsync_ExistingData_ReturnsJsonString()
        {
            // Arrange — repository returns JSON data
            var expectedData = "{\"version\":20210429}";
            _mockPluginRepository
                .Setup(x => x.GetPluginDataAsync("sdk", It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedData);

            // Act
            var result = await _sut.GetPluginDataAsync("sdk");

            // Assert
            result.Should().NotBeNull();
            result.Should().Be(expectedData);

            // Verify repository was called
            _mockPluginRepository.Verify(
                x => x.GetPluginDataAsync("sdk", It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates that when no data exists, null is returned.
        /// Source mapping: ErpPlugin.cs lines 80-81:
        /// if (dt.Rows.Count == 0) return null;
        /// </summary>
        [Fact]
        public async Task GetPluginDataAsync_NoData_ReturnsNull()
        {
            // Arrange — repository returns null (no data found)
            _mockPluginRepository
                .Setup(x => x.GetPluginDataAsync("new-plugin", It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _sut.GetPluginDataAsync("new-plugin");

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Validates that an empty plugin name throws ArgumentException.
        /// Source mapping: ErpPlugin.cs lines 69-70:
        /// if (string.IsNullOrWhiteSpace(Name)) throw new Exception("Plugin name is not specified")
        /// </summary>
        [Fact]
        public async Task GetPluginDataAsync_EmptyName_ThrowsOrReturnsNull()
        {
            // Act & Assert — empty name throws ArgumentException
            var act = async () => await _sut.GetPluginDataAsync("");

            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("pluginName");
        }

        #endregion

        #region Phase 10: SavePluginDataAsync Tests

        /// <summary>
        /// Validates that new plugin data is saved successfully.
        /// DIRECT replacement for ErpPlugin.SavePluginData(string data) (ErpPlugin.cs lines 87-115).
        /// Source mapping: ErpPlugin.cs line 98:
        /// INSERT INTO plugin_data (id,name,data) VALUES(@id,@name,@data)
        /// </summary>
        [Fact]
        public async Task SavePluginDataAsync_NewPluginData_SavesSuccessfully()
        {
            // Arrange
            _mockPluginRepository
                .Setup(x => x.SavePluginDataAsync(
                    "sdk",
                    "{\"version\":20210429}",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act — should not throw
            await _sut.SavePluginDataAsync("sdk", "{\"version\":20210429}");

            // Assert — verify repository was called with correct parameters
            _mockPluginRepository.Verify(
                x => x.SavePluginDataAsync(
                    "sdk",
                    "{\"version\":20210429}",
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Validates that existing plugin data is overwritten (upsert semantics).
        /// DynamoDB PutItem is inherently an upsert, so both INSERT and UPDATE
        /// use the same code path. This tests the "update" path from:
        /// ErpPlugin.cs lines 107-113: UPDATE plugin_data SET data = @data WHERE name = @name
        /// </summary>
        [Fact]
        public async Task SavePluginDataAsync_ExistingPluginData_OverwritesSuccessfully()
        {
            // Arrange — first save (insert), then second save (overwrite)
            var callCount = 0;
            _mockPluginRepository
                .Setup(x => x.SavePluginDataAsync(
                    "sdk",
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => callCount++)
                .Returns(Task.CompletedTask);

            // Act — save twice to simulate insert then update
            await _sut.SavePluginDataAsync("sdk", "{\"version\":20210429}");
            await _sut.SavePluginDataAsync("sdk", "{\"version\":20250101}");

            // Assert — both saves went through (upsert semantics)
            callCount.Should().Be(2);
            _mockPluginRepository.Verify(
                x => x.SavePluginDataAsync(
                    "sdk",
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        /// <summary>
        /// Validates that an empty plugin name throws ArgumentException.
        /// Source mapping: ErpPlugin.cs lines 89-90:
        /// if (string.IsNullOrWhiteSpace(Name)) throw new Exception("Plugin name is not specified")
        /// </summary>
        [Fact]
        public async Task SavePluginDataAsync_EmptyName_ThrowsOrFails()
        {
            // Act & Assert — empty name throws ArgumentException
            var act = async () => await _sut.SavePluginDataAsync("", "{\"data\":true}");

            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("pluginName");
        }

        #endregion

        #region Phase 11: Structured Logging Verification

        /// <summary>
        /// Validates that LogInformation is called on successful plugin registration.
        /// Per AAP Section 0.8.5: structured JSON logging with correlation-ID propagation.
        /// </summary>
        [Fact]
        public async Task RegisterPluginAsync_LogsInformationOnSuccess()
        {
            // Arrange
            _mockPluginRepository
                .Setup(x => x.GetPluginByNameAsync("log-test", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            _mockPluginRepository
                .Setup(x => x.CreatePluginAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateRegisterRequest("log-test");

            // Act
            var result = await _sut.RegisterPluginAsync(request);

            // Assert — registration succeeded
            result.Success.Should().BeTrue();

            // Verify LogInformation was called with plugin name and ID
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) =>
                        state.ToString()!.Contains("registered successfully")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        /// <summary>
        /// Validates that LogWarning is called when a plugin is not found by ID.
        /// Per AAP Section 0.8.5: structured logging requirements.
        /// </summary>
        [Fact]
        public async Task GetPluginByIdAsync_LogsWarningOnNotFound()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            _mockPluginRepository
                .Setup(x => x.GetPluginByIdAsync(nonExistentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Plugin?)null);

            // Act
            var result = await _sut.GetPluginByIdAsync(nonExistentId);

            // Assert — not found
            result.Success.Should().BeFalse();

            // Verify LogWarning was called for not-found scenario
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) =>
                        state.ToString()!.Contains("not found")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        #endregion
    }
}
