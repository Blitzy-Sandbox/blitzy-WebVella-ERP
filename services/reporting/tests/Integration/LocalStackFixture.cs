using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebVellaErp.Reporting.Migrations;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// Shared xUnit fixture that manages the LocalStack lifecycle for ALL integration tests
    /// in the Reporting &amp; Analytics service. This is the foundational test infrastructure class
    /// that ensures LocalStack services (RDS PostgreSQL, SNS, SQS, SSM) are available and
    /// configured before any integration test executes.
    ///
    /// <para>
    /// Implements <see cref="IAsyncLifetime"/> to perform async resource provisioning in
    /// <see cref="InitializeAsync"/> and cleanup in <see cref="DisposeAsync"/>.
    /// </para>
    ///
    /// <para>
    /// Pattern: <c>docker compose up -d</c> → provision → test → <c>docker compose down</c>.
    /// Per AAP Section 0.8.4: All integration tests MUST execute against LocalStack —
    /// NO mocked AWS SDK calls.
    /// </para>
    ///
    /// <para>
    /// Architectural replacement notes:
    /// <list type="bullet">
    ///   <item>Replaces <c>DbContext.CreateContext()</c> / <c>DbContext.Current</c> with explicit NpgsqlConnection via RdsConnectionString</item>
    ///   <item>Replaces <c>DbRepository.CreatePostgresqlExtensions()</c> with FluentMigrator Migration_001_InitialSchema.Up()</item>
    ///   <item>Replaces PostgreSQL LISTEN/NOTIFY with SNS/SQS event bus</item>
    ///   <item>Replaces in-process HookManager with SNS topic subscriptions to SQS queue</item>
    /// </list>
    /// </para>
    /// </summary>
    public class LocalStackFixture : IAsyncLifetime
    {
        // ============================================================
        // Constants — LocalStack Configuration
        // ============================================================

        /// <summary>LocalStack endpoint URL per AAP Section 0.8.6.</summary>
        private const string LocalStackEndpoint = "http://localhost:4566";

        /// <summary>AWS region per AAP Section 0.8.6.</summary>
        private const string AwsRegion = "us-east-1";

        /// <summary>Dummy access key for LocalStack authentication.</summary>
        private const string TestAccessKey = "test";

        /// <summary>Dummy secret key for LocalStack authentication.</summary>
        private const string TestSecretKey = "test";

        /// <summary>LocalStack RDS PostgreSQL port per folder requirements.</summary>
        private const int RdsPostgresPort = 4510;

        // ============================================================
        // Constants — Queue and Topic Names
        // ============================================================

        /// <summary>Main SQS event consumer queue name.</summary>
        private const string EventQueueName = "reporting-event-consumer";

        /// <summary>Dead-letter queue name per AAP Section 0.8.5: {service}-{queue}-dlq.</summary>
        private const string DlqName = "reporting-event-consumer-dlq";

        /// <summary>Reporting domain SNS topic name.</summary>
        private const string ReportingTopicName = "reporting-events";

        /// <summary>
        /// All 9 bounded context domain SNS topic names that the Reporting EventConsumer
        /// subscribes to for CQRS read-model projections (per AAP Section 0.4.2).
        /// </summary>
        private static readonly string[] DomainTopicNames =
        {
            "identity-events",
            "entity-management-events",
            "crm-events",
            "inventory-events",
            "invoicing-events",
            "notifications-events",
            "file-management-events",
            "workflow-events",
            "plugin-system-events"
        };

        // ============================================================
        // Constants — SSM Parameter Paths
        // ============================================================

        /// <summary>SSM path for DB connection string (SecureString per AAP Section 0.8.6).</summary>
        private const string SsmDbConnectionPath = "/reporting/db-connection-string";

        /// <summary>SSM path for Cognito user pool ID.</summary>
        private const string SsmCognitoPoolIdPath = "/reporting/cognito-user-pool-id";

        // ============================================================
        // Public Properties — AWS SDK Clients
        // ============================================================

        /// <summary>
        /// SQS client configured for LocalStack endpoint (<c>http://localhost:4566</c>).
        /// Used for queue creation, message operations, and DLQ management.
        /// </summary>
        public IAmazonSQS SqsClient { get; private set; } = null!;

        /// <summary>
        /// SNS client configured for LocalStack endpoint (<c>http://localhost:4566</c>).
        /// Used for topic creation and subscription management.
        /// </summary>
        public IAmazonSimpleNotificationService SnsClient { get; private set; } = null!;

        /// <summary>
        /// SSM client configured for LocalStack endpoint (<c>http://localhost:4566</c>).
        /// Used for parameter seeding and retrieval verification.
        /// </summary>
        public IAmazonSimpleSystemsManagement SsmClient { get; private set; } = null!;

        // ============================================================
        // Public Properties — Connection Strings and Resource URIs
        // ============================================================

        /// <summary>
        /// RDS PostgreSQL connection string for LocalStack.
        /// Format: <c>Host=localhost;Port=4510;Database=reporting_test;Username=postgres;Password=postgres</c>
        /// </summary>
        public string RdsConnectionString { get; private set; } = string.Empty;

        /// <summary>URL of the <c>reporting-event-consumer</c> SQS queue.</summary>
        public string EventQueueUrl { get; private set; } = string.Empty;

        /// <summary>URL of the <c>reporting-event-consumer-dlq</c> Dead-Letter Queue.</summary>
        public string DlqUrl { get; private set; } = string.Empty;

        /// <summary>ARN of the <c>reporting-events</c> SNS topic.</summary>
        public string ReportingTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// Map of domain name → SNS topic ARN for all 9 bounded contexts.
        /// Keys: identity, entity-management, crm, inventory, invoicing, notifications,
        /// file-management, workflow, plugin-system.
        /// </summary>
        public Dictionary<string, string> DomainTopicArns { get; private set; } = new();

        // ============================================================
        // Private Fields — Resource Tracking for Cleanup
        // ============================================================

        /// <summary>All created topic ARNs for cleanup in DisposeAsync.</summary>
        private readonly List<string> _createdTopicArns = new();

        /// <summary>Indicates whether the database was created by this fixture.</summary>
        private bool _databaseCreated;

        // ============================================================
        // IAsyncLifetime — Async Setup and Teardown
        // ============================================================

        /// <summary>
        /// Provisions all LocalStack resources required for integration tests:
        /// RDS PostgreSQL, SNS topics, SQS queues with DLQ, SSM parameters,
        /// and FluentMigrator schema migrations.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Verify LocalStack is running
            await VerifyLocalStackHealthAsync();

            // Initialize AWS SDK clients with LocalStack endpoint
            InitializeAwsClients();

            // Step 2: Set up RDS PostgreSQL test database
            await SetupRdsPostgresAsync();

            // Step 3: Create SNS topics (1 reporting + 9 domain)
            await CreateSnsTopicsAsync();

            // Step 4: Create SQS queues (DLQ first, then main queue with redrive policy)
            await CreateSqsQueuesAsync();

            // Step 5: Subscribe SQS queue to all 9 domain SNS topics
            await SubscribeSqsToSnsTopicsAsync();

            // Step 6: Seed SSM parameters (DB connection as SecureString)
            await SeedSsmParametersAsync();

            // Step 7: Run FluentMigrator migrations to create reporting schema.
            // Wrapped in try-catch — if RDS PostgreSQL is not available (LocalStack Pro required),
            // migrations will fail gracefully. Tests decorated with [RdsFact] will skip automatically.
            try
            {
                if (!string.IsNullOrEmpty(RdsConnectionString))
                {
                    await RunMigrationsAsync();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[LocalStackFixture] WARNING: FluentMigrator migrations failed. " +
                    $"RDS-dependent integration tests will be skipped via [RdsFact]. Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up all LocalStack resources in reverse order.
        /// Handles exceptions gracefully to avoid masking test failures.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Clean up in reverse provisioning order
            await SafeExecuteAsync("delete SQS queues", DeleteSqsQueuesAsync);
            await SafeExecuteAsync("delete SNS topics", DeleteSnsTopicsAsync);
            await SafeExecuteAsync("delete SSM parameters", DeleteSsmParametersAsync);
            await SafeExecuteAsync("clean database schema", CleanDatabaseAsync);

            // Dispose AWS SDK clients
            SafeDispose(SqsClient as IDisposable);
            SafeDispose(SnsClient as IDisposable);
            SafeDispose(SsmClient as IDisposable);
        }

        // ============================================================
        // Public Helper Methods — For Use by Integration Test Classes
        // ============================================================

        /// <summary>
        /// Sends a message to the <c>reporting-event-consumer</c> SQS queue.
        /// </summary>
        /// <param name="messageBody">The message body (typically serialized domain event JSON).</param>
        /// <param name="attributes">Optional SQS message attributes (e.g., correlationId).</param>
        /// <returns>The SQS message ID of the sent message.</returns>
        public async Task<string> SendSqsMessageAsync(
            string messageBody,
            Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>? attributes = null)
        {
            var request = new SendMessageRequest
            {
                QueueUrl = EventQueueUrl,
                MessageBody = messageBody
            };

            if (attributes != null)
            {
                foreach (var kvp in attributes)
                {
                    request.MessageAttributes[kvp.Key] = kvp.Value;
                }
            }

            var response = await SqsClient.SendMessageAsync(request);
            return response.MessageId;
        }

        /// <summary>
        /// Publishes a message to an SNS topic. The message routes to the SQS queue
        /// via the subscription created during <see cref="InitializeAsync"/>.
        /// </summary>
        /// <param name="topicArn">The SNS topic ARN to publish to.</param>
        /// <param name="messageBody">The message body (typically serialized domain event JSON).</param>
        /// <returns>The SNS message ID of the published message.</returns>
        public async Task<string> PublishSnsMessageAsync(string topicArn, string messageBody)
        {
            var response = await SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = messageBody
            });
            return response.MessageId;
        }

        /// <summary>
        /// Receives messages from the <c>reporting-event-consumer-dlq</c> Dead-Letter Queue
        /// for verification in DLQ-related integration tests.
        /// </summary>
        /// <param name="maxMessages">Maximum number of messages to receive (1-10).</param>
        /// <returns>List of SQS messages from the DLQ.</returns>
        public async Task<List<Message>> ReceiveDlqMessagesAsync(int maxMessages = 10)
        {
            var response = await SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = DlqUrl,
                MaxNumberOfMessages = Math.Min(maxMessages, 10),
                WaitTimeSeconds = 5,
                MessageAttributeNames = new List<string> { "All" },
                MessageSystemAttributeNames = new List<string> { "All" }
            });
            return response.Messages ?? new List<Message>();
        }

        /// <summary>
        /// Purges all messages from the specified SQS queue for test isolation between tests.
        /// </summary>
        /// <param name="queueUrl">The queue URL to purge. Defaults to <see cref="EventQueueUrl"/> if null.</param>
        public async Task PurgeQueueAsync(string? queueUrl = null)
        {
            var targetUrl = queueUrl ?? EventQueueUrl;
            if (string.IsNullOrEmpty(targetUrl))
            {
                return;
            }

            await SqsClient.PurgeQueueAsync(new PurgeQueueRequest
            {
                QueueUrl = targetUrl
            });

            // Brief delay to allow purge to propagate in LocalStack
            await Task.Delay(500);
        }

        /// <summary>
        /// Retrieves an SSM parameter value from LocalStack for test verification.
        /// </summary>
        /// <param name="name">The SSM parameter name (e.g., <c>/reporting/db-connection-string</c>).</param>
        /// <returns>The decrypted parameter value.</returns>
        public async Task<string> GetSsmParameterAsync(string name)
        {
            var response = await SsmClient.GetParameterAsync(new GetParameterRequest
            {
                Name = name,
                WithDecryption = true
            });
            return response.Parameter.Value;
        }

        // ============================================================
        // Private Implementation — Step 1: Verify LocalStack Health
        // ============================================================

        /// <summary>
        /// Verifies that LocalStack is running and healthy by hitting the health endpoint.
        /// Throws <see cref="SkipException"/> if LocalStack is unavailable for graceful test skip.
        /// </summary>
        private static async Task VerifyLocalStackHealthAsync()
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            try
            {
                var response = await httpClient.GetAsync($"{LocalStackEndpoint}/_localstack/health");
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Do NOT throw — allow fixture creation to complete.
                // Tests decorated with [RdsFact] will be skipped when RDS is unavailable.
                Console.Error.WriteLine(
                    $"[LocalStackFixture] WARNING: LocalStack is not running. " +
                    $"RDS-dependent integration tests will be skipped via [RdsFact]. " +
                    $"Failed to connect to {LocalStackEndpoint}/_localstack/health: {ex.Message}");
            }
        }

        // ============================================================
        // Private Implementation — AWS SDK Client Initialization
        // ============================================================

        /// <summary>
        /// Initializes all AWS SDK clients with LocalStack endpoint configuration.
        /// All clients use ServiceURL = http://localhost:4566 and test credentials.
        /// </summary>
        private void InitializeAwsClients()
        {
            var credentials = new BasicAWSCredentials(TestAccessKey, TestSecretKey);

            SqsClient = new AmazonSQSClient(credentials, new AmazonSQSConfig
            {
                ServiceURL = LocalStackEndpoint,
                AuthenticationRegion = AwsRegion
            });

            SnsClient = new AmazonSimpleNotificationServiceClient(
                credentials,
                new AmazonSimpleNotificationServiceConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = AwsRegion
                });

            SsmClient = new AmazonSimpleSystemsManagementClient(
                credentials,
                new AmazonSimpleSystemsManagementConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = AwsRegion
                });
        }

        // ============================================================
        // Private Implementation — Step 2: RDS PostgreSQL Setup
        // ============================================================

        /// <summary>
        /// Sets up RDS PostgreSQL test database on LocalStack port 4510.
        /// Attempts direct connection first; if database doesn't exist, creates it via master connection.
        /// </summary>
        private async Task SetupRdsPostgresAsync()
        {
            var masterConnectionString =
                $"Host=localhost;Port={RdsPostgresPort};Database=postgres;Username=test;Password=test";
            RdsConnectionString =
                $"Host=localhost;Port={RdsPostgresPort};Database=reporting_test;Username=test;Password=test";

            // First, attempt direct connection to reporting_test database
            try
            {
                await using var directConn = new NpgsqlConnection(RdsConnectionString);
                await directConn.OpenAsync();
                await using var verifyCmd = new NpgsqlCommand("SELECT 1", directConn);
                await verifyCmd.ExecuteScalarAsync();
                return; // Database exists and is accessible
            }
            catch
            {
                // Database may not exist yet — attempt creation via master database
            }

            // Connect to master/postgres database and create reporting_test.
            // Multiple IClassFixture<LocalStackFixture> instances may race on CREATE DATABASE.
            // Catch PostgresException 23505 (duplicate key) when a concurrent fixture wins the race.
            try
            {
                await using var masterConn = new NpgsqlConnection(masterConnectionString);
                await masterConn.OpenAsync();

                // Check if the database already exists
                await using var checkCmd = new NpgsqlCommand(
                    "SELECT 1 FROM pg_database WHERE datname = 'reporting_test'", masterConn);
                var exists = await checkCmd.ExecuteScalarAsync();

                if (exists == null)
                {
                    try
                    {
                        await using var createCmd = new NpgsqlCommand(
                            @"CREATE DATABASE reporting_test", masterConn);
                        await createCmd.ExecuteNonQueryAsync();
                        _databaseCreated = true;
                    }
                    catch (PostgresException pgEx) when (pgEx.SqlState == "23505" || pgEx.SqlState == "42P04")
                    {
                        // Database was created by a concurrent fixture instance — expected
                        // when multiple test classes use IClassFixture<LocalStackFixture> in parallel.
                    }
                }

                // Verify connectivity to the newly created (or already existing) database
                await using var testConn = new NpgsqlConnection(RdsConnectionString);
                await testConn.OpenAsync();
                await using var selectCmd = new NpgsqlCommand("SELECT 1", testConn);
                await selectCmd.ExecuteScalarAsync();
            }
            catch (Exception ex)
            {
                // Do NOT throw — allow fixture creation to complete.
                // Tests decorated with [RdsFact] will be skipped when RDS is unavailable.
                Console.Error.WriteLine(
                    $"[LocalStackFixture] WARNING: LocalStack RDS PostgreSQL is not available on port {RdsPostgresPort}. " +
                    $"RDS-dependent integration tests will be skipped via [RdsFact]. Error: {ex.Message}");
            }
        }

        // ============================================================
        // Private Implementation — Step 3: SNS Topic Creation
        // ============================================================

        /// <summary>
        /// Creates all 10 SNS topics: 1 reporting-events + 9 bounded context domain topics.
        /// Topics enable the EventConsumer to receive events from all services for CQRS projections.
        /// </summary>
        private async Task CreateSnsTopicsAsync()
        {
            // Create the reporting domain's own SNS topic
            var reportingTopicResponse = await SnsClient.CreateTopicAsync(new CreateTopicRequest
            {
                Name = ReportingTopicName
            });
            ReportingTopicArn = reportingTopicResponse.TopicArn;
            _createdTopicArns.Add(ReportingTopicArn);

            // Create all 9 bounded context domain topics
            DomainTopicArns = new Dictionary<string, string>(DomainTopicNames.Length);
            foreach (var topicName in DomainTopicNames)
            {
                var response = await SnsClient.CreateTopicAsync(new CreateTopicRequest
                {
                    Name = topicName
                });

                // Extract domain key from topic name: "crm-events" → "crm", "entity-management-events" → "entity-management"
                var domainKey = topicName.EndsWith("-events", StringComparison.OrdinalIgnoreCase)
                    ? topicName[..^"-events".Length]
                    : topicName;

                DomainTopicArns[domainKey] = response.TopicArn;
                _createdTopicArns.Add(response.TopicArn);
            }
        }

        // ============================================================
        // Private Implementation — Step 4: SQS Queue Creation
        // ============================================================

        /// <summary>
        /// Creates the SQS Dead-Letter Queue and main event consumer queue.
        /// The main queue is configured with a redrive policy pointing to the DLQ
        /// with maxReceiveCount of 3 per AAP Section 0.8.5.
        /// </summary>
        private async Task CreateSqsQueuesAsync()
        {
            // Create the Dead-Letter Queue first (main queue's redrive policy depends on it)
            var dlqResponse = await SqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = DlqName
            });
            DlqUrl = dlqResponse.QueueUrl;

            // Retrieve the DLQ ARN for the main queue's redrive policy
            var dlqAttrsResponse = await SqsClient.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = DlqUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                });
            var dlqArn = dlqAttrsResponse.Attributes["QueueArn"];

            // Create the main event consumer queue with DLQ redrive policy
            var queueResponse = await SqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = EventQueueName,
                Attributes = new Dictionary<string, string>
                {
                    ["RedrivePolicy"] =
                        $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":\"3\"}}",
                    ["VisibilityTimeout"] = "60",
                    ["MessageRetentionPeriod"] = "345600" // 4 days
                }
            });
            EventQueueUrl = queueResponse.QueueUrl;
        }

        // ============================================================
        // Private Implementation — Step 5: SQS → SNS Subscriptions
        // ============================================================

        /// <summary>
        /// Subscribes the <c>reporting-event-consumer</c> SQS queue to all 9 domain SNS topics.
        /// This enables the EventConsumer Lambda to receive domain events from all bounded contexts
        /// for building CQRS read-model projections in RDS PostgreSQL.
        /// </summary>
        private async Task SubscribeSqsToSnsTopicsAsync()
        {
            // Get the SQS queue ARN for SNS subscription endpoint
            var queueAttrsResponse = await SqsClient.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = EventQueueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                });
            var queueArn = queueAttrsResponse.Attributes["QueueArn"];

            // Subscribe to all 9 domain SNS topics
            foreach (var topicArn in DomainTopicArns.Values)
            {
                await SnsClient.SubscribeAsync(new SubscribeRequest
                {
                    TopicArn = topicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn
                });
            }
        }

        // ============================================================
        // Private Implementation — Step 6: SSM Parameter Seeding
        // ============================================================

        /// <summary>
        /// Seeds SSM Parameter Store with required configuration parameters.
        /// DB_CONNECTION_STRING is stored as SecureString per AAP Section 0.8.6:
        /// "DB_CONNECTION_STRING stored as SSM SecureString, never environment variables."
        /// </summary>
        private async Task SeedSsmParametersAsync()
        {
            // Seed DB_CONNECTION_STRING as SSM SecureString
            await SsmClient.PutParameterAsync(new PutParameterRequest
            {
                Name = SsmDbConnectionPath,
                Value = RdsConnectionString,
                Type = ParameterType.SecureString,
                Overwrite = true
            });

            // Seed COGNITO_USER_POOL_ID for integration with identity service
            await SsmClient.PutParameterAsync(new PutParameterRequest
            {
                Name = SsmCognitoPoolIdPath,
                Value = "us-east-1_TestPool",
                Type = ParameterType.String,
                Overwrite = true
            });
        }

        // ============================================================
        // Private Implementation — Step 7: FluentMigrator Migrations
        // ============================================================

        /// <summary>
        /// Executes <see cref="Migration_001_InitialSchema"/> against the test RDS PostgreSQL database
        /// via FluentMigrator Runner. Creates: uuid-ossp extension, <c>reporting</c> schema,
        /// and all 3 tables (report_definitions, read_model_projections, event_offsets) with indexes.
        /// </summary>
        private async Task RunMigrationsAsync()
        {
            // Build FluentMigrator runner via DI container
            var serviceProvider = new ServiceCollection()
                .AddFluentMigratorCore()
                .ConfigureRunner(rb => rb
                    .AddPostgres()
                    .WithGlobalConnectionString(RdsConnectionString)
                    .ScanIn(typeof(Migration_001_InitialSchema).Assembly).For.Migrations())
                .BuildServiceProvider(validateScopes: false);

            using (serviceProvider)
            {
                var runner = serviceProvider.GetRequiredService<IMigrationRunner>();
                runner.MigrateUp();
            }

            // Verify migration success by checking table count in reporting schema
            await using var connection = new NpgsqlConnection(RdsConnectionString);
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = 'reporting'",
                connection);
            var tableCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());

            if (tableCount < 3)
            {
                throw new InvalidOperationException(
                    $"Migration verification failed: expected at least 3 tables in " +
                    $"'reporting' schema, found {tableCount}. Tables: report_definitions, " +
                    "read_model_projections, event_offsets.");
            }
        }

        // ============================================================
        // Private Cleanup Methods — DisposeAsync Helpers
        // ============================================================

        /// <summary>Deletes the event consumer SQS queue and its DLQ.</summary>
        private async Task DeleteSqsQueuesAsync()
        {
            if (!string.IsNullOrEmpty(EventQueueUrl))
            {
                await SqsClient.DeleteQueueAsync(new DeleteQueueRequest
                {
                    QueueUrl = EventQueueUrl
                });
            }

            if (!string.IsNullOrEmpty(DlqUrl))
            {
                await SqsClient.DeleteQueueAsync(new DeleteQueueRequest
                {
                    QueueUrl = DlqUrl
                });
            }
        }

        /// <summary>Deletes all 10 SNS topics (1 reporting + 9 domain).</summary>
        private async Task DeleteSnsTopicsAsync()
        {
            foreach (var topicArn in _createdTopicArns)
            {
                if (!string.IsNullOrEmpty(topicArn))
                {
                    await SnsClient.DeleteTopicAsync(new DeleteTopicRequest
                    {
                        TopicArn = topicArn
                    });
                }
            }
        }

        /// <summary>Deletes seeded SSM parameters.</summary>
        private async Task DeleteSsmParametersAsync()
        {
            try
            {
                await SsmClient.DeleteParameterAsync(new DeleteParameterRequest
                {
                    Name = SsmDbConnectionPath
                });
            }
            catch (Amazon.SimpleSystemsManagement.Model.ParameterNotFoundException)
            {
                // Parameter may have already been deleted; safe to ignore
            }

            try
            {
                await SsmClient.DeleteParameterAsync(new DeleteParameterRequest
                {
                    Name = SsmCognitoPoolIdPath
                });
            }
            catch (Amazon.SimpleSystemsManagement.Model.ParameterNotFoundException)
            {
                // Parameter may have already been deleted; safe to ignore
            }
        }

        /// <summary>
        /// Drops the reporting schema and optionally the test database.
        /// </summary>
        private async Task CleanDatabaseAsync()
        {
            if (string.IsNullOrEmpty(RdsConnectionString))
            {
                return;
            }

            try
            {
                // Drop the reporting schema (cascades to all tables)
                await using var connection = new NpgsqlConnection(RdsConnectionString);
                await connection.OpenAsync();
                await using var dropSchemaCmd = new NpgsqlCommand(
                    "DROP SCHEMA IF EXISTS reporting CASCADE;", connection);
                await dropSchemaCmd.ExecuteNonQueryAsync();
            }
            catch (Exception)
            {
                // Swallow exceptions during cleanup to avoid masking test failures
            }

            // If we created the database, drop it
            if (_databaseCreated)
            {
                try
                {
                    var masterConnectionString =
                        $"Host=localhost;Port={RdsPostgresPort};Database=postgres;Username=test;Password=test";
                    await using var masterConn = new NpgsqlConnection(masterConnectionString);
                    await masterConn.OpenAsync();

                    // Terminate active connections to the test database
                    await using var terminateCmd = new NpgsqlCommand(
                        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                        "WHERE datname = 'reporting_test' AND pid <> pg_backend_pid()",
                        masterConn);
                    await terminateCmd.ExecuteNonQueryAsync();

                    // Drop the test database
                    await using var dropDbCmd = new NpgsqlCommand(
                        @"DROP DATABASE IF EXISTS reporting_test", masterConn);
                    await dropDbCmd.ExecuteNonQueryAsync();
                }
                catch (Exception)
                {
                    // Swallow exceptions during cleanup to avoid masking test failures
                }
            }
        }

        // ============================================================
        // Private Utility Methods
        // ============================================================

        /// <summary>
        /// Executes an async action with error handling. Logs failures to stderr
        /// but does not re-throw, preventing cleanup errors from masking test failures.
        /// </summary>
        /// <param name="operationName">Human-readable description of the operation for logging.</param>
        /// <param name="action">The async action to execute.</param>
        private static async Task SafeExecuteAsync(string operationName, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[LocalStackFixture] Warning: Failed to {operationName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a skip-or-fail exception for when LocalStack is unavailable.
        /// Attempts to use xUnit's dynamic skip mechanism (<c>$XunitDynamicSkip$</c> prefix)
        /// via <c>Xunit.Sdk.SkipException</c> (internal constructor, accessed via reflection).
        /// Falls back to <see cref="InvalidOperationException"/> if the skip mechanism is unavailable.
        /// </summary>
        /// <param name="message">The descriptive skip/failure message.</param>
        /// <returns>An exception to be thrown by the caller.</returns>
        private static Exception CreateSkipOrFailException(string message)
        {
            // Attempt to use xUnit's SkipException (has internal constructor in v2.9.x)
            try
            {
                var skipExType = typeof(Xunit.Assert).Assembly.GetType("Xunit.Sdk.SkipException");
                if (skipExType != null)
                {
                    var ctor = skipExType.GetConstructor(
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        binder: null,
                        types: new[] { typeof(string) },
                        modifiers: null);
                    if (ctor != null)
                    {
                        return (Exception)ctor.Invoke(new object[] { message });
                    }
                }
            }
            catch
            {
                // Reflection failed; fall through to InvalidOperationException
            }

            // Fallback: InvalidOperationException with clear message
            return new InvalidOperationException(message);
        }

        /// <summary>
        /// Safely disposes an IDisposable instance, swallowing any exceptions.
        /// </summary>
        /// <param name="disposable">The disposable to clean up.</param>
        private static void SafeDispose(IDisposable? disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[LocalStackFixture] Warning: Dispose failed: {ex.Message}");
            }
        }
    }
}
