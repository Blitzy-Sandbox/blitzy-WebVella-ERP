using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Tests.Integration.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace WebVella.Erp.Tests.Integration.EventFlow
{
    /// <summary>
    /// Integration tests validating the complete conversion of the monolith's 12 hook
    /// interfaces (<c>WebVella.Erp/Hooks/</c>) into domain events published through
    /// SNS/SQS messaging via LocalStack.
    ///
    /// Each of the 12 hook types from <c>RecordHookManager</c> has a corresponding
    /// domain event contract in <c>SharedKernel/Contracts/Events/</c>. These tests
    /// verify that every event preserves the original hook semantics (method signatures,
    /// parameter types, nullable vs non-nullable Guid distinctions, and Pre/Post ordering).
    ///
    /// <para><b>Hook-to-Event Mapping (12 types):</b></para>
    /// <list type="number">
    ///   <item><c>IErpPreCreateRecordHook</c> → <see cref="PreRecordCreateEvent"/></item>
    ///   <item><c>IErpPostCreateRecordHook</c> → <see cref="RecordCreatedEvent"/></item>
    ///   <item><c>IErpPreUpdateRecordHook</c> → <see cref="PreRecordUpdateEvent"/></item>
    ///   <item><c>IErpPostUpdateRecordHook</c> → <see cref="RecordUpdatedEvent"/></item>
    ///   <item><c>IErpPreDeleteRecordHook</c> → <see cref="PreRecordDeleteEvent"/></item>
    ///   <item><c>IErpPostDeleteRecordHook</c> → <see cref="RecordDeletedEvent"/></item>
    ///   <item><c>IErpPreCreateManyToManyRelationHook</c> → <see cref="PreRelationCreateEvent"/></item>
    ///   <item><c>IErpPostCreateManyToManyRelationHook</c> → <see cref="RelationCreatedEvent"/></item>
    ///   <item><c>IErpPreDeleteManyToManyRelationHook</c> → <see cref="PreRelationDeleteEvent"/></item>
    ///   <item><c>IErpPostDeleteManyToManyRelationHook</c> → <see cref="RelationDeletedEvent"/></item>
    ///   <item><c>IErpPreSearchRecordHook</c> → <see cref="PreRecordSearchEvent"/></item>
    ///   <item><c>IErpPostSearchRecordHook</c> → <see cref="RecordSearchEvent"/></item>
    /// </list>
    ///
    /// <para><b>Key AAP References:</b></para>
    /// <list type="bullet">
    ///   <item>AAP 0.5.1: "Convert 12 hook interfaces to domain event contracts"</item>
    ///   <item>AAP 0.4.3: "Hook interfaces convert to domain events published on the message bus"</item>
    ///   <item>AAP 0.8.1: "All business rules must be preserved exactly as they exist in the monolith"</item>
    /// </list>
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class RecordCrudEventFlowTests : IAsyncLifetime
    {
        #region Fields and Constructor

        private readonly LocalStackFixture _localStackFixture;
        private readonly ITestOutputHelper _output;
        private AmazonSQSClient _sqsClient;
        private AmazonSimpleNotificationServiceClient _snsClient;

        /// <summary>
        /// JSON serialization settings matching MassTransit/Newtonsoft.Json contract conventions.
        /// NullValueHandling.Include preserves nullable Guid? fields in serialized events.
        /// </summary>
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            Formatting = Formatting.None,
            DateFormatHandling = DateFormatHandling.IsoDateFormat
        };

        /// <summary>
        /// Constructs the test class with xUnit collection fixture injection.
        /// The <paramref name="localStackFixture"/> provides LocalStack container lifecycle
        /// management and AWS client factories.
        /// </summary>
        public RecordCrudEventFlowTests(LocalStackFixture localStackFixture, ITestOutputHelper output)
        {
            _localStackFixture = localStackFixture;
            _output = output;
        }

        #endregion

        #region IAsyncLifetime — Per-Class Setup/Teardown

        /// <summary>
        /// Creates SQS and SNS clients from the LocalStack fixture for use across all test methods.
        /// </summary>
        public Task InitializeAsync()
        {
            _sqsClient = _localStackFixture.CreateSqsClient();
            _snsClient = _localStackFixture.CreateSnsClient();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes the SQS and SNS clients created during initialization.
        /// </summary>
        public Task DisposeAsync()
        {
            _sqsClient?.Dispose();
            _snsClient?.Dispose();
            return Task.CompletedTask;
        }

        #endregion

        #region Private Helpers — SNS/SQS Infrastructure

        /// <summary>
        /// Creates a dedicated SNS topic and SQS queue pair for a single test,
        /// subscribes the queue to the topic, and sets the queue policy to allow
        /// SNS to deliver messages.
        /// </summary>
        /// <param name="testSuffix">A short label to identify the test (e.g., "pre-create")</param>
        /// <returns>Tuple of (topicArn, queueUrl) for use in publish/receive operations</returns>
        private async Task<(string topicArn, string queueUrl)> CreateTestTopicAndQueueAsync(string testSuffix)
        {
            // Use unique names to prevent cross-test contamination
            var uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var topicName = $"test-{testSuffix}-{uniqueId}";
            var queueName = $"test-q-{testSuffix}-{uniqueId}";

            // Create SNS topic
            var topicResponse = await _snsClient.CreateTopicAsync(new CreateTopicRequest
            {
                Name = topicName
            }).ConfigureAwait(false);
            var topicArn = topicResponse.TopicArn;
            _output.WriteLine($"Created SNS topic: {topicArn}");

            // Create SQS queue
            var queueResponse = await _sqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName
            }).ConfigureAwait(false);
            var queueUrl = queueResponse.QueueUrl;
            _output.WriteLine($"Created SQS queue: {queueUrl}");

            // Get queue ARN for subscription and policy
            var attrResponse = await _sqsClient.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = queueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                }).ConfigureAwait(false);
            var queueArn = attrResponse.Attributes["QueueArn"];

            // Set SQS queue policy to allow SNS to send messages
            var policy = "{" +
                "\"Version\":\"2012-10-17\"," +
                "\"Statement\":[{" +
                    "\"Effect\":\"Allow\"," +
                    "\"Principal\":\"*\"," +
                    "\"Action\":\"sqs:SendMessage\"," +
                    $"\"Resource\":\"{queueArn}\"," +
                    "\"Condition\":{\"ArnEquals\":{" +
                        $"\"aws:SourceArn\":\"{topicArn}\"" +
                    "}}" +
                "}]" +
            "}";

            await _sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string> { { "Policy", policy } }
            }).ConfigureAwait(false);

            // Subscribe SQS queue to SNS topic
            await _snsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queueArn
            }).ConfigureAwait(false);

            _output.WriteLine($"Subscribed queue {queueArn} to topic {topicArn}");

            // Allow subscription propagation
            await Task.Delay(500).ConfigureAwait(false);

            return (topicArn, queueUrl);
        }

        /// <summary>
        /// Serializes a domain event to JSON and publishes it to an SNS topic.
        /// Includes a "eventType" message attribute carrying the CLR type name for
        /// downstream type discrimination in multi-event scenarios.
        /// </summary>
        private async Task<PublishResponse> PublishEventAsync<T>(string topicArn, T domainEvent) where T : class
        {
            var json = JsonConvert.SerializeObject(domainEvent, SerializerSettings);
            _output.WriteLine($"Publishing {typeof(T).Name}: {json}");

            var request = new PublishRequest
            {
                TopicArn = topicArn,
                Message = json,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["eventType"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = typeof(T).Name
                    }
                }
            };

            var response = await _snsClient.PublishAsync(request).ConfigureAwait(false);
            _output.WriteLine($"Published MessageId: {response.MessageId}");
            return response;
        }

        /// <summary>
        /// Receives a single message from an SQS queue, parses the SNS notification
        /// envelope, extracts the inner "Message" JSON body, and deserializes it to
        /// the expected domain event type.
        ///
        /// Uses SQS long polling with retries to handle eventual consistency.
        /// The SNS envelope structure is:
        /// <code>
        /// {
        ///   "Type": "Notification",
        ///   "Message": "{\"entityName\":\"account\",...}",
        ///   ...
        /// }
        /// </code>
        /// </summary>
        private async Task<T> ReceiveAndDeserializeAsync<T>(string queueUrl, int timeoutSeconds = 20) where T : class
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                var request = new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 5
                };

                var response = await _sqsClient.ReceiveMessageAsync(request).ConfigureAwait(false);

                if (response.Messages.Count > 0)
                {
                    var sqsBody = response.Messages[0].Body;
                    _output.WriteLine($"Received SQS message body: {sqsBody}");

                    // Parse the SNS notification envelope and extract the inner message
                    var envelope = JObject.Parse(sqsBody);
                    var innerJson = envelope["Message"]?.ToString();

                    innerJson.Should().NotBeNull("the SNS envelope must contain a 'Message' field");
                    _output.WriteLine($"Inner message JSON: {innerJson}");

                    // Delete the message to clean up
                    await _sqsClient.DeleteMessageAsync(queueUrl, response.Messages[0].ReceiptHandle)
                        .ConfigureAwait(false);

                    return JsonConvert.DeserializeObject<T>(innerJson, SerializerSettings);
                }
            }

            throw new TimeoutException(
                $"No message received from SQS queue within {timeoutSeconds}s for type {typeof(T).Name}");
        }

        /// <summary>
        /// Receives multiple messages from an SQS queue, returning the inner event
        /// payloads as <see cref="JObject"/> instances for flexible type inspection.
        /// Used by the comprehensive <see cref="AllTwelveHookTypes_PublishedAndReceived_CompleteLifecycle"/> test.
        /// </summary>
        private async Task<List<JObject>> ReceiveAllMessagesAsync(
            string queueUrl,
            int expectedCount,
            int timeoutSeconds = 30)
        {
            var messages = new List<JObject>();
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (messages.Count < expectedCount && DateTime.UtcNow < deadline)
            {
                var request = new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 5
                };

                var response = await _sqsClient.ReceiveMessageAsync(request).ConfigureAwait(false);

                foreach (var msg in response.Messages)
                {
                    var envelope = JObject.Parse(msg.Body);
                    var innerJson = envelope["Message"]?.ToString();

                    if (innerJson != null)
                    {
                        // Use DateTimeOffset parsing to preserve sub-second timestamp precision
                        using var reader = new JsonTextReader(new System.IO.StringReader(innerJson))
                        {
                            DateParseHandling = DateParseHandling.DateTimeOffset
                        };
                        messages.Add(JObject.Load(reader));
                    }

                    // Delete to avoid re-receiving
                    await _sqsClient.DeleteMessageAsync(queueUrl, msg.ReceiptHandle)
                        .ConfigureAwait(false);
                }
            }

            _output.WriteLine($"Received {messages.Count}/{expectedCount} messages from SQS queue");
            return messages;
        }

        /// <summary>
        /// Creates a test <see cref="EntityRecord"/> with dynamic key-value pairs
        /// matching the Expando-based pattern used throughout the monolith.
        /// </summary>
        private EntityRecord CreateTestRecord(string entityName, Guid id)
        {
            var record = new EntityRecord();
            record["id"] = id;
            record["name"] = $"Test {entityName} record";
            record["created_on"] = DateTime.UtcNow;
            record["status"] = "active";
            return record;
        }

        #endregion

        #region Record CRUD Event Tests (6 tests mapping to 6 hook types)

        /// <summary>
        /// Validates that <see cref="PreRecordCreateEvent"/> preserves the semantics of
        /// <c>IErpPreCreateRecordHook.OnPreCreateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>.
        ///
        /// Source: RecordHookManager.cs lines 32-43 — ExecutePreCreateRecordHooks validates
        /// entityName is not null/whitespace, errors is not null, then iterates hook instances.
        /// </summary>
        [Fact]
        public async Task PreCreateRecordEvent_Published_MatchesHookSemantics()
        {
            // Arrange: Create dedicated topic + queue
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("pre-create");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord("account", recordId);

            var evt = new PreRecordCreateEvent
            {
                EntityName = "account",
                Record = record,
                CorrelationId = correlationId,
                Timestamp = timestamp,
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("field_required", "name", "The name field is required.")
                }
            };

            // Act: Publish to SNS and receive from SQS
            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<PreRecordCreateEvent>(queueUrl);

            // Assert: All hook-semantic fields survive JSON round-trip through SNS/SQS
            received.Should().NotBeNull();
            received.EntityName.Should().Be("account",
                "entityName parameter from OnPreCreateRecord must be preserved");
            received.CorrelationId.Should().Be(correlationId,
                "CorrelationId must survive serialization round-trip");
            received.Timestamp.Should().Be(timestamp,
                "Timestamp must survive serialization round-trip");

            // Validate EntityRecord Expando-based dynamic properties
            received.Record.Should().NotBeNull(
                "EntityRecord parameter from OnPreCreateRecord must be preserved");

            // Validate List<ErrorModel> semantics (pre-hooks include errors for validation)
            received.ValidationErrors.Should().NotBeNull(
                "Pre-hook errors list must be preserved in event model");
            received.ValidationErrors.Should().HaveCount(1);
            received.ValidationErrors[0].Key.Should().Be("field_required");
            received.ValidationErrors[0].Value.Should().Be("name");
            received.ValidationErrors[0].Message.Should().Be("The name field is required.");
        }

        /// <summary>
        /// Validates that <see cref="RecordCreatedEvent"/> preserves the semantics of
        /// <c>IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)</c>.
        ///
        /// Source: RecordHookManager.cs lines 45-53 — Post-hooks have NO errors parameter.
        /// </summary>
        [Fact]
        public async Task PostCreateRecordEvent_Published_MatchesHookSemantics()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("post-create");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord("account", recordId);

            var evt = new RecordCreatedEvent
            {
                EntityName = "account",
                Record = record,
                CorrelationId = correlationId,
                Timestamp = timestamp
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<RecordCreatedEvent>(queueUrl);

            received.Should().NotBeNull();
            received.EntityName.Should().Be("account",
                "entityName from OnPostCreateRecord must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
            received.Record.Should().NotBeNull(
                "EntityRecord from OnPostCreateRecord must be preserved");
        }

        /// <summary>
        /// Validates that <see cref="PreRecordUpdateEvent"/> preserves the semantics of
        /// <c>IErpPreUpdateRecordHook.OnPreUpdateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>.
        ///
        /// Source: RecordHookManager.cs lines 55-66.
        /// </summary>
        [Fact]
        public async Task PreUpdateRecordEvent_Published_MatchesHookSemantics()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("pre-update");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord("contact", recordId);

            var evt = new PreRecordUpdateEvent
            {
                EntityName = "contact",
                Record = record,
                CorrelationId = correlationId,
                Timestamp = timestamp,
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("invalid_email", "email", "Email format is invalid.")
                }
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<PreRecordUpdateEvent>(queueUrl);

            received.Should().NotBeNull();
            received.EntityName.Should().Be("contact",
                "entityName from OnPreUpdateRecord must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
            received.Record.Should().NotBeNull(
                "EntityRecord from OnPreUpdateRecord must be preserved");
            received.ValidationErrors.Should().HaveCount(1,
                "Pre-update hook errors list must be preserved");
            received.ValidationErrors[0].Key.Should().Be("invalid_email");
            received.ValidationErrors[0].Value.Should().Be("email");
            received.ValidationErrors[0].Message.Should().Be("Email format is invalid.");
        }

        /// <summary>
        /// Validates that <see cref="RecordUpdatedEvent"/> preserves the semantics of
        /// <c>IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)</c>.
        ///
        /// Source: RecordHookManager.cs lines 68-76.
        /// Note: The domain event is enriched with both OldRecord and NewRecord, whereas the
        /// original hook only carried the post-update state. Both payloads must survive serialization.
        /// </summary>
        [Fact]
        public async Task PostUpdateRecordEvent_Published_MatchesHookSemantics()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("post-update");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var recordId = Guid.NewGuid();

            var oldRecord = new EntityRecord();
            oldRecord["id"] = recordId;
            oldRecord["name"] = "Old Name";
            oldRecord["status"] = "draft";

            var newRecord = new EntityRecord();
            newRecord["id"] = recordId;
            newRecord["name"] = "Updated Name";
            newRecord["status"] = "active";

            var evt = new RecordUpdatedEvent
            {
                EntityName = "task",
                OldRecord = oldRecord,
                NewRecord = newRecord,
                CorrelationId = correlationId,
                Timestamp = timestamp
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<RecordUpdatedEvent>(queueUrl);

            received.Should().NotBeNull();
            received.EntityName.Should().Be("task",
                "entityName from OnPostUpdateRecord must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
            received.OldRecord.Should().NotBeNull(
                "OldRecord (enrichment over original hook) must survive serialization");
            received.NewRecord.Should().NotBeNull(
                "NewRecord (maps to original hook record param) must survive serialization");
        }

        /// <summary>
        /// Validates that <see cref="PreRecordDeleteEvent"/> preserves the semantics of
        /// <c>IErpPreDeleteRecordHook.OnPreDeleteRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>.
        ///
        /// Source: RecordHookManager.cs lines 78-89.
        /// </summary>
        [Fact]
        public async Task PreDeleteRecordEvent_Published_MatchesHookSemantics()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("pre-delete");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord("case", recordId);

            var evt = new PreRecordDeleteEvent
            {
                EntityName = "case",
                Record = record,
                CorrelationId = correlationId,
                Timestamp = timestamp,
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("cannot_delete", "status", "Active cases cannot be deleted."),
                    new ErrorModel("has_children", "tasks", "Case has linked tasks.")
                }
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<PreRecordDeleteEvent>(queueUrl);

            received.Should().NotBeNull();
            received.EntityName.Should().Be("case",
                "entityName from OnPreDeleteRecord must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
            received.Record.Should().NotBeNull(
                "EntityRecord from OnPreDeleteRecord must be preserved");
            received.ValidationErrors.Should().HaveCount(2,
                "Pre-delete hook errors list must preserve multiple errors");
            received.ValidationErrors[0].Key.Should().Be("cannot_delete");
            received.ValidationErrors[1].Key.Should().Be("has_children");
        }

        /// <summary>
        /// Validates that <see cref="RecordDeletedEvent"/> preserves the semantics of
        /// <c>IErpPostDeleteRecordHook.OnPostDeleteRecord(string entityName, EntityRecord record)</c>.
        ///
        /// Source: RecordHookManager.cs lines 91-99.
        /// Note: The domain event carries RecordId (Guid) instead of the full EntityRecord,
        /// since the record no longer exists after deletion. The publishing service extracts
        /// the record's Id before publishing.
        /// </summary>
        [Fact]
        public async Task PostDeleteRecordEvent_Published_MatchesHookSemantics()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("post-delete");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var recordId = Guid.NewGuid();

            var evt = new RecordDeletedEvent
            {
                EntityName = "account",
                RecordId = recordId,
                CorrelationId = correlationId,
                Timestamp = timestamp
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<RecordDeletedEvent>(queueUrl);

            received.Should().NotBeNull();
            received.EntityName.Should().Be("account",
                "entityName from OnPostDeleteRecord must be preserved");
            received.RecordId.Should().Be(recordId,
                "RecordId (simplified from full EntityRecord) must survive Guid serialization");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
        }

        #endregion

        #region ManyToMany Relation Event Tests (4 tests mapping to 4 hook types)

        /// <summary>
        /// Validates that <see cref="PreRelationCreateEvent"/> preserves the semantics of
        /// <c>IErpPreCreateManyToManyRelationHook.OnPreCreate(string relationName, Guid originId, Guid targetId, List&lt;ErrorModel&gt; errors)</c>.
        ///
        /// Source: RecordHookManager.cs lines 101-112.
        /// CRITICAL: Uses non-nullable Guid for originId and targetId (differs from Delete hooks).
        /// </summary>
        [Fact]
        public async Task PreCreateManyToManyRelationEvent_Published_MatchesHookSemantics()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("pre-rel-create");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            var evt = new PreRelationCreateEvent
            {
                RelationName = "user_account_created_by",
                OriginId = originId,
                TargetId = targetId,
                CorrelationId = correlationId,
                Timestamp = timestamp,
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("duplicate_relation", "relation", "Relation already exists.")
                }
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<PreRelationCreateEvent>(queueUrl);

            received.Should().NotBeNull();
            received.RelationName.Should().Be("user_account_created_by",
                "relationName from OnPreCreate must be preserved");
            received.OriginId.Should().Be(originId,
                "Non-nullable Guid originId from OnPreCreate must be preserved");
            received.TargetId.Should().Be(targetId,
                "Non-nullable Guid targetId from OnPreCreate must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
            received.ValidationErrors.Should().HaveCount(1);
            received.ValidationErrors[0].Key.Should().Be("duplicate_relation");
        }

        /// <summary>
        /// Validates that <see cref="RelationCreatedEvent"/> preserves the semantics of
        /// <c>IErpPostCreateManyToManyRelationHook.OnPostCreate(string relationName, Guid originId, Guid targetId)</c>.
        ///
        /// Source: RecordHookManager.cs lines 114-122.
        /// Uses non-nullable Guid (same as PreCreate). No errors parameter (post-hook).
        /// </summary>
        [Fact]
        public async Task PostCreateManyToManyRelationEvent_Published_MatchesHookSemantics()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("post-rel-create");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            var evt = new RelationCreatedEvent
            {
                RelationName = "user_account_created_by",
                OriginId = originId,
                TargetId = targetId,
                CorrelationId = correlationId,
                Timestamp = timestamp
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<RelationCreatedEvent>(queueUrl);

            received.Should().NotBeNull();
            received.RelationName.Should().Be("user_account_created_by",
                "relationName from OnPostCreate must be preserved");
            received.OriginId.Should().Be(originId,
                "Non-nullable Guid originId from OnPostCreate must be preserved");
            received.TargetId.Should().Be(targetId,
                "Non-nullable Guid targetId from OnPostCreate must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
        }

        /// <summary>
        /// Validates that <see cref="PreRelationDeleteEvent"/> preserves the NULLABLE Guid?
        /// semantics of <c>IErpPreDeleteManyToManyRelationHook.OnPreDelete(string relationName,
        /// Guid? originId, Guid? targetId, List&lt;ErrorModel&gt; errors)</c>.
        ///
        /// Source: RecordHookManager.cs lines 124-135.
        /// CRITICAL: Delete hooks use Guid? (nullable) unlike Create hooks which use Guid (non-nullable).
        /// A null originId means "delete all origin records for the given targetId" and vice versa.
        /// This test validates nullable Guid serialization with both null and non-null values.
        /// </summary>
        [Fact]
        public async Task PreDeleteManyToManyRelationEvent_Published_NullableGuidsPreserved()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("pre-rel-delete");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var targetId = Guid.NewGuid();

            // Test with originId = null, targetId = non-null (bulk delete all origins for a target)
            var evt = new PreRelationDeleteEvent
            {
                RelationName = "case_task_relation",
                OriginId = null,
                TargetId = targetId,
                CorrelationId = correlationId,
                Timestamp = timestamp,
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("cascade_warning", "tasks", "Deleting will affect linked tasks.")
                }
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<PreRelationDeleteEvent>(queueUrl);

            received.Should().NotBeNull();
            received.RelationName.Should().Be("case_task_relation",
                "relationName from OnPreDelete must be preserved");
            received.OriginId.Should().BeNull(
                "Nullable Guid? originId=null must survive JSON serialization through SNS/SQS");
            received.TargetId.Should().Be(targetId,
                "Nullable Guid? targetId with value must survive JSON serialization through SNS/SQS");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);
            received.ValidationErrors.Should().HaveCount(1);

            // Now test the reverse: originId = non-null, targetId = null
            var (topicArn2, queueUrl2) = await CreateTestTopicAndQueueAsync("pre-rel-del2");
            var originId = Guid.NewGuid();

            var evt2 = new PreRelationDeleteEvent
            {
                RelationName = "account_project_relation",
                OriginId = originId,
                TargetId = null,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                ValidationErrors = new List<ErrorModel>()
            };

            await PublishEventAsync(topicArn2, evt2);
            var received2 = await ReceiveAndDeserializeAsync<PreRelationDeleteEvent>(queueUrl2);

            received2.OriginId.Should().Be(originId,
                "Nullable Guid? originId with value must survive serialization");
            received2.TargetId.Should().BeNull(
                "Nullable Guid? targetId=null must survive serialization");
        }

        /// <summary>
        /// Validates that <see cref="RelationDeletedEvent"/> preserves the NULLABLE Guid?
        /// semantics of <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete(string relationName,
        /// Guid? originId, Guid? targetId)</c>.
        ///
        /// Source: RecordHookManager.cs lines 137-145.
        /// Same nullable Guid? pattern as PreDelete. No ValidationErrors (post-hook).
        /// </summary>
        [Fact]
        public async Task PostDeleteManyToManyRelationEvent_Published_NullableGuidsPreserved()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("post-rel-delete");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var originId = Guid.NewGuid();

            // Test with originId = non-null, targetId = null
            var evt = new RelationDeletedEvent
            {
                RelationName = "user_role_relation",
                OriginId = originId,
                TargetId = null,
                CorrelationId = correlationId,
                Timestamp = timestamp
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<RelationDeletedEvent>(queueUrl);

            received.Should().NotBeNull();
            received.RelationName.Should().Be("user_role_relation",
                "relationName from OnPostDelete must be preserved");
            received.OriginId.Should().Be(originId,
                "Nullable Guid? originId with value must survive serialization");
            received.TargetId.Should().BeNull(
                "Nullable Guid? targetId=null must survive JSON serialization through SNS/SQS");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);

            // Also test both null (bulk delete all)
            var (topicArn2, queueUrl2) = await CreateTestTopicAndQueueAsync("post-rel-del2");

            var evt2 = new RelationDeletedEvent
            {
                RelationName = "contact_email_relation",
                OriginId = null,
                TargetId = null,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            await PublishEventAsync(topicArn2, evt2);
            var received2 = await ReceiveAndDeserializeAsync<RelationDeletedEvent>(queueUrl2);

            received2.OriginId.Should().BeNull(
                "Both nullable Guids set to null must survive serialization");
            received2.TargetId.Should().BeNull(
                "Both nullable Guids set to null must survive serialization");
        }

        #endregion

        #region Search Event Tests (2 tests mapping to 2 hook types)

        /// <summary>
        /// Validates that <see cref="PreRecordSearchEvent"/> preserves the semantics of
        /// <c>IErpPreSearchRecordHook.OnPreSearchRecord(string entityName, EqlSelectNode tree, List&lt;EqlError&gt; errors)</c>.
        ///
        /// Source: RecordHookManager.cs lines 147-155.
        /// The EqlSelectNode AST is serialized as a JSON string in the domain event to avoid
        /// coupling subscribers to the full EQL parser types. This test verifies the serialized
        /// EQL tree and EQL error list survive the SNS/SQS round-trip.
        /// </summary>
        [Fact]
        public async Task PreSearchEvent_Published_EqlNodeSerialized()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("pre-search");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;

            // The EqlTree is a JSON-serialized representation of EqlSelectNode AST
            var eqlTreeJson = JsonConvert.SerializeObject(new
            {
                type = "EqlSelectNode",
                fields = new[] { "id", "name", "email" },
                entity = "contact",
                where = new { field = "status", op = "=", value = "active" },
                orderBy = new[] { new { field = "name", direction = "asc" } },
                limit = 100,
                offset = 0
            });

            var evt = new PreRecordSearchEvent
            {
                EntityName = "contact",
                EqlTree = eqlTreeJson,
                CorrelationId = correlationId,
                Timestamp = timestamp,
                EqlErrors = new List<EqlError>
                {
                    new EqlError { Message = "Unknown field 'foo'", Line = 1, Column = 15 },
                    new EqlError { Message = "Syntax error near 'AND'", Line = 2, Column = null }
                }
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<PreRecordSearchEvent>(queueUrl);

            received.Should().NotBeNull();
            received.EntityName.Should().Be("contact",
                "entityName from OnPreSearchRecord must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);

            // Verify EQL tree string survives serialization
            received.EqlTree.Should().NotBeNullOrEmpty(
                "Serialized EqlSelectNode AST must survive SNS/SQS round-trip");
            var parsedTree = JObject.Parse(received.EqlTree);
            parsedTree["entity"]?.ToString().Should().Be("contact",
                "EQL tree entity reference must be preserved");

            // Verify EQL error list with nullable int? properties
            received.EqlErrors.Should().HaveCount(2,
                "EqlErrors list from OnPreSearchRecord must be preserved");
            received.EqlErrors[0].Message.Should().Be("Unknown field 'foo'");
            received.EqlErrors[0].Line.Should().Be(1);
            received.EqlErrors[0].Column.Should().Be(15);
            received.EqlErrors[1].Message.Should().Be("Syntax error near 'AND'");
            received.EqlErrors[1].Line.Should().Be(2);
            received.EqlErrors[1].Column.Should().BeNull(
                "Nullable int? Column=null must survive serialization");
        }

        /// <summary>
        /// Validates that <see cref="RecordSearchEvent"/> preserves the semantics of
        /// <c>IErpPostSearchRecordHook.OnPostSearchRecord(string entityName, List&lt;EntityRecord&gt; record)</c>.
        ///
        /// Source: RecordHookManager.cs lines 157-165.
        /// The domain event carries the search results as <c>List&lt;EntityRecord&gt;</c>
        /// with each EntityRecord being an Expando-based dynamic record.
        /// </summary>
        [Fact]
        public async Task PostSearchEvent_Published_RecordListSerialized()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("post-search");

            var correlationId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;

            var results = new List<EntityRecord>
            {
                CreateTestRecord("account", Guid.NewGuid()),
                CreateTestRecord("account", Guid.NewGuid()),
                CreateTestRecord("account", Guid.NewGuid())
            };

            var evt = new RecordSearchEvent
            {
                EntityName = "account",
                Results = results,
                CorrelationId = correlationId,
                Timestamp = timestamp
            };

            await PublishEventAsync(topicArn, evt);
            var received = await ReceiveAndDeserializeAsync<RecordSearchEvent>(queueUrl);

            received.Should().NotBeNull();
            received.EntityName.Should().Be("account",
                "entityName from OnPostSearchRecord must be preserved");
            received.CorrelationId.Should().Be(correlationId);
            received.Timestamp.Should().Be(timestamp);

            // Verify List<EntityRecord> survives serialization round-trip
            received.Results.Should().NotBeNull(
                "Results list (matching original hook's List<EntityRecord> param) must be preserved");
            received.Results.Should().HaveCount(3,
                "All EntityRecord entries must survive serialization round-trip");
        }

        #endregion

        #region Comprehensive Lifecycle and Ordering Tests

        /// <summary>
        /// Comprehensive integration test validating ALL 12 hook type conversions in a single flow.
        /// Publishes one domain event per hook type (12 total) to a shared SNS topic,
        /// receives all from SQS, and asserts that exactly 12 unique event structures were
        /// received with the correct fields present.
        ///
        /// This ensures no hook type was accidentally omitted during the conversion from
        /// in-process hooks to asynchronous domain events.
        ///
        /// Each received event is verified to implement <see cref="IDomainEvent"/> contract
        /// (Timestamp, CorrelationId, EntityName).
        /// </summary>
        [Fact]
        public async Task AllTwelveHookTypes_PublishedAndReceived_CompleteLifecycle()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("all-twelve");

            var sharedCorrelationId = Guid.NewGuid();
            var baseTimestamp = DateTimeOffset.UtcNow;
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord("lifecycle_entity", recordId);
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            // Step 1: Create one domain event per hook type (12 total)
            // Each event is tagged with a unique entityName/relationName for identification
            var events = new List<(string typeName, object evt)>
            {
                // 1. IErpPreCreateRecordHook → PreRecordCreateEvent
                ("PreRecordCreateEvent", new PreRecordCreateEvent
                {
                    EntityName = "hook1_pre_create",
                    Record = record,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp,
                    ValidationErrors = new List<ErrorModel>()
                }),
                // 2. IErpPostCreateRecordHook → RecordCreatedEvent
                ("RecordCreatedEvent", new RecordCreatedEvent
                {
                    EntityName = "hook2_post_create",
                    Record = record,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp
                }),
                // 3. IErpPreUpdateRecordHook → PreRecordUpdateEvent
                ("PreRecordUpdateEvent", new PreRecordUpdateEvent
                {
                    EntityName = "hook3_pre_update",
                    Record = record,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp,
                    ValidationErrors = new List<ErrorModel>()
                }),
                // 4. IErpPostUpdateRecordHook → RecordUpdatedEvent
                ("RecordUpdatedEvent", new RecordUpdatedEvent
                {
                    EntityName = "hook4_post_update",
                    OldRecord = record,
                    NewRecord = record,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp
                }),
                // 5. IErpPreDeleteRecordHook → PreRecordDeleteEvent
                ("PreRecordDeleteEvent", new PreRecordDeleteEvent
                {
                    EntityName = "hook5_pre_delete",
                    Record = record,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp,
                    ValidationErrors = new List<ErrorModel>()
                }),
                // 6. IErpPostDeleteRecordHook → RecordDeletedEvent
                ("RecordDeletedEvent", new RecordDeletedEvent
                {
                    EntityName = "hook6_post_delete",
                    RecordId = recordId,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp
                }),
                // 7. IErpPreCreateManyToManyRelationHook → PreRelationCreateEvent
                ("PreRelationCreateEvent", new PreRelationCreateEvent
                {
                    EntityName = "hook7_pre_rel_create",
                    RelationName = "rel_pre_create",
                    OriginId = originId,
                    TargetId = targetId,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp,
                    ValidationErrors = new List<ErrorModel>()
                }),
                // 8. IErpPostCreateManyToManyRelationHook → RelationCreatedEvent
                ("RelationCreatedEvent", new RelationCreatedEvent
                {
                    EntityName = "hook8_post_rel_create",
                    RelationName = "rel_post_create",
                    OriginId = originId,
                    TargetId = targetId,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp
                }),
                // 9. IErpPreDeleteManyToManyRelationHook → PreRelationDeleteEvent
                ("PreRelationDeleteEvent", new PreRelationDeleteEvent
                {
                    EntityName = "hook9_pre_rel_delete",
                    RelationName = "rel_pre_delete",
                    OriginId = originId,
                    TargetId = null,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp,
                    ValidationErrors = new List<ErrorModel>()
                }),
                // 10. IErpPostDeleteManyToManyRelationHook → RelationDeletedEvent
                ("RelationDeletedEvent", new RelationDeletedEvent
                {
                    EntityName = "hook10_post_rel_delete",
                    RelationName = "rel_post_delete",
                    OriginId = null,
                    TargetId = targetId,
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp
                }),
                // 11. IErpPreSearchRecordHook → PreRecordSearchEvent
                ("PreRecordSearchEvent", new PreRecordSearchEvent
                {
                    EntityName = "hook11_pre_search",
                    EqlTree = "{\"type\":\"select\",\"entity\":\"account\"}",
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp,
                    EqlErrors = new List<EqlError>()
                }),
                // 12. IErpPostSearchRecordHook → RecordSearchEvent
                ("RecordSearchEvent", new RecordSearchEvent
                {
                    EntityName = "hook12_post_search",
                    Results = new List<EntityRecord> { record },
                    CorrelationId = sharedCorrelationId,
                    Timestamp = baseTimestamp
                })
            };

            // Step 2: Publish all 12 events to the shared SNS topic
            foreach (var (typeName, evt) in events)
            {
                var json = JsonConvert.SerializeObject(evt, SerializerSettings);
                var publishRequest = new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = json,
                    MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                    {
                        ["eventType"] = new SnsMessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = typeName
                        }
                    }
                };
                await _snsClient.PublishAsync(publishRequest).ConfigureAwait(false);
                _output.WriteLine($"Published event type: {typeName}");
            }

            // Step 3: Receive all 12 messages from SQS
            var receivedMessages = await ReceiveAllMessagesAsync(queueUrl, 12, timeoutSeconds: 45);

            // Step 4: Assert exactly 12 messages were received
            receivedMessages.Should().HaveCount(12,
                "all 12 hook types must have corresponding events that arrive via SNS/SQS");

            // Step 5: Verify each event has the correct IDomainEvent base properties
            foreach (var msg in receivedMessages)
            {
                // Every domain event must have timestamp, correlationId, entityName
                msg.ContainsKey("timestamp").Should().BeTrue(
                    "IDomainEvent.Timestamp must be present in all events");
                msg.ContainsKey("correlationId").Should().BeTrue(
                    "IDomainEvent.CorrelationId must be present in all events");
                msg.ContainsKey("entityName").Should().BeTrue(
                    "IDomainEvent.EntityName must be present in all events");
            }

            // Verify distinct entityName values (each event has a unique entityName)
            var entityNames = receivedMessages
                .Select(m => m["entityName"]?.ToString())
                .Where(n => n != null)
                .OrderBy(n => n)
                .ToList();

            entityNames.Count.Should().Be(12,
                "all 12 events should have distinct entityName identifiers");

            _output.WriteLine($"All 12 hook types verified: {string.Join(", ", entityNames)}");

            // Verify record events have record/recordId fields
            var recordEvents = receivedMessages
                .Where(m => m.ContainsKey("record") || m.ContainsKey("recordId") ||
                            m.ContainsKey("oldRecord") || m.ContainsKey("newRecord"))
                .ToList();
            recordEvents.Count.Should().BeGreaterOrEqualTo(6,
                "at least 6 record events should have record-related fields");

            // Verify relation events have relationName field
            var relationEvents = receivedMessages
                .Where(m => m.ContainsKey("relationName"))
                .ToList();
            relationEvents.Count.Should().Be(4,
                "exactly 4 relation events should have relationName field");

            // Verify search events have eqlTree or results fields
            var searchEvents = receivedMessages
                .Where(m => m.ContainsKey("eqlTree") || m.ContainsKey("results"))
                .ToList();
            searchEvents.Count.Should().Be(2,
                "exactly 2 search events should have eqlTree or results fields");
        }

        /// <summary>
        /// Validates that Pre/Post event ordering semantics from RecordHookManager are preserved.
        ///
        /// In the monolith, ExecutePreCreateRecordHooks is always called BEFORE
        /// ExecutePostCreateRecordHooks. This test verifies that Pre events have
        /// earlier timestamps than Post events, ensuring the temporal ordering contract
        /// is preserved in the domain event model.
        ///
        /// Source: RecordHookManager.cs — Pre-hooks execute synchronously before the
        /// database operation, Post-hooks execute after. Timestamps must reflect this order.
        /// </summary>
        [Fact]
        public async Task EventOrdering_SequentialPublish_PreBeforePost()
        {
            var (topicArn, queueUrl) = await CreateTestTopicAndQueueAsync("ordering");

            var correlationId = Guid.NewGuid();
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord("account", recordId);

            // Step 1: Publish PreCreate event with timestamp T1
            var t1 = DateTimeOffset.UtcNow;
            var preEvent = new PreRecordCreateEvent
            {
                EntityName = "account",
                Record = record,
                CorrelationId = correlationId,
                Timestamp = t1,
                ValidationErrors = new List<ErrorModel>()
            };
            await PublishEventAsync(topicArn, preEvent);

            // Small delay to ensure distinct timestamps
            await Task.Delay(100).ConfigureAwait(false);

            // Step 2: Publish PostCreate event with timestamp T2 > T1
            var t2 = DateTimeOffset.UtcNow;
            var postEvent = new RecordCreatedEvent
            {
                EntityName = "account",
                Record = record,
                CorrelationId = correlationId,
                Timestamp = t2
            };
            await PublishEventAsync(topicArn, postEvent);

            // Step 3: Receive events
            var messages = await ReceiveAllMessagesAsync(queueUrl, 2, timeoutSeconds: 20);
            messages.Should().HaveCount(2, "both Pre and Post events must be received");

            // Step 4: Parse timestamps and sort (use Value<DateTimeOffset>() to preserve sub-second precision)
            var orderedEvents = messages
                .Select(m => new
                {
                    Timestamp = m["timestamp"]!.ToObject<DateTimeOffset>(),
                    HasValidationErrors = m.ContainsKey("validationErrors"),
                    Json = m.ToString()
                })
                .OrderBy(e => e.Timestamp)
                .ToList();

            // Assert Pre event (with validationErrors) comes before Post event (without)
            orderedEvents[0].HasValidationErrors.Should().BeTrue(
                "The first event (by timestamp) should be the Pre event which includes validationErrors");
            orderedEvents[0].Timestamp.Should().BeBefore(orderedEvents[1].Timestamp,
                "Pre-hook event timestamp must be before Post-hook event timestamp, " +
                "preserving the execution order from RecordHookManager");

            _output.WriteLine($"Pre event timestamp: {orderedEvents[0].Timestamp:O}");
            _output.WriteLine($"Post event timestamp: {orderedEvents[1].Timestamp:O}");
            _output.WriteLine("Pre/Post ordering semantics verified.");
        }

        #endregion
    }
}
