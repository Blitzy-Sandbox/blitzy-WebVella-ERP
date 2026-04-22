using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Xunit;

namespace WebVellaErp.PluginSystem.Tests.Integration
{
    /// <summary>
    /// Integration tests for DynamoDB single-table design of the Plugin / Extension System service.
    /// All tests execute against real LocalStack DynamoDB — zero mocked AWS SDK calls.
    /// Per AAP Section 0.8.4: "All integration and E2E tests MUST execute against LocalStack."
    ///
    /// Validates:
    /// - Table schema and GSI configuration
    /// - Plugin CRUD operations (PutItem/GetItem/DeleteItem)
    /// - GSI1 status-based queries
    /// - Plugin data persistence (GetPluginData/SavePluginData behavioral parity)
    /// - Scan/list operations with EntityType discriminator
    /// - Complete attribute mapping for all 13 ErpPlugin properties
    ///
    /// DynamoDB Key Patterns:
    ///   PK=PLUGIN#{pluginId}, SK=META                → Plugin metadata definitions
    ///   PK=PLUGIN#{pluginName}, SK=DATA               → Plugin settings/data (JSON)
    ///   GSI1PK=STATUS#{status}, GSI1SK=NAME#{name}    → Status-based queries
    ///
    /// EntityType Discriminators:
    ///   PLUGIN_META  → Plugin metadata items
    ///   PLUGIN_DATA  → Plugin settings/data items
    ///
    /// Source mappings:
    ///   - WebVella.Erp/ErpPlugin.cs: 13 plugin metadata properties (lines 14-51)
    ///   - WebVella.Erp/ErpPlugin.cs: GetPluginData() (lines 67-85), SavePluginData() (lines 87-115)
    ///   - WebVella.Erp.Plugins.SDK/SdkPlugin._.cs: ProcessPatches version progression (final: 20210429)
    /// </summary>
    public class PluginRepositoryIntegrationTests : IClassFixture<LocalStackFixture>
    {
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly string _tableName;

