using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Xunit;

namespace WebVellaErp.PluginSystem.Tests.Integration
{
    /// <summary>
    /// Shared xUnit IAsyncLifetime test fixture for LocalStack integration tests.
    /// Provisions real AWS resources (DynamoDB table with single-table design
    /// for plugin metadata/data, and SNS topic for plugin domain events) in
    /// LocalStack before tests run and cleans them up after.
    ///
    /// Used via IClassFixture&lt;LocalStackFixture&gt; by all integration test classes
    /// in the Plugin System service (PluginLifecycleIntegrationTests,
    /// PluginRepositoryIntegrationTests). Ensures all tests run against real
    /// LocalStack infrastructure with zero mocked AWS SDK calls.
    ///
    /// Per AAP Section 0.8.4: "All integration and E2E tests MUST execute against
    /// LocalStack. No mocked AWS SDK calls in integration tests. Pattern:
    /// docker compose up -d → test → docker compose down."
    ///
    /// DynamoDB Single-Table Design:
    ///   PK=PLUGIN#{pluginId}, SK=META           → Plugin metadata definitions
    ///   PK=PLUGIN#{pluginName}, SK=DATA          → Plugin settings/data (JSON)
    ///   GSI1PK=STATUS#{status}, GSI1SK=NAME#{name} → Status-based queries
    ///
    /// Source mappings:
    ///   - WebVella.Erp/ErpPlugin.cs: 13 plugin metadata properties
    ///   - WebVella.Erp.Plugins.SDK/SdkPlugin._.cs: ProcessPatches version progression
    ///   - WebVella.Erp.Plugins.SDK/Model/PluginSettings.cs: Version (int) JSON schema
    /// </summary>
    public class LocalStackFixture : IAsyncLifetime
    {
        /// <summary>
        /// Pre-configured DynamoDB client pointing to LocalStack.
        /// Used by integration tests for all DynamoDB table operations including
        /// single-table design CRUD, GSI queries, and plugin metadata management.
        /// </summary>
        public IAmazonDynamoDB DynamoDbClient { get; private set; }

        /// <summary>
        /// Pre-configured SNS client pointing to LocalStack.
        /// Used by integration tests for verifying domain event publishing
        /// on plugin-system.plugin.created, plugin-system.plugin.activated,
        /// and plugin-system.plugin.deactivated topics.
        /// </summary>
        public IAmazonSimpleNotificationService SnsClient { get; private set; }

        /// <summary>
        /// The name of the DynamoDB plugin-system table created for this test run.
        /// Uses a unique name per run (plugin-system-{guid}) to avoid collisions
        /// with parallel test executions.
        /// Table uses single-table design with PK/SK + GSI1 (status-based queries).
        /// </summary>
        public string TableName { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the SNS topic for plugin domain events.
        /// Publishes: plugin-system.plugin.created, plugin-system.plugin.activated,
        /// plugin-system.plugin.deactivated
        /// Per AAP Section 0.8.5 event naming convention: {domain}.{entity}.{action}
        /// </summary>
        public string PluginEventsTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// The GUID of the sample SDK plugin seeded in InitializeAsync.
        /// Tests can reference this to query/verify the pre-seeded plugin metadata
        /// item (PK=PLUGIN#{SamplePluginId}, SK=META) in DynamoDB.
        /// </summary>
        public Guid SamplePluginId { get; private set; }

        /// <summary>
        /// Constructor configures all AWS SDK clients for LocalStack.
        /// Reads endpoint from AWS_ENDPOINT_URL environment variable
        /// (falls back to http://localhost:4566 which is the standard LocalStack port).
        /// Uses BasicAWSCredentials("test", "test") since LocalStack accepts any credentials.
        /// Region is us-east-1 per AAP Section 0.8.6.
        /// </summary>
        public LocalStackFixture()
        {
            var localStackEndpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL")
                ?? "http://localhost:4566";

            var credentials = new BasicAWSCredentials("test", "test");

            // Configure DynamoDB client for LocalStack
            var dynamoConfig = new AmazonDynamoDBConfig
            {
                ServiceURL = localStackEndpoint,
                AuthenticationRegion = "us-east-1"
            };
            DynamoDbClient = new AmazonDynamoDBClient(credentials, dynamoConfig);

            // Configure SNS client for LocalStack
            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = localStackEndpoint,
                AuthenticationRegion = "us-east-1"
            };
            SnsClient = new AmazonSimpleNotificationServiceClient(credentials, snsConfig);

            // Generate unique resource names for this test run to avoid collisions
            var runId = Guid.NewGuid().ToString("N");
            TableName = $"plugin-system-{runId}";
        }

