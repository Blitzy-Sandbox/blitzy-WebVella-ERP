using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using WebVellaErp.EntityManagement.Models;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Fixtures
{
    /// <summary>
    /// Shared xUnit test fixture implementing IAsyncLifetime that sets up and tears down
    /// all AWS resources needed by Entity Management service integration tests running
    /// against LocalStack. Creates DynamoDB tables with single-table design pattern,
    /// SNS topics for domain events, and S3 buckets for import/export file handling.
    /// All tests can reference this fixture via IClassFixture&lt;LocalStackFixture&gt;.
    /// </summary>
    public class LocalStackFixture : IAsyncLifetime
    {
        #region Configuration Constants

        /// <summary>
        /// Default LocalStack endpoint URL. Per AAP §0.8.6: AWS_ENDPOINT_URL=http://localhost:4566.
        /// </summary>
        private const string DefaultLocalStackEndpoint = "http://localhost:4566";

        /// <summary>
        /// Default AWS region for LocalStack. Per AAP §0.8.6: AWS_REGION=us-east-1.
        /// </summary>
        private const string DefaultAwsRegion = "us-east-1";

        /// <summary>
        /// LocalStack default access key for authentication.
        /// </summary>
        private const string AwsAccessKey = "test";

        /// <summary>
        /// LocalStack default secret key for authentication.
        /// </summary>
        private const string AwsSecretKey = "test";

        /// <summary>
        /// Maximum time in seconds to wait for DynamoDB tables to become ACTIVE.
        /// </summary>
        private const int TableCreationTimeoutSeconds = 30;

        /// <summary>
        /// Polling interval in milliseconds when waiting for table status.
        /// </summary>
        private const int TableStatusPollIntervalMs = 500;

        #endregion

        #region DynamoDB Table Name Constants

        /// <summary>
        /// DynamoDB table name for entity metadata storage (single-table design).
        /// Must match TestDataHelper.EntityMetadataTableName.
        /// PK=ENTITY#{entityId}, SK=META for entities, SK=FIELD#{fieldId} for fields,
        /// SK=RELATION#{relationId} for relations.
        /// </summary>
        public const string EntityMetadataTableName = "entity-management-metadata-test";

        /// <summary>
        /// DynamoDB table name for record storage.
        /// PK=ENTITY#{entityName}, SK=RECORD#{recordId} per AAP §0.7.3.
        /// </summary>
        public const string RecordStorageTableName = "entity-management-records-test";

        #endregion

        #region SNS Topic Name Constants

        /// <summary>
        /// SNS topic name for entity created domain events.
        /// </summary>
        private const string EntityCreatedTopicName = "entity-management-entity-created";

        /// <summary>
        /// SNS topic name for record created domain events.
        /// </summary>
        private const string RecordCreatedTopicName = "entity-management-record-created";

        /// <summary>
        /// SNS topic name for record updated domain events.
        /// </summary>
        private const string RecordUpdatedTopicName = "entity-management-record-updated";

        /// <summary>
        /// SNS topic name for record deleted domain events.
        /// </summary>
        private const string RecordDeletedTopicName = "entity-management-record-deleted";

        /// <summary>
        /// Unified events topic name matching RecordService.PublishDomainEvent
        /// which publishes all record events to "{prefix}entity-management-events".
        /// </summary>
        private const string UnifiedEventsTopicName = "entity-management-events";

        #endregion

        #region S3 Bucket Name Constants

        /// <summary>
        /// S3 bucket name for CSV import/export file handling.
        /// </summary>
        public const string ImportExportBucketName = "entity-management-import-export-test";

        #endregion

        #region AWS SDK Client Properties

        /// <summary>
        /// DynamoDB client configured to point to LocalStack.
        /// Used by integration tests for entity/record data access.
        /// </summary>
        public IAmazonDynamoDB DynamoDbClient { get; private set; }

        /// <summary>
        /// SNS client configured to point to LocalStack.
        /// Used by integration tests for domain event publishing verification.
        /// </summary>
        public IAmazonSimpleNotificationService SnsClient { get; private set; }

        /// <summary>
        /// S3 client configured to point to LocalStack with ForcePathStyle=true.
        /// Used by integration tests for import/export file operations.
        /// </summary>
        public IAmazonS3 S3Client { get; private set; }

        #endregion

        #region SNS Topic ARN Properties

        /// <summary>
        /// ARN of the entity-created SNS topic, populated during InitializeAsync.
        /// </summary>
        public string EntityCreatedTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the record-created SNS topic, populated during InitializeAsync.
        /// </summary>
        public string RecordCreatedTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the record-updated SNS topic, populated during InitializeAsync.
        /// </summary>
        public string RecordUpdatedTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the record-deleted SNS topic, populated during InitializeAsync.
        /// </summary>
        public string RecordDeletedTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// ARN for the unified "entity-management-events" SNS topic that
        /// RecordService.PublishDomainEvent actually publishes to.
        /// </summary>
        public string UnifiedEventsTopicArn { get; private set; } = string.Empty;

        #endregion

        #region Private Fields

        /// <summary>
        /// Resolved LocalStack endpoint URL, allowing environment variable override.
        /// </summary>
        private readonly string _serviceEndpoint;

        /// <summary>
        /// Resolved AWS region string, allowing environment variable override.
        /// </summary>
        private readonly string _awsRegion;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes all AWS SDK clients configured to point to LocalStack.
        /// Reads AWS_ENDPOINT_URL and AWS_REGION environment variables for overrides,
        /// falling back to default LocalStack values.
        /// </summary>
        public LocalStackFixture()
        {
            // Allow environment variable overrides per AAP §0.8.6
            _serviceEndpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL")
                ?? DefaultLocalStackEndpoint;
            _awsRegion = Environment.GetEnvironmentVariable("AWS_REGION")
                ?? DefaultAwsRegion;

            var credentials = new BasicAWSCredentials(AwsAccessKey, AwsSecretKey);

            // Configure DynamoDB client with LocalStack endpoint
            var dynamoConfig = new AmazonDynamoDBConfig
            {
                ServiceURL = _serviceEndpoint,
                AuthenticationRegion = _awsRegion
            };
            DynamoDbClient = new AmazonDynamoDBClient(credentials, dynamoConfig);

            // Configure SNS client with LocalStack endpoint
            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = _serviceEndpoint,
                AuthenticationRegion = _awsRegion
            };
            SnsClient = new AmazonSimpleNotificationServiceClient(credentials, snsConfig);

            // Configure S3 client with LocalStack endpoint and ForcePathStyle
            // ForcePathStyle is required for LocalStack S3 compatibility
            var s3Config = new AmazonS3Config
            {
                ServiceURL = _serviceEndpoint,
                ForcePathStyle = true,
                AuthenticationRegion = _awsRegion
            };
            S3Client = new AmazonS3Client(credentials, s3Config);
        }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Provisions all AWS resources in LocalStack required for integration tests.
        /// Runs once before any test that uses this fixture.
        /// Creates DynamoDB tables, SNS topics, and S3 buckets.
        /// </summary>
        public async Task InitializeAsync()
        {
            await CreateDynamoDbTablesAsync();
            await WaitForTablesActiveAsync();
            await CreateSnsTopicsAsync();
            await CreateS3BucketAsync();
        }

        /// <summary>
        /// Cleans up all AWS resources created during initialization.
        /// Runs after all tests using this fixture complete.
        /// Deletes DynamoDB tables, SNS topics, S3 bucket contents and bucket, then disposes clients.
        /// </summary>
        public async Task DisposeAsync()
        {
            await CleanupDynamoDbTablesAsync();
            await CleanupSnsTopicsAsync();
            await CleanupS3BucketAsync();
            DisposeSdkClients();
        }

        #endregion

        #region Resource Provisioning — DynamoDB Tables

        /// <summary>
        /// Creates both DynamoDB tables with the single-table design pattern.
        /// EntityMetadata table: PK/SK + GSI1 for name-based entity lookups.
        /// RecordStorage table: PK/SK + GSI1 for chronological record listing.
        /// </summary>
        private async Task CreateDynamoDbTablesAsync()
        {
            await CreateEntityMetadataTableAsync();
            await CreateRecordStorageTableAsync();
        }

        /// <summary>
        /// Creates the Entity Metadata DynamoDB table with:
        /// - PK (HASH, String) + SK (RANGE, String) key schema
        /// - GSI1 (GSI1PK HASH + GSI1SK RANGE) for name-based entity lookups and listing
        /// - GSI2 (GSI2PK HASH + GSI2SK RANGE) for relation-based lookups (required by GetAllRelations)
        /// Per AAP §0.7.3: PK=ENTITY#{entityId}, SK=META|FIELD#{fieldId}|RELATION#{relationId}
        /// </summary>
        private async Task CreateEntityMetadataTableAsync()
        {
            try
            {
                // Delete existing table first to ensure correct schema (including GSI2)
                try
                {
                    await DynamoDbClient.DeleteTableAsync(new DeleteTableRequest { TableName = EntityMetadataTableName });
                    // Brief delay to allow LocalStack to process the deletion
                    await Task.Delay(500);
                }
                catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException) { /* Table doesn't exist yet — OK */ }
                var request = new CreateTableRequest
                {
                    TableName = EntityMetadataTableName,
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
                        new AttributeDefinition("GSI2SK", ScalarAttributeType.S)
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
                            Projection = new Projection
                            {
                                ProjectionType = ProjectionType.ALL
                            },
                            ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                        },
                        new GlobalSecondaryIndex
                        {
                            IndexName = "GSI2",
                            KeySchema = new List<KeySchemaElement>
                            {
                                new KeySchemaElement("GSI2PK", KeyType.HASH),
                                new KeySchemaElement("GSI2SK", KeyType.RANGE)
                            },
                            Projection = new Projection
                            {
                                ProjectionType = ProjectionType.ALL
                            },
                            ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                        }
                    },
                    ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                };

                await DynamoDbClient.CreateTableAsync(request);
            }
            catch (ResourceInUseException)
            {
                // Table already exists from a previous test run that failed to clean up.
                // This is safe to ignore — the table will be cleaned before tests run.
            }
        }

        /// <summary>
        /// Creates the Record Storage DynamoDB table with:
        /// - PK (HASH, String) + SK (RANGE, String) key schema
        /// - GSI1 (GSI1PK HASH + GSI1SK RANGE) for listing records by entity with chronological ordering
        /// Per AAP §0.7.3: PK=ENTITY#{entityName}, SK=RECORD#{recordId}
        /// </summary>
        private async Task CreateRecordStorageTableAsync()
        {
            try
            {
                // Delete existing table first to ensure correct schema
                try
                {
                    await DynamoDbClient.DeleteTableAsync(new DeleteTableRequest { TableName = RecordStorageTableName });
                    await Task.Delay(500);
                }
                catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException) { /* Table doesn't exist yet — OK */ }

                var request = new CreateTableRequest
                {
                    TableName = RecordStorageTableName,
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
                        new AttributeDefinition("GSI2SK", ScalarAttributeType.S)
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
                            Projection = new Projection
                            {
                                ProjectionType = ProjectionType.ALL
                            },
                            ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                        },
                        new GlobalSecondaryIndex
                        {
                            IndexName = "GSI2",
                            KeySchema = new List<KeySchemaElement>
                            {
                                new KeySchemaElement("GSI2PK", KeyType.HASH),
                                new KeySchemaElement("GSI2SK", KeyType.RANGE)
                            },
                            Projection = new Projection
                            {
                                ProjectionType = ProjectionType.ALL
                            },
                            ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                        }
                    },
                    ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                };

                await DynamoDbClient.CreateTableAsync(request);
            }
            catch (ResourceInUseException)
            {
                // Table already exists from a previous test run that failed to clean up.
            }
        }

        /// <summary>
        /// Polls DynamoDB until both tables reach ACTIVE status.
        /// Uses 500ms polling intervals with a 30-second timeout.
        /// Throws TimeoutException if tables don't become active within the timeout.
        /// </summary>
        private async Task WaitForTablesActiveAsync()
        {
            var tableNames = new[] { EntityMetadataTableName, RecordStorageTableName };

            foreach (var tableName in tableNames)
            {
                var startTime = DateTime.UtcNow;
                var isActive = false;

                while (!isActive)
                {
                    if ((DateTime.UtcNow - startTime).TotalSeconds > TableCreationTimeoutSeconds)
                    {
                        throw new TimeoutException(
                            $"DynamoDB table '{tableName}' did not reach ACTIVE status " +
                            $"within {TableCreationTimeoutSeconds} seconds.");
                    }

                    try
                    {
                        var response = await DynamoDbClient.DescribeTableAsync(
                            new DescribeTableRequest { TableName = tableName });

                        if (response.Table.TableStatus == TableStatus.ACTIVE)
                        {
                            isActive = true;
                        }
                        else
                        {
                            await Task.Delay(TableStatusPollIntervalMs);
                        }
                    }
                    catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
                    {
                        // Table not yet visible — wait and retry
                        await Task.Delay(TableStatusPollIntervalMs);
                    }
                }
            }
        }

        #endregion

        #region Resource Provisioning — SNS Topics

        /// <summary>
        /// Creates 4 SNS topics for domain events per AAP §0.8.5 event naming convention.
        /// Topic names follow {service}-{entity}-{action} pattern.
        /// Stores returned ARNs for test assertions and cleanup.
        /// </summary>
        private async Task CreateSnsTopicsAsync()
        {
            var entityCreatedResponse = await SnsClient.CreateTopicAsync(
                new CreateTopicRequest { Name = EntityCreatedTopicName });
            EntityCreatedTopicArn = entityCreatedResponse.TopicArn;

            var recordCreatedResponse = await SnsClient.CreateTopicAsync(
                new CreateTopicRequest { Name = RecordCreatedTopicName });
            RecordCreatedTopicArn = recordCreatedResponse.TopicArn;

            var recordUpdatedResponse = await SnsClient.CreateTopicAsync(
                new CreateTopicRequest { Name = RecordUpdatedTopicName });
            RecordUpdatedTopicArn = recordUpdatedResponse.TopicArn;

            var recordDeletedResponse = await SnsClient.CreateTopicAsync(
                new CreateTopicRequest { Name = RecordDeletedTopicName });
            RecordDeletedTopicArn = recordDeletedResponse.TopicArn;

            // Create unified events topic that RecordService.PublishDomainEvent targets
            var unifiedEventsResponse = await SnsClient.CreateTopicAsync(
                new CreateTopicRequest { Name = UnifiedEventsTopicName });
            UnifiedEventsTopicArn = unifiedEventsResponse.TopicArn;
        }

        #endregion

        #region Resource Provisioning — S3 Bucket

        /// <summary>
        /// Creates the S3 bucket for CSV import/export file handling.
        /// Wraps in try/catch for BucketAlreadyOwnedByYouException for idempotency.
        /// </summary>
        private async Task CreateS3BucketAsync()
        {
            try
            {
                await S3Client.PutBucketAsync(new PutBucketRequest
                {
                    BucketName = ImportExportBucketName
                });
            }
            catch (BucketAlreadyOwnedByYouException)
            {
                // Bucket already exists from a previous test run — safe to reuse.
            }
        }

        #endregion

        #region Resource Cleanup — DynamoDB Tables

        /// <summary>
        /// Deletes both DynamoDB tables during fixture teardown.
        /// Wraps each deletion in try/catch for ResourceNotFoundException.
        /// </summary>
        private async Task CleanupDynamoDbTablesAsync()
        {
            var tableNames = new[] { EntityMetadataTableName, RecordStorageTableName };

            foreach (var tableName in tableNames)
            {
                try
                {
                    await DynamoDbClient.DeleteTableAsync(new DeleteTableRequest
                    {
                        TableName = tableName
                    });
                }
                catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
                {
                    // Table already deleted or never created — safe to ignore.
                }
                catch (Exception)
                {
                    // Swallow any other cleanup errors to prevent masking test failures.
                }
            }
        }

        #endregion

        #region Resource Cleanup — SNS Topics

        /// <summary>
        /// Deletes all 4 SNS topics during fixture teardown.
        /// Wraps each deletion in try/catch to prevent cleanup errors from masking test failures.
        /// </summary>
        private async Task CleanupSnsTopicsAsync()
        {
            var topicArns = new[]
            {
                EntityCreatedTopicArn,
                RecordCreatedTopicArn,
                RecordUpdatedTopicArn,
                RecordDeletedTopicArn,
                UnifiedEventsTopicArn
            };

            foreach (var topicArn in topicArns)
            {
                if (string.IsNullOrEmpty(topicArn))
                {
                    continue;
                }

                try
                {
                    await SnsClient.DeleteTopicAsync(new DeleteTopicRequest
                    {
                        TopicArn = topicArn
                    });
                }
                catch (Exception)
                {
                    // Swallow cleanup errors to prevent masking test failures.
                }
            }
        }

        #endregion

        #region Resource Cleanup — S3 Bucket

        /// <summary>
        /// Empties and deletes the S3 bucket during fixture teardown.
        /// First lists and deletes all objects, then deletes the bucket itself.
        /// Wraps in try/catch for AmazonS3Exception to handle already-deleted scenarios.
        /// </summary>
        private async Task CleanupS3BucketAsync()
        {
            try
            {
                // List and delete all objects in the bucket first
                await EmptyS3BucketAsync(ImportExportBucketName);

                // Then delete the bucket
                await S3Client.DeleteBucketAsync(new DeleteBucketRequest
                {
                    BucketName = ImportExportBucketName
                });
            }
            catch (AmazonS3Exception)
            {
                // Bucket already deleted or never created — safe to ignore.
            }
            catch (Exception)
            {
                // Swallow any other cleanup errors to prevent masking test failures.
            }
        }

        /// <summary>
        /// Removes all objects from the specified S3 bucket.
        /// Uses ListObjectsV2 pagination to handle buckets with many objects.
        /// </summary>
        /// <param name="bucketName">The S3 bucket to empty.</param>
        private async Task EmptyS3BucketAsync(string bucketName)
        {
            var listRequest = new ListObjectsV2Request
            {
                BucketName = bucketName
            };

            ListObjectsV2Response listResponse;
            do
            {
                listResponse = await S3Client.ListObjectsV2Async(listRequest);

                if (listResponse.S3Objects != null && listResponse.S3Objects.Count > 0)
                {
                    var deleteTasks = listResponse.S3Objects
                        .Select(obj => S3Client.DeleteObjectAsync(new DeleteObjectRequest
                        {
                            BucketName = bucketName,
                            Key = obj.Key
                        }))
                        .ToList();

                    await Task.WhenAll(deleteTasks);
                }

                listRequest.ContinuationToken = listResponse.NextContinuationToken;
            }
            while (listResponse.IsTruncated);
        }

        #endregion

        #region Client Disposal

        /// <summary>
        /// Disposes all AWS SDK clients to release unmanaged resources.
        /// </summary>
        private void DisposeSdkClients()
        {
            try { DynamoDbClient?.Dispose(); } catch { /* Swallow disposal errors */ }
            try { (SnsClient as IDisposable)?.Dispose(); } catch { /* Swallow disposal errors */ }
            try { S3Client?.Dispose(); } catch { /* Swallow disposal errors */ }
        }

        #endregion

        #region Helper Methods for Tests

        /// <summary>
        /// Scans all items in the specified DynamoDB table and deletes them.
        /// Useful for per-test cleanup without the overhead of re-creating tables.
        /// Uses ScanAsync + individual DeleteItemAsync calls for each item.
        /// </summary>
        /// <param name="tableName">The DynamoDB table to clean.</param>
        public async Task CleanTableAsync(string tableName)
        {
            Dictionary<string, AttributeValue>? lastEvaluatedKey = null;

            do
            {
                var scanRequest = new ScanRequest
                {
                    TableName = tableName,
                    ProjectionExpression = "PK, SK",
                    ExclusiveStartKey = lastEvaluatedKey
                };

                var scanResponse = await DynamoDbClient.ScanAsync(scanRequest);

                if (scanResponse.Items != null && scanResponse.Items.Count > 0)
                {
                    var deleteTasks = scanResponse.Items.Select(item =>
                        DynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                        {
                            TableName = tableName,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                { "PK", item["PK"] },
                                { "SK", item["SK"] }
                            }
                        })).ToList();

                    await Task.WhenAll(deleteTasks);
                }

                lastEvaluatedKey = scanResponse.LastEvaluatedKey?.Count > 0
                    ? scanResponse.LastEvaluatedKey
                    : null;
            }
            while (lastEvaluatedKey != null);
        }

        /// <summary>
        /// Resets the fixture state by cleaning all items from both DynamoDB tables
        /// and removing all objects from the S3 bucket. Provides a fast reset between
        /// test classes without the overhead of re-creating tables, topics, or buckets.
        /// </summary>
        public async Task ResetAsync()
        {
            // Clean both DynamoDB tables in parallel
            var cleanMetadataTask = CleanTableAsync(EntityMetadataTableName);
            var cleanRecordsTask = CleanTableAsync(RecordStorageTableName);

            // Clean S3 bucket objects
            var cleanBucketTask = EmptyS3BucketSafeAsync();

            await Task.WhenAll(cleanMetadataTask, cleanRecordsTask, cleanBucketTask);
        }

        /// <summary>
        /// Safely empties the S3 bucket, swallowing exceptions if the bucket doesn't exist.
        /// Used by ResetAsync for non-destructive cleanup between test runs.
        /// </summary>
        private async Task EmptyS3BucketSafeAsync()
        {
            try
            {
                await EmptyS3BucketAsync(ImportExportBucketName);
            }
            catch (AmazonS3Exception)
            {
                // Bucket may not exist — safe to ignore during reset.
            }
        }

        /// <summary>
        /// Seeds entity metadata and field metadata items directly into the DynamoDB
        /// EntityMetadata table. Inserts one entity metadata item (PK=ENTITY#{entity.Id},
        /// SK=META) and one field metadata item per field in entity.Fields
        /// (PK=ENTITY#{entity.Id}, SK=FIELD#{field.Id}).
        /// Uses TestDataHelper.CreateEntityMetadataItem and TestDataHelper.CreateFieldMetadataItem
        /// for DynamoDB item construction.
        /// </summary>
        /// <param name="entity">The entity definition to seed, including its Fields collection.</param>
        public async Task SeedEntityAsync(Entity entity)
        {
            // Insert entity metadata item
            var entityItem = TestDataHelper.CreateEntityMetadataItem(entity);
            await DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = EntityMetadataTableName,
                Item = entityItem
            });

            // Insert field metadata items for each field
            if (entity.Fields != null && entity.Fields.Count > 0)
            {
                var fieldTasks = entity.Fields.Select(field =>
                {
                    var fieldItem = TestDataHelper.CreateFieldMetadataItem(entity.Id, field);
                    return DynamoDbClient.PutItemAsync(new PutItemRequest
                    {
                        TableName = EntityMetadataTableName,
                        Item = fieldItem
                    });
                }).ToList();

                await Task.WhenAll(fieldTasks);
            }
        }

        #endregion
    }
}
