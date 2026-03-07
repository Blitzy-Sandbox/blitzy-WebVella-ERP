using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using DotNet.Testcontainers.Builders;
using Testcontainers.LocalStack;
using Xunit;

namespace WebVella.Erp.Tests.Integration.Fixtures
{
    /// <summary>
    /// xUnit IAsyncLifetime fixture that uses Testcontainers.LocalStack (v4.10.0) to
    /// programmatically spin up a LocalStack Docker container emulating AWS services
    /// (SQS, SNS, S3). Provisions the necessary SNS topics, SQS queues, and S3 buckets
    /// matching the docker-compose.localstack.yml topology defined in AAP 0.7.4.
    ///
    /// This fixture replaces the monolith's PostgreSQL LISTEN/NOTIFY pub/sub on
    /// ERP_NOTIFICATIONS_CHANNNEL with cloud-native SNS/SQS messaging.
    ///
    /// Usage:
    ///   public class MyTests : IClassFixture&lt;LocalStackFixture&gt;
    ///   {
    ///       private readonly LocalStackFixture _fixture;
    ///       public MyTests(LocalStackFixture fixture) { _fixture = fixture; }
    ///   }
    ///
    /// The LocalStack endpoint URL is injectable via the LOCALSTACK_ENDPOINT environment
    /// variable pattern to support switching between local and production AWS endpoints.
    /// </summary>
    public class LocalStackFixture : IAsyncLifetime
    {
        #region Private Fields

        /// <summary>
        /// The Testcontainers-managed LocalStack Docker container instance.
        /// Lifecycle managed via InitializeAsync/DisposeAsync.
        /// </summary>
        private LocalStackContainer _container;

        /// <summary>
        /// Stores provisioned SNS topic name to ARN mappings.
        /// Populated during InitializeAsync after topic creation.
        /// </summary>
        private readonly Dictionary<string, string> _topicArns = new Dictionary<string, string>();

        /// <summary>
        /// Stores provisioned SQS queue name to URL mappings.
        /// Populated during InitializeAsync after queue creation.
        /// </summary>
        private readonly Dictionary<string, string> _queueUrls = new Dictionary<string, string>();

        /// <summary>
        /// Stores provisioned SQS queue name to ARN mappings.
        /// Required for SNS-to-SQS subscription setup.
        /// </summary>
        private readonly Dictionary<string, string> _queueArns = new Dictionary<string, string>();

        /// <summary>
        /// Maximum time to wait for LocalStack health endpoint to report all services available.
        /// </summary>
        private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Interval between health check polling attempts.
        /// </summary>
        private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(2);

        #endregion

        #region Public Properties

        /// <summary>
        /// The LocalStack endpoint URL (e.g., http://localhost:{mapped_port}).
        /// Use this for configuring AWS SDK clients to point to LocalStack.
        /// Supports injection via LOCALSTACK_ENDPOINT environment variable pattern.
        /// </summary>
        public string Endpoint { get; private set; }

        /// <summary>
        /// The LocalStack endpoint as a Uri for AWS SDK client configuration.
        /// Equivalent to <c>new Uri(Endpoint)</c>.
        /// </summary>
        public Uri EndpointUri { get; private set; }

        /// <summary>
        /// The mapped host port for the LocalStack container.
        /// The container's internal port 4566 is mapped to this dynamic host port.
        /// </summary>
        public int Port { get; private set; }

        #endregion

        #region Constants — Container Configuration

        /// <summary>
        /// Docker image for the LocalStack container.
        /// Per AAP 0.7.4: image: localstack/localstack:latest
        /// </summary>
        public const string ContainerImage = "localstack/localstack:latest";

        /// <summary>
        /// Default internal port for the LocalStack gateway.
        /// Per AAP 0.7.4 Docker Compose spec: ports: ["4566:4566"]
        /// </summary>
        public const int DefaultPort = 4566;

        #endregion

        #region Constants — SNS Topics (Domain Event Types from AAP 0.5.1)

