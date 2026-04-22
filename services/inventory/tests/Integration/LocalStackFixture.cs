using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Inventory.DataAccess;
using WebVellaErp.Inventory.Services;
using Xunit;

namespace WebVellaErp.Inventory.Tests.Integration
{
    /// <summary>
    /// xUnit collection definition that shares a single <see cref="LocalStackFixture"/>
    /// instance across all test classes decorated with <c>[Collection("LocalStack")]</c>.
    /// This prevents parallel fixture initialization which would cause DynamoDB table
    /// creation race conditions (both classes targeting the same "inventory-table-test" table).
    /// Tests within this collection run sequentially, ensuring clean resource lifecycle.
    /// </summary>
    [CollectionDefinition("LocalStack")]
    public class LocalStackCollection : ICollectionFixture<LocalStackFixture>
    {
        // This class has no code — it only serves as the anchor for the CollectionDefinition attribute.
    }

    /// <summary>
    /// Shared xUnit <see cref="IAsyncLifetime"/> fixture class that initializes the
    /// LocalStack-backed test infrastructure for all Inventory service integration tests.
    ///
    /// This fixture provisions the complete AWS resource set required by the Inventory
    /// (Project Management) microservice:
    ///   1. DynamoDB table with single-table design (PK/SK + 3 GSIs) matching
    ///      <see cref="InventoryRepository"/> key schema
    ///   2. SNS topic for domain event publishing (inventory.*.* events)
    ///   3. SQS queue subscribed to SNS for test event verification
    ///   4. Seed reference data: task statuses and task types with well-known GUIDs
    ///      from the source monolith (TaskService.cs, StartTasksOnStartDate.cs)
    ///   5. DI service provider wired to real LocalStack-backed AWS clients
    ///
    /// All integration tests share this fixture via <c>IClassFixture&lt;LocalStackFixture&gt;</c>.
    ///
    /// Per AAP §0.8.4: All integration tests MUST run against LocalStack — NO mocked AWS SDK calls.
    /// Per AAP §0.8.6: Pattern is docker compose up -d → test → docker compose down.
    /// </summary>
    public class LocalStackFixture : IAsyncLifetime
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  PUBLIC PROPERTIES — Exposed to test classes via IClassFixture
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// DynamoDB client configured for LocalStack (ServiceURL=http://localhost:4566).
        /// Used by test classes to directly verify DynamoDB state after service operations.
        /// </summary>
        public IAmazonDynamoDB DynamoDbClient { get; }

        /// <summary>
        /// SNS client configured for LocalStack for domain event publishing verification.
        /// Registered as singleton in the DI container for TaskService SNS event publishing.
        /// </summary>
        public IAmazonSimpleNotificationService SnsClient { get; }

        /// <summary>
        /// SQS client configured for LocalStack for event verification in integration tests.
        /// Test classes poll this queue to verify that SNS domain events were published correctly
        /// via the SNS-SQS subscription created during initialization.
        /// </summary>
        public IAmazonSQS SqsClient { get; }

        /// <summary>
        /// IConfiguration with test-specific settings:
        ///   - AWS_ENDPOINT_URL = http://localhost:4566
        ///   - AWS_REGION = us-east-1
        ///   - IS_LOCAL = true
        ///   - DynamoDB:TableName = inventory-table-test
        ///   - SNS:InventoryTopicArn = (set after topic creation in InitializeAsync)
        /// </summary>
        public IConfiguration Configuration { get; }

        /// <summary>
        /// DynamoDB table name used for this test run.
        /// Matches the value read by InventoryRepository via Configuration["DynamoDB:TableName"].
        /// </summary>
        public string TableName { get; }

