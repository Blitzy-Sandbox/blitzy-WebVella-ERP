using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Newtonsoft.Json;

namespace WebVella.Erp.Tests.Mail.Fixtures
{
    /// <summary>
    /// Seeds a test PostgreSQL database with SMTP service records and email records
    /// in various lifecycle states (Pending, Sent, Aborted) for Mail service integration testing.
    /// Uses <see cref="SmtpServiceBuilder"/> and <see cref="EmailBuilder"/> from the
    /// TestDataBuilders to construct entities and inserts them using Npgsql directly
    /// against the test database. Provides cleanup methods for test isolation between test runs.
    /// </summary>
    /// <remarks>
    /// Table names use the <c>rec_</c> prefix matching the monolith's dynamic entity
    /// table naming pattern (AAP 0.2.1: "Dynamic rec_* CRUD"). JSONB columns for
    /// sender, recipients, and attachments match the monolith's JSON-shaped entity
    /// record storage conventions. All seeding operations are idempotent using
    /// <c>INSERT ... ON CONFLICT (id) DO NOTHING</c>.
    ///
    /// Column schemas are derived from the source model properties:
    ///   - rec_smtp_service: 14 columns from SmtpService.cs (id, name, server, port, username,
    ///     password, default_from_name, default_from_email, default_reply_to_email,
    ///     max_retries_count, retry_wait_minutes, is_default, is_enabled, connection_security)
    ///   - rec_email: 17 columns from Email.cs (id, service_id, sender, recipients,
    ///     reply_to_email, subject, content_text, content_html, created_on, sent_on,
    ///     status, priority, server_error, scheduled_on, retries_count, x_search, attachments)
    /// </remarks>
    public static class DatabaseSeeder
    {
        #region Well-Known Test Entity IDs

        /// <summary>
        /// GUID for the default (primary) SMTP service test record.
        /// Configured as is_default=true, is_enabled=true, server=localhost:587, Auto connection security.
        /// </summary>
        public static readonly Guid DefaultSmtpServiceId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        /// <summary>
        /// GUID for the secondary SMTP service test record.
        /// Configured as is_default=false, is_enabled=true, server=smtp.secondary.test:465, SslOnConnect.
        /// </summary>
        public static readonly Guid SecondarySmtpServiceId = new Guid("b2c3d4e5-f6a7-8901-bcde-f12345678901");

        /// <summary>
        /// GUID for the pending (queued) email test record.
        /// Status=Pending (0), Priority=Normal (1), scheduled for future delivery.
        /// </summary>
        public static readonly Guid PendingEmailId = new Guid("c3d4e5f6-a7b8-9012-cdef-123456789012");

        /// <summary>
        /// GUID for the successfully sent email test record.
        /// Status=Sent (1), Priority=Normal (1), sent_on is populated.
        /// </summary>
        public static readonly Guid SentEmailId = new Guid("d4e5f6a7-b8c9-0123-defa-234567890123");

        /// <summary>
        /// GUID for the aborted (permanently failed) email test record.
        /// Status=Aborted (2), Priority=High (2), server_error populated, retries exhausted at 3.
        /// </summary>
        public static readonly Guid AbortedEmailId = new Guid("e5f6a7b8-c9d0-1234-efab-345678901234");

        #endregion

        #region SQL Constants

        /// <summary>
        /// DDL for the rec_smtp_service table matching all 14 SmtpService model properties.
        /// Column types follow the monolith's PostgreSQL mapping conventions:
        ///   - connection_security stored as INTEGER matching MailKit SecureSocketOptions enum
        ///     (None=0, Auto=1, SslOnConnect=2, StartTls=3, StartTlsWhenAvailable=4)
        /// </summary>
        private const string CreateSmtpServiceTableSql = @"
            CREATE TABLE IF NOT EXISTS rec_smtp_service (
                id                    UUID PRIMARY KEY,
                name                  TEXT NOT NULL,
                server                TEXT,
                port                  INTEGER DEFAULT 587,
                username              TEXT,
                password              TEXT,
                default_from_name     TEXT,
                default_from_email    TEXT,
                default_reply_to_email TEXT,
                max_retries_count     INTEGER DEFAULT 3,
                retry_wait_minutes    INTEGER DEFAULT 5,
                is_default            BOOLEAN DEFAULT false,
                is_enabled            BOOLEAN DEFAULT true,
                connection_security   INTEGER DEFAULT 0
            );";