        /// <summary>
        /// Provisions all AWS resources in LocalStack before integration tests run.
        /// Executes sequentially to ensure proper dependency ordering:
        /// 1. DynamoDB table with GSI1 (must exist before seeding)
        /// 2. SNS topic for plugin domain events
        /// 3. Seed sample plugin metadata (modeled after SdkPlugin)
        /// 4. Seed sample plugin data (matching SdkPlugin._.cs ProcessPatches final state)
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Create DynamoDB Plugin-System Table with single-table design
            // Table has PK/SK primary key + GSI1 for status-based plugin queries
            await CreateDynamoDbTableAsync();

            // Step 2: Create SNS Topic for plugin domain events
            // Topic: plugin-system-events-{guid} for plugin lifecycle events
            await CreateSnsTopicAsync();

            // Step 3: Seed sample plugin metadata modeled after SdkPlugin
            // PK=PLUGIN#{pluginId}, SK=META with all 13 ErpPlugin properties
            await SeedSamplePluginMetadataAsync();

            // Step 4: Seed sample plugin data matching SdkPlugin._.cs ProcessPatches final state
            // PK=PLUGIN#sdk, SK=DATA with serialized PluginSettings JSON
            await SeedSamplePluginDataAsync();
        }

        /// <summary>
        /// Cleans up all provisioned AWS resources after tests complete.
        /// Each cleanup operation is wrapped in try-catch to handle failures gracefully,
        /// since tests may have already cleaned up individual resources, or resources
        /// may not have been created due to earlier failures.
        /// Disposes all SDK clients to release underlying HTTP connections.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Delete the DynamoDB plugin-system table
            await SafeDeleteDynamoDbTableAsync();

            // Delete SNS topic for plugin domain events
            await SafeDeleteSnsTopicAsync();

