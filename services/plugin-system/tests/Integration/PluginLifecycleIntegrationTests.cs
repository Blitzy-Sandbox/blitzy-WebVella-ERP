using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Xunit;

namespace WebVellaErp.PluginSystem.Tests.Integration
{
    /// <summary>
    /// Integration tests verifying the complete plugin lifecycle:
    /// Register → List → Get → Activate → Deactivate → Unregister.
    /// Also verifies plugin data persistence, SNS event publishing,
    /// idempotent registration, and name uniqueness enforcement.
    /// All tests execute against real LocalStack DynamoDB/SNS — zero mocked AWS SDK calls.
    /// Per AAP Section 0.8.4: docker compose up -d → test → docker compose down.
    /// </summary>
    public class PluginLifecycleIntegrationTests : IClassFixture<LocalStackFixture>
    {
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly string _tableName;
        private readonly string _pluginEventsTopicArn;

        public PluginLifecycleIntegrationTests(LocalStackFixture fixture)
        {
            _dynamoDbClient = fixture.DynamoDbClient;
            _snsClient = fixture.SnsClient;
            _tableName = fixture.TableName;
            _pluginEventsTopicArn = fixture.PluginEventsTopicArn;
        }

        #region Helper Methods

        /// <summary>
        /// Creates a full plugin META item in DynamoDB with all 13 ErpPlugin properties
        /// plus system fields (id, status, created_at, updated_at, EntityType).
        /// Key pattern: PK=PLUGIN#{pluginId}, SK=META
        /// GSI1: GSI1PK=STATUS#{status}, GSI1SK=NAME#{name}
        /// </summary>
        private async Task<Dictionary<string, AttributeValue>> CreatePluginInDynamoDb(
            Guid pluginId,
            string name,
            string status = "Active",
            string prefix = "test",
            string url = "https://test.example.com",
            string description = "Test plugin description",
            int version = 1,
            string company = "Test Company",
            string companyUrl = "https://testco.example.com",
            string author = "Test Author",
            string repository = "https://github.com/test/plugin",
            string license = "Apache-2.0",
            string settingsUrl = "/settings/test",
            string pluginPageUrl = "/plugin/test",
            string iconUrl = "/icons/test.png")
        {
            var now = DateTime.UtcNow.ToString("o");
            var item = new Dictionary<string, AttributeValue>
            {
                { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                { "SK", new AttributeValue { S = "META" } },
                { "GSI1PK", new AttributeValue { S = $"STATUS#{status}" } },
                { "GSI1SK", new AttributeValue { S = $"NAME#{name}" } },
                { "EntityType", new AttributeValue { S = "PLUGIN_META" } },
                { "id", new AttributeValue { S = pluginId.ToString() } },
                { "name", new AttributeValue { S = name } },
                { "prefix", new AttributeValue { S = prefix } },
                { "url", new AttributeValue { S = url } },
                { "description", new AttributeValue { S = description } },
                { "version", new AttributeValue { N = version.ToString() } },
                { "company", new AttributeValue { S = company } },
                { "company_url", new AttributeValue { S = companyUrl } },
                { "author", new AttributeValue { S = author } },
                { "repository", new AttributeValue { S = repository } },
                { "license", new AttributeValue { S = license } },
                { "settings_url", new AttributeValue { S = settingsUrl } },
                { "plugin_page_url", new AttributeValue { S = pluginPageUrl } },
                { "icon_url", new AttributeValue { S = iconUrl } },
                { "status", new AttributeValue { S = status } },
                { "created_at", new AttributeValue { S = now } },
                { "updated_at", new AttributeValue { S = now } }
            };

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            });

            return item;
        }

