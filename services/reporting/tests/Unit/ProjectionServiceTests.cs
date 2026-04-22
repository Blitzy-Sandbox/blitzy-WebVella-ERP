using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Models;
using WebVellaErp.Reporting.Services;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Unit
{
    /// <summary>
    /// Unit tests for ProjectionService (IProjectionService).
    /// Covers CQRS read-model projection create/update/delete, financial entity classification,
    /// event offset tracking, event routing from all 9 bounded contexts, and
    /// NpgsqlConnection/NpgsqlTransaction parameter passing.
    ///
    /// Replaces monolith's RecordHookManager synchronous post-hook dispatch pattern
    /// (ExecutePostCreateRecordHooks, ExecutePostUpdateRecordHooks, ExecutePostDeleteRecordHooks)
    /// with async, event-driven projection updates verified via Moq and FluentAssertions.
    ///
    /// Note: NpgsqlConnection and NpgsqlTransaction are sealed in Npgsql 9.0.4,
    /// so real instances are used rather than Moq mocks.
    /// NpgsqlConnection uses its public parameterless constructor;
    /// NpgsqlTransaction uses RuntimeHelpers.GetUninitializedObject since it only has internal constructors.
    /// These are passthrough objects — the mocked IReportRepository intercepts calls before
    /// any real database interaction occurs.
    /// </summary>
    [Trait("Category", "Unit")]
    public class ProjectionServiceTests
    {
        private readonly Mock<IReportRepository> _reportRepositoryMock;
        private readonly Mock<ILogger<ProjectionService>> _loggerMock;
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly ProjectionService _service;

        public ProjectionServiceTests()
        {
            _reportRepositoryMock = new Mock<IReportRepository>();
            _loggerMock = new Mock<ILogger<ProjectionService>>();

            // NpgsqlConnection is sealed but has a public parameterless constructor.
            // Never opened — purely a non-null reference for parameter passing validation.
            _connection = new NpgsqlConnection();

            // NpgsqlTransaction is sealed with only internal constructors;
            // RuntimeHelpers.GetUninitializedObject creates an instance without calling any constructor.
            // Safe because unit tests never invoke real DB operations on this object.
            _transaction = (NpgsqlTransaction)RuntimeHelpers.GetUninitializedObject(typeof(NpgsqlTransaction));

            _service = new ProjectionService(_reportRepositoryMock.Object, _loggerMock.Object);
        }

        /// <summary>
        /// Creates a test DomainEvent with the given parameters.
        /// Auto-generates recordId, correlationId, and default payload if not provided.
        /// Ensures the payload always contains an "id" field for ExtractRecordId.
        /// </summary>
        private static DomainEvent CreateTestDomainEvent(
            string domain,
            string entity,
            string action,
            Guid? recordId = null,
            Dictionary<string, object?>? payload = null)
        {
            var id = recordId ?? Guid.NewGuid();
            var eventPayload = payload ?? new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = $"Test {entity}",
                ["created_at"] = DateTime.UtcNow.ToString("O")
            };

            // Ensure the payload always has an id field for ExtractRecordId to find
            if (!eventPayload.ContainsKey("id") &&
                !eventPayload.ContainsKey("record_id") &&
                !eventPayload.ContainsKey("Id"))
            {
                eventPayload["id"] = id;
            }

            return new DomainEvent
            {
                EventId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid().ToString(),
                SourceDomain = domain,
                EntityName = entity,
                Action = action,
                Timestamp = DateTime.UtcNow,
                Payload = eventPayload
            };
        }

        #region ProcessEntityCreatedAsync Tests

        /// <summary>
        /// Verifies that a valid create event delegates to UpsertProjectionAsync on the repository
        /// with the correct domain, entity, and record ID.
        /// Source parity: Replaces RecordHookManager.ExecutePostCreateRecordHooks (lines 45-53).
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreatedAsync_ValidEvent_CallsUpsertProjection()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "crm", "contact", recordId,
                    It.IsAny<JsonElement>(),
                    _connection, _transaction,
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies UPSERT idempotency: duplicate create events for the same record are handled
        /// gracefully via INSERT ... ON CONFLICT ... DO UPDATE semantics.
        /// This is the key difference from the monolith — UPSERT handles at-least-once SQS delivery.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreatedAsync_UpsertLogic_HandlesOutOfOrderEvents()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act: Send create event twice (simulates at-least-once delivery)
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: UPSERT called twice without error — idempotent handling
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "crm", "contact", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        /// <summary>
        /// Verifies that projection data serialized to the repository includes both the original
        /// event payload fields and the enriched metadata: _source_event_type, _source_event_id,
        /// _source_timestamp, _source_correlation_id.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreatedAsync_SerializesProjectionData()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var payload = new Dictionary<string, object?>
            {
                ["id"] = recordId,
                ["name"] = "Test Contact",
                ["email"] = "test@example.com"
            };
            var domainEvent = CreateTestDomainEvent("crm", "contact", "created", recordId, payload);

            JsonElement capturedProjectionData = default;
            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, Guid, JsonElement, NpgsqlConnection?, NpgsqlTransaction?, CancellationToken>(
                    (_, _, _, data, _, _, _) => capturedProjectionData = data)
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Projection data includes source metadata fields
            var json = capturedProjectionData.GetRawText();
            json.Should().Contain("_source_event_type");
            json.Should().Contain("crm.contact.created");
            json.Should().Contain("_source_event_id");
            json.Should().Contain(domainEvent.EventId.ToString());
            json.Should().Contain("_source_timestamp");
            json.Should().Contain("_source_correlation_id");
            json.Should().Contain(domainEvent.CorrelationId);
        }

        /// <summary>
        /// Verifies that passing a null DomainEvent throws ArgumentNullException
        /// per the ValidateDomainEvent guard clause.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreatedAsync_NullEvent_ThrowsArgumentNullException()
        {
            // Act & Assert
            await _service.Invoking(s =>
                    s.ProcessEntityCreatedAsync(null!, _connection, _transaction, CancellationToken.None))
                .Should().ThrowAsync<ArgumentNullException>();
        }

        /// <summary>
        /// Verifies that a DomainEvent with an empty SourceDomain throws ArgumentException
        /// per the ValidateDomainEvent guard clause enforcing {domain}.{entity}.{action} convention.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreatedAsync_EmptyDomain_ThrowsArgumentException()
        {
            // Arrange
            var domainEvent = new DomainEvent
            {
                EventId = Guid.NewGuid(),
                SourceDomain = "",
                EntityName = "contact",
                Action = "created",
                Payload = new Dictionary<string, object?> { ["id"] = Guid.NewGuid() }
            };

            // Act & Assert
            await _service.Invoking(s =>
                    s.ProcessEntityCreatedAsync(domainEvent, _connection, _transaction, CancellationToken.None))
                .Should().ThrowAsync<ArgumentException>();
        }

        #endregion

        #region ProcessEntityUpdatedAsync Tests

        /// <summary>
        /// Verifies that a valid update event delegates to UpsertProjectionAsync on the repository.
        /// Source parity: Replaces RecordHookManager.ExecutePostUpdateRecordHooks (lines 67-76).
        /// </summary>
        [Fact]
        public async Task ProcessEntityUpdatedAsync_ValidEvent_CallsUpsertProjection()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "updated", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityUpdatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "crm", "contact", recordId,
                    It.IsAny<JsonElement>(),
                    _connection, _transaction,
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that an update event arriving before the create event is handled gracefully.
        /// UPSERT inserts a new row instead of failing with a foreign key violation.
        /// Critical for event-driven architecture where SQS ordering is best-effort FIFO.
        /// </summary>
        [Fact]
        public async Task ProcessEntityUpdatedAsync_OutOfOrderEvent_UpsertHandlesGracefully()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "updated", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act & Assert: Should not throw even for out-of-order event
            Func<Task> act = () => _service.ProcessEntityUpdatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            await act.Should().NotThrowAsync();

            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "crm", "contact", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that update projection data includes enriched metadata:
        /// _source_event_type, _source_timestamp, _source_correlation_id.
        /// </summary>
        [Fact]
        public async Task ProcessEntityUpdatedAsync_PreservesMetadata()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var payload = new Dictionary<string, object?>
            {
                ["id"] = recordId,
                ["name"] = "Updated Contact",
                ["updated_by"] = "user-123"
            };
            var domainEvent = CreateTestDomainEvent("crm", "contact", "updated", recordId, payload);

            JsonElement capturedData = default;
            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, Guid, JsonElement, NpgsqlConnection?, NpgsqlTransaction?, CancellationToken>(
                    (_, _, _, data, _, _, _) => capturedData = data)
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityUpdatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Metadata preserved in projection data
            var json = capturedData.GetRawText();
            json.Should().Contain("_source_event_type");
            json.Should().Contain("crm.contact.updated");
            json.Should().Contain("_source_timestamp");
            json.Should().Contain("_source_correlation_id");
            json.Should().Contain(domainEvent.CorrelationId);
        }

        #endregion

        #region ProcessEntityDeletedAsync Tests

        /// <summary>
        /// Verifies that non-financial entities (CRM domain) are hard-deleted via DeleteProjectionAsync.
        /// Source parity: Replaces RecordHookManager.ExecutePostDeleteRecordHooks (lines 91-99).
        /// </summary>
        [Fact]
        public async Task ProcessEntityDeletedAsync_NonFinancialEntity_HardDeletes()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "deleted", recordId);

            _reportRepositoryMock
                .Setup(r => r.DeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityDeletedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Hard delete called, NOT soft-delete
            _reportRepositoryMock.Verify(
                r => r.DeleteProjectionAsync(
                    "crm", "contact", recordId,
                    _connection, _transaction,
                    It.IsAny<CancellationToken>()),
                Times.Once());

            _reportRepositoryMock.Verify(
                r => r.SoftDeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Verifies that invoicing domain entities are soft-deleted via SoftDeleteProjectionAsync
        /// to preserve the audit trail for financial data integrity per AAP §0.8.1.
        /// </summary>
        [Fact]
        public async Task ProcessEntityDeletedAsync_FinancialEntity_SoftDeletes()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("invoicing", "invoice", "deleted", recordId);

            _reportRepositoryMock
                .Setup(r => r.SoftDeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityDeletedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Soft-delete for financial entity (invoicing domain)
            _reportRepositoryMock.Verify(
                r => r.SoftDeleteProjectionAsync(
                    "invoicing", "invoice", recordId,
                    _connection, _transaction,
                    It.IsAny<CancellationToken>()),
                Times.Once());

            _reportRepositoryMock.Verify(
                r => r.DeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Verifies that invoicing.payment entities also use soft-delete — the entire invoicing
        /// domain is classified as financial, not just individual entity types.
        /// </summary>
        [Fact]
        public async Task ProcessEntityDeletedAsync_InvoicingPayment_SoftDeletes()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("invoicing", "payment", "deleted", recordId);

            _reportRepositoryMock
                .Setup(r => r.SoftDeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityDeletedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Soft-delete for invoicing.payment (all invoicing entities are financial)
            _reportRepositoryMock.Verify(
                r => r.SoftDeleteProjectionAsync(
                    "invoicing", "payment", recordId,
                    _connection, _transaction,
                    It.IsAny<CancellationToken>()),
                Times.Once());

            _reportRepositoryMock.Verify(
                r => r.DeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Verifies that inventory domain entities are hard-deleted (NOT financial).
        /// </summary>
        [Fact]
        public async Task ProcessEntityDeletedAsync_InventoryEntity_HardDeletes()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("inventory", "task", "deleted", recordId);

            _reportRepositoryMock
                .Setup(r => r.DeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityDeletedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Hard delete for non-financial inventory domain
            _reportRepositoryMock.Verify(
                r => r.DeleteProjectionAsync(
                    "inventory", "task", recordId,
                    _connection, _transaction,
                    It.IsAny<CancellationToken>()),
                Times.Once());

            _reportRepositoryMock.Verify(
                r => r.SoftDeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Verifies idempotent delete behavior: deleting a record that does not exist in
        /// projections succeeds silently (no exception). Repository returns Task.CompletedTask
        /// even when zero rows are affected.
        /// </summary>
        [Fact]
        public async Task ProcessEntityDeletedAsync_RecordNotInProjection_HandlesGracefully()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "deleted", recordId);

            _reportRepositoryMock
                .Setup(r => r.DeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act & Assert: Should not throw even if projection row does not exist
            Func<Task> act = () => _service.ProcessEntityDeletedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            await act.Should().NotThrowAsync();
        }

        #endregion

        #region IsFinancialEntity Tests

        /// <summary>
        /// Verifies that all entities in the invoicing domain are classified as financial.
        /// The entire domain is financial, not just specific entity types.
        /// </summary>
        [Fact]
        public void IsFinancialEntity_InvoicingDomain_ReturnsTrue()
        {
            _service.IsFinancialEntity("invoicing", "invoice").Should().BeTrue();
            _service.IsFinancialEntity("invoicing", "payment").Should().BeTrue();
            _service.IsFinancialEntity("invoicing", "any-entity").Should().BeTrue();
        }

        [Fact]
        public void IsFinancialEntity_CrmDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("crm", "contact").Should().BeFalse();
        }

        [Fact]
        public void IsFinancialEntity_IdentityDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("identity", "user").Should().BeFalse();
        }

        [Fact]
        public void IsFinancialEntity_EntityManagementDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("entity-management", "entity").Should().BeFalse();
        }

        [Fact]
        public void IsFinancialEntity_InventoryDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("inventory", "product").Should().BeFalse();
        }

        [Fact]
        public void IsFinancialEntity_NotificationsDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("notifications", "email").Should().BeFalse();
        }

        [Fact]
        public void IsFinancialEntity_FileManagementDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("file-management", "file").Should().BeFalse();
        }

        [Fact]
        public void IsFinancialEntity_WorkflowDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("workflow", "workflow").Should().BeFalse();
        }

        [Fact]
        public void IsFinancialEntity_PluginSystemDomain_ReturnsFalse()
        {
            _service.IsFinancialEntity("plugin-system", "plugin").Should().BeFalse();
        }

        #endregion

        #region UpdateEventOffsetAsync Tests

        /// <summary>
        /// Verifies that UpdateEventOffsetAsync delegates to UpsertEventOffsetAsync on the repository
        /// for a new domain's first event offset.
        /// </summary>
        [Fact]
        public async Task UpdateEventOffsetAsync_NewDomain_CallsUpsertEventOffset()
        {
            // Arrange
            _reportRepositoryMock
                .Setup(r => r.UpsertEventOffsetAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.UpdateEventOffsetAsync(
                "crm", "event-123", _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertEventOffsetAsync(
                    "crm", "event-123",
                    _connection, _transaction,
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that calling UpdateEventOffsetAsync multiple times for the same domain
        /// correctly updates the offset each time via UPSERT semantics.
        /// </summary>
        [Fact]
        public async Task UpdateEventOffsetAsync_ExistingDomain_UpdatesOffset()
        {
            // Arrange
            _reportRepositoryMock
                .Setup(r => r.UpsertEventOffsetAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act: Call twice with different eventIds for same domain
            await _service.UpdateEventOffsetAsync(
                "crm", "event-100", _connection, _transaction, CancellationToken.None);
            await _service.UpdateEventOffsetAsync(
                "crm", "event-200", _connection, _transaction, CancellationToken.None);

            // Assert: UpsertEventOffsetAsync called twice with different event IDs
            _reportRepositoryMock.Verify(
                r => r.UpsertEventOffsetAsync(
                    "crm", "event-100",
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());

            _reportRepositoryMock.Verify(
                r => r.UpsertEventOffsetAsync(
                    "crm", "event-200",
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region GetLastProcessedEventIdAsync Tests

        /// <summary>
        /// Verifies that GetLastProcessedEventIdAsync returns the event ID from the repository
        /// for a domain that has been previously processed.
        /// </summary>
        [Fact]
        public async Task GetLastProcessedEventIdAsync_ExistingDomain_ReturnsEventId()
        {
            // Arrange
            _reportRepositoryMock
                .Setup(r => r.GetLastEventIdAsync("crm", It.IsAny<CancellationToken>()))
                .ReturnsAsync("event-456");

            // Act
            var result = await _service.GetLastProcessedEventIdAsync(
                "crm", _connection, CancellationToken.None);

            // Assert
            result.Should().Be("event-456");
        }

        /// <summary>
        /// Verifies that GetLastProcessedEventIdAsync returns null for a domain that has never
        /// been processed (first-time processing).
        /// </summary>
        [Fact]
        public async Task GetLastProcessedEventIdAsync_NewDomain_ReturnsNull()
        {
            // Arrange
            _reportRepositoryMock
                .Setup(r => r.GetLastEventIdAsync("new-domain", It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _service.GetLastProcessedEventIdAsync(
                "new-domain", _connection, CancellationToken.None);

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region Event Routing from All 9 Bounded Contexts Tests

        /// <summary>
        /// Verifies that identity domain events (identity.user.created) are correctly routed
        /// and projected via UpsertProjectionAsync.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromIdentity_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("identity", "user", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "identity", "user", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that entity-management domain events are correctly routed and projected.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromEntityManagement_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("entity-management", "entity", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "entity-management", "entity", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that CRM domain events (crm.account.created) are correctly routed.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromCrm_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "account", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "crm", "account", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that inventory domain events (inventory.task.created) are correctly routed.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromInventory_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("inventory", "task", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "inventory", "task", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that invoicing domain events (invoicing.invoice.created) are correctly routed.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromInvoicing_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("invoicing", "invoice", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "invoicing", "invoice", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that notifications domain events (notifications.email.sent) are correctly routed.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromNotifications_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("notifications", "email", "sent", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "notifications", "email", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that file-management domain events (file-management.file.uploaded) are correctly routed.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromFileManagement_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("file-management", "file", "uploaded", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "file-management", "file", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that workflow domain events (workflow.workflow.started) are correctly routed.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromWorkflow_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("workflow", "workflow", "started", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "workflow", "workflow", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that plugin-system domain events (plugin-system.plugin.registered) are correctly routed.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreated_FromPluginSystem_ProcessesCorrectly()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("plugin-system", "plugin", "registered", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    "plugin-system", "plugin", recordId,
                    It.IsAny<JsonElement>(),
                    It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region Transaction Parameter Passing Tests

        /// <summary>
        /// Verifies that the NpgsqlConnection and NpgsqlTransaction instances are passed through
        /// from ProcessEntityCreatedAsync to the repository's UpsertProjectionAsync method.
        /// The EventConsumer manages the transaction lifecycle; ProjectionService operates within it.
        /// </summary>
        [Fact]
        public async Task ProcessEntityCreatedAsync_PassesConnectionAndTransaction()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "created", recordId);

            _reportRepositoryMock
                .Setup(r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(), It.IsAny<NpgsqlConnection?>(),
                    It.IsAny<NpgsqlTransaction?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityCreatedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Exact same NpgsqlConnection and NpgsqlTransaction references passed through
            _reportRepositoryMock.Verify(
                r => r.UpsertProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<JsonElement>(),
                    It.Is<NpgsqlConnection?>(c => ReferenceEquals(c, _connection)),
                    It.Is<NpgsqlTransaction?>(t => ReferenceEquals(t, _transaction)),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Verifies that the NpgsqlConnection and NpgsqlTransaction instances are passed through
        /// from ProcessEntityDeletedAsync to the repository's DeleteProjectionAsync method.
        /// </summary>
        [Fact]
        public async Task ProcessEntityDeletedAsync_PassesConnectionAndTransaction()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var domainEvent = CreateTestDomainEvent("crm", "contact", "deleted", recordId);

            _reportRepositoryMock
                .Setup(r => r.DeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.IsAny<NpgsqlConnection?>(), It.IsAny<NpgsqlTransaction?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            await _service.ProcessEntityDeletedAsync(
                domainEvent, _connection, _transaction, CancellationToken.None);

            // Assert: Exact same NpgsqlConnection and NpgsqlTransaction references passed through
            _reportRepositoryMock.Verify(
                r => r.DeleteProjectionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(),
                    It.Is<NpgsqlConnection?>(c => ReferenceEquals(c, _connection)),
                    It.Is<NpgsqlTransaction?>(t => ReferenceEquals(t, _transaction)),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion
    }
}
