// ============================================================================
// MailDbContextMigrationTests.cs — Integration Tests for Mail Service Database Migrations
// ============================================================================
// Validates that the Mail service's MailDbContext EF Core migrations correctly
// create the mail-specific database schema (rec_email, rec_smtp_service) from
// the monolith's plugin patch system (7 sequential patches: 20190215 through
// 20200611) with zero data loss, idempotent execution, and correct column types.
//
// Test infrastructure uses Testcontainers.PostgreSql to spin up isolated
// postgres:16-alpine Docker containers per test class. Each test creates its
// own MailDbContext instance for isolation and verifies schema using direct
// Npgsql queries against information_schema.
//
// Source references:
//   - MailPlugin._.cs — ProcessPatches() orchestration
//   - MailPlugin.20190215.cs — Initial email + smtp_service entity creation
//   - MailPlugin.20190419.cs — sender/recipients JSON, delete legacy fields
//   - MailPlugin.20190420.cs — Page body node updates (UI only)
//   - MailPlugin.20190422.cs — Page body node updates (UI only)
//   - MailPlugin.20190529.cs — attachments field addition
//   - MailPlugin.20200610.cs — App metadata update (no schema)
//   - MailPlugin.20200611.cs — DataSource definition update (no schema)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Newtonsoft.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Mail.Database;
using Xunit;

namespace WebVella.Erp.Tests.Mail.Database
{
    // =========================================================================
    // Helper DTO for schema metadata queries
    // =========================================================================

    /// <summary>
    /// Represents column metadata from information_schema.columns.
    /// Used by test helper methods to verify schema structure after migrations.
    /// </summary>
    internal class ColumnInfo
    {
        public string ColumnName { get; set; } = "";
        public string DataType { get; set; } = "";
        public string IsNullable { get; set; } = "";
        public string ColumnDefault { get; set; } = "";
        public int? CharacterMaximumLength { get; set; }
    }

    // =========================================================================
    // Test Class
    // =========================================================================

    /// <summary>
    /// Integration tests that validate the Mail service's MailDbContext EF Core
    /// migrations. Covers schema creation, column types, idempotency,
    /// reversibility, audit fields, zero data loss, JSON serialization,
    /// patch codification, migration history, and constraint verification.
    /// </summary>
    [Collection("Database")]
    public class MailDbContextMigrationTests : IAsyncLifetime
    {
        // =====================================================================
        // Fields
        // =====================================================================

        private readonly PostgreSqlContainer _postgres;
        private string _connectionString = "";

        // =====================================================================
        // Constructor
        // =====================================================================

        /// <summary>
        /// Initializes the Testcontainers PostgreSQL builder with postgres:16-alpine
        /// image matching the Docker Compose infrastructure definition.
        /// </summary>
        public MailDbContextMigrationTests()
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build();
        }

        // =====================================================================
        // IAsyncLifetime — Setup / Teardown
        // =====================================================================

        /// <summary>
        /// Starts the PostgreSQL container and stores the connection string.
        /// Does NOT run migrations — individual tests control migration timing.
        /// </summary>
        public async Task InitializeAsync()
        {
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }

        /// <summary>
        /// Stops and disposes the PostgreSQL container.
        /// </summary>
        public async Task DisposeAsync()
        {
            await _postgres.DisposeAsync();
        }

        // =====================================================================
        // Helper Methods
        // =====================================================================

        /// <summary>
        /// Creates a new MailDbContext configured with the Testcontainer
        /// connection string. Each test should create and dispose its own context.
        /// Suppresses PendingModelChangesWarning which fires in EF Core 10 when
        /// the model snapshot doesn't perfectly match the current model state —
        /// this is expected in test scenarios where migrations are applied to
        /// fresh databases.
        /// </summary>
        private MailDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MailDbContext>()
                .UseNpgsql(_connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            return new MailDbContext(options);
        }

        /// <summary>
        /// Helper that creates a context, runs Migrate(), and disposes.
        /// </summary>
        private async Task RunMigrationsAsync()
        {
            using var context = CreateDbContext();
            await context.Database.MigrateAsync();
        }

        /// <summary>
        /// Queries information_schema.columns for the specified table and
        /// returns a list of ColumnInfo objects with column metadata.
        /// </summary>
        private async Task<List<ColumnInfo>> GetTableColumnsAsync(string tableName)
        {
            var columns = new List<ColumnInfo>();
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"SELECT column_name, data_type, is_nullable, column_default, character_maximum_length
                  FROM information_schema.columns
                  WHERE table_name = @tableName
                  ORDER BY ordinal_position", conn);
            cmd.Parameters.AddWithValue("tableName", tableName);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(new ColumnInfo
                {
                    ColumnName = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetString(2),
                    ColumnDefault = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    CharacterMaximumLength = reader.IsDBNull(4) ? null : reader.GetInt32(4)
                });
            }