            // Dispose all SDK clients to release HTTP connections and resources
            DisposeSdkClients();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Private helper methods for InitializeAsync
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the DynamoDB plugin-system table with single-table design:
        /// - Primary key: PK (String, HASH) + SK (String, RANGE)
        /// - GSI1: GSI1PK (HASH) + GSI1SK (RANGE) for status-based plugin queries
        ///   Enables: querying all active plugins, all inactive plugins, etc.
        ///   Key pattern: GSI1PK=STATUS#{status}, GSI1SK=NAME#{name}
        ///
        /// GSI1 uses ProjectionType.ALL to include all attributes in query results.
        /// No GSI2 is needed (unlike Identity service which needs GSI2 for username lookups).
        /// No Cognito resources are created (plugin-system does not use Cognito — that's Identity service).
        ///
        /// Waits for table to become ACTIVE before returning.
        /// </summary>
        private async Task CreateDynamoDbTableAsync()
        {
            var createTableRequest = new CreateTableRequest
            {
                TableName = TableName,
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement("PK", KeyType.HASH),
                    new KeySchemaElement("SK", KeyType.RANGE)
                },
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition("PK", ScalarAttributeType.S),
                    new AttributeDefinition("SK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI1PK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI1SK", ScalarAttributeType.S)
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI1",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement("GSI1PK", KeyType.HASH),
                            new KeySchemaElement("GSI1SK", KeyType.RANGE)
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                    }
                },
                ProvisionedThroughput = new ProvisionedThroughput(5, 5),
                BillingMode = BillingMode.PROVISIONED
            };

            await DynamoDbClient.CreateTableAsync(createTableRequest);

            // Poll DescribeTable until the table status transitions to ACTIVE
            await WaitForTableActiveAsync();
        }

        /// <summary>
        /// Polls DescribeTable until the table status is ACTIVE.
        /// LocalStack typically creates tables instantly, but this polling loop
        /// ensures correctness for any environment and prevents race conditions.
        /// Throws TimeoutException if the table does not become ACTIVE within 30 seconds.
        /// </summary>
        private async Task WaitForTableActiveAsync()
        {
            const int maxRetries = 30;
            const int delayMilliseconds = 1000;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var response = await DynamoDbClient.DescribeTableAsync(new DescribeTableRequest
                {
                    TableName = TableName
                });

                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }

                await Task.Delay(delayMilliseconds);
            }

            throw new TimeoutException(
                $"DynamoDB table '{TableName}' did not become ACTIVE within {maxRetries} seconds.");
        }

        /// <summary>
        /// Creates an SNS topic for plugin domain events:
        /// - plugin-system-events: Publishes plugin-system.plugin.created,
        ///   plugin-system.plugin.activated, plugin-system.plugin.deactivated events
        ///   when plugin lifecycle operations occur
        ///
        /// Topic name includes a unique suffix per test run to avoid collisions.
        /// ARN is stored in PluginEventsTopicArn for test assertions.
        ///
        /// Per AAP Section 0.8.5: Event naming convention is {domain}.{entity}.{action}
        /// </summary>
        private async Task CreateSnsTopicAsync()
        {
            var runSuffix = Guid.NewGuid().ToString("N");

            var pluginEventsResponse = await SnsClient.CreateTopicAsync(new CreateTopicRequest
            {
                Name = $"plugin-system-events-{runSuffix}"
            });
            PluginEventsTopicArn = pluginEventsResponse.TopicArn;
        }

        /// <summary>
        /// Seeds a sample plugin metadata item modeled after the SDK plugin
        /// from the source monolith (WebVella.Erp.Plugins.SDK/SdkPlugin.cs).
        ///
        /// DynamoDB item structure:
        /// - PK = PLUGIN#{pluginId}  (partition key identifying the plugin entity)
        /// - SK = META               (sort key indicating this is plugin metadata)
        /// - GSI1PK = STATUS#{status} (for status-based index queries)
        /// - GSI1SK = NAME#{name}     (for ordered name lookups within status)
        /// - EntityType = PLUGIN_META (discriminator for scan filtering)
        ///
        /// All 13 ErpPlugin properties from source ErpPlugin.cs are included:
        /// name, prefix, url, description, version (Number type), company,
        /// company_url, author, repository, license, settings_url,
        /// plugin_page_url, icon_url
        ///
        /// Source: SdkPlugin.cs line 13: Name = "sdk"
        /// Source: SdkPlugin._.cs line 12: WEBVELLA_SDK_INIT_VERSION = 20181001,
        ///   final version = 20210429 (after all patches applied)
        /// Version field stored as Number (N) matching PluginSettings.Version (int) type
        /// </summary>
        private async Task SeedSamplePluginMetadataAsync()
        {
            SamplePluginId = Guid.NewGuid();

            var samplePlugin = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"PLUGIN#{SamplePluginId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = "STATUS#Active" },
                ["GSI1SK"] = new AttributeValue { S = "NAME#sdk" },
                ["EntityType"] = new AttributeValue { S = "PLUGIN_META" },
                ["id"] = new AttributeValue { S = SamplePluginId.ToString() },
                ["name"] = new AttributeValue { S = "sdk" },
                ["prefix"] = new AttributeValue { S = "" },
                ["url"] = new AttributeValue { S = "" },
                ["description"] = new AttributeValue { S = "Software Development Kit plugin" },
                ["version"] = new AttributeValue { N = "20210429" },
                ["company"] = new AttributeValue { S = "WebVella" },
                ["company_url"] = new AttributeValue { S = "" },
                ["author"] = new AttributeValue { S = "" },
                ["repository"] = new AttributeValue { S = "" },
                ["license"] = new AttributeValue { S = "Apache-2.0" },
                ["settings_url"] = new AttributeValue { S = "" },
                ["plugin_page_url"] = new AttributeValue { S = "" },
                ["icon_url"] = new AttributeValue { S = "" },
                ["status"] = new AttributeValue { S = "Active" },
                ["created_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            };

            await DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = samplePlugin
            });
        }

        /// <summary>
        /// Seeds a sample plugin data item matching the SdkPlugin._.cs ProcessPatches
        /// final state. This mirrors the source monolith's plugin_data PostgreSQL table
        /// structure (id UUID PK, name TEXT UNIQUE, data TEXT).
        ///
        /// DynamoDB item structure:
        /// - PK = PLUGIN#sdk          (partition key using plugin name)
        /// - SK = DATA                (sort key indicating this is plugin settings data)
        /// - EntityType = PLUGIN_DATA (discriminator for scan filtering)
        ///
        /// Source: SdkPlugin._.cs line 151:
        ///   SavePluginData(JsonConvert.SerializeObject(currentPluginSettings))
        /// The data field contains the serialized PluginSettings JSON with final
        /// version 20210429 after all patches from SdkPlugin._.cs are applied.
        /// </summary>
        private async Task SeedSamplePluginDataAsync()
        {
            var samplePluginData = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = "PLUGIN#sdk" },
                ["SK"] = new AttributeValue { S = "DATA" },
                ["EntityType"] = new AttributeValue { S = "PLUGIN_DATA" },
                ["plugin_name"] = new AttributeValue { S = "sdk" },
                ["data"] = new AttributeValue { S = "{\"version\":20210429}" },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            };

            await DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = samplePluginData
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        // Private helper methods for DisposeAsync (safe cleanup)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Safely deletes the DynamoDB plugin-system table.
        /// Catches and ignores all exceptions since the table may have already
        /// been deleted by a test or may not have been created due to earlier failures.
        /// </summary>
        private async Task SafeDeleteDynamoDbTableAsync()
        {
            if (string.IsNullOrEmpty(TableName))
            {
                return;
            }

            try
            {
                await DynamoDbClient.DeleteTableAsync(new DeleteTableRequest
                {
                    TableName = TableName
                });
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
                // Table was already deleted or never created; this is expected and safe to ignore
            }
            catch (Exception)
            {
                // Catch all other exceptions during cleanup to prevent masking test failures
            }
        }

        /// <summary>
        /// Safely deletes the SNS topic for plugin domain events.
        /// Catches and ignores all exceptions since the topic may have already
        /// been deleted or may not have been created.
        /// </summary>
        private async Task SafeDeleteSnsTopicAsync()
        {
            if (string.IsNullOrEmpty(PluginEventsTopicArn))
            {
                return;
            }

            try
            {
                await SnsClient.DeleteTopicAsync(new DeleteTopicRequest
                {
                    TopicArn = PluginEventsTopicArn
                });
            }
            catch (Exception)
            {
                // Topic may have already been deleted; safe to ignore
            }
        }

        /// <summary>
        /// Disposes all AWS SDK clients to release underlying HTTP connections
        /// and other unmanaged resources. Each client is checked for IDisposable
        /// implementation before disposal.
        /// </summary>
        private void DisposeSdkClients()
        {
            if (DynamoDbClient is IDisposable dynamoDisposable)
            {
                dynamoDisposable.Dispose();
            }

            if (SnsClient is IDisposable snsDisposable)
            {
                snsDisposable.Dispose();
            }
        }
    }
}