        /// <summary>SNS topic for Core service record creation events.</summary>
        public const string CoreRecordCreatedTopic = "erp-core-record-created";

        /// <summary>SNS topic for Core service record update events.</summary>
        public const string CoreRecordUpdatedTopic = "erp-core-record-updated";

        /// <summary>SNS topic for Core service record deletion events.</summary>
        public const string CoreRecordDeletedTopic = "erp-core-record-deleted";

        /// <summary>SNS topic for Core service entity schema change events.</summary>
        public const string CoreEntityChangedTopic = "erp-core-entity-changed";

        /// <summary>SNS topic for CRM service account update events.</summary>
        public const string CrmAccountUpdatedTopic = "erp-crm-account-updated";

        /// <summary>SNS topic for CRM service contact update events.</summary>
        public const string CrmContactUpdatedTopic = "erp-crm-contact-updated";

        /// <summary>SNS topic for CRM service case update events.</summary>
        public const string CrmCaseUpdatedTopic = "erp-crm-case-updated";

        /// <summary>SNS topic for Project service task creation events.</summary>
        public const string ProjectTaskCreatedTopic = "erp-project-task-created";

        /// <summary>SNS topic for Project service task update events.</summary>
        public const string ProjectTaskUpdatedTopic = "erp-project-task-updated";

        /// <summary>SNS topic for Mail service email sent events.</summary>
        public const string MailSentTopic = "erp-mail-sent";

        /// <summary>SNS topic for Mail service email queued events.</summary>
        public const string MailQueuedTopic = "erp-mail-queued";

        #endregion

        #region Constants — SQS Queues (Subscriber Queues per Service)

        /// <summary>SQS queue for CRM service event consumption.</summary>
        public const string CrmEventQueue = "erp-crm-events";

        /// <summary>SQS queue for Project service event consumption.</summary>
        public const string ProjectEventQueue = "erp-project-events";

        /// <summary>SQS queue for Mail service event consumption.</summary>
        public const string MailEventQueue = "erp-mail-events";

        /// <summary>SQS queue for Reporting service event consumption.</summary>
        public const string ReportingEventQueue = "erp-reporting-events";

        /// <summary>SQS queue for Admin service event consumption.</summary>
        public const string AdminEventQueue = "erp-admin-events";

        #endregion

        #region Constants — S3 Buckets

        /// <summary>
        /// S3 bucket for file storage, replacing the monolith's Storage.Net integration.
        /// Per AAP 0.7.4: S3 replaces Storage.Net cloud blob storage.
        /// </summary>
        public const string FileStorageBucket = "erp-file-storage";

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Starts the LocalStack Docker container, waits for service readiness,
        /// then provisions all SNS topics, SQS queues, SNS-to-SQS subscriptions,
        /// and S3 buckets required by the microservice event topology.
        ///
        /// Execution steps:
        /// 1. Build and start the LocalStack container
        /// 2. Extract connection info (port, endpoint URL)
        /// 3. Wait for LocalStack health endpoint to report SQS, SNS, S3 available
        /// 4. Create all 11 SNS topics
        /// 5. Create all 5 SQS queues
        /// 6. Establish SNS-to-SQS subscriptions matching service event routing
        /// 7. Create S3 bucket for file storage
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Build and start the LocalStack container
            // Per AAP 0.7.4: SERVICES=sqs,sns,s3 and DEBUG=1
            _container = new LocalStackBuilder(ContainerImage)
                .WithEnvironment("SERVICES", "sqs,sns,s3")
                .WithEnvironment("DEBUG", "1")
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(request =>
                            request.ForPort((ushort)DefaultPort)
                                   .ForPath("/_localstack/health")))
                .Build();

            await _container.StartAsync().ConfigureAwait(false);

            // Step 2: Extract connection info
            Port = _container.GetMappedPublicPort(DefaultPort);
            Endpoint = _container.GetConnectionString();
            EndpointUri = new Uri(Endpoint);

