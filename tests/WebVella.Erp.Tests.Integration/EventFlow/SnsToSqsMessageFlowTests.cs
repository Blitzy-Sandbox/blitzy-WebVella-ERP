using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Tests.Integration.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace WebVella.Erp.Tests.Integration.EventFlow
{
    /// <summary>
    /// End-to-end integration tests validating that domain events published to
    /// SNS topics are correctly routed to SQS subscriber queues via LocalStack.
    ///
    /// These tests validate the replacement of the monolith's PostgreSQL
    /// LISTEN/NOTIFY pub/sub on ERP_NOTIFICATIONS_CHANNNEL (NotificationContext.cs)
    /// with cloud-native SNS/SQS messaging for inter-service communication.
    ///
    /// Test coverage:
    ///   - Single-event publish and receive (PublishDomainEvent_SnsToSqs)
    ///   - Fan-out pattern with multiple subscribers (FanOutPattern)
    ///   - Domain event contract JSON serialization fidelity (JsonSerialization)
    ///   - Message delivery latency within acceptable bounds (MessageLatency)
    ///   - Empty/minimal payload handling (EmptyPayload)
    ///   - SNS→SQS subscription topology verification (TopicToQueueMapping)
    ///
    /// Per AAP 0.8.2: "LocalStack validation: End-to-end tests must validate
    /// message flow through SNS/SQS"
    /// Per AAP 0.7.4: "Cross-service integration tests validate that domain events
    /// published by one service are received and processed by subscribers"
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class SnsToSqsMessageFlowTests : IAsyncLifetime
    {
        #region Private Fields

        /// <summary>
        /// Shared LocalStack fixture providing container management, AWS client
        /// factories, and provisioned SNS topic ARN / SQS queue URL resolution.
        /// Injected by xUnit collection fixture infrastructure.
        /// </summary>
        private readonly LocalStackFixture _localStackFixture;

        /// <summary>
        /// xUnit test output helper for diagnostic logging during test execution.
        /// </summary>
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// AWS SQS client configured to communicate with LocalStack.
        /// Created during <see cref="InitializeAsync"/> from the fixture's
        /// <see cref="LocalStackFixture.CreateSqsClient"/>.
        /// </summary>
        private AmazonSQSClient _sqsClient;

        /// <summary>
        /// AWS SNS client configured to communicate with LocalStack.
        /// Created during <see cref="InitializeAsync"/> from the fixture's
        /// <see cref="LocalStackFixture.CreateSnsClient"/>.
        /// </summary>
        private AmazonSimpleNotificationServiceClient _snsClient;

        /// <summary>Maximum number of retry attempts when polling SQS for messages.</summary>
        private const int MaxPollRetries = 5;

        /// <summary>Delay in milliseconds between SQS polling retry attempts.</summary>
        private const int PollRetryDelayMs = 2000;

        /// <summary>SQS long-polling wait time in seconds per receive request.</summary>
        private const int SqsWaitTimeSeconds = 10;

        /// <summary>Maximum number of messages to request per SQS receive call.</summary>
        private const int SqsMaxMessages = 10;

        /// <summary>
        /// Maximum acceptable message delivery latency for LocalStack.
        /// Set to 30 seconds — generous for container-based testing to account
        /// for container startup and network overhead.
        /// </summary>
        private static readonly TimeSpan MaxAcceptableLatency = TimeSpan.FromSeconds(30);

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="SnsToSqsMessageFlowTests"/> class.
        /// </summary>
        /// <param name="localStackFixture">
        /// Shared LocalStack fixture injected by xUnit's collection fixture mechanism.
        /// Provides AWS client factories and provisioned resource resolution.
        /// </param>
        /// <param name="output">
        /// xUnit test output helper for diagnostic logging.
        /// </param>
        public SnsToSqsMessageFlowTests(
            LocalStackFixture localStackFixture,
            ITestOutputHelper output)
        {
            _localStackFixture = localStackFixture
                ?? throw new ArgumentNullException(nameof(localStackFixture));
            _output = output
                ?? throw new ArgumentNullException(nameof(output));
        }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Creates SQS and SNS clients from the LocalStack fixture's factory methods.
        /// Called by xUnit before the first test in this class executes.
        /// </summary>
        public Task InitializeAsync()
        {
            _sqsClient = _localStackFixture.CreateSqsClient();
            _snsClient = _localStackFixture.CreateSnsClient();
            _output.WriteLine(
                $"SNS/SQS clients initialized. Endpoint: {_localStackFixture.Endpoint}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes the SQS and SNS clients.
        /// Called by xUnit after all tests in this class complete.
        /// </summary>
        public Task DisposeAsync()
        {
            _snsClient?.Dispose();
            _sqsClient?.Dispose();
            _output.WriteLine("SNS/SQS clients disposed.");
            return Task.CompletedTask;
        }

        #endregion

        #region Test Methods

        /// <summary>
        /// Validates that a domain event published to an SNS topic is received by
        /// an SQS subscriber queue. Tests the basic publish-subscribe pattern that
        /// replaces the monolith's PostgreSQL LISTEN/NOTIFY.
        ///
        /// Flow: Core service publishes RecordCreatedEvent → CoreRecordCreatedTopic
        ///       → CrmEventQueue receives the event.
        ///
        /// Source context:
        ///   NotificationContext.SendNotification serialised a Notification to JSON,
        ///   base64-encoded it, and sent via PostgreSQL NOTIFY. Listeners decoded and
        ///   dispatched in-process. This test proves SNS/SQS delivers equivalent
        ///   behaviour across process and network boundaries.
        ///
        /// Validates: AAP 0.1.1 replacement of LISTEN/NOTIFY with SNS/SQS.
        /// </summary>
        [Fact]
        public async Task PublishDomainEvent_SnsToSqs_MessageReceivedBySubscriber()
        {
            // Arrange — resolve provisioned resources
            var topicArn = _localStackFixture.GetTopicArn(
                LocalStackFixture.CoreRecordCreatedTopic);
            var queueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.CrmEventQueue);

            // Drain queue to ensure clean state
            await DrainQueue(queueUrl);

            // Create a test domain event matching the RecordCreatedEvent contract.
            // Mirrors the monolith's ErpRecordChangeNotification fields:
            //   EntityId, EntityName, RecordId
            var correlationId = Guid.NewGuid();
            var testEvent = new RecordCreatedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                EntityName = "account",
                Record = CreateTestEntityRecord("test-account")
            };

            // Serialize with Newtonsoft.Json per AAP 0.8.2 contract stability
            var messageJson = JsonConvert.SerializeObject(testEvent);
            _output.WriteLine(
                $"Publishing RecordCreatedEvent: CorrelationId={correlationId}");

            // Act — publish to SNS topic
            var publishResponse = await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = messageJson
            });

            publishResponse.MessageId.Should()
                .NotBeNullOrEmpty("SNS should return a MessageId on successful publish");
            _output.WriteLine($"Published SNS message: {publishResponse.MessageId}");

            // Assert — poll SQS queue and verify message received
            var messages = await PollSqsQueue(queueUrl);

            messages.Should().NotBeEmpty(
                "CRM event queue should receive the published domain event");

            // Parse SNS envelope and extract inner message
            var innerJson = ExtractSnsMessageBody(messages.First().Body);
            var receivedEvent =
                JsonConvert.DeserializeObject<RecordCreatedEvent>(innerJson);

            receivedEvent.Should().NotBeNull(
                "deserialized RecordCreatedEvent should not be null");
            receivedEvent.CorrelationId.Should().Be(correlationId,
                "CorrelationId should match published event");
            receivedEvent.EntityName.Should().Be("account",
                "EntityName should match published event");
            receivedEvent.Record.Should().NotBeNull(
                "Record should be present after deserialization");
        }

        /// <summary>
        /// Validates the fan-out pattern: a single domain event published to an SNS
        /// topic is delivered to ALL subscribed SQS queues simultaneously.
        ///
        /// Flow: CoreRecordCreatedTopic → CrmEventQueue
        ///                              → ProjectEventQueue
        ///                              → ReportingEventQueue
        ///
        /// In the monolith, NotificationContext.HandleNotification dispatched to all
        /// registered in-process listeners on matching channels. Fan-out was limited
        /// to listeners within the same process. SNS→SQS extends this to cross-service
        /// subscriber queues without any coupling between consumer services.
        ///
        /// Validates: AAP 0.7.4 "Core events → CRM queue, Project queue, Reporting queue"
        /// </summary>
        [Fact]
        public async Task FanOutPattern_MultipleSubscribers_AllReceiveMessage()
        {
            // Arrange — resolve provisioned resources
            var topicArn = _localStackFixture.GetTopicArn(
                LocalStackFixture.CoreRecordCreatedTopic);
            var crmQueueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.CrmEventQueue);
            var projectQueueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.ProjectEventQueue);
            var reportingQueueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.ReportingEventQueue);

            // Drain all three queues to ensure clean state
            await DrainQueue(crmQueueUrl);
            await DrainQueue(projectQueueUrl);
            await DrainQueue(reportingQueueUrl);

            // Create test event with unique CorrelationId for identification
            var correlationId = Guid.NewGuid();
            var testEvent = new RecordCreatedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                EntityName = "account",
                Record = CreateTestEntityRecord("fan-out-test")
            };

            var messageJson = JsonConvert.SerializeObject(testEvent);
            _output.WriteLine(
                $"Publishing fan-out event: CorrelationId={correlationId}");

            // Act — publish single event to SNS topic
            await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = messageJson
            });

            // Assert — all three subscriber queues should receive the event
            var crmMessages = await PollSqsQueue(crmQueueUrl);
            var projectMessages = await PollSqsQueue(projectQueueUrl);
            var reportingMessages = await PollSqsQueue(reportingQueueUrl);

            crmMessages.Should().NotBeEmpty(
                "CRM queue should receive the fan-out event");
            projectMessages.Should().NotBeEmpty(
                "Project queue should receive the fan-out event");
            reportingMessages.Should().NotBeEmpty(
                "Reporting queue should receive the fan-out event");

            // Verify each queue received the same event by checking CorrelationId
            VerifyReceivedEventCorrelation<RecordCreatedEvent>(
                crmMessages.First(), correlationId);
            VerifyReceivedEventCorrelation<RecordCreatedEvent>(
                projectMessages.First(), correlationId);
            VerifyReceivedEventCorrelation<RecordCreatedEvent>(
                reportingMessages.First(), correlationId);

            _output.WriteLine(
                "Fan-out verified: all 3 queues received the event");
        }

        /// <summary>
        /// Validates that all domain event contracts from SharedKernel/Contracts/Events/
        /// serialize and deserialize correctly through the SNS/SQS transport layer.
        ///
        /// Tests all 6 post-operation event types replacing the 6 post-hook interfaces:
        ///   RecordCreatedEvent    → IErpPostCreateRecordHook
        ///   RecordUpdatedEvent    → IErpPostUpdateRecordHook
        ///   RecordDeletedEvent    → IErpPostDeleteRecordHook
        ///   RelationCreatedEvent  → IErpPostCreateManyToManyRelationHook
        ///   RelationDeletedEvent  → IErpPostDeleteManyToManyRelationHook
        ///   RecordSearchEvent     → IErpPostSearchRecordHook
        ///
        /// Per AAP 0.8.2: "Maintain Newtonsoft.Json [JsonProperty] annotations for
        /// API contract stability"
        /// </summary>
        [Fact]
        public async Task JsonSerialization_DomainEventContracts_ValidFormat()
        {
            // Arrange — resolve resources
            var topicArn = _localStackFixture.GetTopicArn(
                LocalStackFixture.CoreRecordCreatedTopic);
            var queueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.CrmEventQueue);

            // --- Test RecordCreatedEvent (replaces IErpPostCreateRecordHook) ---
            await DrainQueue(queueUrl);
            var createdEvent = new RecordCreatedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                EntityName = "account",
                Record = CreateTestEntityRecord("created-test")
            };
            await PublishAndVerifyRoundTrip(topicArn, queueUrl, createdEvent,
                (original, receivedJson) =>
                {
                    var d = JsonConvert.DeserializeObject<RecordCreatedEvent>(
                        receivedJson);
                    d.Should().NotBeNull();
                    d.CorrelationId.Should().Be(original.CorrelationId);
                    d.EntityName.Should().Be(original.EntityName);
                    d.Timestamp.Should().BeCloseTo(
                        original.Timestamp, TimeSpan.FromSeconds(1));
                    d.Record.Should().NotBeNull();
                    _output.WriteLine("RecordCreatedEvent serialization verified");
                });

            // --- Test RecordUpdatedEvent (replaces IErpPostUpdateRecordHook) ---
            await DrainQueue(queueUrl);
            var updatedEvent = new RecordUpdatedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                EntityName = "contact",
                OldRecord = CreateTestEntityRecord("old-contact"),
                NewRecord = CreateTestEntityRecord("new-contact")
            };
            await PublishAndVerifyRoundTrip(topicArn, queueUrl, updatedEvent,
                (original, receivedJson) =>
                {
                    var d = JsonConvert.DeserializeObject<RecordUpdatedEvent>(
                        receivedJson);
                    d.Should().NotBeNull();
                    d.CorrelationId.Should().Be(original.CorrelationId);
                    d.EntityName.Should().Be(original.EntityName);
                    d.OldRecord.Should().NotBeNull(
                        "OldRecord should survive serialization");
                    d.NewRecord.Should().NotBeNull(
                        "NewRecord should survive serialization");
                    _output.WriteLine("RecordUpdatedEvent serialization verified");
                });

            // --- Test RecordDeletedEvent (replaces IErpPostDeleteRecordHook) ---
            await DrainQueue(queueUrl);
            var recordId = Guid.NewGuid();
            var deletedEvent = new RecordDeletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                EntityName = "case",
                RecordId = recordId
            };
            await PublishAndVerifyRoundTrip(topicArn, queueUrl, deletedEvent,
                (original, receivedJson) =>
                {
                    var d = JsonConvert.DeserializeObject<RecordDeletedEvent>(
                        receivedJson);
                    d.Should().NotBeNull();
                    d.CorrelationId.Should().Be(original.CorrelationId);
                    d.EntityName.Should().Be(original.EntityName);
                    d.RecordId.Should().Be(recordId,
                        "RecordId (Guid) should survive serialization");
                    _output.WriteLine(
                        "RecordDeletedEvent serialization verified");
                });

            // --- Test RelationCreatedEvent (IErpPostCreateManyToManyRelationHook) ---
            await DrainQueue(queueUrl);
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var relationCreatedEvent = new RelationCreatedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                EntityName = "account",
                RelationName = "user_account_created_by",
                OriginId = originId,
                TargetId = targetId
            };
            await PublishAndVerifyRoundTrip(topicArn, queueUrl,
                relationCreatedEvent, (original, receivedJson) =>
                {
                    var d = JsonConvert.DeserializeObject<RelationCreatedEvent>(
                        receivedJson);
                    d.Should().NotBeNull();
                    d.CorrelationId.Should().Be(original.CorrelationId);
                    d.RelationName.Should().Be(original.RelationName);
                    d.OriginId.Should().Be(originId,
                        "non-nullable OriginId should match");
                    d.TargetId.Should().Be(targetId,
                        "non-nullable TargetId should match");
                    _output.WriteLine(
                        "RelationCreatedEvent serialization verified");
                });

            // --- Test RelationDeletedEvent (nullable OriginId / TargetId) ---
            await DrainQueue(queueUrl);
            var relationDeletedEvent = new RelationDeletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                EntityName = "task",
                RelationName = "user_task_created_by",
                OriginId = Guid.NewGuid(),
                TargetId = null // Nullable — bulk delete scenario
            };
            await PublishAndVerifyRoundTrip(topicArn, queueUrl,
                relationDeletedEvent, (original, receivedJson) =>
                {
                    var d = JsonConvert.DeserializeObject<RelationDeletedEvent>(
                        receivedJson);
                    d.Should().NotBeNull();
                    d.CorrelationId.Should().Be(original.CorrelationId);
                    d.RelationName.Should().Be(original.RelationName);
                    d.OriginId.Should().Be(original.OriginId,
                        "nullable OriginId should match");
                    d.TargetId.Should().BeNull(
                        "nullable TargetId should remain null after roundtrip");
                    _output.WriteLine(
                        "RelationDeletedEvent serialization verified " +
                        "(nullable fields)");
                });

            // --- Test RecordSearchEvent (replaces IErpPostSearchRecordHook) ---
            await DrainQueue(queueUrl);
            var searchEvent = new RecordSearchEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                EntityName = "account",
                Results = new List<EntityRecord>
                {
                    CreateTestEntityRecord("search-result-1"),
                    CreateTestEntityRecord("search-result-2")
                }
            };
            await PublishAndVerifyRoundTrip(topicArn, queueUrl, searchEvent,
                (original, receivedJson) =>
                {
                    var d = JsonConvert.DeserializeObject<RecordSearchEvent>(
                        receivedJson);
                    d.Should().NotBeNull();
                    d.CorrelationId.Should().Be(original.CorrelationId);
                    d.EntityName.Should().Be(original.EntityName);
                    d.Results.Should().NotBeNull(
                        "Results list should survive serialization");
                    _output.WriteLine(
                        "RecordSearchEvent serialization verified");
                });

            // Verify JSON property naming follows [JsonProperty] annotations
            // (lowercase camelCase per Newtonsoft.Json attribute specification)
            var json = JsonConvert.SerializeObject(createdEvent);
            json.Should().Contain("\"timestamp\"",
                "property name should follow [JsonProperty] annotation");
            json.Should().Contain("\"correlationId\"",
                "property name should follow [JsonProperty] annotation");
            json.Should().Contain("\"entityName\"",
                "property name should follow [JsonProperty] annotation");
            json.Should().Contain("\"record\"",
                "property name should follow [JsonProperty] annotation");

            _output.WriteLine(
                "All 6 domain event contracts verified through SNS/SQS transport");
        }

        /// <summary>
        /// Validates that domain events are delivered within acceptable latency bounds
        /// when routed through LocalStack SNS→SQS.
        ///
        /// The acceptable latency threshold is 30 seconds, generous for LocalStack
        /// container-based testing to account for container startup and network overhead.
        ///
        /// Validates: "SQS subscribers receive messages within expected latency"
        /// </summary>
        [Fact]
        public async Task MessageLatency_EventDeliveredWithinAcceptableTime()
        {
            // Arrange
            var topicArn = _localStackFixture.GetTopicArn(
                LocalStackFixture.CoreRecordCreatedTopic);
            var queueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.CrmEventQueue);
            await DrainQueue(queueUrl);

            var testEvent = new RecordCreatedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                EntityName = "account",
                Record = CreateTestEntityRecord("latency-test")
            };
            var messageJson = JsonConvert.SerializeObject(testEvent);

            // Act — record timestamp, publish, receive, record timestamp
            var publishTime = DateTime.UtcNow;
            _output.WriteLine($"Publishing at: {publishTime:O}");

            await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = messageJson
            });

            // Use shorter wait time for latency measurement
            var messages = await PollSqsQueue(
                queueUrl, maxRetries: 3, waitTimeSeconds: 5);
            var receiveTime = DateTime.UtcNow;
            _output.WriteLine($"Received at: {receiveTime:O}");

            // Assert — delivery should be within acceptable latency
            messages.Should().NotBeEmpty(
                "message should be received for latency measurement");

            var latency = receiveTime - publishTime;
            _output.WriteLine(
                $"Measured latency: {latency.TotalMilliseconds:F0}ms");

            latency.Should().BeLessThan(MaxAcceptableLatency,
                $"SNS→SQS delivery latency ({latency.TotalSeconds:F1}s) " +
                $"should be within {MaxAcceptableLatency.TotalSeconds}s " +
                "for LocalStack");
        }

        /// <summary>
        /// Validates that publishing an empty or minimal payload to an SNS topic is
        /// handled gracefully — the message is delivered to SQS without errors and
        /// the subscriber can process the empty payload without exceptions.
        ///
        /// This tests defensive handling of edge cases in the event transport layer.
        /// </summary>
        [Fact]
        public async Task EmptyPayload_PublishEmptyMessage_HandledGracefully()
        {
            // Arrange
            var topicArn = _localStackFixture.GetTopicArn(
                LocalStackFixture.CoreRecordCreatedTopic);
            var queueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.CrmEventQueue);
            await DrainQueue(queueUrl);

            // Publish a minimal / empty JSON payload
            var minimalPayload = "{}";
            _output.WriteLine(
                "Publishing empty JSON payload to SNS topic");

            // Act — publish empty message
            var publishResponse = await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = minimalPayload
            });

            publishResponse.MessageId.Should().NotBeNullOrEmpty(
                "SNS should accept empty payload");

            // Assert — SQS should still receive the message
            var messages = await PollSqsQueue(queueUrl);

            messages.Should().NotBeEmpty(
                "SQS should receive the empty payload message");

            // Verify the message body can be parsed without exceptions
            var innerJson = ExtractSnsMessageBody(messages.First().Body);
            innerJson.Should().NotBeNull(
                "inner message should be extractable from SNS envelope");

            // Verify that deserializing to a domain event type handles the
            // empty payload gracefully — Newtonsoft.Json creates an instance
            // with default property values
            var deserialized =
                JsonConvert.DeserializeObject<RecordCreatedEvent>(innerJson);
            deserialized.Should().NotBeNull(
                "empty JSON should deserialize to a default event instance");

            _output.WriteLine(
                "Empty payload handled gracefully through SNS→SQS transport");
        }

        /// <summary>
        /// Validates that the SNS→SQS subscription topology matches the service
        /// event routing defined in docker-compose.localstack.yml and AAP 0.7.1:
        ///
        ///   Core events  (4 topics) → CRM, Project, Reporting queues  (12 subs)
        ///   CRM events   (3 topics) → Project, Reporting queues        (6 subs)
        ///   Project events (2 topics) → Reporting queue                 (2 subs)
        ///   Mail events  (2 topics) → Reporting queue                  (2 subs)
        ///   Total: 22 SNS-to-SQS subscriptions
        ///
        /// Per folder spec: "Tests topic-to-queue fan-out patterns matching
        /// docker-compose.localstack.yml topology"
        /// </summary>
        [Fact]
        public async Task TopicToQueueMapping_MatchesDockerComposeTopology()
        {
            // Resolve queue ARNs for subscription endpoint matching
            var crmQueueArn = await GetQueueArn(
                _localStackFixture.GetQueueUrl(LocalStackFixture.CrmEventQueue));
            var projectQueueArn = await GetQueueArn(
                _localStackFixture.GetQueueUrl(LocalStackFixture.ProjectEventQueue));
            var reportingQueueArn = await GetQueueArn(
                _localStackFixture.GetQueueUrl(LocalStackFixture.ReportingEventQueue));

            // Also verify MailEventQueue is provisioned and accessible
            var mailQueueUrl = _localStackFixture.GetQueueUrl(
                LocalStackFixture.MailEventQueue);
            var mailQueueArn = await GetQueueArn(mailQueueUrl);
            mailQueueArn.Should().NotBeNullOrEmpty(
                "Mail event queue should have a valid ARN");

            _output.WriteLine($"CRM Queue ARN:       {crmQueueArn}");
            _output.WriteLine($"Project Queue ARN:   {projectQueueArn}");
            _output.WriteLine($"Reporting Queue ARN: {reportingQueueArn}");
            _output.WriteLine($"Mail Queue ARN:      {mailQueueArn}");

            // ---------------------------------------------------------------
            // Verify Core events route to CRM, Project, Reporting queues
            // ---------------------------------------------------------------
            var coreTopics = new[]
            {
                LocalStackFixture.CoreRecordCreatedTopic,
                LocalStackFixture.CoreRecordUpdatedTopic,
                LocalStackFixture.CoreRecordDeletedTopic,
                LocalStackFixture.CoreEntityChangedTopic
            };

            foreach (var topicName in coreTopics)
            {
                var topicArn = _localStackFixture.GetTopicArn(topicName);
                var subs = await ListTopicSubscriptions(topicArn);
                var endpoints = subs.Select(s => s.Endpoint).ToList();

                endpoints.Should().Contain(crmQueueArn,
                    $"Core topic '{topicName}' should route to CRM queue");
                endpoints.Should().Contain(projectQueueArn,
                    $"Core topic '{topicName}' should route to Project queue");
                endpoints.Should().Contain(reportingQueueArn,
                    $"Core topic '{topicName}' should route to Reporting queue");

                subs.Count(s => s.Protocol == "sqs").Should()
                    .BeGreaterThanOrEqualTo(3,
                        $"Core topic '{topicName}' should have ≥3 SQS subs");

                _output.WriteLine(
                    $"Core topic '{topicName}': " +
                    $"{subs.Count} subscriptions verified");
            }

            // ---------------------------------------------------------------
            // Verify CRM events route to Project, Reporting queues
            // ---------------------------------------------------------------
            var crmTopics = new[]
            {
                LocalStackFixture.CrmAccountUpdatedTopic,
                LocalStackFixture.CrmContactUpdatedTopic,
                LocalStackFixture.CrmCaseUpdatedTopic
            };

            foreach (var topicName in crmTopics)
            {
                var topicArn = _localStackFixture.GetTopicArn(topicName);
                var subs = await ListTopicSubscriptions(topicArn);
                var endpoints = subs.Select(s => s.Endpoint).ToList();

                endpoints.Should().Contain(projectQueueArn,
                    $"CRM topic '{topicName}' should route to Project queue");
                endpoints.Should().Contain(reportingQueueArn,
                    $"CRM topic '{topicName}' should route to Reporting queue");

                subs.Count(s => s.Protocol == "sqs").Should()
                    .BeGreaterThanOrEqualTo(2,
                        $"CRM topic '{topicName}' should have ≥2 SQS subs");

                _output.WriteLine(
                    $"CRM topic '{topicName}': " +
                    $"{subs.Count} subscriptions verified");
            }

            // ---------------------------------------------------------------
            // Verify Project events route to Reporting queue
            // ---------------------------------------------------------------
            var projectTopics = new[]
            {
                LocalStackFixture.ProjectTaskCreatedTopic,
                LocalStackFixture.ProjectTaskUpdatedTopic
            };

            foreach (var topicName in projectTopics)
            {
                var topicArn = _localStackFixture.GetTopicArn(topicName);
                var subs = await ListTopicSubscriptions(topicArn);
                var endpoints = subs.Select(s => s.Endpoint).ToList();

                endpoints.Should().Contain(reportingQueueArn,
                    $"Project topic '{topicName}' should route to Reporting");

                _output.WriteLine(
                    $"Project topic '{topicName}': " +
                    $"{subs.Count} subscriptions verified");
            }

            // ---------------------------------------------------------------
            // Verify Mail events route to Reporting queue
            // ---------------------------------------------------------------
            var mailTopics = new[]
            {
                LocalStackFixture.MailSentTopic,
                LocalStackFixture.MailQueuedTopic
            };

            foreach (var topicName in mailTopics)
            {
                var topicArn = _localStackFixture.GetTopicArn(topicName);
                var subs = await ListTopicSubscriptions(topicArn);
                var endpoints = subs.Select(s => s.Endpoint).ToList();

                endpoints.Should().Contain(reportingQueueArn,
                    $"Mail topic '{topicName}' should route to Reporting");

                _output.WriteLine(
                    $"Mail topic '{topicName}': " +
                    $"{subs.Count} subscriptions verified");
            }

            _output.WriteLine(
                "Full SNS→SQS topology verified: " +
                "matches docker-compose.localstack.yml");
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Polls an SQS queue with long-polling and retry logic until messages are
        /// received or the maximum number of retries is exhausted.
        /// </summary>
        /// <param name="queueUrl">The SQS queue URL to poll.</param>
        /// <param name="maxRetries">Maximum polling attempts (default 5).</param>
        /// <param name="waitTimeSeconds">
        /// SQS long-poll wait time per attempt (default 10 seconds).
        /// </param>
        /// <returns>
        /// A list of received SQS <see cref="Message"/> objects. May be empty if no
        /// messages arrived within the retry budget.
        /// </returns>
        private async Task<List<Message>> PollSqsQueue(
            string queueUrl,
            int maxRetries = MaxPollRetries,
            int waitTimeSeconds = SqsWaitTimeSeconds)
        {
            var allMessages = new List<Message>();

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var response = await _sqsClient.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = queueUrl,
                            WaitTimeSeconds = waitTimeSeconds,
                            MaxNumberOfMessages = SqsMaxMessages
                        });

                    if (response.Messages != null && response.Messages.Any())
                    {
                        allMessages.AddRange(response.Messages);
                        _output.WriteLine(
                            $"Received {response.Messages.Count} message(s) " +
                            $"on attempt {attempt + 1}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine(
                        $"SQS polling attempt {attempt + 1} failed: " +
                        ex.Message);
                }

                if (attempt < maxRetries - 1)
                {
                    await Task.Delay(PollRetryDelayMs);
                }
            }

            return allMessages;
        }

        /// <summary>
        /// Drains all messages from an SQS queue to ensure a clean state before
        /// each test. Repeatedly receives and deletes messages until the queue
        /// is empty (zero messages returned with zero wait time).
        /// </summary>
        /// <param name="queueUrl">The SQS queue URL to drain.</param>
        private async Task DrainQueue(string queueUrl)
        {
            int totalDrained = 0;
            while (true)
            {
                var response = await _sqsClient.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        WaitTimeSeconds = 0,
                        MaxNumberOfMessages = SqsMaxMessages
                    });

                if (response.Messages == null || !response.Messages.Any())
                    break;

                foreach (var msg in response.Messages)
                {
                    await _sqsClient.DeleteMessageAsync(
                        queueUrl, msg.ReceiptHandle);
                    totalDrained++;
                }
            }

            if (totalDrained > 0)
            {
                _output.WriteLine(
                    $"Drained {totalDrained} message(s) from queue");
            }
        }

        /// <summary>
        /// Extracts the original message payload from an SNS notification envelope
        /// delivered to SQS. When SNS publishes to SQS, the SQS message body is a
        /// JSON envelope containing fields like Type, MessageId, TopicArn, and —
        /// critically — a <c>Message</c> field holding the original published payload.
        /// </summary>
        /// <param name="sqsMessageBody">
        /// The raw SQS message body (potentially an SNS envelope JSON).
        /// </param>
        /// <returns>
        /// The inner message string extracted from the SNS envelope, or the raw body
        /// if the envelope cannot be parsed.
        /// </returns>
        private string ExtractSnsMessageBody(string sqsMessageBody)
        {
            try
            {
                var envelope = JObject.Parse(sqsMessageBody);
                var innerMessage = envelope["Message"]?.ToString();
                return innerMessage ?? sqsMessageBody;
            }
            catch (JsonReaderException)
            {
                // If the body is not valid JSON or not an SNS envelope,
                // return the raw body as-is
                return sqsMessageBody;
            }
        }

        /// <summary>
        /// Creates a test <see cref="EntityRecord"/> populated with standard fields.
        /// Mirrors the monolith's dynamic record structure where field names are
        /// determined at runtime and accessed via the dictionary-based indexer.
        /// </summary>
        /// <param name="name">A descriptive name for the test record.</param>
        /// <returns>A populated EntityRecord instance for use in event payloads.</returns>
        private EntityRecord CreateTestEntityRecord(string name)
        {
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();
            record["name"] = name;
            record["created_on"] = DateTime.UtcNow;
            record["created_by"] = Guid.NewGuid();
            return record;
        }

        /// <summary>
        /// Verifies that a received SQS message contains a domain event with the
        /// expected <see cref="IDomainEvent.CorrelationId"/> after extracting it
        /// from the SNS envelope.
        /// </summary>
        /// <typeparam name="T">
        /// The domain event type implementing <see cref="IDomainEvent"/>.
        /// </typeparam>
        /// <param name="sqsMessage">The raw SQS <see cref="Message"/>.</param>
        /// <param name="expectedCorrelationId">
        /// The CorrelationId that should match after deserialization.
        /// </param>
        private void VerifyReceivedEventCorrelation<T>(
            Message sqsMessage, Guid expectedCorrelationId)
            where T : IDomainEvent
        {
            var innerJson = ExtractSnsMessageBody(sqsMessage.Body);
            var receivedEvent =
                JsonConvert.DeserializeObject<T>(innerJson);
            receivedEvent.Should().NotBeNull(
                $"event of type {typeof(T).Name} should deserialize");
            receivedEvent.CorrelationId.Should().Be(expectedCorrelationId,
                $"CorrelationId should match for {typeof(T).Name}");
        }

        /// <summary>
        /// Helper for JSON serialization round-trip tests. Publishes an event to SNS,
        /// receives from SQS, extracts the inner message from the SNS envelope, and
        /// invokes the caller-provided verification delegate for assertion.
        /// </summary>
        /// <typeparam name="T">The domain event type.</typeparam>
        /// <param name="topicArn">The SNS topic ARN to publish to.</param>
        /// <param name="queueUrl">The SQS queue URL to receive from.</param>
        /// <param name="originalEvent">The event to publish.</param>
        /// <param name="verifyAction">
        /// Delegate receiving the original event and the extracted JSON string
        /// for assertion.
        /// </param>
        private async Task PublishAndVerifyRoundTrip<T>(
            string topicArn,
            string queueUrl,
            T originalEvent,
            Action<T, string> verifyAction) where T : class
        {
            var messageJson = JsonConvert.SerializeObject(originalEvent);

            await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = messageJson
            });

            var messages = await PollSqsQueue(queueUrl);
            messages.Should().NotBeEmpty(
                $"SQS should receive {typeof(T).Name} event");

            var innerJson = ExtractSnsMessageBody(messages.First().Body);
            verifyAction(originalEvent, innerJson);
        }

        /// <summary>
        /// Lists all subscriptions for a given SNS topic ARN.
        /// Handles pagination for topics with many subscriptions.
        /// </summary>
        /// <param name="topicArn">The SNS topic ARN.</param>
        /// <returns>
        /// A complete list of <see cref="Subscription"/> objects for the topic.
        /// </returns>
        private async Task<List<Subscription>> ListTopicSubscriptions(
            string topicArn)
        {
            var allSubscriptions = new List<Subscription>();
            string nextToken = null;

            do
            {
                var response = await _snsClient
                    .ListSubscriptionsByTopicAsync(
                        new ListSubscriptionsByTopicRequest
                        {
                            TopicArn = topicArn,
                            NextToken = nextToken
                        });

                if (response.Subscriptions != null)
                {
                    allSubscriptions.AddRange(response.Subscriptions);
                }

                nextToken = response.NextToken;
            }
            while (!string.IsNullOrEmpty(nextToken));

            return allSubscriptions;
        }

        /// <summary>
        /// Retrieves the ARN for an SQS queue given its URL.
        /// Required for matching SNS subscription endpoints to queue identities.
        /// </summary>
        /// <param name="queueUrl">The SQS queue URL.</param>
        /// <returns>The ARN of the SQS queue.</returns>
        private async Task<string> GetQueueArn(string queueUrl)
        {
            var response = await _sqsClient.GetQueueAttributesAsync(
                queueUrl,
                new List<string> { "QueueArn" });
            return response.Attributes["QueueArn"];
        }

        #endregion
    }
}