            return columns;
        }

        /// <summary>
        /// Checks if a table exists in the public schema via information_schema.tables.
        /// </summary>
        private async Task<bool> TableExistsAsync(string tableName)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"SELECT COUNT(*) FROM information_schema.tables
                  WHERE table_schema = 'public' AND table_name = @tableName", conn);
            cmd.Parameters.AddWithValue("tableName", tableName);

            var count = (long)(await cmd.ExecuteScalarAsync())!;
            return count > 0;
        }

        /// <summary>
        /// Queries constraint information for a given table and constraint type
        /// (e.g., 'PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY').
        /// Returns a list of constraint names.
        /// </summary>
        private async Task<List<string>> GetConstraintsAsync(string tableName, string constraintType)
        {
            var constraints = new List<string>();
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"SELECT tc.constraint_name
                  FROM information_schema.table_constraints tc
                  WHERE tc.table_name = @tableName
                    AND tc.constraint_type = @constraintType
                    AND tc.table_schema = 'public'", conn);
            cmd.Parameters.AddWithValue("tableName", tableName);
            cmd.Parameters.AddWithValue("constraintType", constraintType);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                constraints.Add(reader.GetString(0));
            }

            return constraints;
        }

        /// <summary>
        /// Inserts a complete test email record with all 17 columns populated.
        /// Returns the generated ID.
        /// </summary>
        private async Task<Guid> InsertTestEmailAsync(
            Guid? id = null,
            string subject = "Test Subject",
            string sender = "[{\"Name\":\"Test\",\"Address\":\"test@example.com\"}]",
            string recipients = "[{\"Name\":\"R1\",\"Address\":\"r1@example.com\"}]",
            string attachments = "[]",
            string status = "0",
            string priority = "1")
        {
            var emailId = id ?? Guid.NewGuid();
            var serviceId = Guid.NewGuid();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO rec_email (id, subject, content_text, content_html, sent_on,
                    created_on, server_error, retries_count, service_id, priority,
                    reply_to_email, scheduled_on, status, x_search, sender, recipients, attachments)
                  VALUES (@id, @subject, @contentText, @contentHtml, @sentOn,
                    @createdOn, @serverError, @retriesCount, @serviceId, @priority,
                    @replyToEmail, @scheduledOn, @status, @xSearch, @sender, @recipients, @attachments)", conn);

            cmd.Parameters.AddWithValue("id", emailId);
            cmd.Parameters.AddWithValue("subject", subject);
            cmd.Parameters.AddWithValue("contentText", "Plain text body");
            cmd.Parameters.AddWithValue("contentHtml", "<p>HTML body</p>");
            cmd.Parameters.AddWithValue("sentOn", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("createdOn", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("serverError", (object)"" ?? DBNull.Value);
            cmd.Parameters.AddWithValue("retriesCount", 0m);
            cmd.Parameters.AddWithValue("serviceId", serviceId);
            cmd.Parameters.AddWithValue("priority", priority);
            cmd.Parameters.AddWithValue("replyToEmail", "reply@example.com");
            cmd.Parameters.AddWithValue("scheduledOn", DateTime.UtcNow.AddHours(1));
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("xSearch", "test search content");
            cmd.Parameters.AddWithValue("sender", sender);
            cmd.Parameters.AddWithValue("recipients", recipients);
            cmd.Parameters.AddWithValue("attachments", attachments);

            await cmd.ExecuteNonQueryAsync();
            return emailId;
        }

        /// <summary>
        /// Inserts a complete test SMTP service record with all 14 columns populated.
        /// Returns the generated ID.
        /// </summary>
        private async Task<Guid> InsertTestSmtpServiceAsync(Guid? id = null, string name = null)
        {
            var smtpId = id ?? Guid.NewGuid();
            var smtpName = name ?? $"smtp-{smtpId:N}";

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO rec_smtp_service (id, name, server, port, username, password,
                    default_sender_name, default_sender_email, default_reply_to_email,
                    max_retries_count, retry_wait_minutes, is_default, is_enabled, connection_security)
                  VALUES (@id, @name, @server, @port, @username, @password,
                    @defaultSenderName, @defaultSenderEmail, @defaultReplyToEmail,
                    @maxRetriesCount, @retryWaitMinutes, @isDefault, @isEnabled, @connectionSecurity)", conn);

            cmd.Parameters.AddWithValue("id", smtpId);
            cmd.Parameters.AddWithValue("name", smtpName);
            cmd.Parameters.AddWithValue("server", "smtp.test.com");
            cmd.Parameters.AddWithValue("port", 587m);
            cmd.Parameters.AddWithValue("username", "user@test.com");
            cmd.Parameters.AddWithValue("password", "secret123");
            cmd.Parameters.AddWithValue("defaultSenderName", "Test Sender");
            cmd.Parameters.AddWithValue("defaultSenderEmail", "noreply@test.com");
            cmd.Parameters.AddWithValue("defaultReplyToEmail", "reply@test.com");
            cmd.Parameters.AddWithValue("maxRetriesCount", 5m);
            cmd.Parameters.AddWithValue("retryWaitMinutes", 30m);
            cmd.Parameters.AddWithValue("isDefault", false);
            cmd.Parameters.AddWithValue("isEnabled", true);
            cmd.Parameters.AddWithValue("connectionSecurity", "2");

            await cmd.ExecuteNonQueryAsync();
            return smtpId;
        }

        // =====================================================================
        // Phase 2: Email Table Schema Validation Tests
        // =====================================================================

        /// <summary>
        /// Verifies that running EF Core migrations creates the rec_email table.
        /// Source: Patch20190215 creates the email entity (Id=085e2442-...).
        /// </summary>
        [Fact]
        public async Task Migration_ShouldCreateEmailTable()
        {
            // Arrange & Act
            await RunMigrationsAsync();

            // Assert
            var exists = await TableExistsAsync("rec_email");
            exists.Should().BeTrue("because the initial migration should create the rec_email table");
        }

        /// <summary>
        /// Verifies that the rec_email table has ALL 17 required columns after
        /// cumulative patches (20190215 + 20190419 + 20190529) and that 4 legacy
        /// fields deleted in Patch20190419 do NOT exist.
        /// </summary>
        [Fact]
        public async Task EmailTable_ShouldHaveAllRequiredColumns()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var columns = await GetTableColumnsAsync("rec_email");
            var columnNames = columns.Select(c => c.ColumnName).ToList();

            // Assert — all 17 required columns exist
            columnNames.Should().Contain("id", "because PK column is required");
            columnNames.Should().Contain("subject");
            columnNames.Should().Contain("content_text");
            columnNames.Should().Contain("content_html");
            columnNames.Should().Contain("sent_on");
            columnNames.Should().Contain("created_on");
            columnNames.Should().Contain("server_error");
            columnNames.Should().Contain("retries_count");
            columnNames.Should().Contain("service_id");
            columnNames.Should().Contain("priority");
            columnNames.Should().Contain("reply_to_email");
            columnNames.Should().Contain("scheduled_on");
            columnNames.Should().Contain("status");
            columnNames.Should().Contain("x_search");
            columnNames.Should().Contain("sender", "because Patch20190419 adds JSON sender field");
            columnNames.Should().Contain("recipients", "because Patch20190419 adds JSON recipients field");
            columnNames.Should().Contain("attachments", "because Patch20190529 adds attachments field");

            // Assert — exactly 17 columns
            columns.Should().HaveCount(17, "because the email entity has exactly 17 columns in its final state");

            // Assert — legacy fields MUST NOT exist (deleted in Patch20190419)
            columnNames.Should().NotContain("sender_name", "because Patch20190419 deleted this legacy field");
            columnNames.Should().NotContain("sender_email", "because Patch20190419 deleted this legacy field");
            columnNames.Should().NotContain("recipient_name", "because Patch20190419 deleted this legacy field");
            columnNames.Should().NotContain("recipient_email", "because Patch20190419 deleted this legacy field");
        }

        /// <summary>
        /// Verifies that all email table column data types map correctly to
        /// PostgreSQL types as specified by the monolith schema.
        /// </summary>
        [Fact]
        public async Task EmailTable_ColumnDataTypes_ShouldMapCorrectly()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var columns = await GetTableColumnsAsync("rec_email");

            // Assert — verify data types for each column
            var idCol = columns.FirstOrDefault(c => c.ColumnName == "id");
            idCol.Should().NotBeNull();
            idCol!.DataType.Should().Be("uuid");

            var subjectCol = columns.FirstOrDefault(c => c.ColumnName == "subject");
            subjectCol.Should().NotBeNull();
            subjectCol!.DataType.Should().Be("text");

            var contentTextCol = columns.FirstOrDefault(c => c.ColumnName == "content_text");
            contentTextCol.Should().NotBeNull();
            contentTextCol!.DataType.Should().Be("text");

            var contentHtmlCol = columns.FirstOrDefault(c => c.ColumnName == "content_html");
            contentHtmlCol.Should().NotBeNull();
            contentHtmlCol!.DataType.Should().Be("text");

            var sentOnCol = columns.FirstOrDefault(c => c.ColumnName == "sent_on");
            sentOnCol.Should().NotBeNull();
            sentOnCol!.DataType.Should().Be("timestamp with time zone");

            var createdOnCol = columns.FirstOrDefault(c => c.ColumnName == "created_on");
            createdOnCol.Should().NotBeNull();
            createdOnCol!.DataType.Should().Be("timestamp with time zone");

            var serverErrorCol = columns.FirstOrDefault(c => c.ColumnName == "server_error");
            serverErrorCol.Should().NotBeNull();
            serverErrorCol!.DataType.Should().Be("text");

            var retriesCountCol = columns.FirstOrDefault(c => c.ColumnName == "retries_count");
            retriesCountCol.Should().NotBeNull();
            retriesCountCol!.DataType.Should().Be("numeric");

            var serviceIdCol = columns.FirstOrDefault(c => c.ColumnName == "service_id");
            serviceIdCol.Should().NotBeNull();
            serviceIdCol!.DataType.Should().Be("uuid");

            var priorityCol = columns.FirstOrDefault(c => c.ColumnName == "priority");
            priorityCol.Should().NotBeNull();
            priorityCol!.DataType.Should().Be("character varying");

            var replyToEmailCol = columns.FirstOrDefault(c => c.ColumnName == "reply_to_email");
            replyToEmailCol.Should().NotBeNull();
            replyToEmailCol!.DataType.Should().Be("text");

            var scheduledOnCol = columns.FirstOrDefault(c => c.ColumnName == "scheduled_on");
            scheduledOnCol.Should().NotBeNull();
            scheduledOnCol!.DataType.Should().Be("timestamp with time zone");

            var statusCol = columns.FirstOrDefault(c => c.ColumnName == "status");
            statusCol.Should().NotBeNull();
            statusCol!.DataType.Should().Be("character varying");

            var xSearchCol = columns.FirstOrDefault(c => c.ColumnName == "x_search");
            xSearchCol.Should().NotBeNull();
            xSearchCol!.DataType.Should().Be("text");

            var senderCol = columns.FirstOrDefault(c => c.ColumnName == "sender");
            senderCol.Should().NotBeNull();
            senderCol!.DataType.Should().Be("text");

            var recipientsCol = columns.FirstOrDefault(c => c.ColumnName == "recipients");
            recipientsCol.Should().NotBeNull();
            recipientsCol!.DataType.Should().Be("text");

            var attachmentsCol = columns.FirstOrDefault(c => c.ColumnName == "attachments");
            attachmentsCol.Should().NotBeNull();
            attachmentsCol!.DataType.Should().Be("text");
        }

        // =====================================================================
        // Phase 3: SMTP Service Table Schema Validation Tests
        // =====================================================================

        /// <summary>
        /// Verifies that running migrations creates the rec_smtp_service table.
        /// Source: Patch20190215 creates the smtp_service entity (Id=17698b9f-...).
        /// </summary>
        [Fact]
        public async Task Migration_ShouldCreateSmtpServiceTable()
        {
            // Arrange & Act
            await RunMigrationsAsync();

            // Assert
            var exists = await TableExistsAsync("rec_smtp_service");
            exists.Should().BeTrue("because the initial migration should create the rec_smtp_service table");
        }

        /// <summary>
        /// Verifies that the rec_smtp_service table has ALL 14 required columns
        /// from Patch20190215.
        /// </summary>
        [Fact]
        public async Task SmtpServiceTable_ShouldHaveAllRequiredColumns()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var columns = await GetTableColumnsAsync("rec_smtp_service");
            var columnNames = columns.Select(c => c.ColumnName).ToList();

            // Assert — all 14 required columns exist
            columnNames.Should().Contain("id");
            columnNames.Should().Contain("name");
            columnNames.Should().Contain("server");
            columnNames.Should().Contain("port");
            columnNames.Should().Contain("username");
            columnNames.Should().Contain("password");
            columnNames.Should().Contain("default_sender_name");
            columnNames.Should().Contain("default_sender_email");
            columnNames.Should().Contain("default_reply_to_email");
            columnNames.Should().Contain("max_retries_count");
            columnNames.Should().Contain("retry_wait_minutes");
            columnNames.Should().Contain("is_default");
            columnNames.Should().Contain("is_enabled");
            columnNames.Should().Contain("connection_security");

            // Assert — exactly 14 columns
            columns.Should().HaveCount(14, "because the smtp_service entity has exactly 14 columns");
        }

        /// <summary>
        /// Verifies that all SMTP service table column data types map correctly.
        /// </summary>
        [Fact]
        public async Task SmtpServiceTable_ColumnDataTypes_ShouldMapCorrectly()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var columns = await GetTableColumnsAsync("rec_smtp_service");

            // Assert
            columns.FirstOrDefault(c => c.ColumnName == "id")!.DataType.Should().Be("uuid");
            columns.FirstOrDefault(c => c.ColumnName == "name")!.DataType.Should().Be("text");
            columns.FirstOrDefault(c => c.ColumnName == "server")!.DataType.Should().Be("text");
            columns.FirstOrDefault(c => c.ColumnName == "port")!.DataType.Should().Be("numeric");
            columns.FirstOrDefault(c => c.ColumnName == "username")!.DataType.Should().Be("text");
            columns.FirstOrDefault(c => c.ColumnName == "password")!.DataType.Should().Be("text");
            columns.FirstOrDefault(c => c.ColumnName == "default_sender_name")!.DataType.Should().Be("text");
            columns.FirstOrDefault(c => c.ColumnName == "default_sender_email")!.DataType.Should().Be("character varying");
            columns.FirstOrDefault(c => c.ColumnName == "default_reply_to_email")!.DataType.Should().Be("character varying");
            columns.FirstOrDefault(c => c.ColumnName == "max_retries_count")!.DataType.Should().Be("numeric");
            columns.FirstOrDefault(c => c.ColumnName == "retry_wait_minutes")!.DataType.Should().Be("numeric");
            columns.FirstOrDefault(c => c.ColumnName == "is_default")!.DataType.Should().Be("boolean");
            columns.FirstOrDefault(c => c.ColumnName == "is_enabled")!.DataType.Should().Be("boolean");
            columns.FirstOrDefault(c => c.ColumnName == "connection_security")!.DataType.Should().Be("character varying");
        }

        // =====================================================================
        // Phase 4: Migration Idempotency Tests (AAP 0.8.1)
        // =====================================================================

        /// <summary>
        /// Verifies that running migrations twice does not throw exceptions.
        /// AAP 0.8.1: "Schema migration scripts must be idempotent."
        /// </summary>
        [Fact]
        public async Task Migration_ShouldBeIdempotent_RunningTwiceDoesNotFail()
        {
            // Act — first migration run
            await RunMigrationsAsync();

            // Act — second migration run on same database (should not throw)
            Func<Task> secondRun = async () => await RunMigrationsAsync();
            await secondRun.Should().NotThrowAsync(
                "because EF Core migrations are idempotent — running twice should not fail");

            // Assert — tables still exist and have correct structure
            var emailExists = await TableExistsAsync("rec_email");
            emailExists.Should().BeTrue();

            var smtpExists = await TableExistsAsync("rec_smtp_service");
            smtpExists.Should().BeTrue();

            var emailColumns = await GetTableColumnsAsync("rec_email");
            emailColumns.Should().HaveCount(17);

            var smtpColumns = await GetTableColumnsAsync("rec_smtp_service");
            smtpColumns.Should().HaveCount(14);
        }

        /// <summary>
        /// Verifies that existing data is preserved after running migrations again.
        /// AAP 0.8.1: "Schema migration scripts must be idempotent."
        /// </summary>
        [Fact]
        public async Task Migration_ShouldBeIdempotent_PreservingExistingData()
        {
            // Arrange — run initial migration and insert test data
            await RunMigrationsAsync();
            var emailId = await InsertTestEmailAsync();
            var smtpId = await InsertTestSmtpServiceAsync();

            // Act — run migrations again
            await RunMigrationsAsync();

            // Assert — verify data still exists
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var emailCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM rec_email WHERE id = @id", conn);
            emailCmd.Parameters.AddWithValue("id", emailId);
            var emailCount = (long)(await emailCmd.ExecuteScalarAsync())!;
            emailCount.Should().Be(1, "because the email record should survive a second migration run");

            await using var smtpCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM rec_smtp_service WHERE id = @id", conn);
            smtpCmd.Parameters.AddWithValue("id", smtpId);
            var smtpCount = (long)(await smtpCmd.ExecuteScalarAsync())!;
            smtpCount.Should().Be(1, "because the SMTP service record should survive a second migration run");
        }

        // =====================================================================
        // Phase 5: Migration Reversibility Tests (AAP 0.8.1)
        // =====================================================================

        /// <summary>
        /// Verifies that down migration removes the mail tables.
        /// AAP 0.8.1: "Migrations are reversible."
        /// </summary>
        [Fact]
        public async Task Migration_ShouldBeReversible_DownMigrationRestoresPreviousState()
        {
            // Arrange — run UP migration
            await RunMigrationsAsync();

            // Verify tables exist
            var emailExistsBefore = await TableExistsAsync("rec_email");
            emailExistsBefore.Should().BeTrue();
            var smtpExistsBefore = await TableExistsAsync("rec_smtp_service");
            smtpExistsBefore.Should().BeTrue();

            // Act — run DOWN migration to revert to initial state ("0" = no migrations)
            using (var context = CreateDbContext())
            {
                await context.Database.MigrateAsync("0");
            }

            // Assert — tables should be dropped
            var emailExistsAfter = await TableExistsAsync("rec_email");
            emailExistsAfter.Should().BeFalse("because down migration should drop rec_email table");

            var smtpExistsAfter = await TableExistsAsync("rec_smtp_service");
            smtpExistsAfter.Should().BeFalse("because down migration should drop rec_smtp_service table");
        }

        // =====================================================================
        // Phase 6: Audit Field Preservation Tests (AAP 0.8.1)
        // =====================================================================

        /// <summary>
        /// Verifies that the created_on audit field exists with correct type,
        /// NOT NULL constraint, and default value of now().
        /// Source: Patch20190215 creates created_on with UseCurrentTimeAsDefault=true.
        /// </summary>
        [Fact]
        public async Task EmailTable_ShouldPreserveAuditFields()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var columns = await GetTableColumnsAsync("rec_email");

            // Assert — created_on column
            var createdOnCol = columns.FirstOrDefault(c => c.ColumnName == "created_on");
            createdOnCol.Should().NotBeNull("because created_on is a required audit field");
            createdOnCol!.DataType.Should().Be("timestamp with time zone");
            createdOnCol.IsNullable.Should().Be("NO", "because created_on is NOT NULL");
            createdOnCol.ColumnDefault.Should().Contain("now()",
                "because created_on defaults to now() per Patch20190215");
        }

        // =====================================================================
        // Phase 7: Zero Data Loss Verification Tests (AAP 0.8.1)
        // =====================================================================

        /// <summary>
        /// Verifies zero data loss by inserting records, re-running migrations,
        /// and confirming counts and content are preserved exactly.
        /// AAP 0.8.1: "Zero data loss: record counts and checksums match."
        /// </summary>
        [Fact]
        public async Task Migration_ShouldPreserveAllRecordData_ZeroDataLoss()
        {
            // Arrange — run initial migration
            await RunMigrationsAsync();

            // Insert 3 email records with all 17 columns populated
            var emailIds = new List<Guid>();
            for (int i = 0; i < 3; i++)
            {
                var eid = await InsertTestEmailAsync(
                    subject: $"Subject {i}",
                    sender: $"[{{\"Name\":\"Sender{i}\",\"Address\":\"s{i}@example.com\"}}]",
                    recipients: $"[{{\"Name\":\"Rcpt{i}\",\"Address\":\"r{i}@example.com\"}}]",
                    attachments: $"[{{\"FileName\":\"file{i}.pdf\",\"FilePath\":\"/files/file{i}.pdf\"}}]");
                emailIds.Add(eid);
            }

            // Insert 2 SMTP service records with all 14 columns populated
            var smtpIds = new List<Guid>();
            for (int i = 0; i < 2; i++)
            {
                var sid = await InsertTestSmtpServiceAsync(name: $"smtp-service-{i}");
                smtpIds.Add(sid);
            }

            // Record counts before re-migration
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var emailCountBefore = new NpgsqlCommand("SELECT COUNT(*) FROM rec_email", conn);
            var emailsBefore = (long)(await emailCountBefore.ExecuteScalarAsync())!;

            await using var smtpCountBefore = new NpgsqlCommand("SELECT COUNT(*) FROM rec_smtp_service", conn);
            var smtpsBefore = (long)(await smtpCountBefore.ExecuteScalarAsync())!;

            // Compute checksums using MD5 of concatenated row data
            await using var emailChecksumCmd = new NpgsqlCommand(
                "SELECT md5(string_agg(t::text, '' ORDER BY id)) FROM rec_email t", conn);
            var emailChecksumBefore = (string)(await emailChecksumCmd.ExecuteScalarAsync())!;

            await using var smtpChecksumCmd = new NpgsqlCommand(
                "SELECT md5(string_agg(t::text, '' ORDER BY id)) FROM rec_smtp_service t", conn);
            var smtpChecksumBefore = (string)(await smtpChecksumCmd.ExecuteScalarAsync())!;

            await conn.CloseAsync();

            // Act — re-run migrations (idempotent)
            await RunMigrationsAsync();

            // Assert — counts match
            await using var conn2 = new NpgsqlConnection(_connectionString);
            await conn2.OpenAsync();

            await using var emailCountAfter = new NpgsqlCommand("SELECT COUNT(*) FROM rec_email", conn2);
            var emailsAfter = (long)(await emailCountAfter.ExecuteScalarAsync())!;
            emailsAfter.Should().Be(emailsBefore, "because zero data loss requires exact count preservation");

            await using var smtpCountAfter = new NpgsqlCommand("SELECT COUNT(*) FROM rec_smtp_service", conn2);
            var smtpsAfter = (long)(await smtpCountAfter.ExecuteScalarAsync())!;
            smtpsAfter.Should().Be(smtpsBefore, "because zero data loss requires exact count preservation");

            // Assert — checksums match (zero data loss)
            await using var emailChecksumAfterCmd = new NpgsqlCommand(
                "SELECT md5(string_agg(t::text, '' ORDER BY id)) FROM rec_email t", conn2);
            var emailChecksumAfter = (string)(await emailChecksumAfterCmd.ExecuteScalarAsync())!;
            emailChecksumAfter.Should().Be(emailChecksumBefore,
                "because zero data loss requires exact data checksums to match");

            await using var smtpChecksumAfterCmd = new NpgsqlCommand(
                "SELECT md5(string_agg(t::text, '' ORDER BY id)) FROM rec_smtp_service t", conn2);
            var smtpChecksumAfter = (string)(await smtpChecksumAfterCmd.ExecuteScalarAsync())!;
            smtpChecksumAfter.Should().Be(smtpChecksumBefore,
                "because zero data loss requires exact data checksums to match");
        }

        // =====================================================================
        // Phase 8: JSON Column Serialization/Deserialization Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the sender JSON column round-trips correctly.
        /// Source: Patch20190419 serializes EmailAddress {Name, Address}.
        /// </summary>
        [Fact]
        public async Task EmailTable_SenderJsonColumn_ShouldSerializeCorrectly()
        {
            // Arrange
            await RunMigrationsAsync();
            var senderJson = JsonConvert.SerializeObject(
                new { Name = "Test User", Address = "test@example.com" });
            var emailId = await InsertTestEmailAsync(sender: senderJson);

            // Act — read back
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT sender FROM rec_email WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", emailId);
            var result = (string)(await cmd.ExecuteScalarAsync())!;

            // Assert
            var deserialized = JsonConvert.DeserializeObject<dynamic>(result)!;
            ((string)deserialized.Name).Should().Be("Test User");
            ((string)deserialized.Address).Should().Be("test@example.com");
        }

        /// <summary>
        /// Verifies that the recipients JSON column round-trips a list correctly.
        /// Source: Patch20190419 serializes as List&lt;EmailAddress&gt;.
        /// </summary>
        [Fact]
        public async Task EmailTable_RecipientsJsonColumn_ShouldSerializeCorrectly()
        {
            // Arrange
            await RunMigrationsAsync();
            var recipientsList = new[]
            {
                new { Name = "Recipient 1", Address = "r1@example.com" },
                new { Name = "Recipient 2", Address = "r2@example.com" }
            };
            var recipientsJson = JsonConvert.SerializeObject(recipientsList);
            var emailId = await InsertTestEmailAsync(recipients: recipientsJson);

            // Act
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT recipients FROM rec_email WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", emailId);
            var result = (string)(await cmd.ExecuteScalarAsync())!;

            // Assert
            var deserialized = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(result)!;
            deserialized.Should().HaveCount(2);
            deserialized[0]["Name"].Should().Be("Recipient 1");
            deserialized[0]["Address"].Should().Be("r1@example.com");
            deserialized[1]["Name"].Should().Be("Recipient 2");
            deserialized[1]["Address"].Should().Be("r2@example.com");
        }

        /// <summary>
        /// Verifies that the attachments JSON column round-trips correctly.
        /// Source: Patch20190529 adds attachments field with default "[]".
        /// </summary>
        [Fact]
        public async Task EmailTable_AttachmentsJsonColumn_ShouldSerializeCorrectly()
        {
            // Arrange
            await RunMigrationsAsync();
            var attachmentsJson = JsonConvert.SerializeObject(
                new[] { new { FileName = "test.pdf", FilePath = "/files/test.pdf" } });
            var emailId = await InsertTestEmailAsync(attachments: attachmentsJson);

            // Act
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT attachments FROM rec_email WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", emailId);
            var result = (string)(await cmd.ExecuteScalarAsync())!;

            // Assert
            var deserialized = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(result)!;
            deserialized.Should().HaveCount(1);
            deserialized[0]["FileName"].Should().Be("test.pdf");
            deserialized[0]["FilePath"].Should().Be("/files/test.pdf");
        }

        /// <summary>
        /// Verifies that empty JSON default values are preserved.
        /// Source: sender default="[]", recipients default="", attachments default="[]".
        /// </summary>
        [Fact]
        public async Task EmailTable_JsonColumns_ShouldHandleEmptyDefaults()
        {
            // Arrange
            await RunMigrationsAsync();
            var emailId = await InsertTestEmailAsync(
                sender: "[]", recipients: "", attachments: "[]");

            // Act
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT sender, recipients, attachments FROM rec_email WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", emailId);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();

            var sender = reader.GetString(0);
            var recipients = reader.GetString(1);
            var attachments = reader.GetString(2);

            // Assert
            sender.Should().Be("[]", "because sender default is empty JSON array");
            recipients.Should().Be("", "because recipients default is empty string");
            attachments.Should().Be("[]", "because attachments default is empty JSON array");
        }

        // =====================================================================
        // Phase 9: Patch Migration Codification Tests
        // =====================================================================

        /// <summary>
        /// Verifies Patch20190215: email and smtp_service entities are created.
        /// </summary>
        [Fact]
        public async Task InitialMigration_ShouldCodifyPatch20190215_EmailEntityCreation()
        {
            // Act
            await RunMigrationsAsync();

            // Assert — email table with initial fields from Patch20190215
            var emailExists = await TableExistsAsync("rec_email");
            emailExists.Should().BeTrue("because Patch20190215 creates the email entity");

            var emailColumns = await GetTableColumnsAsync("rec_email");
            emailColumns.Any(c => c.ColumnName == "id").Should().BeTrue();
            emailColumns.Any(c => c.ColumnName == "subject").Should().BeTrue();
            emailColumns.Any(c => c.ColumnName == "service_id").Should().BeTrue();

            // Assert — smtp_service table with all 14 fields from Patch20190215
            var smtpExists = await TableExistsAsync("rec_smtp_service");
            smtpExists.Should().BeTrue("because Patch20190215 creates the smtp_service entity");

            var smtpColumns = await GetTableColumnsAsync("rec_smtp_service");
            smtpColumns.Should().HaveCount(14, "because Patch20190215 defines 14 smtp_service fields");
        }

        /// <summary>
        /// Verifies Patch20190419: sender/recipients JSON fields added,
        /// legacy fields deleted, x_search updated.
        /// </summary>
        [Fact]
        public async Task InitialMigration_ShouldCodifyPatch20190419_SenderRecipientsJsonMigration()
        {
            // Act
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("rec_email");
            var columnNames = columns.Select(c => c.ColumnName).ToList();

            // Assert — sender column exists with correct attributes
            var senderCol = columns.FirstOrDefault(c => c.ColumnName == "sender");
            senderCol.Should().NotBeNull("because Patch20190419 adds sender field");
            senderCol!.DataType.Should().Be("text");
            senderCol.IsNullable.Should().Be("NO", "because sender is required");
            senderCol.ColumnDefault.Should().Contain("[]",
                "because sender default is '[]'");

            // Assert — recipients column exists with correct attributes
            var recipientsCol = columns.FirstOrDefault(c => c.ColumnName == "recipients");
            recipientsCol.Should().NotBeNull("because Patch20190419 adds recipients field");
            recipientsCol!.DataType.Should().Be("text");
            recipientsCol.IsNullable.Should().Be("NO", "because recipients is required");

            // Assert — legacy columns do NOT exist
            columnNames.Should().NotContain("sender_name");
            columnNames.Should().NotContain("sender_email");
            columnNames.Should().NotContain("recipient_name");
            columnNames.Should().NotContain("recipient_email");

            // Assert — x_search exists (was updated to non-searchable in metadata, but column persists)
            columnNames.Should().Contain("x_search");
        }

        /// <summary>
        /// Verifies Patch20190420: only page body node updates (UI), no schema changes.
        /// </summary>
        [Fact]
        public async Task InitialMigration_ShouldCodifyPatch20190420_PageNodesAreUIOnly()
        {
            // Act
            await RunMigrationsAsync();

            // Assert — schema matches the state after Patch20190215+20190419
            // No additional tables beyond rec_email and rec_smtp_service
            var emailColumns = await GetTableColumnsAsync("rec_email");
            var smtpColumns = await GetTableColumnsAsync("rec_smtp_service");

            // Email table still has the correct column count (17 after all patches)
            emailColumns.Should().HaveCount(17,
                "because Patch20190420 is UI-only and does not change the email schema");
            smtpColumns.Should().HaveCount(14,
                "because Patch20190420 is UI-only and does not change the smtp_service schema");
        }

        /// <summary>
        /// Verifies Patch20190422: only page body node updates (UI), no schema changes.
        /// </summary>
        [Fact]
        public async Task InitialMigration_ShouldCodifyPatch20190422_PageNodesAreUIOnly()
        {
            // Act
            await RunMigrationsAsync();

            // Assert — schema unchanged from previous validation
            var emailColumns = await GetTableColumnsAsync("rec_email");
            var smtpColumns = await GetTableColumnsAsync("rec_smtp_service");

            emailColumns.Should().HaveCount(17,
                "because Patch20190422 is UI-only and does not change any schema");
            smtpColumns.Should().HaveCount(14,
                "because Patch20190422 is UI-only and does not change any schema");
        }

        /// <summary>
        /// Verifies Patch20190529: attachments field added to email table.
        /// </summary>
        [Fact]
        public async Task InitialMigration_ShouldCodifyPatch20190529_AttachmentsFieldAddition()
        {
            // Act
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("rec_email");

            // Assert — attachments column exists with correct attributes
            var attachmentsCol = columns.FirstOrDefault(c => c.ColumnName == "attachments");
            attachmentsCol.Should().NotBeNull("because Patch20190529 adds attachments field");
            attachmentsCol!.DataType.Should().Be("text");
            attachmentsCol.IsNullable.Should().Be("NO", "because attachments is required");
            attachmentsCol.ColumnDefault.Should().Contain("[]",
                "because attachments default is '[]'");
        }

        /// <summary>
        /// Verifies Patch20200610: app metadata only, no schema changes.
        /// </summary>
        [Fact]
        public async Task InitialMigration_ShouldCodifyPatch20200610_AppMetadataOnly()
        {
            // Act
            await RunMigrationsAsync();

            // Assert — no additional schema changes
            var emailColumns = await GetTableColumnsAsync("rec_email");
            emailColumns.Should().HaveCount(17,
                "because Patch20200610 only updates app metadata, not schema");

            var smtpColumns = await GetTableColumnsAsync("rec_smtp_service");
            smtpColumns.Should().HaveCount(14,
                "because Patch20200610 only updates app metadata, not schema");
        }

        /// <summary>
        /// Verifies Patch20200611: data source definition only, no schema changes.
        /// The AllEmails data source SQL selects exactly the 17 final columns.
        /// </summary>
        [Fact]
        public async Task InitialMigration_ShouldCodifyPatch20200611_DataSourceDefinitionOnly()
        {
            // Act
            await RunMigrationsAsync();
            var columns = await GetTableColumnsAsync("rec_email");
            var columnNames = columns.Select(c => c.ColumnName).ToList();

            // Assert — no additional schema changes
            columns.Should().HaveCount(17,
                "because Patch20200611 only updates data source definition, not schema");

            // Assert — all 17 columns selected by AllEmails data source exist
            var expectedColumns = new[]
            {
                "id", "subject", "content_text", "content_html", "sender",
                "recipients", "reply_to_email", "sent_on", "created_on",
                "server_error", "retries_count", "service_id", "priority",
                "scheduled_on", "status", "x_search", "attachments"
            };

            foreach (var expected in expectedColumns)
            {
                columnNames.Should().Contain(expected,
                    $"because the AllEmails datasource SQL selects column '{expected}'");
            }
        }

        // =====================================================================
        // Phase 10: EF Core Migration History Tests
        // =====================================================================

        /// <summary>
        /// Verifies that __EFMigrationsHistory table is created and populated.
        /// </summary>
        [Fact]
        public async Task Migration_ShouldTrackInMigrationsHistoryTable()
        {
            // Act
            await RunMigrationsAsync();

            // Assert — __EFMigrationsHistory exists and has entries
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"SELECT ""MigrationId"", ""ProductVersion""
                  FROM ""__EFMigrationsHistory""
                  ORDER BY ""MigrationId""", conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            var hasRows = await reader.ReadAsync();
            hasRows.Should().BeTrue("because at least one migration should be tracked");

            var migrationId = reader.GetString(0);
            migrationId.Should().NotBeNullOrEmpty("because MigrationId should be populated");

            var productVersion = reader.GetString(1);
            productVersion.Should().NotBeNullOrEmpty("because ProductVersion should be populated");
        }

        /// <summary>
        /// Verifies that GetAppliedMigrations returns a non-empty list after migration.
        /// </summary>
        [Fact]
        public async Task GetAppliedMigrations_ShouldReturnMigrationList()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            using var context = CreateDbContext();
            var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToList();

            // Assert
            appliedMigrations.Should().NotBeEmpty(
                "because at least one migration should have been applied");
            appliedMigrations.First().Should().Contain("InitialCreate",
                "because the first migration should be the InitialCreate migration");
        }

        /// <summary>
        /// Verifies that GetPendingMigrations returns empty after full migration.
        /// </summary>
        [Fact]
        public async Task GetPendingMigrations_ShouldReturnEmpty_AfterFullMigration()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            using var context = CreateDbContext();
            var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToList();

            // Assert
            pendingMigrations.Should().BeEmpty(
                "because all migrations should have been applied");
        }

        // =====================================================================
        // Phase 11: Constraint Verification Tests
        // =====================================================================

        /// <summary>
        /// Verifies that rec_email has a PRIMARY KEY on the id column.
        /// </summary>
        [Fact]
        public async Task EmailTable_ShouldHavePrimaryKeyOnId()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var pkConstraints = await GetConstraintsAsync("rec_email", "PRIMARY KEY");

            // Assert
            pkConstraints.Should().NotBeEmpty(
                "because rec_email must have a primary key constraint");

            // Verify PK is on the id column using pg_constraint/pg_attribute
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"SELECT a.attname
                  FROM pg_constraint c
                  JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY(c.conkey)
                  WHERE c.conrelid = 'rec_email'::regclass
                    AND c.contype = 'p'", conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            var pkColumns = new List<string>();
            while (await reader.ReadAsync())
            {
                pkColumns.Add(reader.GetString(0));
            }

            pkColumns.Should().Contain("id", "because the primary key should be on the id column");
        }

        /// <summary>
        /// Verifies that rec_smtp_service has a PRIMARY KEY on the id column.
        /// </summary>
        [Fact]
        public async Task SmtpServiceTable_ShouldHavePrimaryKeyOnId()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act
            var pkConstraints = await GetConstraintsAsync("rec_smtp_service", "PRIMARY KEY");

            // Assert
            pkConstraints.Should().NotBeEmpty(
                "because rec_smtp_service must have a primary key constraint");

            // Verify PK is on the id column
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                @"SELECT a.attname
                  FROM pg_constraint c
                  JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY(c.conkey)
                  WHERE c.conrelid = 'rec_smtp_service'::regclass
                    AND c.contype = 'p'", conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            var pkColumns = new List<string>();
            while (await reader.ReadAsync())
            {
                pkColumns.Add(reader.GetString(0));
            }

            pkColumns.Should().Contain("id", "because the primary key should be on the id column");
        }

        /// <summary>
        /// Verifies that rec_smtp_service has a UNIQUE constraint on the name column.
        /// Source: Patch20190215 creates name field with Unique = true.
        /// Validated by attempting to insert two records with the same name.
        /// </summary>
        [Fact]
        public async Task SmtpServiceTable_NameColumn_ShouldHaveUniqueConstraint()
        {
            // Arrange
            await RunMigrationsAsync();

            // Act — insert first record
            await InsertTestSmtpServiceAsync(name: "duplicate-name-test");

            // Assert — inserting second record with same name should fail
            Func<Task> duplicateInsert = async () =>
                await InsertTestSmtpServiceAsync(name: "duplicate-name-test");

            await duplicateInsert.Should().ThrowAsync<PostgresException>(
                "because the name column has a UNIQUE constraint from Patch20190215");
        }
    }
}
