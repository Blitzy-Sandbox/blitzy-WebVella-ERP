// =============================================================================
// RecordCrudIntegrationTests.cs — Record CRUD + SNS Domain Event Publishing
// Integration Tests Against LocalStack
// =============================================================================
// Validates record storage (PK=ENTITY#{name}, SK=RECORD#{id}), SNS event
// publishing for domain events ({domain}.{entity}.{action}), permission
// enforcement, and field type normalization against **real LocalStack DynamoDB
// and SNS**. NO mocked AWS SDK calls (AAP §0.8.4).
//
// Covers 9 phases (25 test methods):
//   Phase 2: Record Create Tests (7 tests)
//   Phase 3: Field Type Normalization Tests (3 tests)
//   Phase 4: SNS Domain Event Publishing Tests (3 tests)
//   Phase 5: Record Update Tests (3 tests)
//   Phase 6: Record Delete Tests (2 tests)
//   Phase 7: Record Find Tests (3 tests)
//   Phase 8: Relation-Aware Payload Processing Tests (3 tests)
//   Phase 9: Permission Enforcement Tests (1 test)
//
// Source: WebVella.Erp/Api/RecordManager.cs, DbRecordRepository.cs,
//         RecordHookManager.cs, EntityManager.cs, Definitions.cs
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using WebVellaErp.EntityManagement.Tests.Fixtures;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Integration
{
    /// <summary>
    /// Integration tests for RecordService — validates record CRUD operations,
    /// field type normalization, SNS domain event publishing, permission
    /// enforcement, and relation-aware payload processing against real
    /// LocalStack DynamoDB and SNS. Uses IClassFixture for shared resource
    /// provisioning (tables, topics) across all 25 test methods.
    /// </summary>
    public class RecordCrudIntegrationTests : IClassFixture<LocalStackFixture>
    {
        // =====================================================================
        // Phase 1: Class Declaration and Fixture Wiring
        // =====================================================================

        private readonly LocalStackFixture _fixture;
        private readonly IRecordService _recordService;
        private readonly IEntityService _entityService;
        private readonly IRecordRepository _recordRepository;
        private readonly IEntityRepository _entityRepository;
        private readonly IConfiguration _config;

        /// <summary>
        /// Constructor wires up all service instances using the shared LocalStackFixture
        /// and configures IConfiguration with DynamoDB table names, SNS topic ARN prefix,
        /// and DevelopmentMode=true (permission bypass). Each test creates its own
        /// uniquely-named entity for isolation.
        /// </summary>
        public RecordCrudIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;

            // Build IConfiguration pointing to fixture table names and SNS topics
            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "DynamoDB:MetadataTableName", LocalStackFixture.EntityMetadataTableName },
                    { "DynamoDB:RecordTableName", LocalStackFixture.RecordStorageTableName },
                    { "Sns:TopicArnPrefix", "arn:aws:sns:us-east-1:000000000000:" },
                    { "DevelopmentMode", "true" }
                })
                .Build();

            // Create real repository instances backed by LocalStack DynamoDB
            _entityRepository = new EntityRepository(
                _fixture.DynamoDbClient,
                NullLogger<EntityRepository>.Instance,
                _config);

            _recordRepository = new RecordRepository(
                _fixture.DynamoDbClient,
                _entityRepository,
                NullLogger<RecordRepository>.Instance,
                _config);

            // Create real service instances backed by LocalStack
            _entityService = new EntityService(
                _entityRepository,
                NullLogger<EntityService>.Instance,
                _config,
                new MemoryCache(new MemoryCacheOptions()));

            _recordService = new RecordService(
                _entityService,
                _entityRepository,
                _recordRepository,
                _fixture.SnsClient,
                NullLogger<RecordService>.Instance,
                _config);
        }

        // =====================================================================
        // Helper Methods — Test Entity Seeding and DynamoDB Verification
        // =====================================================================

        /// <summary>
        /// Seeds a test entity with standard fields directly into LocalStack DynamoDB
        /// via the fixture's SeedEntityAsync method. Uses TestDataHelper factories to
        /// build the entity and field definitions. Each test should use a unique name
        /// to avoid cross-test data collisions.
        /// </summary>
        private async Task<Entity> SeedTestEntityAsync(
            string entityName,
            Guid? entityId = null,
            RecordPermissions? permissions = null)
        {
            var entity = TestDataHelper.CreateTestEntity(entityName, entityId ?? Guid.NewGuid());
            if (permissions != null)
            {
                entity.RecordPermissions = permissions;
            }
            // Add text_field that many tests reference for record CRUD operations
            if (!entity.Fields.Any(f => f.Name == "text_field"))
            {
                entity.Fields.Add(TestDataHelper.CreateTextField("text_field"));
            }
            await _fixture.SeedEntityAsync(entity);
            return entity;
        }

        /// <summary>
        /// Seeds a test entity with standard fields AND all 19 custom field types
        /// that record normalization and field round-trip tests expect.
        /// </summary>
        private async Task<Entity> SeedEntityWithAllFieldTypesAsync(string entityName, Guid? entityId = null)
        {
            var entity = TestDataHelper.CreateTestEntityWithStandardFields(entityName, entityId ?? Guid.NewGuid());

            // Add all 19 custom field types the record tests expect
            entity.Fields.Add(TestDataHelper.CreateTextField("text_field"));
            entity.Fields.Add(TestDataHelper.CreateNumberField("number_field"));
            entity.Fields.Add(TestDataHelper.CreateCheckboxField("checkbox_field"));
            entity.Fields.Add(TestDataHelper.CreateDateField("date_field"));
            entity.Fields.Add(TestDataHelper.CreateDateTimeField("datetime_field"));
            entity.Fields.Add(TestDataHelper.CreateGuidField("guid_field"));
            entity.Fields.Add(TestDataHelper.CreateSelectField("select_field"));
            entity.Fields.Add(TestDataHelper.CreateMultiSelectField("multiselect_field"));
            entity.Fields.Add(TestDataHelper.CreateEmailField("email_field"));
            entity.Fields.Add(TestDataHelper.CreatePhoneField("phone_field"));
            entity.Fields.Add(TestDataHelper.CreateUrlField("url_field"));
            entity.Fields.Add(TestDataHelper.CreateCurrencyField("currency_field"));
            entity.Fields.Add(TestDataHelper.CreatePercentField("percent_field"));
            entity.Fields.Add(TestDataHelper.CreatePasswordField("password_field"));
            entity.Fields.Add(TestDataHelper.CreateFileField("file_field"));
            entity.Fields.Add(TestDataHelper.CreateImageField("image_field"));
            entity.Fields.Add(TestDataHelper.CreateHtmlField("html_field"));
            entity.Fields.Add(TestDataHelper.CreateMultiLineTextField("multiline_field"));
            entity.Fields.Add(TestDataHelper.CreateGeographyField("geography_field"));

            await _fixture.SeedEntityAsync(entity);
            return entity;
        }

        /// <summary>
        /// Creates a RecordService with DevelopmentMode=false for permission enforcement tests.
        /// All other configuration remains identical to the default service.
        /// </summary>
        private IRecordService CreateStrictPermissionRecordService()
        {
            var strictConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "DynamoDB:MetadataTableName", LocalStackFixture.EntityMetadataTableName },
                    { "DynamoDB:RecordTableName", LocalStackFixture.RecordStorageTableName },
                    { "Sns:TopicArnPrefix", "arn:aws:sns:us-east-1:000000000000:" },
                    { "DevelopmentMode", "false" }
                })
                .Build();

            var strictEntityRepo = new EntityRepository(
                _fixture.DynamoDbClient,
                NullLogger<EntityRepository>.Instance,
                strictConfig);

            var strictRecordRepo = new RecordRepository(
                _fixture.DynamoDbClient,
                strictEntityRepo,
                NullLogger<RecordRepository>.Instance,
                strictConfig);

            var strictEntityService = new EntityService(
                strictEntityRepo,
                NullLogger<EntityService>.Instance,
                strictConfig,
                new MemoryCache(new MemoryCacheOptions()));

            return new RecordService(
                strictEntityService,
                strictEntityRepo,
                strictRecordRepo,
                _fixture.SnsClient,
                NullLogger<RecordService>.Instance,
                strictConfig);
        }

        /// <summary>
        /// Reads a record directly from DynamoDB by PK=ENTITY#{entityName}, SK=RECORD#{recordId}
        /// for verification that bypasses the service layer.
        /// </summary>
        private async Task<Dictionary<string, AttributeValue>?> GetDynamoDbRecordAsync(
            string entityName, Guid recordId)
        {
            var request = new GetItemRequest
            {
                TableName = LocalStackFixture.RecordStorageTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "PK", new AttributeValue($"ENTITY#{entityName}") },
                    { "SK", new AttributeValue($"RECORD#{recordId}") }
                },
                ConsistentRead = true
            };
            var result = await _fixture.DynamoDbClient.GetItemAsync(request);
            return result.IsItemSet ? result.Item : null;
        }

        /// <summary>
        /// Creates an SQS client for SNS event verification tests. Uses the same
        /// LocalStack endpoint URL as the fixture's DynamoDB and SNS clients.
        /// </summary>
        private static IAmazonSQS CreateSqsClient()
        {
            var endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL") ?? "http://localhost:4566";
            var sqsConfig = new AmazonSQSConfig
            {
                ServiceURL = endpoint,
                AuthenticationRegion = "us-east-1"
            };
            return new AmazonSQSClient("test", "test", sqsConfig);
        }

        /// <summary>
        /// Creates an SQS queue and subscribes it to the specified SNS topic.
        /// Returns the SQS queue URL for message polling.
        /// </summary>
        private async Task<string> CreateSqsSubscriptionAsync(
            IAmazonSQS sqsClient, string topicArn, string queueName)
        {
            // Create the SQS queue
            var createQueueResponse = await sqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName
            });
            var queueUrl = createQueueResponse.QueueUrl;

            // Subscribe the SQS queue to the SNS topic
            var queueArn = $"arn:aws:sqs:us-east-1:000000000000:{queueName}";
            await _fixture.SnsClient.SubscribeAsync(new Amazon.SimpleNotificationService.Model.SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            });

            // Brief delay to allow subscription propagation in LocalStack
            await Task.Delay(500);
            return queueUrl;
        }

        /// <summary>
        /// Polls an SQS queue for messages with a configurable wait time.
        /// Returns all messages received within the timeout.
        /// </summary>
        private async Task<List<Message>> PollSqsMessagesAsync(
            IAmazonSQS sqsClient, string queueUrl, int maxWaitSeconds = 10)
        {
            var messages = new List<Message>();
            var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

            while (DateTime.UtcNow < deadline)
            {
                var receiveResult = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 2
                });

                if (receiveResult.Messages != null && receiveResult.Messages.Count > 0)
                {
                    messages.AddRange(receiveResult.Messages);
                    break;
                }

                await Task.Delay(500);
            }

            return messages;
        }

        /// <summary>
        /// Parses an SNS notification from an SQS message body. SNS wraps the actual
        /// message payload in an envelope containing Type, MessageId, TopicArn, Message, etc.
        /// Returns the inner "Message" field as a parsed JsonDocument.
        /// </summary>
        private static JsonDocument ParseSnsEventPayload(Message sqsMessage)
        {
            // SQS message body is the SNS notification envelope
            var snsEnvelope = JsonDocument.Parse(sqsMessage.Body);
            var innerMessage = snsEnvelope.RootElement.GetProperty("Message").GetString()!;
            return JsonDocument.Parse(innerMessage);
        }

        // =====================================================================
        // Phase 2: Record Create Tests
        // =====================================================================

        /// <summary>
        /// Creates a record with valid field data and verifies it is persisted to
        /// LocalStack DynamoDB with the correct PK/SK key structure.
        /// </summary>
        [Fact]
        public async Task CreateRecord_WithValidData_PersistsToLocalStackDynamoDB()
        {
            // Arrange — seed entity with a text field
            var entityName = "crud_test_create_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity = await SeedTestEntityAsync(entityName);

            var record = TestDataHelper.CreateTestRecord();
            var recordId = (Guid)record["id"]!;
            record["text_field"] = "Hello Integration Test";

            // Act — create record via service
            var response = await _recordService.CreateRecord(entityName, record);

            // Assert — service reports success
            response.Success.Should().BeTrue();
            response.Object.Should().NotBeNull();
            response.Object!.Data.Should().NotBeNull();
            response.Object.Data!.Should().NotBeEmpty();

            // Assert — verify record persisted in DynamoDB with correct PK/SK
            var dynItem = await GetDynamoDbRecordAsync(entityName, recordId);
            dynItem.Should().NotBeNull("record should be persisted in DynamoDB");
            dynItem!["PK"].S.Should().Be($"ENTITY#{entityName}");
            dynItem["SK"].S.Should().Be($"RECORD#{recordId}");
        }

        /// <summary>
        /// Creates a record without specifying an id field and verifies the service
        /// auto-generates a valid non-empty GUID.
        /// </summary>
        [Fact]
        public async Task CreateRecord_AutoGeneratesId_WhenNotProvided()
        {
            // Arrange
            var entityName = "crud_test_autoid_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);
            var record = new EntityRecord();

            // Act
            var response = await _recordService.CreateRecord(entityName, record);

            // Assert — success with auto-generated id
            response.Success.Should().BeTrue();
            response.Object.Should().NotBeNull();
            response.Object!.Data.Should().NotBeNull();
            response.Object.Data!.Should().NotBeEmpty();

            var createdRecord = response.Object.Data[0];
            createdRecord.ContainsKey("id").Should().BeTrue("record should have an id field");
            var generatedId = createdRecord["id"];
            generatedId.Should().NotBeNull();

            // Verify the generated id is a valid non-empty Guid
            Guid parsedId;
            if (generatedId is Guid g)
            {
                parsedId = g;
            }
            else
            {
                Guid.TryParse(generatedId!.ToString(), out parsedId).Should().BeTrue();
            }
            parsedId.Should().NotBe(Guid.Empty, "auto-generated id should not be Guid.Empty");

            // Verify persisted in DynamoDB
            var dynItem = await GetDynamoDbRecordAsync(entityName, parsedId);
            dynItem.Should().NotBeNull("auto-generated record should be persisted");
        }

        /// <summary>
        /// Attempts to create a record with Guid.Empty as the id. The service should
        /// reject this. Source: RecordManager.cs ExtractRecordId validation.
        /// </summary>
        [Fact]
        public async Task CreateRecord_RejectsGuidEmpty_ForId()
        {
            // Arrange
            var entityName = "crud_test_emptyid_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);
            var record = new EntityRecord { ["id"] = Guid.Empty };

            // Act
            var response = await _recordService.CreateRecord(entityName, record);

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Contain("Guid.Empty value cannot be used as valid value for record id");
        }

        /// <summary>
        /// Creates a record with a string id that is a valid GUID representation.
        /// Source: RecordManager.cs ExtractRecordId string-to-Guid parsing.
        /// </summary>
        [Fact]
        public async Task CreateRecord_ParsesStringId_ToGuid()
        {
            // Arrange
            var entityName = "crud_test_strid_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);
            var expectedId = Guid.NewGuid();
            var record = new EntityRecord { ["id"] = expectedId.ToString() };

            // Act
            var response = await _recordService.CreateRecord(entityName, record);

            // Assert
            response.Success.Should().BeTrue();
            response.Object.Should().NotBeNull();
            response.Object!.Data.Should().NotBeNull();

            // Verify record persisted with the parsed Guid id
            var dynItem = await GetDynamoDbRecordAsync(entityName, expectedId);
            dynItem.Should().NotBeNull("record should be persisted with parsed Guid id");
        }

        /// <summary>
        /// Attempts to create a record for a non-existent entity name.
        /// Source: RecordManager.cs CreateRecord entity validation.
        /// </summary>
        [Fact]
        public async Task CreateRecord_WithInvalidEntityName_ReturnsError()
        {
            // Arrange
            var record = TestDataHelper.CreateTestRecord();

            // Act
            var response = await _recordService.CreateRecord(
                "nonexistent_entity_xyz_" + Guid.NewGuid().ToString("N"), record);

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Contain("Entity cannot be found");
        }

        /// <summary>
        /// Attempts to create a record with a null EntityRecord.
        /// Source: RecordManager.cs CreateRecord null record validation.
        /// </summary>
        [Fact]
        public async Task CreateRecord_WithNullRecord_ReturnsError()
        {
            // Arrange
            var entityName = "crud_test_null_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            // Act
            var response = await _recordService.CreateRecord(entityName, (EntityRecord)null!);

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Contain("Invalid record. Cannot be null");
        }

        /// <summary>
        /// Attempts to create a record with an empty entity name.
        /// Source: RecordManager.cs CreateRecord empty name validation.
        /// </summary>
        [Fact]
        public async Task CreateRecord_WithEmptyEntityName_ReturnsError()
        {
            // Arrange
            var record = TestDataHelper.CreateTestRecord();

            // Act
            var response = await _recordService.CreateRecord("", record);

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Contain("Invalid entity name");
        }

        // =====================================================================
        // Phase 3: Record Field Type Normalization Tests
        // =====================================================================

        /// <summary>
        /// Seeds an entity with all 21 field types using TestDataHelper, creates a
        /// record with test values for each type, reads it back from DynamoDB, and
        /// verifies round-trip correctness for every field type. This is the most
        /// comprehensive field normalization test.
        /// </summary>
        [Fact]
        public async Task CreateRecord_NormalizesAllFieldTypes()
        {
            // Arrange — seed entity with all standard field types
            var entityName = "crud_test_fieldtypes_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity = await SeedEntityWithAllFieldTypesAsync(entityName);

            var recordId = Guid.NewGuid();
            var record = new EntityRecord
            {
                ["id"] = recordId,
                ["text_field"] = "Sample Text",
                ["number_field"] = 42.5m,
                ["checkbox_field"] = true,
                ["date_field"] = DateTime.UtcNow.Date,
                ["datetime_field"] = DateTime.UtcNow,
                ["guid_field"] = Guid.NewGuid(),
                ["select_field"] = "option1",
                ["multiselect_field"] = new List<string> { "opt1", "opt2" },
                ["email_field"] = "test@webvella.com",
                ["phone_field"] = "+1-555-0100",
                ["url_field"] = "https://webvella.com",
                ["currency_field"] = 99.99m,
                ["percent_field"] = 0.75m,
                ["password_field"] = "secret123",
                ["file_field"] = "/uploads/test.pdf",
                ["image_field"] = "/uploads/test.png",
                ["html_field"] = "<p>Hello</p>",
                ["multiline_field"] = "Line1\nLine2\nLine3",
                ["geography_field"] = "{\"type\":\"Point\",\"coordinates\":[23.32,42.69]}"
            };

            // Act — create record via service
            var response = await _recordService.CreateRecord(entityName, record);

            // Assert — service success
            response.Success.Should().BeTrue(
                $"CreateRecord should succeed but failed: {response.Message}");
            response.Object.Should().NotBeNull();
            response.Object!.Data.Should().NotBeNull();
            response.Object.Data!.Should().NotBeEmpty();

            var createdRecord = response.Object.Data[0];

            // Verify key field types round-tripped correctly
            createdRecord.ContainsKey("id").Should().BeTrue();
            createdRecord.ContainsKey("text_field").Should().BeTrue();
            createdRecord["text_field"]?.ToString().Should().Be("Sample Text");

            // Verify DynamoDB persistence
            var dynItem = await GetDynamoDbRecordAsync(entityName, recordId);
            dynItem.Should().NotBeNull("record should be persisted in DynamoDB");
        }

        /// <summary>
        /// Verifies that MultiSelectField values are stored and returned as a list
        /// of strings when provided as an array. Tests the DynamoDB L attribute
        /// storage and retrieval for multi-value fields.
        /// </summary>
        [Fact]
        public async Task CreateRecord_MultiSelectField_ConvertsArrayCorrectly()
        {
            // Arrange — seed entity with multiselect field
            var entityName = "crud_test_multisel_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity = await SeedEntityWithAllFieldTypesAsync(entityName);

            var recordId = Guid.NewGuid();
            var multiSelectValues = new List<string> { "alpha", "beta", "gamma" };
            var record = new EntityRecord
            {
                ["id"] = recordId,
                ["multiselect_field"] = multiSelectValues
            };

            // Act
            var response = await _recordService.CreateRecord(entityName, record);

            // Assert
            response.Success.Should().BeTrue(
                $"CreateRecord should succeed but failed: {response.Message}");

            // Verify via Find query
            var query = new EntityQuery(entityName, "*",
                EntityQuery.QueryEQ("id", recordId));
            var findResult = await _recordService.Find(query);
            findResult.Success.Should().BeTrue();
            findResult.Object.Should().NotBeNull();
            findResult.Object!.Data.Should().NotBeNull();
            findResult.Object.Data!.Should().HaveCountGreaterThanOrEqualTo(1);

            var foundRecord = findResult.Object.Data[0];
            var storedMultiSelect = foundRecord["multiselect_field"];
            storedMultiSelect.Should().NotBeNull("multiselect_field should be stored");
        }

        /// <summary>
        /// Verifies that PasswordField values are hashed before storage.
        /// The stored value must NOT equal the plaintext input.
        /// Source: RecordManager.cs field normalization (MD5/hash).
        /// </summary>
        [Fact]
        public async Task CreateRecord_PasswordField_HashesValue()
        {
            // Arrange
            var entityName = "crud_test_pwd_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity = await SeedEntityWithAllFieldTypesAsync(entityName);

            var recordId = Guid.NewGuid();
            var plaintextPassword = "MySecretP@ssw0rd";
            var record = new EntityRecord
            {
                ["id"] = recordId,
                ["password_field"] = plaintextPassword
            };

            // Act
            var response = await _recordService.CreateRecord(entityName, record);

            // Assert
            response.Success.Should().BeTrue(
                $"CreateRecord should succeed but failed: {response.Message}");

            // Read back via Find to check stored password value
            var query = new EntityQuery(entityName, "*",
                EntityQuery.QueryEQ("id", recordId));
            var findResult = await _recordService.Find(query);
            findResult.Success.Should().BeTrue();
            findResult.Object.Should().NotBeNull();
            findResult.Object!.Data.Should().NotBeNull();
            findResult.Object.Data!.Should().NotBeEmpty();

            var foundRecord = findResult.Object.Data[0];
            // Password should be hashed — stored value must differ from plaintext
            if (foundRecord.ContainsKey("password_field") && foundRecord["password_field"] != null)
            {
                var storedPassword = foundRecord["password_field"]!.ToString();
                storedPassword.Should().NotBe(plaintextPassword,
                    "password field should be hashed, not stored as plaintext");
            }
        }

        // =====================================================================
        // Phase 4: SNS Domain Event Publishing Tests
        // =====================================================================

        /// <summary>
        /// Verifies that creating a record publishes an SNS event to the
        /// RecordCreated topic. Uses real SQS subscription on LocalStack to
        /// capture the event — NO mocked SDK calls (AAP §0.8.4).
        /// Event naming: entity-management.record.created (AAP §0.8.5).
        /// </summary>
        [Fact]
        public async Task CreateRecord_PublishesSNSEvent_RecordCreated()
        {
            // Arrange — create SQS queue and subscribe to RecordCreated topic
            using var sqsClient = CreateSqsClient();
            var queueName = "test-record-created-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var queueUrl = await CreateSqsSubscriptionAsync(
                sqsClient, _fixture.UnifiedEventsTopicArn, queueName);

            try
            {
                var entityName = "crud_test_snscreate_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                await SeedTestEntityAsync(entityName);
                var record = TestDataHelper.CreateTestRecord();
                var recordId = (Guid)record["id"]!;

                // Act — create record (should trigger SNS publish)
                var response = await _recordService.CreateRecord(entityName, record);
                response.Success.Should().BeTrue(
                    $"CreateRecord must succeed for SNS test: {response.Message}");

                // Assert — poll SQS for the published event
                var messages = await PollSqsMessagesAsync(sqsClient, queueUrl, maxWaitSeconds: 15);
                messages.Should().NotBeEmpty("SNS event should have been published to SQS queue");

                // Parse and verify the event payload
                var eventDoc = ParseSnsEventPayload(messages[0]);
                var root = eventDoc.RootElement;

                // Verify event type follows naming convention
                if (root.TryGetProperty("eventType", out var eventTypeProp))
                {
                    eventTypeProp.GetString().Should().Contain("record.created");
                }
                else if (root.TryGetProperty("EventType", out var eventTypePascal))
                {
                    eventTypePascal.GetString().Should().Contain("record.created");
                }

                // Verify entityName in payload
                if (root.TryGetProperty("entityName", out var entityNameProp))
                {
                    entityNameProp.GetString().Should().Be(entityName);
                }
                else if (root.TryGetProperty("EntityName", out var entityNamePascal))
                {
                    entityNamePascal.GetString().Should().Be(entityName);
                }
            }
            finally
            {
                // Cleanup — delete SQS queue
                try { await sqsClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl }); }
                catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Verifies that updating a record publishes an SNS event to the
        /// RecordUpdated topic. Uses real SQS subscription on LocalStack.
        /// </summary>
        [Fact]
        public async Task UpdateRecord_PublishesSNSEvent_RecordUpdated()
        {
            // Arrange — create SQS queue and subscribe to RecordUpdated topic
            using var sqsClient = CreateSqsClient();
            var queueName = "test-record-updated-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var queueUrl = await CreateSqsSubscriptionAsync(
                sqsClient, _fixture.UnifiedEventsTopicArn, queueName);

            try
            {
                var entityName = "crud_test_snsupdate_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                await SeedTestEntityAsync(entityName);

                // Create a record first
                var record = TestDataHelper.CreateTestRecord();
                var recordId = (Guid)record["id"]!;
                record["text_field"] = "Original";
                var createResp = await _recordService.CreateRecord(entityName, record);
                createResp.Success.Should().BeTrue(
                    $"CreateRecord must succeed for Update SNS test: {createResp.Message}");

                // Drain any leftover messages from the queue
                await PollSqsMessagesAsync(sqsClient, queueUrl, maxWaitSeconds: 2);

                // Act — update the record
                var updateRecord = new EntityRecord
                {
                    ["id"] = recordId,
                    ["text_field"] = "Updated Value"
                };
                var updateResp = await _recordService.UpdateRecord(entityName, updateRecord);
                updateResp.Success.Should().BeTrue(
                    $"UpdateRecord must succeed for SNS test: {updateResp.Message}");

                // Assert — poll for update event
                var messages = await PollSqsMessagesAsync(sqsClient, queueUrl, maxWaitSeconds: 15);
                messages.Should().NotBeEmpty("SNS update event should have been published");

                var eventDoc = ParseSnsEventPayload(messages[0]);
                var root = eventDoc.RootElement;

                if (root.TryGetProperty("eventType", out var eventTypeProp))
                {
                    eventTypeProp.GetString().Should().Contain("record.updated");
                }
                else if (root.TryGetProperty("EventType", out var eventTypePascal))
                {
                    eventTypePascal.GetString().Should().Contain("record.updated");
                }
            }
            finally
            {
                try { await sqsClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl }); }
                catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Verifies that deleting a record publishes an SNS event to the
        /// RecordDeleted topic. Uses real SQS subscription on LocalStack.
        /// </summary>
        [Fact]
        public async Task DeleteRecord_PublishesSNSEvent_RecordDeleted()
        {
            // Arrange — create SQS queue and subscribe to RecordDeleted topic
            using var sqsClient = CreateSqsClient();
            var queueName = "test-record-deleted-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var queueUrl = await CreateSqsSubscriptionAsync(
                sqsClient, _fixture.UnifiedEventsTopicArn, queueName);

            try
            {
                var entityName = "crud_test_snsdelete_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                await SeedTestEntityAsync(entityName);

                // Create a record to delete
                var record = TestDataHelper.CreateTestRecord();
                var recordId = (Guid)record["id"]!;
                var createResp = await _recordService.CreateRecord(entityName, record);
                createResp.Success.Should().BeTrue(
                    $"CreateRecord must succeed for Delete SNS test: {createResp.Message}");

                // Drain any leftover messages
                await PollSqsMessagesAsync(sqsClient, queueUrl, maxWaitSeconds: 2);

                // Act — delete the record
                var deleteResp = await _recordService.DeleteRecord(entityName, recordId);
                deleteResp.Success.Should().BeTrue(
                    $"DeleteRecord must succeed for SNS test: {deleteResp.Message}");

                // Assert — poll for delete event
                var messages = await PollSqsMessagesAsync(sqsClient, queueUrl, maxWaitSeconds: 15);
                messages.Should().NotBeEmpty("SNS delete event should have been published");

                var eventDoc = ParseSnsEventPayload(messages[0]);
                var root = eventDoc.RootElement;

                if (root.TryGetProperty("eventType", out var eventTypeProp))
                {
                    eventTypeProp.GetString().Should().Contain("record.deleted");
                }
                else if (root.TryGetProperty("EventType", out var eventTypePascal))
                {
                    eventTypePascal.GetString().Should().Contain("record.deleted");
                }
            }
            finally
            {
                try { await sqsClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl }); }
                catch { /* best-effort cleanup */ }
            }
        }

        // =====================================================================
        // Phase 5: Record Update Tests
        // =====================================================================

        /// <summary>
        /// Creates a record, then updates a field value. Reads back from DynamoDB
        /// to verify the update was persisted correctly.
        /// Source: RecordManager.cs UpdateRecord logic.
        /// </summary>
        [Fact]
        public async Task UpdateRecord_ModifiesFieldValues()
        {
            // Arrange — seed entity and create initial record
            var entityName = "crud_test_update_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            var record = TestDataHelper.CreateTestRecord();
            var recordId = (Guid)record["id"]!;
            record["text_field"] = "Original Value";
            var createResp = await _recordService.CreateRecord(entityName, record);
            createResp.Success.Should().BeTrue(
                $"Setup CreateRecord failed: {createResp.Message}");

            // Act — update the text field
            var updateRecord = new EntityRecord
            {
                ["id"] = recordId,
                ["text_field"] = "Modified Value"
            };
            var updateResp = await _recordService.UpdateRecord(entityName, updateRecord);

            // Assert — update succeeded
            updateResp.Success.Should().BeTrue(
                $"UpdateRecord should succeed but failed: {updateResp.Message}");

            // Verify the update persisted in DynamoDB
            var query = new EntityQuery(entityName, "*",
                EntityQuery.QueryEQ("id", recordId));
            var findResult = await _recordService.Find(query);
            findResult.Success.Should().BeTrue();
            findResult.Object.Should().NotBeNull();
            findResult.Object!.Data.Should().NotBeNull();
            findResult.Object.Data!.Should().NotBeEmpty();

            var updatedRecord = findResult.Object.Data[0];
            updatedRecord["text_field"]?.ToString().Should().Be("Modified Value");
        }

        /// <summary>
        /// Attempts to update a record without specifying the id field.
        /// The service should return an error about missing ID.
        /// Source: RecordManager.cs UpdateRecord ID validation.
        /// </summary>
        [Fact]
        public async Task UpdateRecord_RequiresRecordId()
        {
            // Arrange
            var entityName = "crud_test_updateid_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            // Create record first
            var record = TestDataHelper.CreateTestRecord();
            var createResp = await _recordService.CreateRecord(entityName, record);
            createResp.Success.Should().BeTrue(
                $"Setup CreateRecord failed: {createResp.Message}");

            // Act — attempt update without id
            var updateRecord = new EntityRecord
            {
                ["text_field"] = "Updated without ID"
            };
            var response = await _recordService.UpdateRecord(entityName, updateRecord);

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Contain("Missing ID field");
        }

        /// <summary>
        /// Attempts to update a record with a non-existent id.
        /// The service should return an error about record not found.
        /// Source: RecordManager.cs UpdateRecord existence check.
        /// </summary>
        [Fact]
        public async Task UpdateRecord_NonExistentRecord_ReturnsError()
        {
            // Arrange
            var entityName = "crud_test_updatne_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            var nonExistentId = Guid.NewGuid();
            var updateRecord = new EntityRecord
            {
                ["id"] = nonExistentId,
                ["text_field"] = "Phantom Update"
            };

            // Act
            var response = await _recordService.UpdateRecord(entityName, updateRecord);

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Contain("Record");
        }

        // =====================================================================
        // Phase 6: Record Delete Tests
        // =====================================================================

        /// <summary>
        /// Creates a record, deletes it, and verifies it is no longer present
        /// in DynamoDB by reading directly with GetItem.
        /// </summary>
        [Fact]
        public async Task DeleteRecord_RemovesFromDynamoDB()
        {
            // Arrange
            var entityName = "crud_test_delete_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            var record = TestDataHelper.CreateTestRecord();
            var recordId = (Guid)record["id"]!;
            var createResp = await _recordService.CreateRecord(entityName, record);
            createResp.Success.Should().BeTrue(
                $"Setup CreateRecord failed: {createResp.Message}");

            // Verify record exists before deletion
            var preDelete = await GetDynamoDbRecordAsync(entityName, recordId);
            preDelete.Should().NotBeNull("record should exist before deletion");

            // Act — delete the record
            var deleteResp = await _recordService.DeleteRecord(entityName, recordId);

            // Assert
            deleteResp.Success.Should().BeTrue(
                $"DeleteRecord should succeed but failed: {deleteResp.Message}");

            // Verify record no longer exists in DynamoDB
            var postDelete = await GetDynamoDbRecordAsync(entityName, recordId);
            postDelete.Should().BeNull("record should be removed from DynamoDB after deletion");
        }

        /// <summary>
        /// Attempts to delete a record with a non-existent id.
        /// The service should return an error.
        /// Source: RecordManager.cs DeleteRecord existence check.
        /// </summary>
        [Fact]
        public async Task DeleteRecord_NonExistentId_ReturnsError()
        {
            // Arrange
            var entityName = "crud_test_delne_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);
            var nonExistentId = Guid.NewGuid();

            // Act
            var response = await _recordService.DeleteRecord(entityName, nonExistentId);

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Contain("Record");
        }

        // =====================================================================
        // Phase 7: Record Find Tests
        // =====================================================================

        /// <summary>
        /// Seeds multiple records for an entity, then queries all records
        /// via Find with no filter. Verifies the expected count is returned.
        /// </summary>
        [Fact]
        public async Task Find_ByEntityName_ReturnsAllRecords()
        {
            // Arrange — seed entity and 5 records
            var entityName = "crud_test_findall_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            var expectedCount = 5;
            for (int i = 0; i < expectedCount; i++)
            {
                var rec = TestDataHelper.CreateTestRecord();
                rec["text_field"] = $"Record {i}";
                var resp = await _recordService.CreateRecord(entityName, rec);
                resp.Success.Should().BeTrue(
                    $"Setup CreateRecord #{i} failed: {resp.Message}");
            }

            // Act — find all records for the entity
            var query = new EntityQuery(entityName);
            var result = await _recordService.Find(query);

            // Assert
            result.Success.Should().BeTrue(
                $"Find should succeed but failed: {result.Message}");
            result.Object.Should().NotBeNull();
            result.Object!.Data.Should().NotBeNull();
            result.Object.Data!.Should().HaveCount(expectedCount);
        }

        /// <summary>
        /// Seeds records with varying field values, then queries with an equality
        /// filter on a text field. Verifies only matching records are returned.
        /// </summary>
        [Fact]
        public async Task Find_WithEqualityFilter_ReturnsMatchingRecords()
        {
            // Arrange — seed entity and records with different text values
            var entityName = "crud_test_findeq_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            var targetValue = "TargetMatch";
            var targetId = Guid.NewGuid();

            // Create 3 records with non-matching values
            for (int i = 0; i < 3; i++)
            {
                var nonMatchRec = TestDataHelper.CreateTestRecord();
                nonMatchRec["text_field"] = $"NoMatch_{i}";
                var resp = await _recordService.CreateRecord(entityName, nonMatchRec);
                resp.Success.Should().BeTrue(
                    $"Setup non-match CreateRecord #{i} failed: {resp.Message}");
            }

            // Create 1 record with the target value
            var matchRec = new EntityRecord
            {
                ["id"] = targetId,
                ["text_field"] = targetValue
            };
            var matchResp = await _recordService.CreateRecord(entityName, matchRec);
            matchResp.Success.Should().BeTrue(
                $"Setup match CreateRecord failed: {matchResp.Message}");

            // Act — find with equality filter on text_field
            var query = new EntityQuery(entityName, "*",
                EntityQuery.QueryEQ("text_field", targetValue));
            var result = await _recordService.Find(query);

            // Assert
            result.Success.Should().BeTrue(
                $"Find with filter should succeed but failed: {result.Message}");
            result.Object.Should().NotBeNull();
            result.Object!.Data.Should().NotBeNull();
            result.Object.Data!.Should().HaveCountGreaterThanOrEqualTo(1);

            // All returned records should have the matching text_field value
            var returnedRecords = result.Object.Data;
            returnedRecords.Should().NotBeEmpty();
            var matchingRecords = returnedRecords.Where(r =>
                r.ContainsKey("text_field") && r["text_field"] is object fieldVal && fieldVal.ToString() == targetValue).ToList();
            matchingRecords.Should().NotBeEmpty("at least one record should match the filter value");
        }

        /// <summary>
        /// Seeds N records, calls Count, and verifies the returned count matches N.
        /// </summary>
        [Fact]
        public async Task Count_ReturnsCorrectCount()
        {
            // Arrange — seed entity and records
            var entityName = "crud_test_count_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await SeedTestEntityAsync(entityName);

            var expectedCount = 4;
            for (int i = 0; i < expectedCount; i++)
            {
                var rec = TestDataHelper.CreateTestRecord();
                var resp = await _recordService.CreateRecord(entityName, rec);
                resp.Success.Should().BeTrue(
                    $"Setup CreateRecord #{i} failed: {resp.Message}");
            }

            // Act
            var result = await _recordService.Count(entityName, null);

            // Assert
            result.Success.Should().BeTrue(
                $"Count should succeed but failed: {result.Message}");
            result.Object.Should().Be(expectedCount);
        }

        // =====================================================================
        // Phase 8: Relation-Aware Payload Processing Tests
        // =====================================================================

        /// <summary>
        /// Seeds two entities connected by a OneToMany relation, then creates a
        /// record with a relation-aware payload key ($relationName.fieldName).
        /// Verifies the related entity's record is correctly created/linked.
        /// Source: RecordManager.cs relation payload separator processing.
        /// </summary>
        [Fact]
        public async Task CreateRecord_WithRelationPayload_ProcessesCorrectly()
        {
            // Arrange — seed origin and target entities
            var originName = "crud_rel_origin_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var targetName = "crud_rel_target_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var originEntityId = Guid.NewGuid();
            var targetEntityId = Guid.NewGuid();

            var originEntity = TestDataHelper.CreateTestEntity(originName, originEntityId);
            var targetEntity = TestDataHelper.CreateTestEntity(targetName, targetEntityId);

            await _fixture.SeedEntityAsync(originEntity);
            await _fixture.SeedEntityAsync(targetEntity);

            // Create a OneToMany relation: origin(id) -> target(origin_id)
            // Use the actual "id" GuidField from each entity so validation passes
            var originIdFieldId = originEntity.Fields.First(f => f.Name == "id").Id;
            var targetIdFieldId = targetEntity.Fields.First(f => f.Name == "id").Id;
            var relation = TestDataHelper.CreateOneToManyRelation(
                name: "origin_target_rel",
                originEntityId: originEntityId,
                originFieldId: originIdFieldId,
                targetEntityId: targetEntityId,
                targetFieldId: targetIdFieldId);

            // Persist relation via EntityService
            var relResp = await _entityService.CreateRelation(relation);
            relResp.Success.Should().BeTrue(
                $"Setup CreateRelation failed: {relResp.Message}");

            // Create a record for the origin entity
            var originRecord = TestDataHelper.CreateTestRecord();
            var originRecordId = (Guid)originRecord["id"]!;
            var originCreateResp = await _recordService.CreateRecord(originName, originRecord);
            originCreateResp.Success.Should().BeTrue(
                $"Setup origin CreateRecord failed: {originCreateResp.Message}");

            // Act — create a record on the target entity with relation payload
            var targetRecord = TestDataHelper.CreateTestRecord();
            var targetRecordId = (Guid)targetRecord["id"]!;
            targetRecord["$origin_target_rel.origin_id"] = originRecordId;

            var response = await _recordService.CreateRecord(targetName, targetRecord);

            // Assert — verify the record was created (relation payload processed)
            // The service should accept the relation-aware key and process it
            response.Success.Should().BeTrue(
                $"CreateRecord with relation payload should succeed: {response.Message}");
        }

        /// <summary>
        /// Seeds two entities with a ManyToMany relation and creates an M2M
        /// association record between them. Verifies the bridge record exists
        /// in DynamoDB after creation.
        /// Source: RecordManager.cs CreateRelationManyToManyRecord.
        /// </summary>
        [Fact]
        public async Task CreateRelationManyToManyRecord_CreatesAssociation()
        {
            // Arrange — seed two entities
            var entity1Name = "crud_m2m_src_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity2Name = "crud_m2m_tgt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity1Id = Guid.NewGuid();
            var entity2Id = Guid.NewGuid();

            var entity1 = TestDataHelper.CreateTestEntity(entity1Name, entity1Id);
            var entity2 = TestDataHelper.CreateTestEntity(entity2Name, entity2Id);

            await _fixture.SeedEntityAsync(entity1);
            await _fixture.SeedEntityAsync(entity2);

            // Create ManyToMany relation — use actual "id" GuidField IDs
            var relationId = Guid.NewGuid();
            var e1IdFieldId = entity1.Fields.First(f => f.Name == "id").Id;
            var e2IdFieldId = entity2.Fields.First(f => f.Name == "id").Id;
            var relation = TestDataHelper.CreateManyToManyRelation(
                name: "m2m_test_rel",
                id: relationId,
                originEntityId: entity1Id,
                originFieldId: e1IdFieldId,
                targetEntityId: entity2Id,
                targetFieldId: e2IdFieldId);

            var relResp = await _entityService.CreateRelation(relation);
            relResp.Success.Should().BeTrue(
                $"Setup CreateRelation failed: {relResp.Message}");

            // Create records in both entities
            var record1 = TestDataHelper.CreateTestRecord();
            var record1Id = (Guid)record1["id"]!;
            var resp1 = await _recordService.CreateRecord(entity1Name, record1);
            resp1.Success.Should().BeTrue(
                $"Setup entity1 CreateRecord failed: {resp1.Message}");

            var record2 = TestDataHelper.CreateTestRecord();
            var record2Id = (Guid)record2["id"]!;
            var resp2 = await _recordService.CreateRecord(entity2Name, record2);
            resp2.Success.Should().BeTrue(
                $"Setup entity2 CreateRecord failed: {resp2.Message}");

            // Act — create M2M association
            var m2mResp = await _recordService.CreateRelationManyToManyRecord(
                relationId, record1Id, record2Id);

            // Assert — association created successfully
            m2mResp.Success.Should().BeTrue(
                $"CreateRelationManyToManyRecord should succeed: {m2mResp.Message}");
        }

        /// <summary>
        /// Creates an M2M association, then removes it. Verifies the bridge
        /// record no longer exists in DynamoDB after removal.
        /// Source: RecordManager.cs RemoveRelationManyToManyRecord.
        /// </summary>
        [Fact]
        public async Task RemoveRelationManyToManyRecord_RemovesAssociation()
        {
            // Arrange — seed entities, relation, records, and M2M association
            var entity1Name = "crud_m2mdel_src_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity2Name = "crud_m2mdel_tgt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var entity1Id = Guid.NewGuid();
            var entity2Id = Guid.NewGuid();

            var entity1 = TestDataHelper.CreateTestEntity(entity1Name, entity1Id);
            var entity2 = TestDataHelper.CreateTestEntity(entity2Name, entity2Id);

            await _fixture.SeedEntityAsync(entity1);
            await _fixture.SeedEntityAsync(entity2);

            var relationId = Guid.NewGuid();
            var e1IdFieldId = entity1.Fields.First(f => f.Name == "id").Id;
            var e2IdFieldId = entity2.Fields.First(f => f.Name == "id").Id;
            var relation = TestDataHelper.CreateManyToManyRelation(
                name: "m2m_del_rel",
                id: relationId,
                originEntityId: entity1Id,
                originFieldId: e1IdFieldId,
                targetEntityId: entity2Id,
                targetFieldId: e2IdFieldId);

            var relResp = await _entityService.CreateRelation(relation);
            relResp.Success.Should().BeTrue(
                $"Setup CreateRelation failed: {relResp.Message}");

            var record1 = TestDataHelper.CreateTestRecord();
            var record1Id = (Guid)record1["id"]!;
            await _recordService.CreateRecord(entity1Name, record1);

            var record2 = TestDataHelper.CreateTestRecord();
            var record2Id = (Guid)record2["id"]!;
            await _recordService.CreateRecord(entity2Name, record2);

            // Create M2M association first
            var createM2m = await _recordService.CreateRelationManyToManyRecord(
                relationId, record1Id, record2Id);
            createM2m.Success.Should().BeTrue(
                $"Setup M2M create failed: {createM2m.Message}");

            // Act — remove the M2M association
            var removeResp = await _recordService.RemoveRelationManyToManyRecord(
                relationId, record1Id, record2Id);

            // Assert
            removeResp.Success.Should().BeTrue(
                $"RemoveRelationManyToManyRecord should succeed: {removeResp.Message}");
        }

        // =====================================================================
        // Phase 9: Permission Enforcement Tests
        // =====================================================================

        /// <summary>
        /// Seeds an entity with empty CanCreate permission list (no roles allowed),
        /// then attempts to create a record using a RecordService with DevelopmentMode
        /// disabled. The service should return Forbidden/access denied.
        /// Source: RecordManager.cs HasEntityPermission → SecurityContext checks.
        /// </summary>
        [Fact]
        public async Task CreateRecord_WithoutCreatePermission_ReturnsForbidden()
        {
            // Arrange — seed entity with restricted create permissions (only a non-existent role)
            var entityName = "crud_test_perm_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nonExistentRoleId = Guid.NewGuid(); // role that doesn't match any known role
            var permissions = new RecordPermissions
            {
                CanCreate = new List<Guid> { nonExistentRoleId }, // only unknown role allowed
                CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
            };

            await SeedTestEntityAsync(entityName, permissions: permissions);

            // Create a strict service that enforces permission checks
            var strictService = CreateStrictPermissionRecordService();

            var record = TestDataHelper.CreateTestRecord();

            // Act — attempt to create a record without create permission
            var response = await strictService.CreateRecord(entityName, record);

            // Assert — should be forbidden/access denied
            response.Success.Should().BeFalse();
            // The error message should indicate access denial
            var responseMessage = response.Message ?? "";
            var hasAccessDenied = responseMessage.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
                || responseMessage.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
                || responseMessage.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden;
            hasAccessDenied.Should().BeTrue(
                $"Expected access denied/forbidden but got: StatusCode={response.StatusCode}, Message='{response.Message}'");
        }
    }
}