        /// <summary>
        /// DDL for the rec_email table matching all 17 Email model properties.
        /// Column types follow the monolith's PostgreSQL mapping conventions:
        ///   - sender/recipients/attachments are JSONB for JSON-shaped entity storage
        ///   - status maps to EmailStatus enum (Pending=0, Sent=1, Aborted=2)
        ///   - priority maps to EmailPriority enum (Low=0, Normal=1, High=2)
        ///   - service_id has a FK reference to rec_smtp_service(id)
        /// </summary>
        private const string CreateEmailTableSql = @"
            CREATE TABLE IF NOT EXISTS rec_email (
                id              UUID PRIMARY KEY,
                service_id      UUID NOT NULL REFERENCES rec_smtp_service(id),
                sender          TEXT,
                recipients      TEXT,
                reply_to_email  TEXT,
                subject         TEXT,
                content_text    TEXT,
                content_html    TEXT,
                created_on      TIMESTAMP WITH TIME ZONE NOT NULL,
                sent_on         TIMESTAMP WITH TIME ZONE,
                status          INTEGER DEFAULT 0,
                priority        INTEGER DEFAULT 1,
                server_error    TEXT,
                scheduled_on    TIMESTAMP WITH TIME ZONE,
                retries_count   INTEGER DEFAULT 0,
                x_search        TEXT,
                attachments     TEXT
            );";

        /// <summary>
        /// Parameterized INSERT for rec_smtp_service with idempotent ON CONFLICT clause.
        /// Matches all 14 columns of the SmtpService model.
        /// </summary>
        private const string InsertSmtpServiceSql = @"
            INSERT INTO rec_smtp_service (
                id, name, server, port, username, password,
                default_from_name, default_from_email, default_reply_to_email,
                max_retries_count, retry_wait_minutes, is_default, is_enabled, connection_security
            ) VALUES (
                @id, @name, @server, @port, @username, @password,
                @default_from_name, @default_from_email, @default_reply_to_email,
                @max_retries_count, @retry_wait_minutes, @is_default, @is_enabled, @connection_security
            ) ON CONFLICT (id) DO NOTHING;";

        /// <summary>
        /// Parameterized INSERT for rec_email with idempotent ON CONFLICT clause.
        /// Matches all 17 columns of the Email model. JSONB columns use ::jsonb cast
        /// to convert serialized JSON strings into PostgreSQL JSONB storage.
        /// </summary>
        private const string InsertEmailSql = @"
            INSERT INTO rec_email (
                id, service_id, sender, recipients, reply_to_email, subject,
                content_text, content_html, created_on, sent_on, status, priority,
                server_error, scheduled_on, retries_count, x_search, attachments
            ) VALUES (
                @id, @service_id, @sender, @recipients, @reply_to_email, @subject,
                @content_text, @content_html, @created_on, @sent_on, @status, @priority,
                @server_error, @scheduled_on, @retries_count, @x_search, @attachments
            ) ON CONFLICT (id) DO NOTHING;";

        /// <summary>
        /// TRUNCATE statement for both tables with CASCADE to handle FK dependencies.
        /// Efficiently removes all rows for test isolation without generating individual
        /// DELETE triggers or WAL entries.
        /// </summary>
        private const string TruncateAllSql = "TRUNCATE rec_email, rec_smtp_service CASCADE;";

        #endregion

        #region Public Methods