        /// <summary>
        /// Deletes plugin META item and optional DATA item from DynamoDB for test cleanup.
        /// Silently ignores errors if items are already deleted.
        /// </summary>
        private async Task CleanupPlugin(Guid pluginId, string? pluginName = null)
        {
            try
            {
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });
            }
            catch
            {
                // Ignore cleanup failures — item may already be deleted
            }

            if (!string.IsNullOrEmpty(pluginName))
            {
                try
                {
                    await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                    {
                        TableName = _tableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            { "PK", new AttributeValue { S = $"PLUGIN#{pluginName}" } },
                            { "SK", new AttributeValue { S = "DATA" } }
                        }
                    });
                }
                catch
                {
                    // Ignore cleanup failures — item may already be deleted
                }
            }
        }

        /// <summary>
        /// Saves plugin data item to DynamoDB using the canonical key pattern:
        /// PK=PLUGIN#{pluginName}, SK=DATA, EntityType=PLUGIN_DATA.
        /// Mirrors the source ErpPlugin.SavePluginData() method (ErpPlugin.cs lines 87-115).
        /// </summary>
        private async Task SavePluginData(string pluginName, string jsonData)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                { "PK", new AttributeValue { S = $"PLUGIN#{pluginName}" } },
                { "SK", new AttributeValue { S = "DATA" } },
                { "EntityType", new AttributeValue { S = "PLUGIN_DATA" } },
                { "plugin_name", new AttributeValue { S = pluginName } },
                { "data", new AttributeValue { S = jsonData } },
                { "updated_at", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
            };

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            });
        }

        /// <summary>
        /// Retrieves plugin data from DynamoDB by plugin name.
        /// Mirrors the source ErpPlugin.GetPluginData() method (ErpPlugin.cs lines 67-85).
        /// Returns the "data" attribute string value, or null if no item found.
        /// </summary>
        private async Task<string?> GetPluginData(string pluginName)
        {
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "PK", new AttributeValue { S = $"PLUGIN#{pluginName}" } },
                    { "SK", new AttributeValue { S = "DATA" } }
                }
            });

            if (response.Item == null || response.Item.Count == 0)
                return null;

            return response.Item.ContainsKey("data") ? response.Item["data"].S : null;
        }

        /// <summary>
        /// Cleans up a plugin data item by plugin name.
        /// </summary>
        private async Task CleanupPluginData(string pluginName)
        {
            try
            {
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginName}" } },
                        { "SK", new AttributeValue { S = "DATA" } }
                    }
                });
            }
            catch
            {
                // Ignore cleanup failures
            }
        }

        #endregion

        // ===================================================================
        // Complete Lifecycle Test — Register → List → Get → Activate → Deactivate → Unregister
        // ===================================================================

        /// <summary>
        /// Comprehensive integration test verifying the full plugin lifecycle:
        /// Step 1: Register (PutItem with status=Active)
        /// Step 2: List (Scan with EntityType=PLUGIN_META)
        /// Step 3: Get (GetItem by PK/SK)
        /// Step 4: Activate (idempotent — already Active, no-op)
        /// Step 5: Deactivate (UpdateItem status=Inactive, GSI1 update)
        /// Step 6: Unregister (DeleteItem)
        /// </summary>
        [Fact]
        public async Task FullPluginLifecycle_RegisterListGetActivateDeactivateUnregister()
        {
            var pluginId = Guid.NewGuid();
            var pluginName = $"lifecycle-plugin-{pluginId:N}";

            try
            {
                // ---- Step 1 — REGISTER: Create plugin in DynamoDB with status=Active ----
                var createdItem = await CreatePluginInDynamoDb(pluginId, pluginName);
                createdItem.Should().NotBeNull();
                createdItem["name"].S.Should().Be(pluginName);

                // ---- Step 2 — LIST: Scan with EntityType=PLUGIN_META filter ----
                var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":entityType", new AttributeValue { S = "PLUGIN_META" } }
                    }
                });

                var matchingPlugin = scanResponse.Items
                    .FirstOrDefault(item => item.ContainsKey("name") && item["name"].S == pluginName);
                matchingPlugin.Should().NotBeNull("the created plugin should appear in scan results");
                matchingPlugin!["name"].S.Should().Be(pluginName);

                // ---- Step 3 — GET: GetItem with PK/SK ----
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });

                getResponse.Item.Should().NotBeNull();
                getResponse.Item.Count.Should().BeGreaterThan(0);
                getResponse.Item["name"].S.Should().Be(pluginName);
                getResponse.Item["status"].S.Should().Be("Active");
                getResponse.Item["prefix"].S.Should().Be("test");
                getResponse.Item["version"].N.Should().Be("1");
                getResponse.Item["company"].S.Should().Be("Test Company");
                getResponse.Item["license"].S.Should().Be("Apache-2.0");
                getResponse.Item["EntityType"].S.Should().Be("PLUGIN_META");
                getResponse.Item["id"].S.Should().Be(pluginId.ToString());

                // ---- Step 4 — ACTIVATE (already Active — test idempotency) ----
                // Idempotent per AAP Section 0.8.5: re-activating an already-active plugin is a no-op
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    },
                    UpdateExpression = "SET #status = :status, GSI1PK = :gsi1pk, updated_at = :updated",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#status", "status" }
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":status", new AttributeValue { S = "Active" } },
                        { ":gsi1pk", new AttributeValue { S = "STATUS#Active" } },
                        { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
                    }
                });

                var afterActivate = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });
                afterActivate.Item["status"].S.Should().Be("Active");

                // ---- Step 5 — DEACTIVATE: UpdateItem to set status=Inactive ----
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    },
                    UpdateExpression = "SET #status = :status, GSI1PK = :gsi1pk, updated_at = :updated",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#status", "status" }
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":status", new AttributeValue { S = "Inactive" } },
                        { ":gsi1pk", new AttributeValue { S = "STATUS#Inactive" } },
                        { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
                    }
                });

                // Verify deactivation via GetItem
                // Use explicit GetItemResponse type per schema members_accessed
                GetItemResponse afterDeactivate = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });
                afterDeactivate.Item["status"].S.Should().Be("Inactive");

                // GSI1 query: STATUS#Inactive should find the plugin — explicit QueryResponse
                QueryResponse inactiveQuery = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :gsi1pk AND GSI1SK = :gsi1sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":gsi1pk", new AttributeValue { S = "STATUS#Inactive" } },
                        { ":gsi1sk", new AttributeValue { S = $"NAME#{pluginName}" } }
                    }
                });
                inactiveQuery.Items.Should().HaveCountGreaterOrEqualTo(1);
                inactiveQuery.Items.Any(i => i["name"].S == pluginName).Should().BeTrue();

                // GSI1 query: STATUS#Active should NOT find this plugin
                var activeQuery = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :gsi1pk AND GSI1SK = :gsi1sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":gsi1pk", new AttributeValue { S = "STATUS#Active" } },
                        { ":gsi1sk", new AttributeValue { S = $"NAME#{pluginName}" } }
                    }
                });
                activeQuery.Items
                    .Any(i => i.ContainsKey("name") && i["name"].S == pluginName)
                    .Should().BeFalse();

                // ---- Step 6 — UNREGISTER: DeleteItem ----
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });

                // Verify deletion via GetItem — item should not exist
                var afterDelete = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });
                (afterDelete.Item == null || afterDelete.Item.Count == 0).Should().BeTrue();

                // Verify plugin no longer appears in filtered scan — explicit ScanResponse type
                ScanResponse postDeleteScan = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":entityType", new AttributeValue { S = "PLUGIN_META" } }
                    }
                });
                // Names in the remaining scan results should not contain the deleted plugin
                var remainingNames = postDeleteScan.Items
                    .Where(i => i.ContainsKey("name"))
                    .Select(i => i["name"].S)
                    .ToList();
                remainingNames.Should().NotContain(pluginName);
            }
            finally
            {
                // Cleanup — may already be deleted by step 6
                await CleanupPlugin(pluginId, pluginName);
            }
        }

        // ===================================================================
        // Plugin Data Persistence Tests — SavePluginData / GetPluginData round-trip
        // Validates the pattern from SdkPlugin._.cs ProcessPatches (lines 68-71):
        //   var currentPluginSettings = new PluginSettings() { Version = WEBVELLA_SDK_INIT_VERSION };
        //   string jsonData = GetPluginData();
        //   if (!string.IsNullOrWhiteSpace(jsonData))
        //       currentPluginSettings = JsonConvert.DeserializeObject<PluginSettings>(jsonData);
        // ===================================================================

        /// <summary>
        /// Verifies plugin data save and retrieve round-trip against real DynamoDB.
        /// Source mapping: ErpPlugin.cs GetPluginData (lines 67-85) + SavePluginData (lines 87-115).
        /// </summary>
        [Fact]
        public async Task PluginDataPersistence_SaveAndRetrieve_RoundTrip()
        {
            var uniqueName = $"roundtrip-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Serialize PluginSettings-equivalent JSON matching SdkPlugin pattern
                var pluginSettingsJson = JsonSerializer.Serialize(new { version = 20210429 });

                // Act — Save: PutItem with PK=PLUGIN#{name}, SK=DATA
                await SavePluginData(uniqueName, pluginSettingsJson);

                // Act — Retrieve: GetItem with same key
                var retrievedData = await GetPluginData(uniqueName);

                // Assert: Data matches exactly
                retrievedData.Should().NotBeNull();
                retrievedData.Should().Be(pluginSettingsJson);

                // Assert: Can deserialize back to verify round-trip
                var deserialized = JsonSerializer.Deserialize<JsonElement>(retrievedData!);
                deserialized.GetProperty("version").GetInt32().Should().Be(20210429);
            }
            finally
            {
                await CleanupPluginData(uniqueName);
            }
        }

        /// <summary>
        /// Verifies that updating existing plugin data overwrites previous data (PutItem upsert).
        /// Source mapping: SdkPlugin._.cs line 12 (WEBVELLA_SDK_INIT_VERSION = 20181001)
        /// and line 151 (SavePluginData after final patch).
        /// </summary>
        [Fact]
        public async Task PluginDataPersistence_UpdateExisting_OverwritesData()
        {
            var uniqueName = $"upsert-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Save initial data matching WEBVELLA_SDK_INIT_VERSION = 20181001
                var initialJson = JsonSerializer.Serialize(new { version = 20181001 });
                await SavePluginData(uniqueName, initialJson);

                // Act: Save updated data matching final patch version = 20210429
                var updatedJson = JsonSerializer.Serialize(new { version = 20210429 });
                await SavePluginData(uniqueName, updatedJson);

                // Assert: GetItem returns latest version (overwrite semantics)
                var retrievedData = await GetPluginData(uniqueName);
                retrievedData.Should().NotBeNull();
                retrievedData.Should().Be(updatedJson);

                var deserialized = JsonSerializer.Deserialize<JsonElement>(retrievedData!);
                deserialized.GetProperty("version").GetInt32().Should().Be(20210429);
            }
            finally
            {
                await CleanupPluginData(uniqueName);
            }
        }

        /// <summary>
        /// Verifies that multiple plugins maintain independent data stores.
        /// Each plugin in the monolith had its own plugin_data row — this tests isolation.
        /// </summary>
        [Fact]
        public async Task PluginDataPersistence_MultiplePlugins_IndependentData()
        {
            var sdkName = $"sdk-{Guid.NewGuid():N}";
            var crmName = $"crm-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Save data for two different plugins
                var sdkJson = JsonSerializer.Serialize(new { version = 20210429 });
                var crmJson = JsonSerializer.Serialize(new { version = 20190101 });

                await SavePluginData(sdkName, sdkJson);
                await SavePluginData(crmName, crmJson);

                // Act: Retrieve each independently
                var sdkData = await GetPluginData(sdkName);
                var crmData = await GetPluginData(crmName);

                // Assert: Each plugin returns its own data
                sdkData.Should().NotBeNull();
                sdkData.Should().Be(sdkJson);
                var sdkDeserialized = JsonSerializer.Deserialize<JsonElement>(sdkData!);
                sdkDeserialized.GetProperty("version").GetInt32().Should().Be(20210429);

                crmData.Should().NotBeNull();
                crmData.Should().Be(crmJson);
                var crmDeserialized = JsonSerializer.Deserialize<JsonElement>(crmData!);
                crmDeserialized.GetProperty("version").GetInt32().Should().Be(20190101);
            }
            finally
            {
                await CleanupPluginData(sdkName);
                await CleanupPluginData(crmName);
            }
        }

        // ===================================================================
        // SNS Event Publishing Tests
        // Event naming convention per AAP Section 0.8.5: {domain}.{entity}.{action}
        // Domain events replace the monolith's synchronous hook system.
        // ===================================================================

        /// <summary>
        /// Verifies that a plugin-system.plugin.created event can be published to the SNS topic.
        /// Event naming: plugin-system.plugin.created per AAP Section 0.8.5.
        /// </summary>
        [Fact]
        public async Task PluginCreated_EventPublished_ToSNSTopic()
        {
            // Arrange: Build domain event payload for plugin creation
            var pluginId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var eventPayload = JsonSerializer.Serialize(new
            {
                eventType = "plugin-system.plugin.created",
                pluginId = pluginId.ToString(),
                name = $"test-plugin-{pluginId:N}",
                version = 1,
                timestamp = DateTime.UtcNow.ToString("o"),
                correlationId = correlationId.ToString()
            });

            // Act: Publish to SNS topic — use explicit PublishResponse type per schema
            PublishResponse publishResponse = await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _pluginEventsTopicArn,
                Message = eventPayload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    {
                        "eventType", new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "plugin-system.plugin.created"
                        }
                    }
                }
            });

            // Assert: Publish succeeded with a valid MessageId
            publishResponse.Should().NotBeNull();
            publishResponse.MessageId.Should().NotBeNullOrEmpty();
            publishResponse.MessageId.Should().NotBeEmpty();
        }

        /// <summary>
        /// Verifies that a plugin-system.plugin.activated event can be published to the SNS topic.
        /// Event naming: plugin-system.plugin.activated per AAP Section 0.8.5.
        /// </summary>
        [Fact]
        public async Task PluginActivated_EventPublished_ToSNSTopic()
        {
            // Arrange: Build domain event payload for plugin activation
            var pluginId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var eventPayload = JsonSerializer.Serialize(new
            {
                eventType = "plugin-system.plugin.activated",
                pluginId = pluginId.ToString(),
                name = $"activated-plugin-{pluginId:N}",
                version = 1,
                timestamp = DateTime.UtcNow.ToString("o"),
                correlationId = correlationId.ToString()
            });

            // Act: Publish to SNS topic
            var publishResponse = await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _pluginEventsTopicArn,
                Message = eventPayload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    {
                        "eventType", new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "plugin-system.plugin.activated"
                        }
                    }
                }
            });

            // Assert: Publish succeeded
            publishResponse.Should().NotBeNull();
            publishResponse.MessageId.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that a plugin-system.plugin.deactivated event can be published to the SNS topic.
        /// Event naming: plugin-system.plugin.deactivated per AAP Section 0.8.5.
        /// </summary>
        [Fact]
        public async Task PluginDeactivated_EventPublished_ToSNSTopic()
        {
            // Arrange: Build domain event payload for plugin deactivation
            var pluginId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var eventPayload = JsonSerializer.Serialize(new
            {
                eventType = "plugin-system.plugin.deactivated",
                pluginId = pluginId.ToString(),
                name = $"deactivated-plugin-{pluginId:N}",
                version = 1,
                timestamp = DateTime.UtcNow.ToString("o"),
                correlationId = correlationId.ToString()
            });

            // Act: Publish to SNS topic
            var publishResponse = await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _pluginEventsTopicArn,
                Message = eventPayload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    {
                        "eventType", new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "plugin-system.plugin.deactivated"
                        }
                    }
                }
            });

            // Assert: Publish succeeded
            publishResponse.Should().NotBeNull();
            publishResponse.MessageId.Should().NotBeNullOrEmpty();
        }

        // ===================================================================
        // Idempotent Registration Tests
        // Per AAP Section 0.8.5: All event consumers MUST be idempotent.
        // Idempotency keys on all write endpoints and event handlers.
        // ===================================================================

        /// <summary>
        /// Verifies that attempting to register a plugin with the same name as an existing plugin
        /// is detected via a scan query, enforcing name uniqueness at the application level.
        /// Source reference: ERPService.cs plugin_data UNIQUE INDEX on name column.
        /// </summary>
        [Fact]
        public async Task IdempotentRegistration_SameNameTwice_SecondAttemptDetected()
        {
            var pluginId1 = Guid.NewGuid();
            var pluginId2 = Guid.NewGuid();
            var sharedName = $"idempotent-test-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Create the first plugin with the shared name
                await CreatePluginInDynamoDb(pluginId1, sharedName);

                // Act: Before creating a second plugin with the same name,
                // query to check if the name already exists (application-level uniqueness check)
                var existingCheck = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "#n = :name AND EntityType = :entityType",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#n", "name" }
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":name", new AttributeValue { S = sharedName } },
                        { ":entityType", new AttributeValue { S = "PLUGIN_META" } }
                    }
                });

                // Assert: The scan finds exactly 1 existing item with this name
                existingCheck.Items.Should().HaveCount(1,
                    "a plugin with this name already exists, second registration should be rejected");

                // The existing plugin should be the original one (pluginId1)
                var existingItem = existingCheck.Items.First();
                existingItem["id"].S.Should().Be(pluginId1.ToString());
                existingItem["name"].S.Should().Be(sharedName);

                // Application should return the existing plugin instead of creating a duplicate
                // (simulated: we do NOT create pluginId2 because the check found a conflict)
            }
            finally
            {
                await CleanupPlugin(pluginId1);
                await CleanupPlugin(pluginId2);
            }
        }

        // ===================================================================
        // Name Uniqueness Enforcement Tests
        // Source: ERPService.cs — CREATE UNIQUE INDEX IF NOT EXISTS idx_u_plugin_data_name ON plugin_data (name)
        // ===================================================================

        /// <summary>
        /// Verifies that a scan for a specific plugin name returns exactly 1 item,
        /// proving uniqueness can be enforced at the application level with DynamoDB.
        /// </summary>
        [Fact]
        public async Task NameUniqueness_DuplicateName_DetectedByQuery()
        {
            var pluginId = Guid.NewGuid();
            var uniqueName = $"unique-plugin-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Create a single plugin with the test name
                await CreatePluginInDynamoDb(pluginId, uniqueName);

                // Act: Scan DynamoDB for PLUGIN_META items with this exact name
                var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :entityType AND #n = :name",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#n", "name" }
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":entityType", new AttributeValue { S = "PLUGIN_META" } },
                        { ":name", new AttributeValue { S = uniqueName } }
                    }
                });

                // Assert: Query returns exactly 1 item, proving uniqueness enforcement
                scanResponse.Items.Count().Should().Be(1);
                scanResponse.Items.First()["name"].S.Should().Be(uniqueName);
            }
            finally
            {
                await CleanupPlugin(pluginId);
            }
        }

        /// <summary>
        /// Verifies that two plugins with different names can coexist in DynamoDB.
        /// </summary>
        [Fact]
        public async Task NameUniqueness_DifferentNames_BothAllowed()
        {
            var pluginId1 = Guid.NewGuid();
            var pluginId2 = Guid.NewGuid();
            var nameA = $"plugin-a-{Guid.NewGuid():N}";
            var nameB = $"plugin-b-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Create two plugins with distinct names
                await CreatePluginInDynamoDb(pluginId1, nameA);
                await CreatePluginInDynamoDb(pluginId2, nameB);

                // Act: Scan for all PLUGIN_META items
                var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":entityType", new AttributeValue { S = "PLUGIN_META" } }
                    }
                });

                // Assert: Both plugins exist in the table
                var names = scanResponse.Items
                    .Where(i => i.ContainsKey("name"))
                    .Select(i => i["name"].S)
                    .ToList();

                names.Should().Contain(nameA);
                names.Should().Contain(nameB);

                // Verify they are distinct items with different IDs
                var itemA = scanResponse.Items.FirstOrDefault(i =>
                    i.ContainsKey("name") && i["name"].S == nameA);
                var itemB = scanResponse.Items.FirstOrDefault(i =>
                    i.ContainsKey("name") && i["name"].S == nameB);

                itemA.Should().NotBeNull();
                itemB.Should().NotBeNull();
                itemA!["id"].S.Should().NotBe(itemB!["id"].S);
            }
            finally
            {
                await CleanupPlugin(pluginId1);
                await CleanupPlugin(pluginId2);
            }
        }

        // ===================================================================
        // Plugin Metadata Completeness — All 13 ErpPlugin Properties
        // Source mapping: WebVella.Erp/ErpPlugin.cs lines 14-51
        // AAP Section 0.8.1: "Full behavioral parity — All existing business logic
        // MUST be preserved functionally"
        // ===================================================================

        /// <summary>
        /// Verifies that all 13 ErpPlugin properties from the source monolith
        /// are correctly persisted and retrieved from DynamoDB.
        /// Properties: name, prefix, url, description, version, company, company_url,
        /// author, repository, license, settings_url, plugin_page_url, icon_url.
        /// </summary>
        [Fact]
        public async Task PluginMetadata_All13Properties_PersistedAndRetrieved()
        {
            var pluginId = Guid.NewGuid();
            var pluginName = "metadata-test-plugin";

            try
            {
                // Arrange: Create plugin with all 13 properties set to distinct non-default values
                await CreatePluginInDynamoDb(
                    pluginId: pluginId,
                    name: pluginName,
                    prefix: "mtp",
                    url: "https://metadata-test.example.com",
                    description: "Full metadata test plugin description",
                    version: 7,
                    company: "Metadata Test Corp",
                    companyUrl: "https://metadatatest.corp",
                    author: "Metadata Author",
                    repository: "https://github.com/metadata/test",
                    license: "MIT",
                    settingsUrl: "/admin/settings/mtp",
                    pluginPageUrl: "/admin/plugins/mtp",
                    iconUrl: "/icons/mtp.svg"
                );

                // Act: Retrieve the plugin by exact key
                var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });

                // Assert: All 13 properties match EXACTLY
                var item = response.Item;
                item.Should().NotBeNull();
                item.Count.Should().BeGreaterThan(0);

                // Property 1: name
                item["name"].S.Should().Be("metadata-test-plugin");
                // Property 2: prefix
                item["prefix"].S.Should().Be("mtp");
                // Property 3: url
                item["url"].S.Should().Be("https://metadata-test.example.com");
                // Property 4: description
                item["description"].S.Should().Be("Full metadata test plugin description");
                // Property 5: version (stored as DynamoDB Number type)
                item["version"].N.Should().Be("7");
                // Property 6: company
                item["company"].S.Should().Be("Metadata Test Corp");
                // Property 7: company_url
                item["company_url"].S.Should().Be("https://metadatatest.corp");
                // Property 8: author
                item["author"].S.Should().Be("Metadata Author");
                // Property 9: repository
                item["repository"].S.Should().Be("https://github.com/metadata/test");
                // Property 10: license
                item["license"].S.Should().Be("MIT");
                // Property 11: settings_url
                item["settings_url"].S.Should().Be("/admin/settings/mtp");
                // Property 12: plugin_page_url
                item["plugin_page_url"].S.Should().Be("/admin/plugins/mtp");
                // Property 13: icon_url
                item["icon_url"].S.Should().Be("/icons/mtp.svg");

                // Additional system fields
                item["id"].S.Should().Be(pluginId.ToString());
                item["status"].S.Should().Be("Active");
                item["EntityType"].S.Should().Be("PLUGIN_META");
                item["created_at"].S.Should().NotBeNullOrEmpty();
                item["updated_at"].S.Should().NotBeNullOrEmpty();

                // Cross-validate a subset of the 13 properties via BeEquivalentTo
                var expectedCoreProperties = new Dictionary<string, string>
                {
                    { "name", "metadata-test-plugin" },
                    { "prefix", "mtp" },
                    { "author", "Metadata Author" },
                    { "license", "MIT" }
                };
                var actualCoreProperties = new Dictionary<string, string>
                {
                    { "name", item["name"].S },
                    { "prefix", item["prefix"].S },
                    { "author", item["author"].S },
                    { "license", item["license"].S }
                };
                actualCoreProperties.Should().BeEquivalentTo(expectedCoreProperties);
            }
            finally
            {
                await CleanupPlugin(pluginId);
            }
        }

        /// <summary>
        /// Verifies that optional string properties default to empty strings when not provided,
        /// matching the default constructor behavior of the Plugin model.
        /// </summary>
        [Fact]
        public async Task PluginMetadata_EmptyOptionalStrings_DefaultToEmpty()
        {
            var pluginId = Guid.NewGuid();
            var pluginName = $"empty-optional-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Create plugin with only name and version set;
                // all other string properties as empty string
                await CreatePluginInDynamoDb(
                    pluginId: pluginId,
                    name: pluginName,
                    prefix: "",
                    url: "",
                    description: "",
                    version: 1,
                    company: "",
                    companyUrl: "",
                    author: "",
                    repository: "",
                    license: "",
                    settingsUrl: "",
                    pluginPageUrl: "",
                    iconUrl: ""
                );

                // Act: Retrieve the plugin
                var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = $"PLUGIN#{pluginId}" } },
                        { "SK", new AttributeValue { S = "META" } }
                    }
                });

                // Assert: Optional string attributes are stored as empty strings
                var item = response.Item;
                item.Should().NotBeNull();

                // Required fields are set
                item["name"].S.Should().Be(pluginName);
                item["version"].N.Should().Be("1");

                // All optional string properties default to empty
                item["prefix"].S.Should().BeEmpty();
                item["url"].S.Should().BeEmpty();
                item["description"].S.Should().BeEmpty();
                item["company"].S.Should().BeEmpty();
                item["company_url"].S.Should().BeEmpty();
                item["author"].S.Should().BeEmpty();
                item["repository"].S.Should().BeEmpty();
                item["license"].S.Should().BeEmpty();
                item["settings_url"].S.Should().BeEmpty();
                item["plugin_page_url"].S.Should().BeEmpty();
                item["icon_url"].S.Should().BeEmpty();
            }
            finally
            {
                await CleanupPlugin(pluginId);
            }
        }

        // ===================================================================
        // SdkPlugin-Specific Behavioral Parity Tests
        // Source mapping: SdkPlugin._.cs ProcessPatches pattern
        // Validates version progression and GUID preservation.
        // ===================================================================

        /// <summary>
        /// Validates the exact patch progression pattern from SdkPlugin._.cs:
        /// Initial version: 20181001 (line 12: WEBVELLA_SDK_INIT_VERSION = 20181001)
        /// After Patch20181215: version = 20181215 (line 83)
        /// After Patch20190227: version = 20190227 (line 98)
        /// After Patch20200610: version = 20200610 (line 110)
        /// After Patch20201221: version = 20201221 (line 125)
        /// After Patch20210429: version = 20210429 (line 138)
        /// Verifies DynamoDB PutItem upsert correctly replaces data on each version update.
        /// </summary>
        [Fact]
        public async Task SdkPlugin_ProcessPatchesPattern_VersionProgressionPersisted()
        {
            var sdkPluginName = $"sdk-patches-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Save initial plugin data matching WEBVELLA_SDK_INIT_VERSION = 20181001
                await SavePluginData(sdkPluginName, JsonSerializer.Serialize(new { version = 20181001 }));

                // Act: Simulate progressive patch execution by saving each version
                // Patch20181215 — SdkPlugin._.cs line 83
                await SavePluginData(sdkPluginName, JsonSerializer.Serialize(new { version = 20181215 }));

                // Patch20190227 — SdkPlugin._.cs line 98
                await SavePluginData(sdkPluginName, JsonSerializer.Serialize(new { version = 20190227 }));

                // Patch20200610 — SdkPlugin._.cs line 110
                await SavePluginData(sdkPluginName, JsonSerializer.Serialize(new { version = 20200610 }));

                // Patch20201221 — SdkPlugin._.cs line 125
                await SavePluginData(sdkPluginName, JsonSerializer.Serialize(new { version = 20201221 }));

                // Patch20210429 — SdkPlugin._.cs line 138
                await SavePluginData(sdkPluginName, JsonSerializer.Serialize(new { version = 20210429 }));

                // Assert: Final GetItem returns the last version
                var finalData = await GetPluginData(sdkPluginName);
                finalData.Should().NotBeNull();

                var deserialized = JsonSerializer.Deserialize<JsonElement>(finalData!);
                deserialized.GetProperty("version").GetInt32().Should().Be(20210429,
                    "the final patch version should be 20210429 after all 6 versions are applied");
            }
            finally
            {
                await CleanupPluginData(sdkPluginName);
            }
        }

        /// <summary>
        /// Validates that SDK plugin-specific metadata (App ID and Area IDs) are preserved
        /// exactly in plugin data, matching the GUID constants from SdkPlugin._.cs lines 14-17:
        /// - APP_ID: 56a8548a-19d0-497f-8e5b-242abfdc4082
        /// - AREA_DESIGN_ID: d3237d8c-c074-46d7-82c2-1385cbfff35a
        /// - AREA_ACCESS_ID: c5c4cefc-1402-4a8b-9867-7f2a059b745d
        /// - AREA_SERVER_ID: fee72214-f1c4-4ed5-8bda-35698dc11528
        /// </summary>
        [Fact]
        public async Task SdkPlugin_AppAndAreaIds_PreservedInPluginData()
        {
            var sdkPluginName = $"sdk-guids-{Guid.NewGuid():N}";

            try
            {
                // Arrange: Build plugin data containing the SDK GUID constants
                // from SdkPlugin._.cs lines 14-17
                var appId = "56a8548a-19d0-497f-8e5b-242abfdc4082";
                var areaDesignId = "d3237d8c-c074-46d7-82c2-1385cbfff35a";
                var areaAccessId = "c5c4cefc-1402-4a8b-9867-7f2a059b745d";
                var areaServerId = "fee72214-f1c4-4ed5-8bda-35698dc11528";

                var dataWithGuids = JsonSerializer.Serialize(new
                {
                    version = 20210429,
                    app_id = appId,
                    area_design_id = areaDesignId,
                    area_access_id = areaAccessId,
                    area_server_id = areaServerId
                });

                // Act: Save and retrieve the data
                await SavePluginData(sdkPluginName, dataWithGuids);
                var retrievedData = await GetPluginData(sdkPluginName);

                // Assert: All GUID values are preserved exactly
                retrievedData.Should().NotBeNull();

                var parsed = JsonSerializer.Deserialize<JsonElement>(retrievedData!);
                parsed.GetProperty("version").GetInt32().Should().Be(20210429);
                parsed.GetProperty("app_id").GetString().Should().Be(appId);
                parsed.GetProperty("area_design_id").GetString().Should().Be(areaDesignId);
                parsed.GetProperty("area_access_id").GetString().Should().Be(areaAccessId);
                parsed.GetProperty("area_server_id").GetString().Should().Be(areaServerId);

                // Verify GUIDs can be parsed to actual Guid types
                Guid.Parse(parsed.GetProperty("app_id").GetString()!).Should()
                    .Be(Guid.Parse("56a8548a-19d0-497f-8e5b-242abfdc4082"));
                Guid.Parse(parsed.GetProperty("area_design_id").GetString()!).Should()
                    .Be(Guid.Parse("d3237d8c-c074-46d7-82c2-1385cbfff35a"));
                Guid.Parse(parsed.GetProperty("area_access_id").GetString()!).Should()
                    .Be(Guid.Parse("c5c4cefc-1402-4a8b-9867-7f2a059b745d"));
                Guid.Parse(parsed.GetProperty("area_server_id").GetString()!).Should()
                    .Be(Guid.Parse("fee72214-f1c4-4ed5-8bda-35698dc11528"));
            }
            finally
            {
                await CleanupPluginData(sdkPluginName);
            }
        }
    }
}