        /// <summary>
        /// Constructor injects the shared LocalStack fixture providing a real DynamoDB client
        /// and table name provisioned by IAsyncLifetime. Per AAP: zero mocked AWS SDK calls.
        /// </summary>
        public PluginRepositoryIntegrationTests(LocalStackFixture fixture)
        {
            _dynamoDbClient = fixture.DynamoDbClient;
            _tableName = fixture.TableName;
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Table Schema and GSI Verification Tests
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies the DynamoDB plugin table exists with correct PK (HASH) / SK (RANGE)
        /// key schema using String attribute types, validating the single-table design.
        /// </summary>
        [Fact]
        public async Task PluginTable_ExistsWithCorrectSchema()
        {
            // Act: DescribeTable for the plugin-system table
            var response = await _dynamoDbClient.DescribeTableAsync(new DescribeTableRequest
            {
                TableName = _tableName
            });

            // Assert: Table status is ACTIVE
            response.Table.TableStatus.Value.Should().Be("ACTIVE");

            // Assert: Key schema has PK (HASH) and SK (RANGE)
            var keySchema = response.Table.KeySchema;
            keySchema.Should().HaveCount(2);

            var pkKey = keySchema.FirstOrDefault(k => k.AttributeName == "PK");
            pkKey.Should().NotBeNull();
            pkKey!.KeyType.Value.Should().Be("HASH");

            var skKey = keySchema.FirstOrDefault(k => k.AttributeName == "SK");
            skKey.Should().NotBeNull();
            skKey!.KeyType.Value.Should().Be("RANGE");

            // Assert: Both keys are String type (S)
            var attrDefs = response.Table.AttributeDefinitions;

            var pkAttr = attrDefs.FirstOrDefault(a => a.AttributeName == "PK");
            pkAttr.Should().NotBeNull();
            pkAttr!.AttributeType.Value.Should().Be("S");

            var skAttr = attrDefs.FirstOrDefault(a => a.AttributeName == "SK");
            skAttr.Should().NotBeNull();
            skAttr!.AttributeType.Value.Should().Be("S");
        }

        /// <summary>
        /// Verifies GSI1 exists for status-based plugin queries with correct key schema
        /// (GSI1PK HASH, GSI1SK RANGE) and ALL projection type. Enables querying
        /// plugins by GSI1PK=STATUS#{status}, GSI1SK=NAME#{name}.
        /// </summary>
        [Fact]
        public async Task PluginTable_HasGSI1ForStatusQueries()
        {
            // Act: DescribeTable and inspect GlobalSecondaryIndexes
            var response = await _dynamoDbClient.DescribeTableAsync(new DescribeTableRequest
            {
                TableName = _tableName
            });

            // Assert: GSI named "GSI1" exists
            var gsi1 = response.Table.GlobalSecondaryIndexes
                .FirstOrDefault(g => g.IndexName == "GSI1");
            gsi1.Should().NotBeNull("GSI1 index should exist for status-based plugin queries");

            // Assert: GSI1 key schema: GSI1PK (HASH, String), GSI1SK (RANGE, String)
            var gsi1KeySchema = gsi1!.KeySchema;
            gsi1KeySchema.Should().HaveCount(2);

            var gsi1pk = gsi1KeySchema.FirstOrDefault(k => k.AttributeName == "GSI1PK");
            gsi1pk.Should().NotBeNull();
            gsi1pk!.KeyType.Value.Should().Be("HASH");

            var gsi1sk = gsi1KeySchema.FirstOrDefault(k => k.AttributeName == "GSI1SK");
            gsi1sk.Should().NotBeNull();
            gsi1sk!.KeyType.Value.Should().Be("RANGE");

            // Verify GSI1PK and GSI1SK are String type in attribute definitions
            var attrDefs = response.Table.AttributeDefinitions;

            var gsi1pkAttr = attrDefs.FirstOrDefault(a => a.AttributeName == "GSI1PK");
            gsi1pkAttr.Should().NotBeNull();
            gsi1pkAttr!.AttributeType.Value.Should().Be("S");

            var gsi1skAttr = attrDefs.FirstOrDefault(a => a.AttributeName == "GSI1SK");
            gsi1skAttr.Should().NotBeNull();
            gsi1skAttr!.AttributeType.Value.Should().Be("S");

            // Assert: GSI1 projection is ALL
            gsi1.Projection.ProjectionType.Value.Should().Be("ALL");
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Plugin CRUD Tests (PutItem / GetItem / UpdateItem / DeleteItem)
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Full round-trip: PutItem a plugin metadata item with ALL 13 ErpPlugin properties
        /// (name, prefix, url, description, version, company, company_url, author, repository,
        /// license, settings_url, plugin_page_url, icon_url) plus DynamoDB keys and timestamps,
        /// then GetItem by PK/SK and assert every attribute is preserved exactly.
        /// Source mapping: WebVella.Erp/ErpPlugin.cs lines 14-51 (all JsonProperty properties).
        /// </summary>
        [Fact]
        public async Task CreatePlugin_PutItem_GetItem_RoundTrip()
        {
            // Arrange: Build plugin item with ALL 13+metadata attributes
            var pluginId = Guid.NewGuid().ToString();
            var pluginName = $"test-plugin-{Guid.NewGuid():N}";
            var now = DateTime.UtcNow.ToString("o");

            var item = BuildPluginMetaItem(
                pluginId: pluginId,
                name: pluginName,
                status: "Active",
                prefix: "tp",
                url: "https://test-plugin.example.com",
                description: "A test plugin",
                version: 1,
                company: "Test Company",
                companyUrl: "https://testco.example.com",
                author: "Test Author",
                repository: "https://github.com/test/plugin",
                license: "Apache-2.0",
                settingsUrl: "/settings/test-plugin",
                pluginPageUrl: "/plugin/test-plugin",
                iconUrl: "/icons/test-plugin.png",
                createdAt: now,
                updatedAt: now
            );

            try
            {
                // Act: PutItem then GetItem
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                });

                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                });

                // Assert: Item exists
                getResponse.Item.Should().NotBeEmpty();
                getResponse.IsItemSet.Should().BeTrue();

                var result = getResponse.Item;

                // Assert: All 13 ErpPlugin properties preserved
                result["id"].S.Should().Be(pluginId);
                result["name"].S.Should().Be(pluginName);
                result["prefix"].S.Should().Be("tp");
                result["url"].S.Should().Be("https://test-plugin.example.com");
                result["description"].S.Should().Be("A test plugin");
                result["version"].N.Should().Be("1");
                result["company"].S.Should().Be("Test Company");
                result["company_url"].S.Should().Be("https://testco.example.com");
                result["author"].S.Should().Be("Test Author");
                result["repository"].S.Should().Be("https://github.com/test/plugin");
                result["license"].S.Should().Be("Apache-2.0");
                result["settings_url"].S.Should().Be("/settings/test-plugin");
                result["plugin_page_url"].S.Should().Be("/plugin/test-plugin");
                result["icon_url"].S.Should().Be("/icons/test-plugin.png");

                // Assert: Status and EntityType
                result["status"].S.Should().Be("Active");
                result["EntityType"].S.Should().Be("PLUGIN_META");