        /// <summary>
        /// ARN of the SNS topic created in LocalStack for inventory domain events.
        /// Set during <see cref="InitializeAsync"/> after topic creation.
        /// </summary>
        public string SnsTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// URL of the SQS queue created in LocalStack for event verification.
        /// Test classes use this to receive messages published to the SNS topic.
        /// </summary>
        public string SqsQueueUrl { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the SQS queue, required for SNS subscription endpoint.
        /// </summary>
        public string SqsQueueArn { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the SNS-to-SQS subscription linking the inventory events topic
        /// to the test verification queue.
        /// </summary>
        public string SqsSubscriptionArn { get; private set; } = string.Empty;

        /// <summary>
        /// DI service provider for resolving real service instances wired to LocalStack.
        /// Test classes use this to resolve ITaskService, IInventoryRepository, etc.
        /// Built during <see cref="InitializeAsync"/> after all AWS resources are provisioned.
        /// </summary>
        public IServiceProvider ServiceProvider { get; private set; } = null!;

        // ═══════════════════════════════════════════════════════════════════════
        //  CONSTRUCTOR — AWS SDK Client Configuration
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Initializes AWS SDK clients targeting LocalStack and builds the base IConfiguration.
        /// All three clients (DynamoDB, SNS, SQS) use BasicAWSCredentials("test", "test")
        /// because LocalStack does not validate credentials.
        ///
        /// Environment variable overrides:
        ///   - AWS_ENDPOINT_URL: defaults to http://localhost:4566
        ///   - AWS_REGION: defaults to us-east-1
        /// </summary>
        public LocalStackFixture()
        {
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL")
                ?? "http://localhost:4566";
            var region = Environment.GetEnvironmentVariable("AWS_REGION")
                ?? "us-east-1";

            // LocalStack does not validate credentials — use dummy values
            var credentials = new BasicAWSCredentials("test", "test");

            // Configure DynamoDB client for LocalStack
            var dynamoConfig = new AmazonDynamoDBConfig
            {
                ServiceURL = endpointUrl,
                AuthenticationRegion = region
            };
            DynamoDbClient = new AmazonDynamoDBClient(credentials, dynamoConfig);

            // Configure SNS client for LocalStack
            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = endpointUrl,
                AuthenticationRegion = region
            };
            SnsClient = new AmazonSimpleNotificationServiceClient(credentials, snsConfig);

            // Configure SQS client for LocalStack
            var sqsConfig = new AmazonSQSConfig
            {
                ServiceURL = endpointUrl,
                AuthenticationRegion = region
            };
            SqsClient = new AmazonSQSClient(credentials, sqsConfig);

            // Table name for this test run
            TableName = "inventory-table-test";

            // Build IConfiguration with test-specific settings
            // SNS:InventoryTopicArn is set to empty here and updated after topic creation
            // in InitializeAsync via the IConfiguration indexer setter
            var configData = new Dictionary<string, string?>
            {
                ["AWS_ENDPOINT_URL"] = endpointUrl,
                ["AWS_REGION"] = region,
                ["IS_LOCAL"] = "true",
                ["DynamoDB:TableName"] = TableName,
                ["SNS:InventoryTopicArn"] = "" // Updated in InitializeAsync after topic creation
            };
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  IAsyncLifetime — InitializeAsync
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Provisions all AWS resources in LocalStack before integration tests run.
        /// Executes sequentially to ensure proper dependency ordering:
        ///   1. DynamoDB table with single-table design (PK/SK + 3 GSIs)
        ///   2. SNS topic for inventory domain events
        ///   3. SQS queue for event verification
        ///   4. SNS-to-SQS subscription
        ///   5. Seed reference data (task statuses and types with well-known GUIDs)
        ///   6. Build DI service provider with real service instances
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Create DynamoDB table with single-table design matching InventoryRepository
            await CreateDynamoDbTableAsync();

            // Step 2: Create SNS topic for inventory domain events
            await CreateSnsTopicAsync();

            // Step 3: Create SQS queue for event verification by test classes
            await CreateSqsQueueAsync();

            // Step 4: Subscribe SQS queue to SNS topic for message capture
            await SubscribeSqsToSnsAsync();

            // Step 5: Seed reference data — task statuses and task types
            await SeedTaskStatusesAsync();
            await SeedTaskTypesAsync();

            // Step 6: Build DI service provider with all real services wired to LocalStack
            BuildServiceProvider();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  IAsyncLifetime — DisposeAsync
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cleans up all provisioned AWS resources after tests complete.
        /// Each cleanup operation is wrapped in try-catch to handle failures gracefully,
        /// since tests may have already cleaned up individual resources, or resources
        /// may not have been created due to earlier failures in InitializeAsync.
        /// Disposes all SDK clients to release underlying HTTP connections.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Delete DynamoDB table
            await SafeDeleteDynamoDbTableAsync();

            // Delete SNS topic (also removes subscriptions)
            await SafeDeleteSnsTopicAsync();

            // Delete SQS queue
            await SafeDeleteSqsQueueAsync();

            // Dispose DI service provider if it implements IDisposable
            if (ServiceProvider is IDisposable disposableProvider)
            {
                disposableProvider.Dispose();
            }

            // Dispose all SDK clients to release HTTP connections
            DisposeSdkClients();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  PRIVATE HELPERS — InitializeAsync Resource Creation
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates the DynamoDB inventory table with single-table design matching
        /// InventoryRepository.cs key schema:
        ///   - Primary key: PK (String, HASH) + SK (String, RANGE)
        ///   - GSI1: GSI1PK/GSI1SK — entity-type index (e.g., ENTITY#task_status, ENTITY#task_type)
        ///   - GSI2: GSI2PK/GSI2SK — user index (e.g., USER#{userId})
        ///   - GSI3: GSI3PK/GSI3SK — project-task index (e.g., PROJECT#{projectId})
        ///
        /// All GSIs use ProjectionType.ALL to include all attributes in query results.
        /// BillingMode.PROVISIONED with 10/10 RCU/WCU for table, 5/5 for each GSI.
        /// Waits for table to become ACTIVE before returning.
        /// </summary>
        private async Task CreateDynamoDbTableAsync()
        {
            // Ensure idempotent table creation: delete pre-existing table from interrupted test runs
            try
            {
                await DynamoDbClient.DeleteTableAsync(new DeleteTableRequest { TableName = TableName });
                // Wait briefly for table deletion to propagate in LocalStack
                await Task.Delay(500);
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
                // Table does not exist — proceed to create
            }

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
                    new AttributeDefinition("GSI1SK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI2PK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI2SK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI3PK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI3SK", ScalarAttributeType.S)
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    // GSI1: Entity-type index — queries by entity type (task_status, task_type, task, etc.)
                    // Key pattern: GSI1PK=ENTITY#{entityType}, GSI1SK varies by entity
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
                    },
                    // GSI2: User index — queries tasks/timelogs by user
                    // Key pattern: GSI2PK=USER#{userId}, GSI2SK varies
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI2",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement("GSI2PK", KeyType.HASH),
                            new KeySchemaElement("GSI2SK", KeyType.RANGE)
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                    },
                    // GSI3: Project-task index — queries tasks within a project
                    // Key pattern: GSI3PK=PROJECT#{projectId}, GSI3SK varies
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI3",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement("GSI3PK", KeyType.HASH),
                            new KeySchemaElement("GSI3SK", KeyType.RANGE)
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                    }
                },
                BillingMode = BillingMode.PROVISIONED,
                ProvisionedThroughput = new ProvisionedThroughput(10, 10)
            };

            await DynamoDbClient.CreateTableAsync(createTableRequest);

            // Poll DescribeTable until the table status transitions to ACTIVE
            await WaitForTableActiveAsync();
        }

        /// <summary>
        /// Polls DescribeTable until the table status is ACTIVE.
        /// LocalStack typically creates tables instantly, but this polling loop
        /// ensures correctness for any environment and prevents race conditions.
        /// Throws <see cref="TimeoutException"/> if table does not become ACTIVE within 30 seconds.
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
        /// Creates an SNS topic for inventory domain events and updates Configuration
        /// with the actual topic ARN. Per AAP §0.8.5: event naming convention is
        /// {domain}.{entity}.{action} (e.g., inventory.task.created, inventory.task.updated).
        /// </summary>
        private async Task CreateSnsTopicAsync()
        {
            var createTopicResponse = await SnsClient.CreateTopicAsync("inventory-events");
            SnsTopicArn = createTopicResponse.TopicArn;

            // Update Configuration with the actual SNS topic ARN.
            // IConfiguration indexer setter propagates through the MemoryConfigurationProvider
            // so that TaskService resolves the correct ARN from Configuration["SNS:InventoryTopicArn"].
            Configuration["SNS:InventoryTopicArn"] = SnsTopicArn;
        }

        /// <summary>
        /// Creates an SQS queue for test event verification. Tests poll this queue
        /// to assert that the correct SNS domain events were published by service operations.
        /// Retrieves the queue ARN via GetQueueAttributes (required for SNS subscription).
        /// </summary>
        private async Task CreateSqsQueueAsync()
        {
            var createQueueResponse = await SqsClient.CreateQueueAsync("inventory-events-test");
            SqsQueueUrl = createQueueResponse.QueueUrl;

            // Retrieve queue ARN — required for SNS subscription endpoint
            var queueAttrs = await SqsClient.GetQueueAttributesAsync(
                SqsQueueUrl,
                new List<string> { "QueueArn" });
            SqsQueueArn = queueAttrs.QueueARN;
        }

        /// <summary>
        /// Subscribes the SQS queue to the SNS topic so that all domain events
        /// published by TaskService are captured in the queue for test verification.
        /// Uses the "sqs" protocol with the queue ARN as the endpoint.
        /// </summary>
        private async Task SubscribeSqsToSnsAsync()
        {
            var subscribeResponse = await SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = SnsTopicArn,
                Protocol = "sqs",
                Endpoint = SqsQueueArn
            });
            SqsSubscriptionArn = subscribeResponse.SubscriptionArn;
        }

        /// <summary>
        /// Seeds task status reference data in DynamoDB matching the well-known GUIDs
        /// from the source monolith. These statuses are used by TaskService business logic
        /// (e.g., GetTasksThatNeedStartingAsync filters by NotStartedStatusId).
        ///
        /// DynamoDB item structure per InventoryRepository.cs constants:
        ///   PK = TASK_STATUS#{id}
        ///   SK = META
        ///   GSI1PK = ENTITY#task_status
        ///   GSI1SK = {sort_order:D4} (zero-padded for lexicographic ordering)
        ///
        /// Source GUIDs:
        ///   - Not Started: f3fdd750-0c16-4215-93b3-5373bd528d1f (TaskService.cs line 548)
        ///   - In Progress: 20d73f63-3501-4565-a55e-2d291549a9bd (StartTasksOnStartDate.cs line 23)
        ///   - Completed: deterministic test GUID for closed-status exclusion tests
        /// </summary>
        private async Task SeedTaskStatusesAsync()
        {
            // Not Started — exact GUID from source TaskService.cs line 548
            // Used by TaskService.NotStartedStatusId for GetTasksThatNeedStartingAsync
            var notStartedId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");
            await PutSeedItemAsync(new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_STATUS#{notStartedId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task_status" },
                ["GSI1SK"] = new AttributeValue { S = "0001" },
                ["id"] = new AttributeValue { S = notStartedId.ToString() },
                ["label"] = new AttributeValue { S = "Not Started" },
                ["is_closed"] = new AttributeValue { BOOL = false },
                ["sort_order"] = new AttributeValue { N = "1" },
                ["EntityType"] = new AttributeValue { S = "TASK_STATUS" },
                ["created_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            });

            // In Progress — exact GUID from source StartTasksOnStartDate.cs line 23
            // Used by StartTasksOnStartDate job to transition tasks on their start date
            var inProgressId = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");
            await PutSeedItemAsync(new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_STATUS#{inProgressId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task_status" },
                ["GSI1SK"] = new AttributeValue { S = "0002" },
                ["id"] = new AttributeValue { S = inProgressId.ToString() },
                ["label"] = new AttributeValue { S = "In Progress" },
                ["is_closed"] = new AttributeValue { BOOL = false },
                ["sort_order"] = new AttributeValue { N = "2" },
                ["EntityType"] = new AttributeValue { S = "TASK_STATUS" },
                ["created_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            });

            // Completed — deterministic test GUID for testing closed-status exclusion
            // in GetTaskQueue and related business logic
            var completedId = new Guid("7a1c9d3e-5f2b-4e8a-b6c0-d4e9f1a2b3c4");
            await PutSeedItemAsync(new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_STATUS#{completedId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task_status" },
                ["GSI1SK"] = new AttributeValue { S = "0003" },
                ["id"] = new AttributeValue { S = completedId.ToString() },
                ["label"] = new AttributeValue { S = "Completed" },
                ["is_closed"] = new AttributeValue { BOOL = true },
                ["sort_order"] = new AttributeValue { N = "3" },
                ["EntityType"] = new AttributeValue { S = "TASK_STATUS" },
                ["created_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            });
        }

        /// <summary>
        /// Seeds task type reference data in DynamoDB for testing task CRUD operations
        /// with different type classifications.
        ///
        /// DynamoDB item structure per InventoryRepository.cs constants:
        ///   PK = TASK_TYPE#{id}
        ///   SK = META
        ///   GSI1PK = ENTITY#task_type
        ///   GSI1SK = {label} (alphabetic ordering)
        /// </summary>
        private async Task SeedTaskTypesAsync()
        {
            // Bug task type — common classification for defect tracking
            var bugTypeId = new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
            await PutSeedItemAsync(new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_TYPE#{bugTypeId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task_type" },
                ["GSI1SK"] = new AttributeValue { S = "Bug" },
                ["id"] = new AttributeValue { S = bugTypeId.ToString() },
                ["label"] = new AttributeValue { S = "Bug" },
                ["EntityType"] = new AttributeValue { S = "TASK_TYPE" },
                ["created_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            });

            // Feature task type — common classification for new functionality
            var featureTypeId = new Guid("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e");
            await PutSeedItemAsync(new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_TYPE#{featureTypeId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task_type" },
                ["GSI1SK"] = new AttributeValue { S = "Feature" },
                ["id"] = new AttributeValue { S = featureTypeId.ToString() },
                ["label"] = new AttributeValue { S = "Feature" },
                ["EntityType"] = new AttributeValue { S = "TASK_TYPE" },
                ["created_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            });
        }

        /// <summary>
        /// Helper method to insert a single seed item into the DynamoDB table.
        /// Wraps PutItemRequest construction for consistent error handling across all seed operations.
        /// </summary>
        /// <param name="item">DynamoDB item attribute map to insert.</param>
        private async Task PutSeedItemAsync(Dictionary<string, AttributeValue> item)
        {
            await DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = item
            });
        }

        /// <summary>
        /// Builds the DI service provider with all real service instances wired to LocalStack.
        /// Registration mirrors the production Lambda function DI setup:
        ///   - IAmazonDynamoDB → DynamoDbClient (singleton, already configured for LocalStack)
        ///   - IAmazonSimpleNotificationService → SnsClient (singleton, for TaskService event publishing)
        ///   - IConfiguration → Configuration (singleton, with LocalStack endpoints and resource ARNs)
        ///   - ILogger → Console logger (for diagnostic output during test runs)
        ///   - IInventoryRepository → InventoryRepository (scoped, per InventoryRepository constructor:
        ///       IAmazonDynamoDB, ILogger&lt;InventoryRepository&gt;, IConfiguration)
        ///   - ITaskService → TaskService (scoped, per TaskService constructor:
        ///       IInventoryRepository, IAmazonSimpleNotificationService, ILogger&lt;TaskService&gt;, IConfiguration)
        ///
        /// Test classes resolve services from ServiceProvider to execute real end-to-end operations
        /// against LocalStack without mocking.
        /// </summary>
        private void BuildServiceProvider()
        {
            var services = new ServiceCollection();

            // Register AWS SDK clients as singletons (shared across all scopes)
            services.AddSingleton<IAmazonDynamoDB>(DynamoDbClient);
            services.AddSingleton<IAmazonSimpleNotificationService>(SnsClient);

            // Register configuration as singleton
            services.AddSingleton<IConfiguration>(Configuration);

            // Register logging with console output for test diagnostics
            services.AddLogging(builder => builder.AddConsole());

            // Register Inventory service layer — scoped lifetime matches Lambda per-request pattern
            // InventoryRepository constructor: (IAmazonDynamoDB, ILogger<InventoryRepository>, IConfiguration)
            services.AddScoped<IInventoryRepository, InventoryRepository>();

            // TaskService constructor: (IInventoryRepository, IAmazonSimpleNotificationService, ILogger<TaskService>, IConfiguration)
            services.AddScoped<ITaskService, TaskService>();

            ServiceProvider = services.BuildServiceProvider();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  PRIVATE HELPERS — DisposeAsync Resource Cleanup
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Safely deletes the DynamoDB inventory table.
        /// Catches and ignores all exceptions since the table may have already been
        /// deleted by a test or may not have been created due to earlier failures.
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
                // Table was already deleted or never created — safe to ignore
            }
            catch (Exception)
            {
                // Catch all other exceptions during cleanup to prevent masking test failures
            }
        }

        /// <summary>
        /// Safely deletes the SNS topic for inventory domain events.
        /// Deleting a topic also removes all subscriptions associated with it.
        /// </summary>
        private async Task SafeDeleteSnsTopicAsync()
        {
            if (string.IsNullOrEmpty(SnsTopicArn))
            {
                return;
            }

            try
            {
                await SnsClient.DeleteTopicAsync(SnsTopicArn);
            }
            catch (Exception)
            {
                // Topic may have already been deleted — safe to ignore
            }
        }

        /// <summary>
        /// Safely deletes the SQS queue used for event verification.
        /// </summary>
        private async Task SafeDeleteSqsQueueAsync()
        {
            if (string.IsNullOrEmpty(SqsQueueUrl))
            {
                return;
            }

            try
            {
                await SqsClient.DeleteQueueAsync(SqsQueueUrl);
            }
            catch (Exception)
            {
                // Queue may have already been deleted — safe to ignore
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

            if (SqsClient is IDisposable sqsDisposable)
            {
                sqsDisposable.Dispose();
            }
        }
    }
}