        /// <summary>
        /// Creates the <c>rec_smtp_service</c> and <c>rec_email</c> tables if they do not
        /// already exist. Uses <c>CREATE TABLE IF NOT EXISTS</c> for idempotency.
        /// The SMTP service table is created first because the email table references it
        /// via a foreign key on the <c>service_id</c> column.
        /// </summary>
        /// <param name="connectionString">
        /// PostgreSQL connection string for the test database.
        /// Must not be null or empty.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connectionString"/> is null or whitespace.
        /// </exception>
        public static async Task InitializeSchemaAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString),
                    "Connection string must not be null or empty.");
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Create SMTP service table first (referenced by email FK)
            await using (var cmd = new NpgsqlCommand(CreateSmtpServiceTableSql, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Create email table (has FK to rec_smtp_service)
            await using (var cmd = new NpgsqlCommand(CreateEmailTableSql, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Main seeding entry point. Initializes the schema, then seeds SMTP service records
        /// and email records in dependency order (SMTP services first, then emails that
        /// reference them). All inserts use <c>ON CONFLICT (id) DO NOTHING</c> for
        /// idempotent re-entrant seeding.
        /// </summary>
        /// <param name="connectionString">
        /// PostgreSQL connection string for the test database.
        /// Must not be null or empty.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connectionString"/> is null or whitespace.
        /// </exception>
        public static async Task SeedAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString),
                    "Connection string must not be null or empty.");
            }

            // Ensure tables exist before inserting data
            await InitializeSchemaAsync(connectionString);

            try
            {
                // Seed in FK dependency order: SMTP services first, then emails
                await SeedSmtpServicesAsync(connectionString);
                await SeedEmailsAsync(connectionString);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                // 23505 = unique_violation — data already seeded, safe to ignore.
                // ON CONFLICT DO NOTHING handles most cases, but this catch provides
                // a safety net for race conditions in parallel test execution.
            }
        }

        /// <summary>
        /// Removes all records from both <c>rec_email</c> and <c>rec_smtp_service</c> tables
        /// using <c>TRUNCATE ... CASCADE</c>. This enables test isolation — test classes
        /// can call CleanupAsync before or after each test to ensure a clean state.
        /// </summary>
        /// <param name="connectionString">
        /// PostgreSQL connection string for the test database.
        /// Must not be null or empty.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connectionString"/> is null or whitespace.
        /// </exception>
        public static async Task CleanupAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString),
                    "Connection string must not be null or empty.");
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var cmd = new NpgsqlCommand(TruncateAllSql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Convenience method that performs a full cleanup followed by a fresh seed.
        /// Useful for test classes that modify seeded data and need to reset to a
        /// known good state between tests.
        /// </summary>
        /// <param name="connectionString">
        /// PostgreSQL connection string for the test database.
        /// Must not be null or empty.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connectionString"/> is null or whitespace.
        /// </exception>
        public static async Task ReseedAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString),
                    "Connection string must not be null or empty.");
            }

            await CleanupAsync(connectionString);
            await SeedAsync(connectionString);
        }

        #endregion

        #region Private Seeding Methods

        /// <summary>
        /// Seeds two SMTP service records using <see cref="SmtpServiceBuilder"/>:
        /// a default primary service (localhost:587, Auto security) and a secondary
        /// service (smtp.secondary.test:465, SslOnConnect security).
        /// </summary>
        /// <param name="connectionString">PostgreSQL connection string for the test database.</param>
        private static async Task SeedSmtpServicesAsync(string connectionString)
        {
            // Build default SMTP service — primary service used by most test emails
            var defaultSmtp = new SmtpServiceBuilder()
                .WithId(DefaultSmtpServiceId)
                .WithName("test-smtp")
                .WithServer("localhost")
                .WithPort(587)
                .WithIsDefault(true)
                .WithIsEnabled(true)
                .WithMaxRetriesCount(3)
                .WithRetryWaitMinutes(5)
                .WithDefaultSenderEmail("noreply@test.webvella.com")
                .WithDefaultSenderName("Test Sender")
                .WithConnectionSecurity(1) // SecureSocketOptions.Auto
                .Build();

            // Build secondary SMTP service — alternate service for multi-service tests
            var secondarySmtp = new SmtpServiceBuilder()
                .WithId(SecondarySmtpServiceId)
                .WithName("secondary-smtp")
                .WithServer("smtp.secondary.test")
                .WithPort(465)
                .WithIsDefault(false)
                .WithIsEnabled(true)
                .WithMaxRetriesCount(5)
                .WithRetryWaitMinutes(10)
                .WithConnectionSecurity(2) // SecureSocketOptions.SslOnConnect
                .Build();

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await InsertSmtpServiceAsync(connection, defaultSmtp);
            await InsertSmtpServiceAsync(connection, secondarySmtp);
        }

        /// <summary>
        /// Seeds three email records using <see cref="EmailBuilder"/>, one for each
        /// lifecycle state: Pending (queued for delivery), Sent (successfully delivered),
        /// and Aborted (permanently failed after exhausting retries).
        /// All emails reference the default SMTP service (<see cref="DefaultSmtpServiceId"/>).
        /// </summary>
        /// <param name="connectionString">PostgreSQL connection string for the test database.</param>
        private static async Task SeedEmailsAsync(string connectionString)
        {
            // Build pending email — queued and scheduled for future delivery
            var pendingEmail = new EmailBuilder()
                .WithId(PendingEmailId)
                .WithServiceId(DefaultSmtpServiceId)
                .WithStatus(MailTestConstants.StatusPending)
                .WithPriority(MailTestConstants.PriorityNormal)
                .WithSubject("Test Pending Email")
                .WithSender("Test Sender", "noreply@test.webvella.com")
                .WithCreatedOn(DateTime.UtcNow)
                .WithScheduledOn(DateTime.UtcNow.AddMinutes(10))
                .Build();

            // Build sent email — successfully delivered 30 minutes ago
            var sentEmail = new EmailBuilder()
                .WithId(SentEmailId)
                .WithServiceId(DefaultSmtpServiceId)
                .WithStatus(MailTestConstants.StatusSent)
                .WithPriority(MailTestConstants.PriorityNormal)
                .WithSubject("Test Sent Email")
                .WithSentOn(DateTime.UtcNow.AddMinutes(-30))
                .WithCreatedOn(DateTime.UtcNow.AddHours(-1))
                .Build();

            // Build aborted email — permanently failed after 3 retry attempts
            var abortedEmail = new EmailBuilder()
                .WithId(AbortedEmailId)
                .WithServiceId(DefaultSmtpServiceId)
                .WithStatus(MailTestConstants.StatusAborted)
                .WithPriority(MailTestConstants.PriorityHigh)
                .WithSubject("Test Aborted Email")
                .WithServerError("Connection refused")
                .WithRetriesCount(3)
                .WithCreatedOn(DateTime.UtcNow.AddHours(-2))
                .Build();

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await InsertEmailAsync(connection, pendingEmail);
            await InsertEmailAsync(connection, sentEmail);
            await InsertEmailAsync(connection, abortedEmail);
        }

        #endregion

        #region Private Insert Helpers

        /// <summary>
        /// Inserts a single SMTP service record into the <c>rec_smtp_service</c> table
        /// using parameterized SQL to prevent SQL injection. Uses <c>ON CONFLICT (id)
        /// DO NOTHING</c> for idempotent seeding.
        /// </summary>
        /// <param name="connection">An open NpgsqlConnection to the test database.</param>
        /// <param name="data">
        /// Dictionary from <see cref="SmtpServiceBuilder.Build()"/> with 14 snake_case keys
        /// matching the rec_smtp_service column names.
        /// </param>
        private static async Task InsertSmtpServiceAsync(NpgsqlConnection connection, Dictionary<string, object> data)
        {
            await using var cmd = new NpgsqlCommand(InsertSmtpServiceSql, connection);

            // UUID primary key
            cmd.Parameters.Add(new NpgsqlParameter("id", (Guid)data["id"]));

            // Text columns — values from builder are never null (empty string defaults)
            cmd.Parameters.Add(new NpgsqlParameter("name", (string)data["name"]));
            cmd.Parameters.Add(new NpgsqlParameter("server", GetValueOrDbNull(data["server"])));
            cmd.Parameters.Add(new NpgsqlParameter("username", GetValueOrDbNull(data["username"])));
            cmd.Parameters.Add(new NpgsqlParameter("password", GetValueOrDbNull(data["password"])));
            cmd.Parameters.Add(new NpgsqlParameter("default_from_name", GetValueOrDbNull(data["default_from_name"])));
            cmd.Parameters.Add(new NpgsqlParameter("default_from_email", GetValueOrDbNull(data["default_from_email"])));
            cmd.Parameters.Add(new NpgsqlParameter("default_reply_to_email", GetValueOrDbNull(data["default_reply_to_email"])));

            // Integer columns
            cmd.Parameters.Add(new NpgsqlParameter("port", (int)data["port"]));
            cmd.Parameters.Add(new NpgsqlParameter("max_retries_count", (int)data["max_retries_count"]));
            cmd.Parameters.Add(new NpgsqlParameter("retry_wait_minutes", (int)data["retry_wait_minutes"]));
            cmd.Parameters.Add(new NpgsqlParameter("connection_security", (int)data["connection_security"]));

            // Boolean columns
            cmd.Parameters.Add(new NpgsqlParameter("is_default", (bool)data["is_default"]));
            cmd.Parameters.Add(new NpgsqlParameter("is_enabled", (bool)data["is_enabled"]));

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Inserts a single email record into the <c>rec_email</c> table using
        /// parameterized SQL with JSON serialization for JSONB columns. Uses
        /// <c>ON CONFLICT (id) DO NOTHING</c> for idempotent seeding.
        /// </summary>
        /// <param name="connection">An open NpgsqlConnection to the test database.</param>
        /// <param name="data">
        /// Dictionary from <see cref="EmailBuilder.Build()"/> with 17 snake_case keys
        /// matching the rec_email column names. The sender, recipients, and attachments
        /// values are complex objects that get serialized to JSON for JSONB storage.
        /// </param>
        private static async Task InsertEmailAsync(NpgsqlConnection connection, Dictionary<string, object> data)
        {
            await using var cmd = new NpgsqlCommand(InsertEmailSql, connection);

            // UUID columns
            cmd.Parameters.Add(new NpgsqlParameter("id", (Guid)data["id"]));
            cmd.Parameters.Add(new NpgsqlParameter("service_id", (Guid)data["service_id"]));

            // JSONB columns — serialize complex objects (Dictionary/List) to JSON strings.
            // The ::jsonb cast in the SQL statement converts these strings to PostgreSQL JSONB.
            // Uses Newtonsoft.Json matching the monolith's serialization convention (AAP 0.8.2).
            cmd.Parameters.Add(new NpgsqlParameter("sender", JsonConvert.SerializeObject(data["sender"])));
            cmd.Parameters.Add(new NpgsqlParameter("recipients", JsonConvert.SerializeObject(data["recipients"])));
            cmd.Parameters.Add(new NpgsqlParameter("attachments", JsonConvert.SerializeObject(data["attachments"])));

            // Text columns
            cmd.Parameters.Add(new NpgsqlParameter("reply_to_email", GetValueOrDbNull(data["reply_to_email"])));
            cmd.Parameters.Add(new NpgsqlParameter("subject", GetValueOrDbNull(data["subject"])));
            cmd.Parameters.Add(new NpgsqlParameter("content_text", GetValueOrDbNull(data["content_text"])));
            cmd.Parameters.Add(new NpgsqlParameter("content_html", GetValueOrDbNull(data["content_html"])));
            cmd.Parameters.Add(new NpgsqlParameter("server_error", GetValueOrDbNull(data["server_error"])));
            cmd.Parameters.Add(new NpgsqlParameter("x_search", GetValueOrDbNull(data["x_search"])));

            // Timestamp columns — created_on is NOT NULL, sent_on and scheduled_on are nullable
            cmd.Parameters.Add(new NpgsqlParameter("created_on", (DateTime)data["created_on"]));
            cmd.Parameters.Add(new NpgsqlParameter("sent_on", GetNullableDateTimeOrDbNull(data["sent_on"])));
            cmd.Parameters.Add(new NpgsqlParameter("scheduled_on", GetNullableDateTimeOrDbNull(data["scheduled_on"])));

            // Integer columns — status, priority, retries_count
            cmd.Parameters.Add(new NpgsqlParameter("status", (int)data["status"]));
            cmd.Parameters.Add(new NpgsqlParameter("priority", (int)data["priority"]));
            cmd.Parameters.Add(new NpgsqlParameter("retries_count", (int)data["retries_count"]));

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Converts a boxed value to either itself or <see cref="DBNull.Value"/> if the
        /// value is null. Ensures Npgsql parameters correctly handle nullable text columns.
        /// </summary>
        /// <param name="value">The boxed value from a builder dictionary entry.</param>
        /// <returns>The original value, or <see cref="DBNull.Value"/> if null.</returns>
        private static object GetValueOrDbNull(object value)
        {
            return value ?? DBNull.Value;
        }

        /// <summary>
        /// Handles nullable DateTime values from builder dictionaries. When a boxed
        /// <see cref="Nullable{DateTime}"/> is null, returns <see cref="DBNull.Value"/>
        /// for correct PostgreSQL TIMESTAMP WITH TIME ZONE parameter handling.
        /// When non-null, returns the DateTime value for Npgsql to serialize.
        /// </summary>
        /// <param name="value">
        /// The boxed nullable DateTime from a builder dictionary entry.
        /// Boxing a null Nullable&lt;DateTime&gt; produces a null object reference.
        /// </param>
        /// <returns>The DateTime value, or <see cref="DBNull.Value"/> if null.</returns>
        private static object GetNullableDateTimeOrDbNull(object value)
        {
            if (value == null)
            {
                return DBNull.Value;
            }

            return (DateTime)value;
        }

        #endregion
    }
}