                // Assert: Timestamps are valid ISO 8601
                result["created_at"].S.Should().NotBeNullOrEmpty();
                result["updated_at"].S.Should().NotBeNullOrEmpty();
                DateTime.TryParse(result["created_at"].S, out var parsedCreatedAt).Should().BeTrue();
                DateTime.TryParse(result["updated_at"].S, out var parsedUpdatedAt).Should().BeTrue();
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
            }
        }

        /// <summary>
        /// Tests idempotent create with optimistic concurrency using ConditionExpression
        /// attribute_not_exists(PK). Second PutItem with same key should throw
        /// ConditionalCheckFailedException. Per AAP Section 0.8.5: idempotency keys on all writes.
        /// </summary>
        [Fact]
        public async Task CreatePlugin_ConditionalWrite_PreventsOverwrite()
        {
            // Arrange: Create a plugin first
            var pluginId = Guid.NewGuid().ToString();
            var item = BuildPluginMetaItem(pluginId, $"cond-write-{Guid.NewGuid():N}", "Active");

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            });

            try
            {
                // Act: Attempt PutItem with ConditionExpression preventing overwrite
                Func<Task> act = async () => await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item,
                    ConditionExpression = "attribute_not_exists(PK)"
                });

                // Assert: Should throw ConditionalCheckFailedException
                await act.Should().ThrowAsync<ConditionalCheckFailedException>();
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
            }
        }

        /// <summary>
        /// GetItem with a non-existent PK returns an empty item (no exception thrown).
        /// Validates graceful handling of missing records.
        /// </summary>
        [Fact]
        public async Task GetPluginById_NonExistentId_ReturnsEmpty()
        {
            // Act: GetItem with non-existent key
            var nonExistentId = Guid.NewGuid().ToString();
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"PLUGIN#{nonExistentId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            // Assert: Item should be empty (no exception thrown)
            response.Item.Should().BeEmpty();
            response.IsItemSet.Should().BeFalse();
        }

        /// <summary>
        /// PutItem update: writes new values for description, version, and updated_at to an
        /// existing item. GetItem confirms updated values while other attributes remain unchanged.
        /// </summary>
        [Fact]
        public async Task UpdatePlugin_PersistsChanges()
        {
            // Arrange: PutItem a plugin
            var pluginId = Guid.NewGuid().ToString();
            var name = $"update-test-{Guid.NewGuid():N}";
            var originalItem = BuildPluginMetaItem(pluginId, name, "Active",
                description: "Original description", version: 1);

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = originalItem
            });

            try
            {
                // Act: PutItem same key with updated description, version, updated_at
                var updatedAt = DateTime.UtcNow.ToString("o");
                var updatedItem = BuildPluginMetaItem(pluginId, name, "Active",
                    description: "Updated description", version: 2,
                    updatedAt: updatedAt);

                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = updatedItem
                });

                // Assert: GetItem returns updated values while name remains unchanged
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                });

                getResponse.Item.Should().NotBeEmpty();
                getResponse.Item["description"].S.Should().Be("Updated description");
                getResponse.Item["version"].N.Should().Be("2");
                getResponse.Item["name"].S.Should().Be(name);
                getResponse.Item["id"].S.Should().Be(pluginId);
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
            }
        }

        /// <summary>
        /// Verifies that changing a plugin's status from Active to Inactive correctly updates
        /// the GSI1 keys. After update, querying GSI1 with STATUS#Inactive finds the plugin,
        /// while querying with STATUS#Active does NOT.
        /// </summary>
        [Fact]
        public async Task UpdatePlugin_StatusChange_UpdatesGSI1Keys()
        {
            // Arrange: PutItem a plugin with GSI1PK=STATUS#Active
            var pluginId = Guid.NewGuid().ToString();
            var name = $"status-change-{Guid.NewGuid():N}";
            var activeItem = BuildPluginMetaItem(pluginId, name, "Active");

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = activeItem
            });

            try
            {
                // Act: PutItem same key with status=Inactive (GSI1PK changes to STATUS#Inactive)
                var inactiveItem = BuildPluginMetaItem(pluginId, name, "Inactive");
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = inactiveItem
                });

                // Assert: GetItem returns status=Inactive and updated GSI1PK
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                });

                getResponse.Item["status"].S.Should().Be("Inactive");
                getResponse.Item["GSI1PK"].S.Should().Be("STATUS#Inactive");

                // Assert: Query GSI1 with STATUS#Inactive returns the plugin
                var inactiveQuery = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk AND GSI1SK = :sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "STATUS#Inactive" },
                        [":sk"] = new AttributeValue { S = $"NAME#{name}" }
                    }
                });

                inactiveQuery.Items.Should().HaveCount(1);
                inactiveQuery.Items[0]["id"].S.Should().Be(pluginId);

                // Assert: Query GSI1 with STATUS#Active does NOT return this plugin
                var activeQuery = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk AND GSI1SK = :sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "STATUS#Active" },
                        [":sk"] = new AttributeValue { S = $"NAME#{name}" }
                    }
                });

                activeQuery.Items.Should().BeEmpty();
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
            }
        }

        /// <summary>
        /// Verifies DeleteItem removes the plugin entry and subsequent GetItem returns empty.
        /// </summary>
        [Fact]
        public async Task DeletePlugin_RemovesEntry()
        {
            // Arrange: PutItem a plugin
            var pluginId = Guid.NewGuid().ToString();
            var item = BuildPluginMetaItem(pluginId, $"delete-test-{Guid.NewGuid():N}", "Active");

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            });

            // Act: DeleteItem
            await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            // Assert: GetItem returns empty
            var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            getResponse.Item.Should().BeEmpty();
        }

        /// <summary>
        /// DeleteItem with a non-existent key does not throw an exception.
        /// Validates idempotent delete per AAP Section 0.8.5.
        /// </summary>
        [Fact]
        public async Task DeletePlugin_NonExistentItem_DoesNotThrow()
        {
            // Act: DeleteItem with non-existent key
            var nonExistentId = Guid.NewGuid().ToString();

            Func<Task> act = async () => await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"PLUGIN#{nonExistentId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            // Assert: No exception thrown (idempotent delete)
            await act.Should().NotThrowAsync();
        }

        // ──────────────────────────────────────────────────────────────────────────
        // GSI1 Status Query Tests
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates 2 Active + 1 Inactive plugins, queries GSI1 with GSI1PK=STATUS#Active,
        /// verifies only Active plugins are returned and the Inactive one is excluded.
        /// </summary>
        [Fact]
        public async Task GetPluginsByStatus_Active_ReturnsOnlyActivePlugins()
        {
            // Arrange: Create 2 Active plugins + 1 Inactive plugin
            var testSuffix = Guid.NewGuid().ToString("N")[..8];
            var activeIds = new List<string>();
            var inactiveId = Guid.NewGuid().ToString();

            for (int i = 0; i < 2; i++)
            {
                var id = Guid.NewGuid().ToString();
                activeIds.Add(id);
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = BuildPluginMetaItem(id, $"active-{testSuffix}-{i}", "Active")
                });
            }

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = BuildPluginMetaItem(inactiveId, $"inactive-{testSuffix}", "Inactive")
            });

            try
            {
                // Act: Query GSI1 with GSI1PK=STATUS#Active
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "STATUS#Active" }
                    }
                });

                // Assert: At least 2 Active items (may include fixture-seeded SDK plugin)
                queryResponse.Items.Count.Should().BeGreaterThanOrEqualTo(2);

                // All returned items should have status=Active
                queryResponse.Items.All(item => item["status"].S == "Active").Should().BeTrue();

                // Our 2 Active test plugins should be in the results
                var returnedIds = queryResponse.Items.Select(item => item["id"].S).ToList();
                foreach (var id in activeIds)
                {
                    returnedIds.Should().Contain(id);
                }

                // Inactive test plugin should NOT be in Active results
                returnedIds.Should().NotContain(inactiveId);
            }
            finally
            {
                foreach (var id in activeIds)
                {
                    await DeleteItemSafelyAsync($"PLUGIN#{id}", "META");
                }
                await DeleteItemSafelyAsync($"PLUGIN#{inactiveId}", "META");
            }
        }

        /// <summary>
        /// Creates 1 Active + 2 Inactive plugins, queries GSI1 with GSI1PK=STATUS#Inactive,
        /// verifies only Inactive plugins are returned and the Active one is excluded.
        /// </summary>
        [Fact]
        public async Task GetPluginsByStatus_Inactive_ReturnsOnlyInactivePlugins()
        {
            // Arrange: Create 1 Active plugin + 2 Inactive plugins
            var testSuffix = Guid.NewGuid().ToString("N")[..8];
            var activeId = Guid.NewGuid().ToString();
            var inactiveIds = new List<string>();

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = BuildPluginMetaItem(activeId, $"active-{testSuffix}", "Active")
            });

            for (int i = 0; i < 2; i++)
            {
                var id = Guid.NewGuid().ToString();
                inactiveIds.Add(id);
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = BuildPluginMetaItem(id, $"inactive-{testSuffix}-{i}", "Inactive")
                });
            }

            try
            {
                // Act: Query GSI1 with GSI1PK=STATUS#Inactive
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "STATUS#Inactive" }
                    }
                });

                // Assert: At least 2 Inactive items returned
                queryResponse.Items.Count.Should().BeGreaterThanOrEqualTo(2);

                // All returned items should have status=Inactive
                queryResponse.Items.All(item => item["status"].S == "Inactive").Should().BeTrue();

                // Our 2 Inactive test plugins should be in the results
                var returnedIds = queryResponse.Items.Select(item => item["id"].S).ToList();
                foreach (var id in inactiveIds)
                {
                    returnedIds.Should().Contain(id);
                }

                // Active test plugin should NOT be in Inactive results
                returnedIds.Should().NotContain(activeId);
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{activeId}", "META");
                foreach (var id in inactiveIds)
                {
                    await DeleteItemSafelyAsync($"PLUGIN#{id}", "META");
                }
            }
        }

        /// <summary>
        /// Creates a plugin with a unique name, queries GSI1 with composite key
        /// GSI1PK=STATUS#Active AND GSI1SK=NAME#{name}, verifies exactly 1 item
        /// with the correct pluginId and name is returned.
        /// Replaces: SELECT * FROM plugin_data WHERE name = @name (ErpPlugin.cs line 74).
        /// </summary>
        [Fact]
        public async Task GetPluginByName_ViaGSI1_FindsCorrectPlugin()
        {
            // Arrange: Create plugin with specific name and GSI1SK=NAME#{name}
            var pluginId = Guid.NewGuid().ToString();
            var pluginName = $"test-find-{Guid.NewGuid():N}";

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = BuildPluginMetaItem(pluginId, pluginName, "Active")
            });

            try
            {
                // Act: Query GSI1 with GSI1PK=STATUS#Active AND GSI1SK=NAME#{name}
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk AND GSI1SK = :sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "STATUS#Active" },
                        [":sk"] = new AttributeValue { S = $"NAME#{pluginName}" }
                    }
                });

                // Assert: Exactly 1 item returned with correct pluginId and name
                queryResponse.Items.Should().HaveCount(1);
                queryResponse.Items[0]["id"].S.Should().Be(pluginId);
                queryResponse.Items[0]["name"].S.Should().Be(pluginName);
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
            }
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Plugin Data Persistence Tests (GetPluginData / SavePluginData parity)
        // Behavioral parity with ErpPlugin.GetPluginData() (lines 67-85)
        // and ErpPlugin.SavePluginData(string data) (lines 87-115).
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates GetPluginData: PutItem with PK=PLUGIN#{name}, SK=DATA, then GetItem
        /// returns the exact JSON data string. Direct replacement for source
        /// ErpPlugin.GetPluginData() which returned (string)dt.Rows[0]["data"].
        /// Source mapping: ErpPlugin.cs lines 72-84.
        /// </summary>
        [Fact]
        public async Task GetPluginData_ExistingPlugin_ReturnsData()
        {
            // Arrange: PutItem with plugin data
            var pluginName = $"data-get-{Guid.NewGuid():N}";
            var dataItem = BuildPluginDataItem(pluginName, "{\"version\":20210429}");

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = dataItem
            });

            try
            {
                // Act: GetItem with PK=PLUGIN#{name}, SK=DATA
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginName}" },
                        ["SK"] = new AttributeValue { S = "DATA" }
                    }
                });

                // Assert: data attribute matches exactly
                getResponse.Item.Should().NotBeEmpty();
                getResponse.IsItemSet.Should().BeTrue();
                getResponse.Item["data"].S.Should().Be("{\"version\":20210429}");
                getResponse.Item["EntityType"].S.Should().Be("PLUGIN_DATA");
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginName}", "DATA");
            }
        }

        /// <summary>
        /// GetPluginData for a non-existent plugin returns null/empty item.
        /// Matches source behavior: "if (dt.Rows.Count == 0) return null;" at ErpPlugin.cs line 80-81.
        /// </summary>
        [Fact]
        public async Task GetPluginData_NonExistentPlugin_ReturnsNull()
        {
            // Act: GetItem with non-existent PK
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"PLUGIN#nonexistent-{Guid.NewGuid():N}" },
                    ["SK"] = new AttributeValue { S = "DATA" }
                }
            });

            // Assert: Item is empty (returning null matches source behavior)
            response.Item.Should().BeEmpty();
            response.IsItemSet.Should().BeFalse();
        }

        /// <summary>
        /// SavePluginData INSERT path: PutItem with PK=PLUGIN#{name}, SK=DATA for a new plugin.
        /// Validates source INSERT path (ErpPlugin.cs lines 96-103):
        /// INSERT INTO plugin_data (id,name,data) VALUES(@id,@name,@data)
        /// </summary>
        [Fact]
        public async Task SavePluginData_NewPlugin_InsertsData()
        {
            // Arrange: No existing data for this plugin name
            var pluginName = $"test-save-{Guid.NewGuid():N}";

            try
            {
                // Act: PutItem with new plugin data
                var dataItem = BuildPluginDataItem(pluginName, "{\"version\":1}");
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = dataItem
                });

                // Assert: GetItem returns the inserted data
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginName}" },
                        ["SK"] = new AttributeValue { S = "DATA" }
                    }
                });

                getResponse.Item.Should().NotBeEmpty();
                getResponse.IsItemSet.Should().BeTrue();
                getResponse.Item["data"].S.Should().Be("{\"version\":1}");
                getResponse.Item["plugin_name"].S.Should().Be(pluginName);
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginName}", "DATA");
            }
        }

        /// <summary>
        /// SavePluginData UPDATE (upsert) path: PutItem initial data, then PutItem same key
        /// with new data value. DynamoDB PutItem is naturally an upsert, simplifying the
        /// source's INSERT-or-UPDATE pattern.
        /// Source mapping: ErpPlugin.cs lines 107-113: UPDATE plugin_data SET data = @data WHERE name = @name
        /// </summary>
        [Fact]
        public async Task SavePluginData_ExistingPlugin_UpdatesData()
        {
            // Arrange: PutItem initial data
            var pluginName = $"test-upsert-{Guid.NewGuid():N}";
            var initialItem = BuildPluginDataItem(pluginName, "{\"version\":1}");

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = initialItem
            });

            try
            {
                // Act: PutItem same key with updated data (DynamoDB upsert semantics)
                var updatedItem = BuildPluginDataItem(pluginName, "{\"version\":2}");
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = updatedItem
                });

                // Assert: GetItem returns updated data (previous value overwritten)
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginName}" },
                        ["SK"] = new AttributeValue { S = "DATA" }
                    }
                });

                getResponse.Item.Should().NotBeEmpty();
                getResponse.Item["data"].S.Should().Be("{\"version\":2}");
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginName}", "DATA");
            }
        }

        /// <summary>
        /// Validates plugin_name attribute is preserved in data items — behavioral parity
        /// with source's UNIQUE constraint on the name column in plugin_data table.
        /// </summary>
        [Fact]
        public async Task SavePluginData_PreservesPluginName()
        {
            // Arrange: Save data for a specific plugin name
            var pluginName = $"name-preserve-{Guid.NewGuid():N}";
            var dataItem = BuildPluginDataItem(pluginName, "{\"version\":1}");

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = dataItem
            });

            try
            {
                // Act: GetItem
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginName}" },
                        ["SK"] = new AttributeValue { S = "DATA" }
                    }
                });

                // Assert: plugin_name attribute stores the name correctly
                getResponse.Item.Should().NotBeEmpty();
                getResponse.Item["plugin_name"].S.Should().Be(pluginName);
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginName}", "DATA");
            }
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Scan / List Operations
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates 3 PLUGIN_META items + 2 PLUGIN_DATA items, scans with EntityType filter
        /// for PLUGIN_META, verifies only metadata items are returned (not data items).
        /// Validates EntityType discriminator pattern in single-table design.
        /// </summary>
        [Fact]
        public async Task ListPlugins_ScanByEntityType_ReturnsOnlyPlugins()
        {
            // Arrange: Create 3 META items + 2 DATA items
            var testSuffix = Guid.NewGuid().ToString("N")[..8];
            var pluginIds = new List<string>();
            var dataKeys = new List<string>();

            for (int i = 0; i < 3; i++)
            {
                var pluginId = Guid.NewGuid().ToString();
                pluginIds.Add(pluginId);
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = BuildPluginMetaItem(pluginId, $"scan-meta-{testSuffix}-{i}", "Active")
                });
            }

            for (int i = 0; i < 2; i++)
            {
                var dataKey = $"scan-data-{testSuffix}-{i}";
                dataKeys.Add(dataKey);
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = BuildPluginDataItem(dataKey, $"{{\"version\":{i}}}")
                });
            }

            try
            {
                // Act: Scan with FilterExpression = "EntityType = :type", :type = "PLUGIN_META"
                var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :type",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":type"] = new AttributeValue { S = "PLUGIN_META" }
                    }
                });

                // Assert: Results are non-empty and contain at least our 3 test items
                scanResponse.Items.Should().NotBeEmpty();
                scanResponse.Items.Count.Should().BeGreaterThanOrEqualTo(3);

                // ALL scan results have EntityType = PLUGIN_META (no PLUGIN_DATA items)
                scanResponse.Items.All(item => item["EntityType"].S == "PLUGIN_META")
                    .Should().BeTrue("scan should only return PLUGIN_META items, not PLUGIN_DATA");

                // Verify our 3 test plugins are in the results
                var returnedIds = scanResponse.Items
                    .Where(item => item.ContainsKey("id"))
                    .Select(item => item["id"].S)
                    .ToList();

                foreach (var pluginId in pluginIds)
                {
                    returnedIds.Should().Contain(pluginId);
                }
            }
            finally
            {
                // Cleanup: Delete all 5 items
                foreach (var pluginId in pluginIds)
                {
                    await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
                }
                foreach (var dataKey in dataKeys)
                {
                    await DeleteItemSafelyAsync($"PLUGIN#{dataKey}", "DATA");
                }
            }
        }

        /// <summary>
        /// Scans with EntityType and name prefix filter that matches no items,
        /// verifying empty list behavior. Simulates listing plugins when none match
        /// the search criteria (shared table contains fixture-seeded data).
        /// </summary>
        [Fact]
        public async Task ListPlugins_EmptyTable_ReturnsEmptyList()
        {
            // Act: Scan with EntityType filter and a non-matching name prefix
            var uniquePrefix = $"nonexistent-{Guid.NewGuid():N}";
            var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
            {
                TableName = _tableName,
                FilterExpression = "EntityType = :type AND begins_with(#n, :prefix)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#n"] = "name"
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":type"] = new AttributeValue { S = "PLUGIN_META" },
                    [":prefix"] = new AttributeValue { S = uniquePrefix }
                }
            });

            // Assert: 0 items returned
            scanResponse.Items.Should().BeEmpty();
            scanResponse.Count.Should().Be(0);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Attribute Mapping Completeness Tests
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a plugin item with ALL 13 source ErpPlugin properties set to explicit
        /// non-default values, performs PutItem then GetItem, and asserts every single
        /// property is preserved in the DynamoDB round-trip.
        /// Source mapping: WebVella.Erp/ErpPlugin.cs lines 14-51 (all JsonProperty properties).
        /// </summary>
        [Fact]
        public async Task AllErpPluginProperties_PreservedInDynamoDbRoundTrip()
        {
            // Arrange: Plugin with ALL 13 ErpPlugin properties set to non-default values
            var pluginId = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow.ToString("o");

            var item = BuildPluginMetaItem(
                pluginId: pluginId,
                name: "round-trip-plugin",
                status: "Active",
                prefix: "rtp",
                url: "https://round-trip.example.com",
                description: "Round-trip test description",
                version: 42,
                company: "Round Trip Co",
                companyUrl: "https://roundtripco.com",
                author: "Round Trip Author",
                repository: "https://github.com/roundtrip/plugin",
                license: "MIT",
                settingsUrl: "/settings/rtp",
                pluginPageUrl: "/pages/rtp",
                iconUrl: "/icons/rtp.svg",
                createdAt: now,
                updatedAt: now
            );

            try
            {
                // Act: PutItem then GetItem
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                });

                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                });

                // Assert: Every single one of the 13 properties is preserved
                var result = getResponse.Item;
                result.Should().NotBeEmpty();

                // 1. name
                result["name"].S.Should().Be("round-trip-plugin");
                // 2. prefix
                result["prefix"].S.Should().Be("rtp");
                // 3. url
                result["url"].S.Should().Be("https://round-trip.example.com");
                // 4. description
                result["description"].S.Should().Be("Round-trip test description");
                // 5. version (stored as Number N, read back as int)
                int.Parse(result["version"].N).Should().Be(42);
                // 6. company
                result["company"].S.Should().Be("Round Trip Co");
                // 7. company_url
                result["company_url"].S.Should().Be("https://roundtripco.com");
                // 8. author
                result["author"].S.Should().Be("Round Trip Author");
                // 9. repository
                result["repository"].S.Should().Be("https://github.com/roundtrip/plugin");
                // 10. license
                result["license"].S.Should().Be("MIT");
                // 11. settings_url
                result["settings_url"].S.Should().Be("/settings/rtp");
                // 12. plugin_page_url
                result["plugin_page_url"].S.Should().Be("/pages/rtp");
                // 13. icon_url
                result["icon_url"].S.Should().Be("/icons/rtp.svg");
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
            }
        }

        /// <summary>
        /// Verifies the version field is stored as DynamoDB Number (N) type and can be
        /// read back and parsed as an integer. Uses the SDK plugin final version 20210429
        /// from SdkPlugin._.cs to validate large version numbers are preserved.
        /// Source mapping: SdkPlugin._.cs ProcessPatches (final version 20210429).
        /// </summary>
        [Fact]
        public async Task VersionField_StoredAsNumber_ReadBackAsInt()
        {
            // Arrange: PutItem with version as Number type (matching SdkPlugin version 20210429)
            var pluginId = Guid.NewGuid().ToString();
            var item = BuildPluginMetaItem(pluginId, $"version-test-{Guid.NewGuid():N}", "Active",
                version: 20210429);

            try
            {
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                });

                // Act: GetItem
                var getResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                });

                // Assert: version.N can be parsed to int 20210429
                getResponse.Item.Should().NotBeEmpty();
                getResponse.Item["version"].N.Should().NotBeNullOrEmpty();
                int.TryParse(getResponse.Item["version"].N, out var versionInt).Should().BeTrue();
                versionInt.Should().Be(20210429);
            }
            finally
            {
                await DeleteItemSafelyAsync($"PLUGIN#{pluginId}", "META");
            }
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Private Helper Methods
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a complete DynamoDB plugin META item with all 13 ErpPlugin properties,
        /// DynamoDB keys (PK, SK, GSI1PK, GSI1SK), EntityType discriminator, and timestamps.
        /// </summary>
        private static Dictionary<string, AttributeValue> BuildPluginMetaItem(
            string pluginId,
            string name,
            string status,
            string prefix = "",
            string url = "",
            string description = "",
            int version = 1,
            string company = "",
            string companyUrl = "",
            string author = "",
            string repository = "",
            string license = "",
            string settingsUrl = "",
            string pluginPageUrl = "",
            string iconUrl = "",
            string? createdAt = null,
            string? updatedAt = null)
        {
            var now = DateTime.UtcNow.ToString("o");
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = $"STATUS#{status}" },
                ["GSI1SK"] = new AttributeValue { S = $"NAME#{name}" },
                ["EntityType"] = new AttributeValue { S = "PLUGIN_META" },
                ["id"] = new AttributeValue { S = pluginId },
                ["name"] = new AttributeValue { S = name },
                ["prefix"] = new AttributeValue { S = prefix },
                ["url"] = new AttributeValue { S = url },
                ["description"] = new AttributeValue { S = description },
                ["version"] = new AttributeValue { N = version.ToString() },
                ["company"] = new AttributeValue { S = company },
                ["company_url"] = new AttributeValue { S = companyUrl },
                ["author"] = new AttributeValue { S = author },
                ["repository"] = new AttributeValue { S = repository },
                ["license"] = new AttributeValue { S = license },
                ["settings_url"] = new AttributeValue { S = settingsUrl },
                ["plugin_page_url"] = new AttributeValue { S = pluginPageUrl },
                ["icon_url"] = new AttributeValue { S = iconUrl },
                ["status"] = new AttributeValue { S = status },
                ["created_at"] = new AttributeValue { S = createdAt ?? now },
                ["updated_at"] = new AttributeValue { S = updatedAt ?? now }
            };
        }

        /// <summary>
        /// Builds a DynamoDB plugin DATA item with PK=PLUGIN#{pluginName}, SK=DATA,
        /// EntityType=PLUGIN_DATA, plugin_name, data, and updated_at attributes.
        /// Matches the structure used in LocalStackFixture.SeedSamplePluginDataAsync.
        /// </summary>
        private static Dictionary<string, AttributeValue> BuildPluginDataItem(
            string pluginName,
            string data)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"PLUGIN#{pluginName}" },
                ["SK"] = new AttributeValue { S = "DATA" },
                ["EntityType"] = new AttributeValue { S = "PLUGIN_DATA" },
                ["plugin_name"] = new AttributeValue { S = pluginName },
                ["data"] = new AttributeValue { S = data },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            };
        }

        /// <summary>
        /// Safely deletes a DynamoDB item by PK/SK. Swallows exceptions to prevent
        /// cleanup failures from masking actual test assertions.
        /// </summary>
        private async Task DeleteItemSafelyAsync(string pk, string sk)
        {
            try
            {
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = pk },
                        ["SK"] = new AttributeValue { S = sk }
                    }
                });
            }
            catch
            {
                // Swallow cleanup exceptions to prevent masking test failures
            }
        }
    }
}
