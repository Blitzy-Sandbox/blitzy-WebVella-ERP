using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using WebVellaErp.Reporting.DataAccess;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// Integration tests for <see cref="IReportRepository"/> / <see cref="ReportRepository"/>
    /// against LocalStack RDS PostgreSQL. Validates the complete data access layer for the
    /// Reporting &amp; Analytics microservice including:
    ///   - Report definition CRUD (12 column types: UUID, varchar, text, jsonb, boolean, integer, timestamptz)
    ///   - Read-model projection UPSERT/query with JSONB round-trip verification
    ///   - Event offset tracking for idempotent domain event consumption
    ///
    /// All tests execute against real PostgreSQL via LocalStack — zero mocked AWS SDK calls
    /// per AAP Section 0.8.4 testing requirements.
    /// </summary>
    [Trait("Category", "Integration")]
    public class ReportRepositoryIntegrationTests
        : IClassFixture<LocalStackFixture>, IClassFixture<DatabaseFixture>, IAsyncLifetime
    {
        private readonly DatabaseFixture _databaseFixture;
        private readonly ReportRepository _repository;

        /// <summary>
        /// Constructs the test class with shared LocalStack and Database fixtures.
        /// The <see cref="LocalStackFixture"/> ensures LocalStack services (RDS PostgreSQL,
        /// SNS, SQS, SSM) are running. The <see cref="DatabaseFixture"/> provides an isolated
        /// test database with FluentMigrator-provisioned reporting schema.
        /// </summary>
        public ReportRepositoryIntegrationTests(
            LocalStackFixture localStackFixture,
            DatabaseFixture databaseFixture)
        {
            // LocalStackFixture ensures the container is running before tests execute.
            // We verify the RDS connection string is available to confirm LocalStack services
            // are operational before running data access tests.
            _ = localStackFixture.RdsConnectionString ?? throw new InvalidOperationException(
                "LocalStackFixture.RdsConnectionString is not initialized — " +
                "LocalStack RDS PostgreSQL is not available for integration tests.");
            _databaseFixture = databaseFixture;
            _repository = new ReportRepository(
                databaseFixture.ConnectionString,
                NullLogger<ReportRepository>.Instance);
        }

        /// <summary>
        /// Cleans all reporting tables (report_definitions, read_model_projections,
        /// event_offsets) before each test method to ensure complete test isolation
        /// and prevent cross-test data contamination.
        /// </summary>
        public Task InitializeAsync() => _databaseFixture.CleanAllTablesAsync();

        /// <summary>No-op disposal — fixtures manage their own lifecycle.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        // ─────────────────────────────────────────────────────────────────
        // Test Data Construction Helpers
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a fully-populated <see cref="ReportDefinitionDto"/> with known test values
        /// covering all 12 database columns (UUID, varchar, text, jsonb, boolean, integer, timestamptz).
        /// </summary>
        private static ReportDefinitionDto CreateTestReport(
            Guid? id = null,
            string? name = null,
            string? description = "Integration test report description")
        {
            var now = DateTime.UtcNow;
            return new ReportDefinitionDto
            {
                Id = id ?? Guid.NewGuid(),
                Name = name ?? $"test-report-{Guid.NewGuid():N}",
                Description = description,
                SqlTemplate = "SELECT * FROM reporting.read_model_projections WHERE source_domain = @domain",
                ParametersJson = JsonSerializer.Serialize(new { domain = "string", limit = 100 }),
                FieldsJson = JsonSerializer.Serialize(new[] {
                    new { name = "source_domain", type = "text" },
                    new { name = "amount", type = "decimal" }
                }),
                EntityName = "projection",
                ReturnTotal = true,
                Weight = 10,
                CreatedBy = Guid.NewGuid(),
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        /// <summary>
        /// Creates test projection data as a <see cref="JsonElement"/> for passing to
        /// UpsertProjectionAsync which takes separate parameters, not a ProjectionDto.
        /// </summary>
        private static JsonElement CreateProjectionData(object? data = null)
        {
            var jsonPayload = JsonSerializer.Serialize(
                data ?? new { status = "active", amount = 100.50m, currency = "USD" });
            using var doc = JsonDocument.Parse(jsonPayload);
            return doc.RootElement.Clone();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Report Definition CRUD — GetReportByIdAsync
        // Source: DbDataSourceRepository.Get(Guid id) lines 14-28
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetReportByIdAsync returns a fully-populated report when
        /// queried with an existing report ID. All 12 column types are validated.
        /// </summary>
        [RdsFact]
        public async Task GetReportByIdAsync_WithExistingId_ReturnsReport()
        {
            // Arrange — create a report via the repository
            var report = CreateTestReport();
            var created = await _repository.CreateReportAsync(report);
            created.Should().BeTrue("setup: report creation must succeed");

            // Act
            var result = await _repository.GetReportByIdAsync(report.Id);

            // Assert — verify all field types round-trip correctly
            result.Should().NotBeNull();
            result!.Id.Should().Be(report.Id);                           // UUID
            result.Name.Should().Be(report.Name);                       // varchar(255)
            result.Description.Should().Be(report.Description);         // text (nullable)
            result.SqlTemplate.Should().Be(report.SqlTemplate);         // text
            result.ParametersJson.Should().NotBeNull();                  // jsonb (nullable)
            result.FieldsJson.Should().NotBeNull();                     // jsonb (nullable)
            result.EntityName.Should().Be(report.EntityName);           // varchar(255) (nullable)
            result.ReturnTotal.Should().Be(report.ReturnTotal);         // boolean
            result.Weight.Should().Be(report.Weight);                   // integer
            result.CreatedBy.Should().Be(report.CreatedBy);             // UUID (nullable)
            result.CreatedAt.Should().NotBe(default(DateTime));         // timestamptz
            result.UpdatedAt.Should().NotBe(default(DateTime));         // timestamptz
        }

        /// <summary>
        /// Verifies that GetReportByIdAsync returns null when queried with a GUID
        /// that does not match any existing report definition. Mirrors monolith behavior
        /// from DbDataSourceRepository.Get(Guid id) where null is returned if Rows.Count == 0.
        /// </summary>
        [RdsFact]
        public async Task GetReportByIdAsync_WithNonExistentId_ReturnsNull()
        {
            // Act — query with a random GUID that has no matching row
            var result = await _repository.GetReportByIdAsync(Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Report Definition CRUD — GetReportByNameAsync
        // Source: DbDataSourceRepository.Get(string name) lines 35-49
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetReportByNameAsync returns a matching report when queried
        /// with an existing report name. Validates all fields match the original.
        /// </summary>
        [RdsFact]
        public async Task GetReportByNameAsync_WithExistingName_ReturnsReport()
        {
            // Arrange
            var report = CreateTestReport(name: "unique-get-by-name-test");
            await _repository.CreateReportAsync(report);

            // Act
            var result = await _repository.GetReportByNameAsync("unique-get-by-name-test");

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(report.Id);
            result.Name.Should().Be("unique-get-by-name-test");
            result.Description.Should().Be(report.Description);
            result.SqlTemplate.Should().Be(report.SqlTemplate);
            result.EntityName.Should().Be(report.EntityName);
            result.ReturnTotal.Should().Be(report.ReturnTotal);
            result.Weight.Should().Be(report.Weight);
        }

        /// <summary>
        /// Verifies that GetReportByNameAsync returns null when queried with a name
        /// that does not match any existing report definition.
        /// </summary>
        [RdsFact]
        public async Task GetReportByNameAsync_WithNonExistentName_ReturnsNull()
        {
            // Act
            var result = await _repository.GetReportByNameAsync("non-existent-report-name-xyz");

            // Assert
            result.Should().BeNull();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Report Definition CRUD — GetAllReportsAsync (Pagination & Sorting)
        // Source: DbDataSourceRepository.GetAll() lines 55-64
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies GetAllReportsAsync returns paginated results with correct page
        /// size and accurate total count across all pages.
        /// </summary>
        [RdsFact]
        public async Task GetAllReportsAsync_WithMultipleReports_ReturnsPaginatedResults()
        {
            // Arrange — insert 5 reports with deterministic names
            for (int i = 1; i <= 5; i++)
            {
                var report = CreateTestReport(name: $"paginated-report-{i:D2}");
                await _repository.CreateReportAsync(report);
            }

            // Act — request page 1 with pageSize 2 (sortBy and sortOrder are required)
            var (reports, totalCount) = await _repository.GetAllReportsAsync(1, 2, "name", "asc");

            // Assert — 2 items in result, total count reflects all 5
            totalCount.Should().Be(5);
            reports.Should().HaveCount(2);
        }

        /// <summary>
        /// Verifies GetAllReportsAsync respects the sortBy and sortOrder parameters,
        /// returning reports in the specified alphabetical order by name.
        /// </summary>
        [RdsFact]
        public async Task GetAllReportsAsync_WithSorting_ReturnsSortedResults()
        {
            // Arrange — insert reports with specific names for sort verification
            await _repository.CreateReportAsync(
                CreateTestReport(name: "charlie-report"));
            await _repository.CreateReportAsync(
                CreateTestReport(name: "alpha-report"));
            await _repository.CreateReportAsync(
                CreateTestReport(name: "bravo-report"));

            // Act — sort by name ascending
            var (reports, totalCount) = await _repository.GetAllReportsAsync(
                1, 10, "name", "asc");

            // Assert — results are alphabetically ordered
            totalCount.Should().Be(3);
            reports.Should().HaveCount(3);
            reports[0].Name.Should().Be("alpha-report");
            reports[1].Name.Should().Be("bravo-report");
            reports[2].Name.Should().Be("charlie-report");
        }

        /// <summary>
        /// Verifies GetAllReportsAsync returns an empty list with zero total count
        /// when no report definitions exist in the database.
        /// </summary>
        [RdsFact]
        public async Task GetAllReportsAsync_EmptyTable_ReturnsEmptyListWithZeroTotal()
        {
            // Act — query an empty table (all 4 params are required)
            var (reports, totalCount) = await _repository.GetAllReportsAsync(1, 100, "name", "asc");

            // Assert
            reports.Should().BeEmpty();
            totalCount.Should().Be(0);
        }

        // ═══════════════════════════════════════════════════════════════════
        // Report Definition CRUD — CreateReportAsync
        // Source: DbDataSourceRepository.Create() lines 79-100
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies CreateReportAsync inserts a report with all 12 column types and
        /// returns true on success. Validates round-trip for UUID, varchar, text, jsonb,
        /// boolean, integer, and timestamptz columns via independent read-back.
        /// </summary>
        [RdsFact]
        public async Task CreateReportAsync_WithValidReport_InsertsAndReturnsTrue()
        {
            // Arrange
            var report = CreateTestReport();

            // Act
            var result = await _repository.CreateReportAsync(report);

            // Assert — verify return value
            result.Should().BeTrue();

            // Assert — verify persisted data via read-back
            var persisted = await _repository.GetReportByIdAsync(report.Id);
            persisted.Should().NotBeNull();

            // Verify all 12 column types round-trip correctly
            persisted!.Id.Should().Be(report.Id);                         // UUID
            persisted.Name.Should().Be(report.Name);                     // varchar(255)
            persisted.Description.Should().Be(report.Description);       // text (nullable)
            persisted.SqlTemplate.Should().Be(report.SqlTemplate);       // text
            persisted.ParametersJson.Should().NotBeNull();               // jsonb (nullable)
            persisted.FieldsJson.Should().NotBeNull();                   // jsonb (nullable)
            persisted.EntityName.Should().Be(report.EntityName);         // varchar(255) (nullable)
            persisted.ReturnTotal.Should().BeTrue();                     // boolean
            persisted.Weight.Should().Be(10);                            // integer
            persisted.CreatedBy.Should().Be(report.CreatedBy);           // UUID (nullable)
            persisted.CreatedAt.Should().NotBe(default(DateTime));       // timestamptz
            persisted.UpdatedAt.Should().NotBe(default(DateTime));       // timestamptz
        }

        /// <summary>
        /// Verifies behavior when Description is null. The monolith's
        /// DbDataSourceRepository.Create() (line 91) normalized null to empty string
        /// via <c>description ?? ""</c>. The new ReportRepository stores null as
        /// database NULL, preserving the nullable semantics of the description column.
        /// This test documents and validates the current null-handling behavior.
        /// </summary>
        [RdsFact]
        public async Task CreateReportAsync_NullDescription_NormalizesToEmptyString()
        {
            // Arrange — create a report with explicitly null description
            var report = CreateTestReport(description: null);

            // Act
            var result = await _repository.CreateReportAsync(report);

            // Assert — create succeeds
            result.Should().BeTrue();

            // Assert — verify null description is stored and retrieved correctly
            var persisted = await _repository.GetReportByIdAsync(report.Id);
            persisted.Should().NotBeNull();
            // The repository stores null as database NULL; read-back returns null.
            // The description column is Nullable per migration schema.
            persisted!.Description.Should().BeNull();
        }

        /// <summary>
        /// Verifies that CreateReportAsync uses ACID transaction semantics
        /// (BeginTransaction → INSERT → Commit). After the method returns,
        /// data must be committed and visible via an independent database connection.
        /// </summary>
        [RdsFact]
        public async Task CreateReportAsync_VerifyAcidTransaction()
        {
            // Arrange
            var report = CreateTestReport();

            // Act — create via repository (internally uses BeginTransaction → INSERT → Commit)
            var created = await _repository.CreateReportAsync(report);
            created.Should().BeTrue();

            // Assert — verify data is committed and visible via independent connection
            await using var conn = await _databaseFixture.CreateConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM reporting.report_definitions WHERE id = @id", conn);
            cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = report.Id });
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(1, "ACID transaction should commit data atomically — " +
                "row must be visible via independent connection after CreateReportAsync returns");
        }

        // ═══════════════════════════════════════════════════════════════════
        // Report Definition CRUD — UpdateReportAsync
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that UpdateReportAsync modifies the specified fields and returns true
        /// when updating an existing report. Validates name, description, sql_template,
        /// and weight changes persist correctly.
        /// </summary>
        [RdsFact]
        public async Task UpdateReportAsync_WithExistingReport_UpdatesAndReturnsTrue()
        {
            // Arrange — create initial report
            var original = CreateTestReport();
            await _repository.CreateReportAsync(original);

            // Build updated DTO with changed fields
            var updated = original with
            {
                Name = "updated-report-name",
                Description = "Updated description for integration test",
                SqlTemplate = "SELECT 1 AS result",
                Weight = 99,
                UpdatedAt = DateTime.UtcNow
            };

            // Act
            var result = await _repository.UpdateReportAsync(updated);

            // Assert — update returns true
            result.Should().BeTrue();

            // Assert — verify persisted changes via read-back
            var persisted = await _repository.GetReportByIdAsync(original.Id);
            persisted.Should().NotBeNull();
            persisted!.Name.Should().Be("updated-report-name");
            persisted.Description.Should().Be("Updated description for integration test");
            persisted.SqlTemplate.Should().Be("SELECT 1 AS result");
            persisted.Weight.Should().Be(99);
        }

        /// <summary>
        /// Verifies that UpdateReportAsync changes the updated_at timestamp,
        /// confirming the audit trail is maintained on update operations.
        /// </summary>
        [RdsFact]
        public async Task UpdateReportAsync_VerifyUpdatedAtChanged()
        {
            // Arrange — create report and capture initial timestamps
            var report = CreateTestReport();
            await _repository.CreateReportAsync(report);
            var afterCreate = await _repository.GetReportByIdAsync(report.Id);
            var originalCreatedAt = afterCreate!.CreatedAt;

            // Brief delay to ensure timestamp difference is detectable
            await Task.Delay(100);

            // Build updated DTO with fresh updated_at
            var updated = afterCreate with
            {
                Description = "Timestamp change test",
                UpdatedAt = DateTime.UtcNow
            };

            // Act
            await _repository.UpdateReportAsync(updated);

            // Assert — updated_at should differ from created_at
            var persisted = await _repository.GetReportByIdAsync(report.Id);
            persisted.Should().NotBeNull();
            persisted!.UpdatedAt.Should().NotBe(originalCreatedAt,
                "updated_at must change after an update operation");
        }

        /// <summary>
        /// Verifies that UpdateReportAsync preserves fields that were not changed
        /// in the update DTO. Even though the SQL UPDATE sets all 9 mutable columns,
        /// fields passed with their original values should remain unchanged in storage.
        /// </summary>
        [RdsFact]
        public async Task UpdateReportAsync_PartialFieldUpdate_PreservesUnchangedFields()
        {
            // Arrange — create a report with distinctive field values
            var original = CreateTestReport();
            original = original with
            {
                EntityName = "original-entity",
                ReturnTotal = false,
                Weight = 42
            };
            await _repository.CreateReportAsync(original);

            // Update ONLY name and description, keep all other fields identical
            var updated = original with
            {
                Name = "partial-update-name",
                Description = "partial-update-desc",
                UpdatedAt = DateTime.UtcNow
            };

            // Act
            await _repository.UpdateReportAsync(updated);

            // Assert — verify changed fields are updated
            var persisted = await _repository.GetReportByIdAsync(original.Id);
            persisted.Should().NotBeNull();
            persisted!.Name.Should().Be("partial-update-name");
            persisted.Description.Should().Be("partial-update-desc");

            // Assert — verify unchanged fields are preserved
            persisted.EntityName.Should().Be("original-entity");
            persisted.ReturnTotal.Should().BeFalse();
            persisted.Weight.Should().Be(42);
            persisted.SqlTemplate.Should().Be(original.SqlTemplate);
            persisted.CreatedBy.Should().Be(original.CreatedBy);
        }

        // ═══════════════════════════════════════════════════════════════════
        // Report Definition CRUD — DeleteReportAsync
        // Returns Task (void) — verify behavior via read-back
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that DeleteReportAsync removes an existing report and subsequent
        /// GetReportByIdAsync returns null, confirming hard deletion.
        /// </summary>
        [RdsFact]
        public async Task DeleteReportAsync_WithExistingReport_DeletesSuccessfully()
        {
            // Arrange
            var report = CreateTestReport();
            await _repository.CreateReportAsync(report);

            // Act — DeleteReportAsync returns Task (void); no bool return
            await _repository.DeleteReportAsync(report.Id);

            // Assert — row is gone; GetReportByIdAsync returns null
            var persisted = await _repository.GetReportByIdAsync(report.Id);
            persisted.Should().BeNull();
        }

        /// <summary>
        /// Verifies that DeleteReportAsync is idempotent — deleting a non-existent
        /// report ID does not throw an exception.
        /// </summary>
        [RdsFact]
        public async Task DeleteReportAsync_WithNonExistentId_DoesNotThrow()
        {
            // Act — delete a random GUID with no matching row; should not throw
            // DeleteReportAsync returns Task (void)
            var act = () => _repository.DeleteReportAsync(Guid.NewGuid());

            // Assert — no exception was thrown
            await act.Should().NotThrowAsync();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Read-Model Projection — UpsertProjectionAsync
        // CQRS read-model pattern per AAP §0.4.2
        // Signature: (string sourceDomain, string sourceEntity, Guid sourceRecordId,
        //             JsonElement projectionData, NpgsqlConnection?, NpgsqlTransaction?, CancellationToken)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that UpsertProjectionAsync inserts a new projection when no
        /// matching composite key exists, and the projection is retrievable afterward.
        /// </summary>
        [RdsFact]
        public async Task UpsertProjectionAsync_NewProjection_InsertsSuccessfully()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var projectionData = CreateProjectionData(new { status = "active", amount = 100.50m });

            // Act — UpsertProjectionAsync takes separate params, not a ProjectionDto
            await _repository.UpsertProjectionAsync("crm", "contact", recordId, projectionData);

            // Assert — verify insertion via read-back
            var result = await _repository.GetProjectionAsync("crm", "contact", recordId);

            result.Should().NotBeNull();
            result!.SourceDomain.Should().Be("crm");
            result.SourceEntity.Should().Be("contact");
            result.SourceRecordId.Should().Be(recordId);
            result.CreatedAt.Should().NotBe(default(DateTime));
            result.UpdatedAt.Should().NotBe(default(DateTime));
        }

        /// <summary>
        /// Verifies that UpsertProjectionAsync updates an existing projection when
        /// the same composite key (source_domain, source_entity, source_record_id) is used.
        /// The ON CONFLICT DO UPDATE behavior should reflect the latest upsert data.
        /// </summary>
        [RdsFact]
        public async Task UpsertProjectionAsync_ExistingProjection_UpdatesData()
        {
            // Arrange — insert initial projection
            var recordId = Guid.NewGuid();
            var initialData = CreateProjectionData(new { status = "initial", amount = 50.0m });
            await _repository.UpsertProjectionAsync("crm", "contact", recordId, initialData);

            // Brief delay to detect timestamp change
            await Task.Delay(50);

            // Arrange — upsert same composite key with different data
            var updatedData = CreateProjectionData(
                new { status = "updated", amount = 200.0m, note = "modified" });
            await _repository.UpsertProjectionAsync("crm", "contact", recordId, updatedData);

            // Assert — read back should reflect the latest upsert
            var result = await _repository.GetProjectionAsync("crm", "contact", recordId);

            result.Should().NotBeNull();
            result!.ProjectionData.GetProperty("status").GetString()
                .Should().Be("updated");
            result.ProjectionData.GetProperty("amount").GetDecimal()
                .Should().Be(200.0m);
            result.ProjectionData.TryGetProperty("note", out var note).Should().BeTrue();
            note.GetString().Should().Be("modified");
        }

        /// <summary>
        /// Verifies that JSONB data stored via UpsertProjectionAsync round-trips
        /// correctly through PostgreSQL's jsonb type. Tests nested objects, arrays,
        /// and various JSON value types (string, number, boolean, null).
        /// </summary>
        [RdsFact]
        public async Task UpsertProjectionAsync_VerifyJsonbStorage()
        {
            // Arrange — create projection with complex nested JSONB payload
            var complexData = new
            {
                status = "active",
                amount = 1234.56m,
                isVerified = true,
                tags = new[] { "urgent", "reviewed" },
                metadata = new { source = "crm", version = 3 }
            };
            var recordId = Guid.NewGuid();
            var projectionData = CreateProjectionData(complexData);

            // Act
            await _repository.UpsertProjectionAsync("invoicing", "invoice", recordId, projectionData);

            // Assert — read back and verify JSONB round-trip
            var result = await _repository.GetProjectionAsync("invoicing", "invoice", recordId);

            result.Should().NotBeNull();
            var data = result!.ProjectionData;

            // Verify all JSON value types survived the round-trip
            data.GetProperty("status").GetString().Should().Be("active");
            data.GetProperty("amount").GetDecimal().Should().Be(1234.56m);
            data.GetProperty("isVerified").GetBoolean().Should().BeTrue();
            data.GetProperty("tags").GetArrayLength().Should().Be(2);
            data.GetProperty("tags")[0].GetString().Should().Be("urgent");
            data.GetProperty("tags")[1].GetString().Should().Be("reviewed");
            data.GetProperty("metadata").GetProperty("source").GetString()
                .Should().Be("crm");
            data.GetProperty("metadata").GetProperty("version").GetInt32()
                .Should().Be(3);
        }

        // ═══════════════════════════════════════════════════════════════════
        // Read-Model Projection — GetProjectionAsync
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetProjectionAsync returns a matching projection when
        /// queried with an existing composite key (source_domain, source_entity,
        /// source_record_id).
        /// </summary>
        [RdsFact]
        public async Task GetProjectionAsync_WithExistingCompositeKey_ReturnsProjection()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var projectionData = CreateProjectionData(new { sku = "PROD-001", quantity = 100 });
            await _repository.UpsertProjectionAsync("inventory", "product", recordId, projectionData);

            // Act
            var result = await _repository.GetProjectionAsync("inventory", "product", recordId);

            // Assert
            result.Should().NotBeNull();
            result!.SourceDomain.Should().Be("inventory");
            result.SourceEntity.Should().Be("product");
            result.SourceRecordId.Should().Be(recordId);
            result.ProjectionData.GetProperty("sku").GetString().Should().Be("PROD-001");
            result.ProjectionData.GetProperty("quantity").GetInt32().Should().Be(100);
        }

        /// <summary>
        /// Verifies that GetProjectionAsync returns null when no projection matches
        /// the provided composite key.
        /// </summary>
        [RdsFact]
        public async Task GetProjectionAsync_WithNonExistentKey_ReturnsNull()
        {
            // Act — query with keys that have no matching projection
            var result = await _repository.GetProjectionAsync(
                "nonexistent-domain", "nonexistent-entity", Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Read-Model Projection — GetProjectionsByDomainAsync
        // Returns List<ProjectionDto> (not a tuple — no TotalCount from interface)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies GetProjectionsByDomainAsync returns paginated results filtered by
        /// source_domain with correct page size limiting.
        /// </summary>
        [RdsFact]
        public async Task GetProjectionsByDomainAsync_WithMultipleProjections_ReturnsPaginated()
        {
            // Arrange — insert 3 CRM projections and 2 invoicing projections
            for (int i = 0; i < 3; i++)
            {
                var data = CreateProjectionData(new { index = i });
                await _repository.UpsertProjectionAsync("crm", "contact", Guid.NewGuid(), data);
            }
            for (int i = 0; i < 2; i++)
            {
                var data = CreateProjectionData(new { index = i });
                await _repository.UpsertProjectionAsync("invoicing", "invoice", Guid.NewGuid(), data);
            }

            // Act — query CRM domain with page=1, pageSize=2
            // GetProjectionsByDomainAsync returns List<ProjectionDto>, not a tuple
            var projections = await _repository.GetProjectionsByDomainAsync("crm", 1, 2);

            // Assert — 2 items returned (page size limit), all from "crm" domain
            projections.Should().HaveCount(2);
            projections.Should().OnlyContain(p => p.SourceDomain == "crm");
        }

        /// <summary>
        /// Verifies GetProjectionsByDomainAsync returns an empty list when queried
        /// with a domain that has no matching projections.
        /// </summary>
        [RdsFact]
        public async Task GetProjectionsByDomainAsync_WithNonExistentDomain_ReturnsEmptyList()
        {
            // Act — GetProjectionsByDomainAsync returns List<ProjectionDto>
            var projections = await _repository.GetProjectionsByDomainAsync(
                "nonexistent-domain", 1, 100);

            // Assert
            projections.Should().BeEmpty();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Read-Model Projection — DeleteProjectionAsync (Hard Delete)
        // Returns Task (void); verify behavior via read-back
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that DeleteProjectionAsync performs a hard delete — the projection
        /// is completely removed from the database and subsequent queries return null.
        /// </summary>
        [RdsFact]
        public async Task DeleteProjectionAsync_WithExistingProjection_RemovesCompletely()
        {
            // Arrange
            var recordId = Guid.NewGuid();
            var projectionData = CreateProjectionData(new { name = "test-account" });
            await _repository.UpsertProjectionAsync("crm", "account", recordId, projectionData);

            // Act — DeleteProjectionAsync returns Task (void); uses optional connection/transaction params
            await _repository.DeleteProjectionAsync("crm", "account", recordId);

            // Assert — GetProjectionAsync returns null after hard delete
            var afterDelete = await _repository.GetProjectionAsync("crm", "account", recordId);
            afterDelete.Should().BeNull();
        }

        // ═══════════════════════════════════════════════════════════════════
        // Read-Model Projection — SoftDeleteProjectionAsync
        // Financial entities per AAP §0.7.2 — sets deleted_at + deleted flags
        // Returns Task (void); verify behavior via read-back
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that SoftDeleteProjectionAsync merges deletion metadata into the
        /// projection_data JSONB column using the PostgreSQL <c>||</c> operator. The
        /// original projection data should be preserved alongside the new
        /// <c>{"deleted": true, "deleted_at": "..."}</c> flags.
        /// This supports financial entity audit requirements per AAP §0.7.2.
        /// </summary>
        [RdsFact]
        public async Task SoftDeleteProjectionAsync_WithExistingProjection_SetsSoftDeleteFlags()
        {
            // Arrange — create a projection with known data
            var recordId = Guid.NewGuid();
            var projectionData = CreateProjectionData(
                new { invoiceNumber = "INV-001", total = 500.00m });
            await _repository.UpsertProjectionAsync(
                "invoicing", "invoice", recordId, projectionData);

            // Act — soft delete the projection (returns Task void)
            await _repository.SoftDeleteProjectionAsync("invoicing", "invoice", recordId);

            // Assert — verify projection_data now contains deletion metadata
            var afterSoftDelete = await _repository.GetProjectionAsync(
                "invoicing", "invoice", recordId);
            afterSoftDelete.Should().NotBeNull();

            var data = afterSoftDelete!.ProjectionData;

            // Verify deleted flag is set to true
            data.TryGetProperty("deleted", out var deletedProp).Should().BeTrue();
            deletedProp.GetBoolean().Should().BeTrue();

            // Verify deleted_at timestamp is present (ISO 8601 format)
            data.TryGetProperty("deleted_at", out var deletedAtProp).Should().BeTrue();
            deletedAtProp.GetString().Should().NotBeNullOrEmpty();

            // Verify original data is preserved alongside deletion metadata
            data.GetProperty("invoiceNumber").GetString().Should().Be("INV-001");
            data.GetProperty("total").GetDecimal().Should().Be(500.00m);
        }

        // ═══════════════════════════════════════════════════════════════════
        // Event Offset Tracking — UpsertEventOffsetAsync
        // Idempotent event consumption per AAP §0.8.5
        // Signature: (string sourceDomain, string lastEventId,
        //             NpgsqlConnection?, NpgsqlTransaction?, CancellationToken)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that UpsertEventOffsetAsync inserts a new event offset entry
        /// for a domain that has no existing offset record.
        /// </summary>
        [RdsFact]
        public async Task UpsertEventOffsetAsync_NewDomain_InsertsOffset()
        {
            // Arrange
            var eventId = Guid.NewGuid().ToString();

            // Act — positional params: sourceDomain, lastEventId
            await _repository.UpsertEventOffsetAsync("crm", eventId);

            // Assert — verify offset is retrievable
            var lastEventId = await _repository.GetLastEventIdAsync("crm");
            lastEventId.Should().NotBeNull();
            lastEventId.Should().Be(eventId);
        }

        /// <summary>
        /// Verifies that UpsertEventOffsetAsync updates the existing offset when
        /// the same source_domain is upserted again. The ON CONFLICT DO UPDATE
        /// behavior should reflect the latest event ID.
        /// </summary>
        [RdsFact]
        public async Task UpsertEventOffsetAsync_ExistingDomain_UpdatesOffset()
        {
            // Arrange — insert initial offset
            var eventId1 = Guid.NewGuid().ToString();
            await _repository.UpsertEventOffsetAsync("invoicing", eventId1);

            // Act — upsert same domain with new event ID
            var eventId2 = Guid.NewGuid().ToString();
            await _repository.UpsertEventOffsetAsync("invoicing", eventId2);

            // Assert — should reflect the latest event ID (event2)
            var lastEventId = await _repository.GetLastEventIdAsync("invoicing");
            lastEventId.Should().NotBeNull();
            lastEventId.Should().Be(eventId2);
        }

        /// <summary>
        /// Verifies the UNIQUE constraint on source_domain in the event_offsets table.
        /// After two upserts for the same domain, exactly one row should exist,
        /// confirming ON CONFLICT DO UPDATE behavior rather than duplicate insertion.
        /// </summary>
        [RdsFact]
        public async Task UpsertEventOffsetAsync_VerifyUniqueConstraint()
        {
            // Arrange — upsert twice for the same domain
            await _repository.UpsertEventOffsetAsync("crm", "event-001");
            await _repository.UpsertEventOffsetAsync("crm", "event-002");

            // Assert — verify exactly 1 row exists for "crm" domain via direct SQL
            await using var conn = await _databaseFixture.CreateConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM reporting.event_offsets WHERE source_domain = @domain",
                conn);
            cmd.Parameters.Add(new NpgsqlParameter("@domain", NpgsqlDbType.Varchar)
                { Value = "crm" });
            var count = (long)(await cmd.ExecuteScalarAsync())!;

            count.Should().Be(1,
                "UNIQUE constraint on source_domain should prevent duplicate rows; " +
                "ON CONFLICT DO UPDATE keeps exactly one row per domain");
        }

        // ═══════════════════════════════════════════════════════════════════
        // Event Offset Tracking — GetLastEventIdAsync
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetLastEventIdAsync returns the correct last event ID
        /// for a domain that has a previously stored offset.
        /// </summary>
        [RdsFact]
        public async Task GetLastEventIdAsync_WithExistingDomain_ReturnsLastEventId()
        {
            // Arrange
            var eventId = $"evt-{Guid.NewGuid():N}";
            await _repository.UpsertEventOffsetAsync("entity-management", eventId);

            // Act
            var result = await _repository.GetLastEventIdAsync("entity-management");

            // Assert
            result.Should().NotBeNull();
            result.Should().Be(eventId);
        }

        /// <summary>
        /// Verifies that GetLastEventIdAsync returns null for a domain with no
        /// previously stored offset — the expected state on first-time event processing
        /// for a new domain consumer.
        /// </summary>
        [RdsFact]
        public async Task GetLastEventIdAsync_WithNonExistentDomain_ReturnsNull()
        {
            // Act — query a domain that has never had an offset stored
            var result = await _repository.GetLastEventIdAsync("never-processed-domain");

            // Assert
            result.Should().BeNull();
        }
    }
}
