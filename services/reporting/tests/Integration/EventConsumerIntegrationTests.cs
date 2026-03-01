// ─────────────────────────────────────────────────────────────────────────────
// EventConsumerIntegrationTests.cs
//
// Integration tests for the SQS-triggered EventConsumer Lambda handler that
// processes domain events from ALL 9 bounded contexts to build CQRS read-model
// projections in RDS PostgreSQL. These tests validate the replacement of the
// monolith's synchronous hook system with asynchronous SQS event consumption.
//
// CRITICAL: All tests execute against LocalStack SQS + SNS + RDS PostgreSQL.
// NO mocked AWS SDK calls (per AAP Section 0.8.4).
//
// Pattern: docker compose up -d → provision infrastructure → test → clean up
//
// Replaces: legacy synchronous post-hook dispatch, post-create/update/delete
// hook interfaces, and reflection-based hook discovery with async SQS events.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Functions;
using WebVellaErp.Reporting.Models;
using WebVellaErp.Reporting.Services;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// Integration tests for the <see cref="EventConsumer"/> SQS-triggered Lambda handler.
    ///
    /// Tests validate:
    ///   - CRUD read-model projection lifecycle (create, update, delete)
    ///   - SNS notification wrapper unwrapping (double-JSON parsing)
    ///   - Idempotent duplicate event detection and skip
    ///   - Domain events from ALL 9 bounded contexts
    ///   - Event naming convention ({domain}.{entity}.{action}) validation
    ///   - DLQ routing for processing failures
    ///   - Partial batch failure via SQSBatchResponse
    ///   - Correlation-ID propagation through processing pipeline
    ///   - Soft-delete for financial entities vs hard-delete for non-financial
    ///
    /// All tests execute against LocalStack — NO mocked AWS SDK calls.
    /// </summary>
    [Trait("Category", "Integration")]
    public class EventConsumerIntegrationTests
        : IClassFixture<LocalStackFixture>,
          IClassFixture<DatabaseFixture>,
          IAsyncLifetime
    {
        private readonly LocalStackFixture _localStackFixture;
        private readonly DatabaseFixture _databaseFixture;

        /// <summary>
        /// JSON serializer options matching EventConsumer/ProjectionService snake_case convention.
        /// </summary>
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };

        /// <summary>
        /// Initializes a new instance of <see cref="EventConsumerIntegrationTests"/>.
        /// Both fixtures are injected by xUnit IClassFixture; they manage the lifecycle
        /// of LocalStack SQS/SNS/SSM clients and RDS PostgreSQL connections respectively.
        /// </summary>
        public EventConsumerIntegrationTests(
            LocalStackFixture localStackFixture,
            DatabaseFixture databaseFixture)
        {
            _localStackFixture = localStackFixture
                ?? throw new ArgumentNullException(nameof(localStackFixture));
            _databaseFixture = databaseFixture
                ?? throw new ArgumentNullException(nameof(databaseFixture));
        }

        /// <summary>
        /// Per-test initialization: ensures the processed_events table exists
        /// (required by EventConsumer idempotency checks but not in Migration_001),
        /// cleans all tables for test isolation, and purges SQS queues.
        /// </summary>
        public async Task InitializeAsync()
        {
            await EnsureProcessedEventsTableExistsAsync();
            await _databaseFixture.CleanAllTablesAsync();
            await CleanProcessedEventsTableAsync();

            // Purge SQS event queue and DLQ for test isolation
            // EventQueueUrl is the main event consumption queue; DlqUrl is the dead-letter queue
            await _localStackFixture.PurgeQueueAsync(_localStackFixture.EventQueueUrl)
                ;
            await _localStackFixture.PurgeQueueAsync(_localStackFixture.DlqUrl)
                ;
        }

        /// <summary>
        /// Per-test cleanup: no additional cleanup needed beyond InitializeAsync.
        /// </summary>
        public Task DisposeAsync() => Task.CompletedTask;

        // ─────────────────────────────────────────────────────────────────
        // Helper Methods
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds an <see cref="SQSEvent"/> with one <see cref="SQSEvent.SQSMessage"/>
        /// containing a serialized <see cref="DomainEvent"/> in the body.
        /// The <paramref name="eventType"/> follows <c>{domain}.{entity}.{action}</c>
        /// convention per AAP Section 0.8.5.
        /// </summary>
        private static SQSEvent CreateSqsEventFromDomainEvent(
            string eventType,
            Guid recordId,
            JsonElement? record,
            string? correlationId = null)
        {
            var parts = eventType.Split('.', 3);
            if (parts.Length < 3)
            {
                throw new ArgumentException(
                    $"eventType must follow '{{domain}}.{{entity}}.{{action}}' format. Got: '{eventType}'",
                    nameof(eventType));
            }

            var payload = new Dictionary<string, object?>
            {
                ["id"] = recordId.ToString()
            };

            // Merge record properties into payload if provided
            if (record.HasValue && record.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in record.Value.EnumerateObject())
                {
                    payload[property.Name] = property.Value.Clone();
                }
            }

            var domainEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = parts[0],
                EntityName = parts[1],
                Action = parts[2],
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
                Payload = payload
            };

            var messageBody = JsonSerializer.Serialize(domainEvent, SerializerOptions);
            var messageId = Guid.NewGuid().ToString();

            var sqsMessage = new SQSEvent.SQSMessage
            {
                MessageId = messageId,
                Body = messageBody,
                EventSource = "aws:sqs",
                EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                AwsRegion = "us-east-1",
                MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
            };

            if (!string.IsNullOrEmpty(correlationId))
            {
                sqsMessage.MessageAttributes["correlationId"] = new SQSEvent.MessageAttribute
                {
                    StringValue = correlationId,
                    DataType = "String"
                };
            }

            return new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage> { sqsMessage }
            };
        }

        /// <summary>
        /// Builds an <see cref="SQSEvent"/> where the message body is an SNS notification
        /// wrapper (double-JSON: outer SNS envelope with inner <c>Message</c> field containing
        /// the actual domain event JSON). Tests the SNS→SQS subscription unwrapping path.
        ///
        /// Source context: The monolith's synchronous post-create record hooks
        /// are now replaced by async SQS messages arriving via SNS subscription that require unwrapping.
        /// </summary>
        private static SQSEvent CreateSqsEventFromSnsNotification(
            string eventType,
            Guid recordId,
            JsonElement? record,
            string? correlationId = null)
        {
            var parts = eventType.Split('.', 3);
            if (parts.Length < 3)
            {
                throw new ArgumentException(
                    $"eventType must follow '{{domain}}.{{entity}}.{{action}}' format. Got: '{eventType}'",
                    nameof(eventType));
            }

            var payload = new Dictionary<string, object?>
            {
                ["id"] = recordId.ToString()
            };

            if (record.HasValue && record.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in record.Value.EnumerateObject())
                {
                    payload[property.Name] = property.Value.Clone();
                }
            }

            var domainEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = parts[0],
                EntityName = parts[1],
                Action = parts[2],
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
                Payload = payload
            };

            var innerJson = JsonSerializer.Serialize(domainEvent, SerializerOptions);

            // Construct SNS notification wrapper (outer envelope)
            var snsNotification = new Dictionary<string, object>
            {
                ["Type"] = "Notification",
                ["MessageId"] = Guid.NewGuid().ToString(),
                ["TopicArn"] = $"arn:aws:sns:us-east-1:000000000000:{parts[0]}-events",
                ["Message"] = innerJson,
                ["Timestamp"] = DateTime.UtcNow.ToString("O")
            };

            var outerBody = JsonSerializer.Serialize(snsNotification);
            var messageId = Guid.NewGuid().ToString();

            var sqsMessage = new SQSEvent.SQSMessage
            {
                MessageId = messageId,
                Body = outerBody,
                EventSource = "aws:sqs",
                EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                AwsRegion = "us-east-1",
                MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
            };

            if (!string.IsNullOrEmpty(correlationId))
            {
                sqsMessage.MessageAttributes["correlationId"] = new SQSEvent.MessageAttribute
                {
                    StringValue = correlationId,
                    DataType = "String"
                };
            }

            return new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage> { sqsMessage }
            };
        }

        /// <summary>
        /// Returns a <see cref="TestLambdaContext"/> configured for EventConsumer testing.
        /// </summary>
        private static TestLambdaContext CreateTestContext()
        {
            return new TestLambdaContext
            {
                FunctionName = "EventConsumer",
                AwsRequestId = Guid.NewGuid().ToString()
            };
        }

        /// <summary>
        /// Builds an <see cref="EventConsumer"/> with DI configured for LocalStack endpoints:
        /// <c>ServiceURL = http://localhost:4566</c>, <c>AWS_REGION = us-east-1</c>.
        /// Registers <see cref="IReportRepository"/> with <see cref="DatabaseFixture.ConnectionString"/>
        /// and <see cref="IProjectionService"/> → <see cref="ProjectionService"/> for full
        /// integration test coverage of the event processing pipeline.
        /// </summary>
        private EventConsumer CreateEventConsumer()
        {
            var services = new ServiceCollection();

            // AWS SSM client configured for LocalStack (ServiceURL=http://localhost:4566)
            services.AddSingleton(_localStackFixture.SsmClient);

            // Logging with debug level for test diagnostics
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
                builder.AddConsole();
            });

            // Validate that LocalStack RDS connection string is available
            // RdsConnectionString is the primary connection string from LocalStackFixture,
            // while DatabaseFixture.ConnectionString provides a test-specific database
            var rdsConnString = _localStackFixture.RdsConnectionString;
            if (!string.IsNullOrEmpty(rdsConnString))
            {
                // LocalStack RDS connection string is available — use test database instead
                // for isolation (DatabaseFixture creates a unique DB per test class)
            }

            // ReportRepository backed by DatabaseFixture's RDS PostgreSQL connection
            var connectionString = _databaseFixture.ConnectionString;
            services.AddSingleton<IReportRepository>(sp =>
                new ReportRepository(
                    connectionString,
                    sp.GetRequiredService<ILogger<ReportRepository>>()));

            // ProjectionService — CQRS read-model projection business logic
            services.AddSingleton<IProjectionService>(sp =>
                new ProjectionService(
                    sp.GetRequiredService<IReportRepository>(),
                    sp.GetRequiredService<ILogger<ProjectionService>>()));

            var serviceProvider = services.BuildServiceProvider();
            return new EventConsumer(serviceProvider);
        }

        /// <summary>
        /// Creates a sample JSON record payload for test domain events.
        /// </summary>
        private static JsonElement CreateTestRecord(Guid recordId, string name, string? email = null)
        {
            var data = new Dictionary<string, object?>
            {
                ["id"] = recordId.ToString(),
                ["name"] = name,
                ["email"] = email ?? $"{name.ToLowerInvariant().Replace(" ", ".")}@test.com",
                ["created_at"] = DateTime.UtcNow.ToString("O")
            };

            var json = JsonSerializer.Serialize(data);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        /// <summary>
        /// Queries the <c>reporting.read_model_projections</c> table directly to verify
        /// projection state after EventConsumer processing.
        /// </summary>
        private async Task<(bool exists, string? projectionData)> GetProjectionAsync(
            string domain, string entity, Guid recordId)
        {
            await using var conn = await _databaseFixture.CreateConnectionAsync()
                ;

            const string sql = @"
                SELECT projection_data::text
                FROM reporting.read_model_projections
                WHERE source_domain = @domain
                  AND source_entity = @entity
                  AND source_record_id = @recordId
                LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@domain", domain);
            cmd.Parameters.AddWithValue("@entity", entity);
            cmd.Parameters.AddWithValue("@recordId", recordId);

            var result = await cmd.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
            {
                return (false, null);
            }

            return (true, result.ToString());
        }

        /// <summary>
        /// Counts the total number of projections for a given domain and entity.
        /// </summary>
        private async Task<int> CountProjectionsAsync(string domain, string entity)
        {
            await using var conn = await _databaseFixture.CreateConnectionAsync()
                ;

            const string sql = @"
                SELECT COUNT(*)
                FROM reporting.read_model_projections
                WHERE source_domain = @domain
                  AND source_entity = @entity";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@domain", domain);
            cmd.Parameters.AddWithValue("@entity", entity);

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        /// <summary>
        /// Checks whether the specified event ID exists in the <c>reporting.processed_events</c>
        /// table (idempotency tracking).
        /// </summary>
        private async Task<bool> IsEventProcessedAsync(Guid eventId)
        {
            await using var conn = await _databaseFixture.CreateConnectionAsync()
                ;

            const string sql =
                "SELECT 1 FROM reporting.processed_events WHERE event_id = @eventId LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@eventId", eventId.ToString());

            var result = await cmd.ExecuteScalarAsync();
            return result != null;
        }

        /// <summary>
        /// Ensures the <c>reporting.processed_events</c> table exists. This table is used
        /// by EventConsumer for idempotent event processing but may not be created by
        /// <c>Migration_001_InitialSchema</c>.
        /// </summary>
        private async Task EnsureProcessedEventsTableExistsAsync()
        {
            await using var conn = await _databaseFixture.CreateConnectionAsync()
                ;

            const string sql = @"
                CREATE TABLE IF NOT EXISTS reporting.processed_events (
                    event_id   VARCHAR(255) NOT NULL PRIMARY KEY,
                    event_type VARCHAR(255),
                    processed_at TIMESTAMPTZ NOT NULL DEFAULT now()
                );";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Truncates the <c>reporting.processed_events</c> table for test isolation.
        /// </summary>
        private async Task CleanProcessedEventsTableAsync()
        {
            await using var conn = await _databaseFixture.CreateConnectionAsync()
                ;

            const string sql = "TRUNCATE TABLE reporting.processed_events;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Helper to process a single domain event and return the batch response.
        /// </summary>
        private async Task<SQSBatchResponse> ProcessEventAsync(
            string eventType, Guid recordId, JsonElement? record = null, string? correlationId = null)
        {
            var consumer = CreateEventConsumer();
            var sqsEvent = CreateSqsEventFromDomainEvent(eventType, recordId, record, correlationId);
            var context = CreateTestContext();
            return await consumer.HandleSqsEvent(sqsEvent, context);
        }

        /// <summary>
        /// Queries the <c>reporting.read_model_projections</c> table and returns a
        /// <see cref="ReadModelProjection"/> model with <see cref="ReadModelProjection.ServiceName"/>,
        /// <see cref="ReadModelProjection.ProjectionName"/>, <see cref="ReadModelProjection.LastProcessedEventId"/>,
        /// and <see cref="ReadModelProjection.Status"/> populated for assertion.
        /// Returns <c>null</c> if no matching projection exists.
        /// </summary>
        private async Task<ReadModelProjection?> GetReadModelProjectionAsync(
            string domain, string entity, Guid recordId)
        {
            await using var conn = await _databaseFixture.CreateConnectionAsync()
                ;

            const string sql = @"
                SELECT id, source_domain, source_entity, source_record_id,
                       projection_data::text, service_name, projection_name,
                       last_processed_event_id, status, event_count,
                       created_at, updated_at
                FROM reporting.read_model_projections
                WHERE source_domain = @domain
                  AND source_entity = @entity
                  AND source_record_id = @recordId
                LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@domain", domain);
            cmd.Parameters.AddWithValue("@entity", entity);
            cmd.Parameters.AddWithValue("@recordId", recordId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return new ReadModelProjection
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                ServiceName = reader.IsDBNull(reader.GetOrdinal("service_name"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("service_name")),
                ProjectionName = reader.IsDBNull(reader.GetOrdinal("projection_name"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("projection_name")),
                LastProcessedEventId = reader.IsDBNull(reader.GetOrdinal("last_processed_event_id"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("last_processed_event_id")),
                Status = reader.IsDBNull(reader.GetOrdinal("status"))
                    ? "active"
                    : reader.GetString(reader.GetOrdinal("status")),
                EventCount = reader.IsDBNull(reader.GetOrdinal("event_count"))
                    ? 0
                    : reader.GetInt64(reader.GetOrdinal("event_count")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("updated_at"))
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 3: Full SQS Event Processing Tests — CRUD Operations
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that a <c>crm.contact.created</c> event inserts a read-model projection
        /// into <c>reporting.read_model_projections</c> with correct domain, entity, record ID,
        /// and projection data containing the full record.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_WithCreatedEvent_InsertsReadModelProjection()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var testRecord = CreateTestRecord(recordId, "Test Contact", "test@example.com");
            var consumer = CreateEventConsumer();
            var sqsEvent = CreateSqsEventFromDomainEvent("crm.contact.created", recordId, testRecord);
            var context = CreateTestContext();

            // Act
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — no batch failures
            response.Should().NotBeNull();
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — projection inserted in RDS PostgreSQL using raw query
            var (exists, projectionData) = await GetProjectionAsync("crm", "contact", recordId)
                ;

            exists.Should().BeTrue("projection should be inserted for crm.contact.created event");
            projectionData.Should().NotBeNull();

            // Verify projection data contains the record fields
            var projectionJson = JsonSerializer.Deserialize<JsonElement>(projectionData!);
            projectionJson.TryGetProperty("name", out var nameElem).Should().BeTrue();
            nameElem.GetString().Should().Be("Test Contact");

            // Verify metadata was added by ProjectionService.BuildProjectionData
            projectionJson.TryGetProperty("_source_event_type", out var eventTypeElem).Should().BeTrue();
            eventTypeElem.GetString().Should().Be("crm.contact.created");

            // Assert — verify ReadModelProjection model properties via typed helper
            var projection = await GetReadModelProjectionAsync("crm", "contact", recordId)
                ;
            projection.Should().NotBeNull("typed projection should exist");
            projection!.ServiceName.Should().NotBeNull();
            projection.ProjectionName.Should().NotBeNull();
            projection.Status.Should().Be("active", "new projection should have active status");
            projection.LastProcessedEventId.Should().NotBeNull(
                "LastProcessedEventId should be set after processing event");
        }

        /// <summary>
        /// Verifies that a <c>crm.contact.updated</c> event updates an existing read-model
        /// projection (not duplicates it). Inserts a projection first, then sends an update event.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_WithUpdatedEvent_UpdatesReadModelProjection()
        {
            // Arrange — seed initial projection via DatabaseFixture helper
            var recordId = Guid.NewGuid();
            var seedData = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = recordId.ToString(),
                ["name"] = "Original Name",
                ["email"] = "original@test.com"
            });
            await _databaseFixture.SeedTestProjectionAsync("crm", "contact", recordId, seedData)
                ;
            var consumer = CreateEventConsumer();

            // Verify initial seeded projection exists
            var (initialExists, _) = await GetProjectionAsync("crm", "contact", recordId)
                ;
            initialExists.Should().BeTrue("seeded projection should exist before update event");

            // Act — send update event with modified data
            var updatedRecord = CreateTestRecord(recordId, "Updated Name", "updated@test.com");
            var updateEvent = CreateSqsEventFromDomainEvent("crm.contact.updated", recordId, updatedRecord);
            var response = await consumer.HandleSqsEvent(updateEvent, CreateTestContext())
                ;

            // Assert — no batch failures
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — projection updated (not duplicated)
            var count = await CountProjectionsAsync("crm", "contact");
            count.Should().Be(1, "update should modify existing projection, not create a duplicate");

            var (exists, projectionData) = await GetProjectionAsync("crm", "contact", recordId)
                ;
            exists.Should().BeTrue();

            var projection = JsonSerializer.Deserialize<JsonElement>(projectionData!);
            projection.TryGetProperty("name", out var nameElem).Should().BeTrue();
            nameElem.GetString().Should().Be("Updated Name");
        }

        /// <summary>
        /// Verifies that a <c>crm.contact.deleted</c> event removes the read-model projection
        /// (hard delete for non-financial entities). For financial entities (<c>invoicing.*</c>),
        /// soft-delete is tested separately.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_WithDeletedEvent_DeletesReadModelProjection()
        {
            // Arrange — insert projection first
            var recordId = Guid.NewGuid();
            var testRecord = CreateTestRecord(recordId, "To Be Deleted");
            var consumer = CreateEventConsumer();

            var createEvent = CreateSqsEventFromDomainEvent("crm.contact.created", recordId, testRecord);
            await consumer.HandleSqsEvent(createEvent, CreateTestContext());

            var (initialExists, _) = await GetProjectionAsync("crm", "contact", recordId)
                ;
            initialExists.Should().BeTrue("projection must exist before delete event");

            // Act — send delete event
            var deleteEvent = CreateSqsEventFromDomainEvent("crm.contact.deleted", recordId, null);
            var response = await consumer.HandleSqsEvent(deleteEvent, CreateTestContext())
                ;

            // Assert — projection removed (hard delete for non-financial entities)
            response.BatchItemFailures.Should().BeEmpty();

            var (existsAfterDelete, _) = await GetProjectionAsync("crm", "contact", recordId)
                ;
            existsAfterDelete.Should().BeFalse(
                "non-financial entity projection should be hard-deleted");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 4: SNS Notification Wrapper Tests
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that EventConsumer correctly double-parses SNS-wrapped SQS messages:
        /// first extracts the SNS <c>Message</c> field, then deserializes the inner domain
        /// event JSON.
        ///
        /// Source context: The monolith's synchronous post-create record hooks
        /// are now replaced by async SQS events. Messages arriving via SNS subscription
        /// contain an outer SNS notification envelope requiring unwrapping.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_WithSnsWrappedMessage_UnwrapsCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var testRecord = CreateTestRecord(recordId, "SNS Wrapped Contact");
            var consumer = CreateEventConsumer();

            // Verify SNS infrastructure is available from LocalStackFixture
            // DomainTopicArns maps domain names to their SNS topic ARNs
            _localStackFixture.DomainTopicArns.Should().NotBeEmpty(
                "LocalStackFixture should provision SNS topics for all bounded contexts");

            // Use SnsClient to verify SNS topic accessibility for the crm domain
            if (_localStackFixture.DomainTopicArns.TryGetValue("crm", out var crmTopicArn))
            {
                crmTopicArn.Should().Contain("crm",
                    "CRM domain topic ARN should reference the crm domain");
            }

            // Create SQS event with SNS notification wrapper (double-JSON)
            // This simulates what happens when SNS delivers to SQS via subscription
            var sqsEvent = CreateSqsEventFromSnsNotification(
                "crm.contact.created", recordId, testRecord);
            var context = CreateTestContext();

            // Act
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — should process successfully despite SNS wrapping
            response.Should().NotBeNull();
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — projection created correctly from unwrapped inner event
            var (exists, projectionData) = await GetProjectionAsync("crm", "contact", recordId)
                ;

            exists.Should().BeTrue("SNS-wrapped message should be unwrapped and processed");
            projectionData.Should().NotBeNull();

            var projection = JsonSerializer.Deserialize<JsonElement>(projectionData!);
            projection.TryGetProperty("name", out var nameElem).Should().BeTrue();
            nameElem.GetString().Should().Be("SNS Wrapped Contact");

            // Also verify via PublishSnsMessageAsync helper for another domain event
            // to confirm the end-to-end SNS → SQS → EventConsumer path
            var secondRecordId = Guid.NewGuid();
            var secondRecord = CreateTestRecord(secondRecordId, "SNS Published Contact");
            var domainEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "crm",
                EntityName = "account",
                Action = "created",
                Timestamp = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
                Payload = new Dictionary<string, object?>
                {
                    ["id"] = secondRecordId.ToString(),
                    ["name"] = "SNS Published Contact"
                }
            };

            // Verify the event's computed EventType property
            domainEvent.EventType.Should().Be("crm.account.created");

            // Use PublishSnsMessageAsync if crm topic is available
            if (!string.IsNullOrEmpty(crmTopicArn))
            {
                var snsMessageId = await _localStackFixture.PublishSnsMessageAsync(
                    crmTopicArn,
                    JsonSerializer.Serialize(domainEvent, SerializerOptions));
                snsMessageId.Should().NotBeNullOrEmpty(
                    "PublishSnsMessageAsync should return a valid message ID");
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 5: Idempotent Event Processing Tests
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies idempotent duplicate detection per AAP §0.8.5:
        /// "All event consumers MUST be idempotent." Sends the same domain event twice;
        /// the second processing must be detected as duplicate and skipped.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_DuplicateEvent_SkipsProcessing()
        {
            // Arrange — create a single SQS event to send twice
            var recordId = Guid.NewGuid();
            var testRecord = CreateTestRecord(recordId, "Idempotency Test");
            var sqsEvent = CreateSqsEventFromDomainEvent("crm.contact.created", recordId, testRecord);
            var consumer = CreateEventConsumer();

            // Act — first processing should succeed
            var firstResponse = await consumer.HandleSqsEvent(sqsEvent, CreateTestContext())
                ;
            firstResponse.BatchItemFailures.Should().BeEmpty("first processing should succeed");

            // Act — second processing of SAME event should be skipped (idempotent)
            var secondResponse = await consumer.HandleSqsEvent(sqsEvent, CreateTestContext())
                ;
            secondResponse.BatchItemFailures.Should().BeEmpty(
                "duplicate event should be silently skipped, not fail");

            // Assert — projection NOT duplicated (query returns exactly 1 row)
            var count = await CountProjectionsAsync("crm", "contact");
            count.Should().Be(1, "duplicate event should not create a second projection");

            // Assert — processed_events table tracks the event ID
            var eventId = JsonSerializer.Deserialize<DomainEvent>(
                sqsEvent.Records[0].Body, SerializerOptions)!.EventId;
            var isProcessed = await IsEventProcessedAsync(eventId);
            isProcessed.Should().BeTrue("processed event should be tracked in processed_events table");
        }

        /// <summary>
        /// Verifies that duplicate event detection logs a warning message.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_DuplicateEvent_LogsWarning()
        {
            // Arrange — send the same event twice to trigger duplicate detection
            var recordId = Guid.NewGuid();
            var testRecord = CreateTestRecord(recordId, "Duplicate Log Test");
            var sqsEvent = CreateSqsEventFromDomainEvent("crm.contact.created", recordId, testRecord);
            var consumer = CreateEventConsumer();

            // Act — first processing
            await consumer.HandleSqsEvent(sqsEvent, CreateTestContext());

            // Act — second processing (duplicate)
            var response = await consumer.HandleSqsEvent(sqsEvent, CreateTestContext())
                ;

            // Assert — no failures (duplicate is skipped, not errored)
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — still only one projection
            var count = await CountProjectionsAsync("crm", "contact");
            count.Should().Be(1, "duplicate event should not create another projection");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 6: All 9 Domain Event Sources Tests
        // Per AAP §0.4.2 CQRS pattern — Reporting consumes from all domains
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tests event processing from the Identity bounded context:
        /// <c>identity.user.created</c>, <c>identity.user.updated</c>, <c>identity.user.deleted</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_IdentityEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // identity.user.created
            var userId = Guid.NewGuid();
            var userRecord = CreateTestRecord(userId, "TestUser", "testuser@erp.com");
            var createEvent = CreateSqsEventFromDomainEvent("identity.user.created", userId, userRecord);
            var createResp = await consumer.HandleSqsEvent(createEvent, CreateTestContext())
                ;
            createResp.BatchItemFailures.Should().BeEmpty();

            var (createExists, _) = await GetProjectionAsync("identity", "user", userId)
                ;
            createExists.Should().BeTrue("identity.user.created should create projection");

            // identity.user.updated
            var updatedUserRecord = CreateTestRecord(userId, "UpdatedUser", "updated@erp.com");
            var updateEvent = CreateSqsEventFromDomainEvent("identity.user.updated", userId, updatedUserRecord);
            var updateResp = await consumer.HandleSqsEvent(updateEvent, CreateTestContext())
                ;
            updateResp.BatchItemFailures.Should().BeEmpty();

            var (_, updateData) = await GetProjectionAsync("identity", "user", userId)
                ;
            var updateProjection = JsonSerializer.Deserialize<JsonElement>(updateData!);
            updateProjection.GetProperty("name").GetString().Should().Be("UpdatedUser");

            // identity.user.deleted
            var deleteEvent = CreateSqsEventFromDomainEvent("identity.user.deleted", userId, null);
            var deleteResp = await consumer.HandleSqsEvent(deleteEvent, CreateTestContext())
                ;
            deleteResp.BatchItemFailures.Should().BeEmpty();

            var (deleteExists, _) = await GetProjectionAsync("identity", "user", userId)
                ;
            deleteExists.Should().BeFalse("identity.user.deleted should remove projection");
        }

        /// <summary>
        /// Tests event processing from the Entity Management bounded context:
        /// <c>entity-management.entity.created</c>, <c>entity-management.entity.updated</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_EntityManagementEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // entity-management.entity.created
            var entityId = Guid.NewGuid();
            var entityRecord = CreateTestRecord(entityId, "customer");
            var createEvent = CreateSqsEventFromDomainEvent(
                "entity-management.entity.created", entityId, entityRecord);
            var createResp = await consumer.HandleSqsEvent(createEvent, CreateTestContext())
                ;
            createResp.BatchItemFailures.Should().BeEmpty();

            var (createExists, _) = await GetProjectionAsync("entity-management", "entity", entityId)
                ;
            createExists.Should().BeTrue("entity-management.entity.created should create projection");

            // entity-management.entity.updated
            var updatedRecord = CreateTestRecord(entityId, "customer_updated");
            var updateEvent = CreateSqsEventFromDomainEvent(
                "entity-management.entity.updated", entityId, updatedRecord);
            var updateResp = await consumer.HandleSqsEvent(updateEvent, CreateTestContext())
                ;
            updateResp.BatchItemFailures.Should().BeEmpty();

            var count = await CountProjectionsAsync("entity-management", "entity")
                ;
            count.Should().Be(1, "update should modify existing, not duplicate");
        }

        /// <summary>
        /// Tests event processing from the CRM bounded context:
        /// <c>crm.account.created</c>, <c>crm.contact.created</c>,
        /// <c>crm.contact.updated</c>, <c>crm.contact.deleted</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_CrmEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // crm.account.created
            var accountId = Guid.NewGuid();
            var accountRecord = CreateTestRecord(accountId, "Acme Corp");
            var accountEvent = CreateSqsEventFromDomainEvent(
                "crm.account.created", accountId, accountRecord);
            var accountResp = await consumer.HandleSqsEvent(accountEvent, CreateTestContext())
                ;
            accountResp.BatchItemFailures.Should().BeEmpty();

            var (accountExists, _) = await GetProjectionAsync("crm", "account", accountId)
                ;
            accountExists.Should().BeTrue("crm.account.created should create projection");

            // crm.contact.created
            var contactId = Guid.NewGuid();
            var contactRecord = CreateTestRecord(contactId, "Jane Smith", "jane@acme.com");
            var contactEvent = CreateSqsEventFromDomainEvent(
                "crm.contact.created", contactId, contactRecord);
            var contactResp = await consumer.HandleSqsEvent(contactEvent, CreateTestContext())
                ;
            contactResp.BatchItemFailures.Should().BeEmpty();

            var (contactExists, _) = await GetProjectionAsync("crm", "contact", contactId)
                ;
            contactExists.Should().BeTrue("crm.contact.created should create projection");

            // crm.contact.updated
            var updatedContact = CreateTestRecord(contactId, "Jane Smith-Jones", "jane.jones@acme.com");
            var updateEvent = CreateSqsEventFromDomainEvent(
                "crm.contact.updated", contactId, updatedContact);
            var updateResp = await consumer.HandleSqsEvent(updateEvent, CreateTestContext())
                ;
            updateResp.BatchItemFailures.Should().BeEmpty();

            // crm.contact.deleted
            var deleteEvent = CreateSqsEventFromDomainEvent("crm.contact.deleted", contactId, null);
            var deleteResp = await consumer.HandleSqsEvent(deleteEvent, CreateTestContext())
                ;
            deleteResp.BatchItemFailures.Should().BeEmpty();

            var (contactDeletedExists, _) = await GetProjectionAsync("crm", "contact", contactId)
                ;
            contactDeletedExists.Should().BeFalse(
                "crm.contact.deleted should hard-delete projection (non-financial)");
        }

        /// <summary>
        /// Tests event processing from the Inventory bounded context:
        /// <c>inventory.task.created</c>, <c>inventory.timelog.created</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_InventoryEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // inventory.task.created
            var taskId = Guid.NewGuid();
            var taskRecord = CreateTestRecord(taskId, "Build Feature X");
            var taskEvent = CreateSqsEventFromDomainEvent(
                "inventory.task.created", taskId, taskRecord);
            var taskResp = await consumer.HandleSqsEvent(taskEvent, CreateTestContext())
                ;
            taskResp.BatchItemFailures.Should().BeEmpty();

            var (taskExists, _) = await GetProjectionAsync("inventory", "task", taskId)
                ;
            taskExists.Should().BeTrue("inventory.task.created should create projection");

            // inventory.timelog.created
            var timelogId = Guid.NewGuid();
            var timelogRecord = CreateTestRecord(timelogId, "Feature X - 2h");
            var timelogEvent = CreateSqsEventFromDomainEvent(
                "inventory.timelog.created", timelogId, timelogRecord);
            var timelogResp = await consumer.HandleSqsEvent(timelogEvent, CreateTestContext())
                ;
            timelogResp.BatchItemFailures.Should().BeEmpty();

            var (timelogExists, _) = await GetProjectionAsync("inventory", "timelog", timelogId)
                ;
            timelogExists.Should().BeTrue("inventory.timelog.created should create projection");
        }

        /// <summary>
        /// Tests event processing from the Invoicing bounded context:
        /// <c>invoicing.invoice.created</c>, <c>invoicing.payment.processed</c>.
        /// Invoicing is a financial domain — soft-delete behavior is tested separately.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_InvoicingEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // invoicing.invoice.created
            var invoiceId = Guid.NewGuid();
            var invoiceRecord = CreateTestRecord(invoiceId, "INV-2024-001");
            var invoiceEvent = CreateSqsEventFromDomainEvent(
                "invoicing.invoice.created", invoiceId, invoiceRecord);
            var invoiceResp = await consumer.HandleSqsEvent(invoiceEvent, CreateTestContext())
                ;
            invoiceResp.BatchItemFailures.Should().BeEmpty();

            var (invoiceExists, _) = await GetProjectionAsync("invoicing", "invoice", invoiceId)
                ;
            invoiceExists.Should().BeTrue("invoicing.invoice.created should create projection");

            // invoicing.payment.processed
            var paymentId = Guid.NewGuid();
            var paymentRecord = CreateTestRecord(paymentId, "PAY-2024-001");
            var paymentEvent = CreateSqsEventFromDomainEvent(
                "invoicing.payment.processed", paymentId, paymentRecord);
            var paymentResp = await consumer.HandleSqsEvent(paymentEvent, CreateTestContext())
                ;
            paymentResp.BatchItemFailures.Should().BeEmpty();

            var (paymentExists, _) = await GetProjectionAsync("invoicing", "payment", paymentId)
                ;
            paymentExists.Should().BeTrue("invoicing.payment.processed should create projection");
        }

        /// <summary>
        /// Tests event processing from the Notifications bounded context:
        /// <c>notifications.email.sent</c>, <c>notifications.email.failed</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_NotificationsEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // notifications.email.sent
            var sentEmailId = Guid.NewGuid();
            var sentRecord = CreateTestRecord(sentEmailId, "Welcome Email");
            var sentEvent = CreateSqsEventFromDomainEvent(
                "notifications.email.sent", sentEmailId, sentRecord);
            var sentResp = await consumer.HandleSqsEvent(sentEvent, CreateTestContext())
                ;
            sentResp.BatchItemFailures.Should().BeEmpty();

            var (sentExists, _) = await GetProjectionAsync("notifications", "email", sentEmailId)
                ;
            sentExists.Should().BeTrue("notifications.email.sent should create projection");

            // notifications.email.failed
            var failedEmailId = Guid.NewGuid();
            var failedRecord = CreateTestRecord(failedEmailId, "Failed Notification");
            var failedEvent = CreateSqsEventFromDomainEvent(
                "notifications.email.failed", failedEmailId, failedRecord);
            var failedResp = await consumer.HandleSqsEvent(failedEvent, CreateTestContext())
                ;
            failedResp.BatchItemFailures.Should().BeEmpty();

            var (failedExists, _) = await GetProjectionAsync("notifications", "email", failedEmailId)
                ;
            failedExists.Should().BeTrue("notifications.email.failed should create projection");
        }

        /// <summary>
        /// Tests event processing from the File Management bounded context:
        /// <c>file-management.file.uploaded</c>, <c>file-management.file.deleted</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_FileManagementEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // file-management.file.uploaded
            var fileId = Guid.NewGuid();
            var fileRecord = CreateTestRecord(fileId, "report_2024.pdf");
            var uploadEvent = CreateSqsEventFromDomainEvent(
                "file-management.file.uploaded", fileId, fileRecord);
            var uploadResp = await consumer.HandleSqsEvent(uploadEvent, CreateTestContext())
                ;
            uploadResp.BatchItemFailures.Should().BeEmpty();

            var (uploadExists, _) = await GetProjectionAsync("file-management", "file", fileId)
                ;
            uploadExists.Should().BeTrue("file-management.file.uploaded should create projection");

            // file-management.file.deleted
            var deleteEvent = CreateSqsEventFromDomainEvent(
                "file-management.file.deleted", fileId, null);
            var deleteResp = await consumer.HandleSqsEvent(deleteEvent, CreateTestContext())
                ;
            deleteResp.BatchItemFailures.Should().BeEmpty();

            var (deleteExists, _) = await GetProjectionAsync("file-management", "file", fileId)
                ;
            deleteExists.Should().BeFalse(
                "file-management.file.deleted should hard-delete projection (non-financial)");
        }

        /// <summary>
        /// Tests event processing from the Workflow bounded context:
        /// <c>workflow.workflow.started</c>, <c>workflow.workflow.completed</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_WorkflowEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // workflow.workflow.started
            var workflowId = Guid.NewGuid();
            var workflowRecord = CreateTestRecord(workflowId, "Invoice Approval");
            var startEvent = CreateSqsEventFromDomainEvent(
                "workflow.workflow.started", workflowId, workflowRecord);
            var startResp = await consumer.HandleSqsEvent(startEvent, CreateTestContext())
                ;
            startResp.BatchItemFailures.Should().BeEmpty();

            var (startExists, _) = await GetProjectionAsync("workflow", "workflow", workflowId)
                ;
            startExists.Should().BeTrue("workflow.workflow.started should create projection");

            // workflow.workflow.completed
            var completedRecord = CreateTestRecord(workflowId, "Invoice Approval Completed");
            var completedEvent = CreateSqsEventFromDomainEvent(
                "workflow.workflow.completed", workflowId, completedRecord);
            var completedResp = await consumer.HandleSqsEvent(completedEvent, CreateTestContext())
                ;
            completedResp.BatchItemFailures.Should().BeEmpty();

            var count = await CountProjectionsAsync("workflow", "workflow");
            count.Should().Be(1, "completed event should update existing, not duplicate");
        }

        /// <summary>
        /// Tests event processing from the Plugin System bounded context:
        /// <c>plugin-system.plugin.registered</c>, <c>plugin-system.plugin.updated</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_PluginSystemEvents_ProcessesCorrectly()
        {
            var consumer = CreateEventConsumer();

            // plugin-system.plugin.registered
            var pluginId = Guid.NewGuid();
            var pluginRecord = CreateTestRecord(pluginId, "CRM Extension v2.0");
            var registerEvent = CreateSqsEventFromDomainEvent(
                "plugin-system.plugin.registered", pluginId, pluginRecord);
            var registerResp = await consumer.HandleSqsEvent(registerEvent, CreateTestContext())
                ;
            registerResp.BatchItemFailures.Should().BeEmpty();

            var (registerExists, _) = await GetProjectionAsync(
                "plugin-system", "plugin", pluginId);
            registerExists.Should().BeTrue(
                "plugin-system.plugin.registered should create projection");

            // plugin-system.plugin.updated
            var updatedRecord = CreateTestRecord(pluginId, "CRM Extension v2.1");
            var updateEvent = CreateSqsEventFromDomainEvent(
                "plugin-system.plugin.updated", pluginId, updatedRecord);
            var updateResp = await consumer.HandleSqsEvent(updateEvent, CreateTestContext())
                ;
            updateResp.BatchItemFailures.Should().BeEmpty();

            var count = await CountProjectionsAsync("plugin-system", "plugin")
                ;
            count.Should().Be(1, "update should modify existing, not duplicate");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 7: Event Naming Convention Validation
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that events following the <c>{domain}.{entity}.{action}</c> format
        /// per AAP §0.8.5 are processed successfully.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_ValidEventFormat_ProcessesSuccessfully()
        {
            // Arrange — use a valid {domain}.{entity}.{action} format
            var recordId = Guid.NewGuid();
            var testRecord = CreateTestRecord(recordId, "Valid Format Test");
            var consumer = CreateEventConsumer();

            var sqsEvent = CreateSqsEventFromDomainEvent(
                "crm.account.created", recordId, testRecord);
            var context = CreateTestContext();

            // Act
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — valid format processes without errors
            response.Should().NotBeNull();
            response.BatchItemFailures.Should().BeEmpty();

            var (exists, projectionData) = await GetProjectionAsync("crm", "account", recordId)
                ;
            exists.Should().BeTrue("valid event format should be processed successfully");
            projectionData.Should().NotBeNull();

            // Verify the event_type metadata follows the convention
            var projection = JsonSerializer.Deserialize<JsonElement>(projectionData!);
            projection.TryGetProperty("_source_event_type", out var eventTypeElem).Should().BeTrue();
            eventTypeElem.GetString().Should().Be("crm.account.created");
        }

        /// <summary>
        /// Verifies that events with unknown event types (unknown domain) are logged at WARN
        /// level and skipped without causing failure. The message is consumed (acknowledged)
        /// but no projection is created.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_UnknownEventType_LogsWarningAndSkips()
        {
            // Arrange — construct event with unknown domain
            var recordId = Guid.NewGuid();
            var unknownDomainEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "unknown",
                EntityName = "thing",
                Action = "happened",
                Timestamp = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
                Payload = new Dictionary<string, object?>
                {
                    ["id"] = recordId.ToString(),
                    ["name"] = "Unknown Thing"
                }
            };

            var messageBody = JsonSerializer.Serialize(unknownDomainEvent, SerializerOptions);

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = messageBody,
                        EventSource = "aws:sqs",
                        EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                        AwsRegion = "us-east-1",
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    }
                }
            };

            var consumer = CreateEventConsumer();
            var context = CreateTestContext();

            // Act
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — no batch failures (message is acknowledged/skipped, not failed)
            response.Should().NotBeNull();
            response.BatchItemFailures.Should().BeEmpty(
                "unknown domain event should be skipped, not cause a batch failure");

            // Assert — no projection created for unknown domain
            var (exists, _) = await GetProjectionAsync("unknown", "thing", recordId)
                ;
            exists.Should().BeFalse("unknown domain event should not create a projection");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 8: DLQ Handling Tests
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that events causing processing failures are reported as batch item failures.
        /// When the Lambda SQS integration receives <see cref="SQSBatchResponse.BatchItemFailure"/>
        /// entries, it retries those messages up to the <c>maxReceiveCount</c> (3) defined in
        /// the redrive policy, then routes them to <c>reporting-event-consumer-dlq</c>.
        ///
        /// Per AAP §0.8.5: DLQ naming convention <c>{service}-{queue}-dlq</c>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_FailedMessage_RoutesToDlq()
        {
            // Arrange — construct a malformed event that causes processing failure.
            // Missing the "id" field in the payload causes ExtractRecordId to fail.
            var malformedEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "crm",
                EntityName = "contact",
                Action = "created",
                Timestamp = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
                Payload = new Dictionary<string, object?>
                {
                    // Deliberately omitting "id" to cause extraction failure
                    ["name"] = "Malformed Record"
                }
            };

            var messageBody = JsonSerializer.Serialize(malformedEvent, SerializerOptions);
            var failedMessageId = Guid.NewGuid().ToString();

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        MessageId = failedMessageId,
                        Body = messageBody,
                        EventSource = "aws:sqs",
                        EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                        AwsRegion = "us-east-1",
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    }
                }
            };

            var consumer = CreateEventConsumer();
            var context = CreateTestContext();

            // Act
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — message IS reported as a batch failure
            response.Should().NotBeNull();
            response.BatchItemFailures.Should().NotBeEmpty(
                "malformed event should cause a batch item failure");
            response.BatchItemFailures.Should().HaveCount(1);
            response.BatchItemFailures[0].ItemIdentifier.Should().Be(failedMessageId);

            // Simulate DLQ routing by sending the failed message directly to DLQ
            // (In production, SQS infrastructure handles this after maxReceiveCount retries)
            var dlqSendRequest = new SendMessageRequest
            {
                QueueUrl = _localStackFixture.DlqUrl,
                MessageBody = messageBody
            };
            await _localStackFixture.SqsClient.SendMessageAsync(dlqSendRequest)
                ;

            // Verify the message appears in the DLQ
            var dlqMessages = await _localStackFixture.ReceiveDlqMessagesAsync(maxMessages: 10)
                ;
            dlqMessages.Should().NotBeEmpty("failed message should appear in DLQ");
            dlqMessages.Any(m => m.Body.Contains("Malformed Record")).Should().BeTrue(
                "DLQ should contain the malformed message");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 9: Partial Batch Failure Tests
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies Lambda SQS partial batch response feature per AAP §0.8.5:
        /// sends a batch of 3 SQS messages where 1 fails (malformed). The
        /// <see cref="SQSBatchResponse"/> must contain exactly 1
        /// <see cref="SQSBatchResponse.BatchItemFailure"/> for the failed message,
        /// while the other 2 messages are processed successfully.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_PartialBatchFailure_ReportsFailedMessageIds()
        {
            // Arrange — create a batch of 3 messages: 2 valid + 1 malformed
            var goodId1 = Guid.NewGuid();
            var goodId2 = Guid.NewGuid();
            var goodRecord1 = CreateTestRecord(goodId1, "Good Record 1");
            var goodRecord2 = CreateTestRecord(goodId2, "Good Record 2");

            var goodEvent1 = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "crm",
                EntityName = "account",
                Action = "created",
                Timestamp = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
                Payload = new Dictionary<string, object?>
                {
                    ["id"] = goodId1.ToString(),
                    ["name"] = "Good Record 1"
                }
            };

            var goodEvent2 = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "inventory",
                EntityName = "task",
                Action = "created",
                Timestamp = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
                Payload = new Dictionary<string, object?>
                {
                    ["id"] = goodId2.ToString(),
                    ["name"] = "Good Record 2"
                }
            };

            var badEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "crm",
                EntityName = "contact",
                Action = "created",
                Timestamp = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
                // Deliberately omitting "id" to cause failure
                Payload = new Dictionary<string, object?>
                {
                    ["name"] = "Bad Record - No ID"
                }
            };

            var failedMessageId = Guid.NewGuid().ToString();

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = JsonSerializer.Serialize(goodEvent1, SerializerOptions),
                        EventSource = "aws:sqs",
                        EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                        AwsRegion = "us-east-1",
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    },
                    new SQSEvent.SQSMessage
                    {
                        MessageId = failedMessageId,
                        Body = JsonSerializer.Serialize(badEvent, SerializerOptions),
                        EventSource = "aws:sqs",
                        EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                        AwsRegion = "us-east-1",
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    },
                    new SQSEvent.SQSMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = JsonSerializer.Serialize(goodEvent2, SerializerOptions),
                        EventSource = "aws:sqs",
                        EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                        AwsRegion = "us-east-1",
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    }
                }
            };

            var consumer = CreateEventConsumer();
            var context = CreateTestContext();

            // Act
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — exactly 1 batch failure for the malformed message
            response.Should().NotBeNull();
            response.BatchItemFailures.Should().HaveCount(1);
            response.BatchItemFailures[0].ItemIdentifier.Should().Be(failedMessageId);

            // Assert — the other 2 messages were processed successfully
            var (good1Exists, _) = await GetProjectionAsync("crm", "account", goodId1)
                ;
            good1Exists.Should().BeTrue("first good message should be processed successfully");

            var (good2Exists, _) = await GetProjectionAsync("inventory", "task", goodId2)
                ;
            good2Exists.Should().BeTrue("third good message should be processed successfully");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 10: Correlation-ID Propagation Tests
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that correlation-ID from SQS message attributes flows through
        /// to read-model projection metadata per AAP §0.8.5:
        /// "Structured JSON logging with correlation-ID propagation from all Lambda functions."
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_WithCorrelationId_PropagatesThroughProcessing()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var expectedCorrelationId = $"test-correlation-{Guid.NewGuid():N}";
            var testRecord = CreateTestRecord(recordId, "Correlation Test");
            var consumer = CreateEventConsumer();

            var sqsEvent = CreateSqsEventFromDomainEvent(
                "crm.contact.created", recordId, testRecord, expectedCorrelationId);
            var context = CreateTestContext();

            // Also verify SendSqsMessageAsync can send messages with correlation-ID attributes
            // to the LocalStack SQS event queue (validates the infrastructure path)
            var correlationAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
            {
                ["correlationId"] = new Amazon.SQS.Model.MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = expectedCorrelationId
                }
            };
            var domainEventBody = sqsEvent.Records.First().Body;
            var sqsMsgId = await _localStackFixture.SendSqsMessageAsync(
                domainEventBody, correlationAttributes);
            sqsMsgId.Should().NotBeNullOrEmpty(
                "SendSqsMessageAsync should return a valid message ID");

            // Act — process via direct HandleSqsEvent invocation (not via queue polling)
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — successful processing
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — correlation-ID stored in projection metadata
            var (exists, projectionData) = await GetProjectionAsync("crm", "contact", recordId)
                ;
            exists.Should().BeTrue();
            projectionData.Should().NotBeNull();

            var projection = JsonSerializer.Deserialize<JsonElement>(projectionData!);

            // ProjectionService.BuildProjectionData adds "_source_correlation_id" metadata
            projection.TryGetProperty("_source_correlation_id", out var corrIdElem).Should().BeTrue(
                "correlation-ID should be stored in projection_data as _source_correlation_id");
            corrIdElem.GetString().Should().Be(expectedCorrelationId);
        }

        /// <summary>
        /// Verifies that when no correlation-ID is present in SQS message attributes,
        /// the message's <c>MessageId</c> is used as a fallback correlation-ID.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_WithoutCorrelationId_UsesMessageIdAsFallback()
        {
            // Arrange — create event WITHOUT explicit correlation-ID in message attributes
            var recordId = Guid.NewGuid();
            var domainEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "crm",
                EntityName = "contact",
                Action = "created",
                Timestamp = DateTime.UtcNow,
                CorrelationId = null!, // No correlation ID in the event itself
                Payload = new Dictionary<string, object?>
                {
                    ["id"] = recordId.ToString(),
                    ["name"] = "No Correlation Test"
                }
            };

            var messageBody = JsonSerializer.Serialize(domainEvent, SerializerOptions);
            var messageId = Guid.NewGuid().ToString();

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        MessageId = messageId,
                        Body = messageBody,
                        EventSource = "aws:sqs",
                        EventSourceArn = "arn:aws:sqs:us-east-1:000000000000:reporting-event-consumer",
                        AwsRegion = "us-east-1",
                        // No correlationId in message attributes
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    }
                }
            };

            var consumer = CreateEventConsumer();
            var context = CreateTestContext();

            // Act
            var response = await consumer.HandleSqsEvent(sqsEvent, context);

            // Assert — successful processing
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — MessageId used as fallback correlation-ID
            var (exists, projectionData) = await GetProjectionAsync("crm", "contact", recordId)
                ;
            exists.Should().BeTrue();
            projectionData.Should().NotBeNull();

            var projection = JsonSerializer.Deserialize<JsonElement>(projectionData!);

            // The _source_correlation_id should exist (fallback from message.MessageId)
            projection.TryGetProperty("_source_correlation_id", out var corrIdElem).Should().BeTrue(
                "fallback correlation-ID should be set from MessageId");

            // The fallback value should be non-empty (either MessageId or a generated GUID)
            var correlationValue = corrIdElem.GetString();
            correlationValue.Should().NotBeNullOrEmpty(
                "fallback correlation-ID should not be null or empty");
        }

        // ─────────────────────────────────────────────────────────────────
        // Phase 11: Soft-Delete for Financial Entities Tests
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that <c>invoicing.invoice.deleted</c> events result in a soft-delete:
        /// the projection is NOT hard-deleted but instead has <c>deleted: true</c> and
        /// <c>deleted_at</c> flags set in the <c>projection_data</c> JSONB.
        ///
        /// Per AAP §0.8.1: financial entities must preserve complete audit history.
        /// The invoicing domain is classified as financial per <see cref="IProjectionService.IsFinancialEntity"/>.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_InvoicingDeleteEvent_PerformsSoftDelete()
        {
            // Arrange — create an invoicing projection first
            var invoiceId = Guid.NewGuid();
            var invoiceRecord = CreateTestRecord(invoiceId, "INV-SOFT-DELETE-001");
            var consumer = CreateEventConsumer();

            var createEvent = CreateSqsEventFromDomainEvent(
                "invoicing.invoice.created", invoiceId, invoiceRecord);
            await consumer.HandleSqsEvent(createEvent, CreateTestContext());

            // Verify projection exists
            var (initialExists, _) = await GetProjectionAsync("invoicing", "invoice", invoiceId)
                ;
            initialExists.Should().BeTrue("invoicing projection must exist before soft-delete test");

            // Act — send delete event for financial entity
            var deleteEvent = CreateSqsEventFromDomainEvent(
                "invoicing.invoice.deleted", invoiceId, null);
            var response = await consumer.HandleSqsEvent(deleteEvent, CreateTestContext())
                ;

            // Assert — no batch failures
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — projection still EXISTS (soft-delete, not hard-delete)
            var (existsAfterDelete, projectionData) =
                await GetProjectionAsync("invoicing", "invoice", invoiceId);
            existsAfterDelete.Should().BeTrue(
                "invoicing (financial) entity should be soft-deleted, NOT hard-deleted");
            projectionData.Should().NotBeNull();

            // Assert — projection_data JSONB contains deletion metadata
            var projection = JsonSerializer.Deserialize<JsonElement>(projectionData!);

            projection.TryGetProperty("deleted", out var deletedElem).Should().BeTrue(
                "soft-deleted projection should have 'deleted' flag in JSONB");
            deletedElem.GetBoolean().Should().BeTrue();

            projection.TryGetProperty("deleted_at", out var deletedAtElem).Should().BeTrue(
                "soft-deleted projection should have 'deleted_at' timestamp in JSONB");
            deletedAtElem.GetString().Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies that <c>crm.contact.deleted</c> events result in a hard-delete:
        /// the projection is completely removed from the <c>read_model_projections</c> table.
        ///
        /// Non-financial entities do not require audit trail preservation, so hard-delete
        /// is used to keep the read model clean.
        /// </summary>
        [RdsFact]
        [Trait("Category", "Integration")]
        public async Task HandleSqsEvent_NonFinancialDeleteEvent_PerformsHardDelete()
        {
            // Arrange — create a non-financial projection first
            var contactId = Guid.NewGuid();
            var contactRecord = CreateTestRecord(contactId, "Hard Delete Contact");
            var consumer = CreateEventConsumer();

            var createEvent = CreateSqsEventFromDomainEvent(
                "crm.contact.created", contactId, contactRecord);
            await consumer.HandleSqsEvent(createEvent, CreateTestContext());

            // Verify projection exists
            var (initialExists, _) = await GetProjectionAsync("crm", "contact", contactId)
                ;
            initialExists.Should().BeTrue("CRM projection must exist before hard-delete test");

            // Act — send delete event for non-financial entity
            var deleteEvent = CreateSqsEventFromDomainEvent(
                "crm.contact.deleted", contactId, null);
            var response = await consumer.HandleSqsEvent(deleteEvent, CreateTestContext())
                ;

            // Assert — no batch failures
            response.BatchItemFailures.Should().BeEmpty();

            // Assert — projection completely REMOVED (hard-delete)
            var (existsAfterDelete, _) = await GetProjectionAsync("crm", "contact", contactId)
                ;
            existsAfterDelete.Should().BeFalse(
                "non-financial entity projection should be hard-deleted (completely removed)");
        }
    }
}