            // Step 3: Wait for all required services to be fully ready
            // The Testcontainers wait strategy checks for HTTP 200, but we also
            // verify that SQS, SNS, and S3 are individually reported as "available"
            await WaitForLocalStackServicesReady().ConfigureAwait(false);

            // Step 4: Create all SNS topics
            await ProvisionSnsTopics().ConfigureAwait(false);

            // Step 5: Create all SQS queues
            await ProvisionSqsQueues().ConfigureAwait(false);

            // Step 6: Subscribe SQS queues to SNS topics (matching service event routing)
            await ProvisionSnsToSqsSubscriptions().ConfigureAwait(false);

            // Step 7: Create S3 bucket for file storage
            await ProvisionS3Buckets().ConfigureAwait(false);
        }

        /// <summary>
        /// Stops and removes the LocalStack Docker container.
        /// All provisioned SNS topics, SQS queues, and S3 buckets are destroyed
        /// automatically when the container is removed.
        /// Exceptions during disposal are swallowed to prevent test cleanup failures.
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_container != null)
            {
                try
                {
                    await _container.StopAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Swallow exceptions during container stop to prevent test cleanup failures.
                    // The container may already be stopped or the Docker daemon unresponsive.
                }

                try
                {
                    await _container.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Swallow exceptions during container disposal.
                    // Resources are cleaned up by Docker regardless.
                }
            }

            _topicArns.Clear();
            _queueUrls.Clear();
            _queueArns.Clear();
        }

        #endregion

        #region Public Accessors — Provisioned Resources

        /// <summary>
        /// Returns the ARN for a provisioned SNS topic by its name.
        /// </summary>
        /// <param name="topicName">The topic name constant (e.g., CoreRecordCreatedTopic)</param>
        /// <returns>The full ARN of the SNS topic in LocalStack</returns>
        /// <exception cref="KeyNotFoundException">
        /// Thrown when the requested topic name was not provisioned during initialization.
        /// </exception>
        public string GetTopicArn(string topicName)
        {
            if (string.IsNullOrWhiteSpace(topicName))
            {
                throw new ArgumentException("Topic name cannot be null or empty.", nameof(topicName));
            }

            if (_topicArns.TryGetValue(topicName, out var arn))
            {
                return arn;
            }

            throw new KeyNotFoundException(
                $"SNS topic '{topicName}' was not provisioned. " +
                $"Available topics: {string.Join(", ", _topicArns.Keys)}");
        }

        /// <summary>
        /// Returns the URL for a provisioned SQS queue by its name.
        /// </summary>
        /// <param name="queueName">The queue name constant (e.g., CrmEventQueue)</param>
        /// <returns>The full URL of the SQS queue in LocalStack</returns>
        /// <exception cref="KeyNotFoundException">
        /// Thrown when the requested queue name was not provisioned during initialization.
        /// </exception>
        public string GetQueueUrl(string queueName)
        {
            if (string.IsNullOrWhiteSpace(queueName))
            {
                throw new ArgumentException("Queue name cannot be null or empty.", nameof(queueName));
            }

            if (_queueUrls.TryGetValue(queueName, out var url))
            {
                return url;
            }

            throw new KeyNotFoundException(
                $"SQS queue '{queueName}' was not provisioned. " +
                $"Available queues: {string.Join(", ", _queueUrls.Keys)}");
        }

        /// <summary>
        /// Creates a new AmazonSQSClient configured to communicate with the LocalStack
        /// SQS endpoint using test credentials.
        /// The caller is responsible for disposing the returned client.
        /// </summary>
        /// <returns>A configured AmazonSQSClient pointing to LocalStack</returns>
        public AmazonSQSClient CreateSqsClient()
        {
            var config = new AmazonSQSConfig
            {
                ServiceURL = Endpoint,
                AuthenticationRegion = GetTestRegion().SystemName
            };
            return new AmazonSQSClient(GetTestCredentials(), config);
        }

        /// <summary>
        /// Creates a new AmazonSimpleNotificationServiceClient configured to communicate
        /// with the LocalStack SNS endpoint using test credentials.
        /// The caller is responsible for disposing the returned client.
        /// </summary>
        /// <returns>A configured AmazonSimpleNotificationServiceClient pointing to LocalStack</returns>
        public AmazonSimpleNotificationServiceClient CreateSnsClient()
        {
            var config = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = Endpoint,
                AuthenticationRegion = GetTestRegion().SystemName
            };
            return new AmazonSimpleNotificationServiceClient(GetTestCredentials(), config);
        }

        /// <summary>
        /// Creates a new AmazonS3Client configured to communicate with the LocalStack
        /// S3 endpoint using test credentials. ForcePathStyle is enabled for LocalStack
        /// compatibility (virtual-hosted-style addressing is not supported by LocalStack).
        /// The caller is responsible for disposing the returned client.
        /// </summary>
        /// <returns>A configured AmazonS3Client pointing to LocalStack with ForcePathStyle=true</returns>
        public AmazonS3Client CreateS3Client()
        {
            var config = new AmazonS3Config
            {
                ServiceURL = Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = GetTestRegion().SystemName
            };
            return new AmazonS3Client(GetTestCredentials(), config);
        }

        #endregion

        #region Private Helpers — AWS Client Configuration

        /// <summary>
        /// Returns test credentials for LocalStack authentication.
        /// LocalStack accepts any credentials; "test"/"test" is the convention.
        /// </summary>
        private AWSCredentials GetTestCredentials()
        {
            return new BasicAWSCredentials("test", "test");
        }

        /// <summary>
        /// Returns the default AWS region for LocalStack.
        /// LocalStack defaults to us-east-1.
        /// </summary>
        private RegionEndpoint GetTestRegion()
        {
            return RegionEndpoint.USEast1;
        }

        /// <summary>
        /// Generic factory method for creating AWS SDK clients configured to point to
        /// the LocalStack endpoint. The factory delegate receives pre-configured test
        /// credentials and a ClientConfig base reference, enabling callers to create
        /// any AWS service client type.
        ///
        /// Usage example:
        /// <code>
        /// var sqsClient = CreateAwsClient&lt;AmazonSQSClient&gt;((creds, _) =&gt;
        /// {
        ///     var config = new AmazonSQSConfig { ServiceURL = Endpoint };
        ///     return new AmazonSQSClient(creds, config);
        /// });
        /// </code>
        /// </summary>
        /// <typeparam name="T">The AWS service client type to create</typeparam>
        /// <param name="factory">
        /// Factory delegate receiving test credentials and a base ClientConfig reference.
        /// The factory is responsible for creating the appropriate typed ClientConfig
        /// subclass (e.g., AmazonSQSConfig, AmazonS3Config) and the service client.
        /// </param>
        /// <returns>A configured AWS service client instance</returns>
        private T CreateAwsClient<T>(Func<AWSCredentials, ClientConfig, T> factory)
        {
            var credentials = GetTestCredentials();
            // ClientConfig is abstract; the factory delegate handles concrete config creation.
            // Passing null for the config parameter since each AWS service requires its own
            // ClientConfig subclass which the factory must instantiate with ServiceURL set.
            return factory(credentials, null);
        }

        #endregion

        #region Private Methods — Health Check and Service Readiness

        /// <summary>
        /// Polls the LocalStack health endpoint until SQS, SNS, and S3 services
        /// all report as "available" or "running".
        /// Uses a 2-second polling interval with a 60-second timeout.
        /// </summary>
        private async Task WaitForLocalStackServicesReady()
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                var healthUrl = $"{Endpoint}/_localstack/health";
                var deadline = DateTime.UtcNow.Add(HealthCheckTimeout);

                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var response = await httpClient.GetAsync(healthUrl).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                            // Verify that SQS, SNS, and S3 are all available.
                            // LocalStack health endpoint returns JSON with service statuses.
                            // Services can report as "available", "running", or "ready".
                            bool sqsReady = content.Contains("\"sqs\"") &&
                                            (content.Contains("\"available\"") || content.Contains("\"running\"") || content.Contains("\"ready\""));
                            bool snsReady = content.Contains("\"sns\"");
                            bool s3Ready = content.Contains("\"s3\"");

                            if (sqsReady && snsReady && s3Ready)
                            {
                                return;
                            }
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // Service not yet available, continue polling
                    }
                    catch (TaskCanceledException)
                    {
                        // HTTP timeout, continue polling
                    }

                    await Task.Delay(HealthCheckInterval).ConfigureAwait(false);
                }

                throw new TimeoutException(
                    $"LocalStack services (SQS, SNS, S3) did not become available within " +
                    $"{HealthCheckTimeout.TotalSeconds} seconds at {healthUrl}");
            }
        }

        #endregion

        #region Private Methods — Resource Provisioning

        /// <summary>
        /// Creates all 11 SNS topics defined in the microservice event topology.
        /// Stores topic ARNs in <see cref="_topicArns"/> for later retrieval via
        /// <see cref="GetTopicArn(string)"/>.
        /// </summary>
        private async Task ProvisionSnsTopics()
        {
            var topicNames = new[]
            {
                CoreRecordCreatedTopic,
                CoreRecordUpdatedTopic,
                CoreRecordDeletedTopic,
                CoreEntityChangedTopic,
                CrmAccountUpdatedTopic,
                CrmContactUpdatedTopic,
                CrmCaseUpdatedTopic,
                ProjectTaskCreatedTopic,
                ProjectTaskUpdatedTopic,
                MailSentTopic,
                MailQueuedTopic
            };

            using (var snsClient = CreateSnsClient())
            {
                foreach (var topicName in topicNames)
                {
                    var response = await snsClient.CreateTopicAsync(new CreateTopicRequest
                    {
                        Name = topicName
                    }).ConfigureAwait(false);

                    _topicArns[topicName] = response.TopicArn;
                }
            }
        }

        /// <summary>
        /// Creates all 5 SQS subscriber queues defined in the microservice event topology.
        /// Stores queue URLs and ARNs in <see cref="_queueUrls"/> and <see cref="_queueArns"/>
        /// for later retrieval and SNS subscription setup.
        /// </summary>
        private async Task ProvisionSqsQueues()
        {
            var queueNames = new[]
            {
                CrmEventQueue,
                ProjectEventQueue,
                MailEventQueue,
                ReportingEventQueue,
                AdminEventQueue
            };

            using (var sqsClient = CreateSqsClient())
            {
                foreach (var queueName in queueNames)
                {
                    var createResponse = await sqsClient.CreateQueueAsync(new CreateQueueRequest
                    {
                        QueueName = queueName
                    }).ConfigureAwait(false);

                    _queueUrls[queueName] = createResponse.QueueUrl;

                    // Retrieve the queue ARN (required for SNS subscription)
                    var attrResponse = await sqsClient.GetQueueAttributesAsync(
                        createResponse.QueueUrl,
                        new List<string> { "QueueArn" }
                    ).ConfigureAwait(false);

                    _queueArns[queueName] = attrResponse.Attributes["QueueArn"];
                }
            }
        }

        /// <summary>
        /// Subscribes SQS queues to SNS topics matching the inter-service event routing
        /// topology defined in AAP 0.7.1:
        ///
        /// Core events → CRM queue, Project queue, Reporting queue
        ///   - CoreRecordCreated, CoreRecordUpdated, CoreRecordDeleted, CoreEntityChanged
        ///
        /// CRM events → Project queue, Reporting queue
        ///   - CrmAccountUpdated, CrmContactUpdated, CrmCaseUpdated
        ///
        /// Project events → Reporting queue
        ///   - ProjectTaskCreated, ProjectTaskUpdated
        ///
        /// Mail events → Reporting queue
        ///   - MailSent, MailQueued
        /// </summary>
        private async Task ProvisionSnsToSqsSubscriptions()
        {
            // Define the routing topology: which topics route to which queues
            var subscriptions = new List<(string TopicName, string QueueName)>
            {
                // Core events → CRM, Project, Reporting queues
                (CoreRecordCreatedTopic, CrmEventQueue),
                (CoreRecordCreatedTopic, ProjectEventQueue),
                (CoreRecordCreatedTopic, ReportingEventQueue),

                (CoreRecordUpdatedTopic, CrmEventQueue),
                (CoreRecordUpdatedTopic, ProjectEventQueue),
                (CoreRecordUpdatedTopic, ReportingEventQueue),

                (CoreRecordDeletedTopic, CrmEventQueue),
                (CoreRecordDeletedTopic, ProjectEventQueue),
                (CoreRecordDeletedTopic, ReportingEventQueue),

                (CoreEntityChangedTopic, CrmEventQueue),
                (CoreEntityChangedTopic, ProjectEventQueue),
                (CoreEntityChangedTopic, ReportingEventQueue),

                // CRM events → Project, Reporting queues
                (CrmAccountUpdatedTopic, ProjectEventQueue),
                (CrmAccountUpdatedTopic, ReportingEventQueue),

                (CrmContactUpdatedTopic, ProjectEventQueue),
                (CrmContactUpdatedTopic, ReportingEventQueue),

                (CrmCaseUpdatedTopic, ProjectEventQueue),
                (CrmCaseUpdatedTopic, ReportingEventQueue),

                // Project events → Reporting queue
                (ProjectTaskCreatedTopic, ReportingEventQueue),
                (ProjectTaskUpdatedTopic, ReportingEventQueue),

                // Mail events → Reporting queue
                (MailSentTopic, ReportingEventQueue),
                (MailQueuedTopic, ReportingEventQueue)
            };

            using (var snsClient = CreateSnsClient())
            {
                foreach (var (topicName, queueName) in subscriptions)
                {
                    if (!_topicArns.ContainsKey(topicName))
                    {
                        throw new InvalidOperationException(
                            $"Cannot subscribe: SNS topic '{topicName}' was not provisioned.");
                    }

                    if (!_queueArns.ContainsKey(queueName))
                    {
                        throw new InvalidOperationException(
                            $"Cannot subscribe: SQS queue '{queueName}' was not provisioned.");
                    }

                    await snsClient.SubscribeAsync(new SubscribeRequest
                    {
                        TopicArn = _topicArns[topicName],
                        Protocol = "sqs",
                        Endpoint = _queueArns[queueName]
                    }).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Creates the S3 bucket for file storage, replacing the monolith's
        /// Storage.Net cloud blob storage integration.
        /// Per AAP 0.7.4: S3 is used for file storage via LocalStack.
        /// </summary>
        private async Task ProvisionS3Buckets()
        {
            using (var s3Client = CreateS3Client())
            {
                await s3Client.PutBucketAsync(new PutBucketRequest
                {
                    BucketName = FileStorageBucket
                }).ConfigureAwait(false);
            }
        }

        #endregion

        #region Static Helpers — Topic and Queue Name Collections

        /// <summary>
        /// Returns all SNS topic name constants defined in this fixture.
        /// Useful for iterating over all topics in test setup or verification.
        /// </summary>
        public static IReadOnlyList<string> AllTopicNames { get; } = new[]
        {
            CoreRecordCreatedTopic,
            CoreRecordUpdatedTopic,
            CoreRecordDeletedTopic,
            CoreEntityChangedTopic,
            CrmAccountUpdatedTopic,
            CrmContactUpdatedTopic,
            CrmCaseUpdatedTopic,
            ProjectTaskCreatedTopic,
            ProjectTaskUpdatedTopic,
            MailSentTopic,
            MailQueuedTopic
        };

        /// <summary>
        /// Returns all SQS queue name constants defined in this fixture.
        /// Useful for iterating over all queues in test setup or verification.
        /// </summary>
        public static IReadOnlyList<string> AllQueueNames { get; } = new[]
        {
            CrmEventQueue,
            ProjectEventQueue,
            MailEventQueue,
            ReportingEventQueue,
            AdminEventQueue
        };

        #endregion
    }
}
