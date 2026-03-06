// ============================================================================
// MailDbContext.cs — Mail/Notification Microservice EF Core Database Context
// ============================================================================
// Replaces the monolith's ambient DbContext.Current singleton pattern with a
// standard EF Core DI-injected DbContext. Owns an independent PostgreSQL
// database (erp_mail) containing two entities: email and smtp_service.
//
// Source references:
//   - WebVella.Erp/Database/DbContext.cs (ambient singleton — replaced)
//   - WebVella.Erp.Plugins.Mail/MailPlugin.20190215.cs (initial entity creation)
//   - WebVella.Erp.Plugins.Mail/MailPlugin.20190419.cs (sender/recipients added)
//   - WebVella.Erp.Plugins.Mail/MailPlugin.20190529.cs (attachments added)
//   - WebVella.Erp.Plugins.Mail/MailPlugin.20200610.cs (app metadata only)
//   - WebVella.Erp.Plugins.Mail/MailPlugin.20200611.cs (datasource metadata only)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Npgsql;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Mail.Database
{
    // =========================================================================
    // Email POCO Entity
    // =========================================================================
    // Represents a row in the rec_email table. This is the CUMULATIVE final
    // state after all 7 patches have been applied:
    //   - Patch20190215: initial creation (14 fields)
    //   - Patch20190419: deleted recipient_name, sender_name, recipient_email,
    //                    sender_email; added sender, recipients; updated x_search
    //   - Patch20190529: added attachments field
    //   - Patch20200610/20200611: no schema changes
    //
    // Final column count: 17 (id + 16 data fields)
    // =========================================================================

    /// <summary>
    /// Email entity mapped to the rec_email table in the Mail service database.
    /// Preserves all column types, defaults, and constraints from the monolith's
    /// cumulative patch state. Legacy fields (recipient_name, sender_name,
    /// recipient_email, sender_email) are NOT included — they were deleted in
    /// Patch20190419 and replaced by JSON sender/recipients fields.
    /// </summary>
    public class Email
    {
        /// <summary>Primary key — UUID assigned by application code.</summary>
        [JsonProperty("id")]
        public Guid Id { get; set; }

        /// <summary>Email subject line. Text, max 1000 chars, optional.</summary>
        [JsonProperty("subject")]
        public string? Subject { get; set; }

        /// <summary>Plain text body content. Unlimited length, optional.</summary>
        [JsonProperty("content_text")]
        public string? ContentText { get; set; }

        /// <summary>HTML body content. Unlimited length, optional.</summary>
        [JsonProperty("content_html")]
        public string? ContentHtml { get; set; }

        /// <summary>
        /// Timestamp when the email was actually sent. Nullable (null = not yet sent).
        /// Searchable field with index.
        /// </summary>
        [JsonProperty("sent_on")]
        public DateTime? SentOn { get; set; }

        /// <summary>
        /// Timestamp when the email record was created. Required, defaults to now().
        /// Searchable field with index.
        /// </summary>
        [JsonProperty("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>Server error message from last send attempt. Optional.</summary>
        [JsonProperty("server_error")]
        public string? ServerError { get; set; }

        /// <summary>
        /// Number of retry attempts made. Numeric, required, defaults to 0.
        /// </summary>
        [JsonProperty("retries_count")]
        public decimal RetriesCount { get; set; }

        /// <summary>
        /// Reference to the smtp_service entity that should send this email.
        /// UUID, required, searchable with index.
        /// </summary>
        [JsonProperty("service_id")]
        public Guid ServiceId { get; set; }

        /// <summary>
        /// Email priority: "0" = low, "1" = normal (default), "2" = high.
        /// Stored as varchar(200) select field, required with index.
        /// </summary>
        [JsonProperty("priority")]
        public string Priority { get; set; } = "1";

        /// <summary>Reply-to email address override. Optional text field.</summary>
        [JsonProperty("reply_to_email")]
        public string? ReplyToEmail { get; set; }

        /// <summary>
        /// Scheduled send time. Nullable (null = send immediately).
        /// Searchable field with index.
        /// </summary>
        [JsonProperty("scheduled_on")]
        public DateTime? ScheduledOn { get; set; }

        /// <summary>
        /// Email status: "0" = pending (default), "1" = sent, "2" = aborted.
        /// Stored as varchar(200) select field, required.
        /// </summary>
        [JsonProperty("status")]
        public string Status { get; set; } = "0";

        /// <summary>
        /// Full-text search index field. Aggregates searchable content from
        /// other fields for ILIKE queries. Required, defaults to empty string.
        /// </summary>
        [JsonProperty("x_search")]
        public string XSearch { get; set; } = "";

        /// <summary>
        /// JSON-serialized sender information. Required, searchable with index.
        /// Default: "[]" (empty JSON array). Added in Patch20190419 to replace
        /// the legacy sender_name/sender_email separate fields.
        /// </summary>
        [JsonProperty("sender")]
        public string Sender { get; set; } = "[]";

        /// <summary>
        /// JSON-serialized recipients list. Required, searchable with index.
        /// Default: "" (empty string). Added in Patch20190419 to replace the
        /// legacy recipient_name/recipient_email separate fields.
        /// </summary>
        [JsonProperty("recipients")]
        public string Recipients { get; set; } = "";

        /// <summary>
        /// JSON-serialized attachments list. Required, defaults to "[]".
        /// Added in Patch20190529.
        /// </summary>
        [JsonProperty("attachments")]
        public string Attachments { get; set; } = "[]";
    }

    // =========================================================================
    // SmtpServiceEntity POCO Entity
    // =========================================================================
    // Represents a row in the rec_smtp_service table. Created in Patch20190215
    // with no subsequent schema changes.
    //
    // Final column count: 14 (id + 13 data fields)
    // =========================================================================

    /// <summary>
    /// SMTP service configuration entity mapped to the rec_smtp_service table.
    /// Each record represents an SMTP server configuration that can be used to
    /// send emails. Preserves all column types, defaults, and constraints from
    /// the monolith's Patch20190215 definition.
    /// </summary>
    public class SmtpServiceEntity
    {
        /// <summary>Primary key — UUID assigned by application code.</summary>
        [JsonProperty("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Default reply-to email address for this SMTP service. Optional.
        /// varchar(500) matching the monolith's email field type default length.
        /// </summary>
        [JsonProperty("default_reply_to_email")]
        public string? DefaultReplyToEmail { get; set; }

        /// <summary>
        /// Maximum number of send retry attempts before aborting.
        /// Numeric, required, default 3, valid range [0, 10].
        /// </summary>
        [JsonProperty("max_retries_count")]
        public decimal MaxRetriesCount { get; set; }

        /// <summary>
        /// Minutes to wait between retry attempts.
        /// Numeric, required, default 60, valid range [0, 1440].
        /// </summary>
        [JsonProperty("retry_wait_minutes")]
        public decimal RetryWaitMinutes { get; set; }

        /// <summary>
        /// Whether this is the default SMTP service used when no specific
        /// service is specified. Boolean, required, default false.
        /// </summary>
        [JsonProperty("is_default")]
        public bool IsDefault { get; set; }

        /// <summary>
        /// Human-readable name for this SMTP service configuration.
        /// Text, max 100 chars, required, unique, searchable.
        /// Default: "smtp service".
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; } = "smtp service";

        /// <summary>
        /// SMTP server hostname or IP address. Required.
        /// Default: "smtp.domain.com".
        /// </summary>
        [JsonProperty("server")]
        public string Server { get; set; } = "smtp.domain.com";

        /// <summary>
        /// SMTP authentication username. Optional (only if server requires auth).
        /// </summary>
        [JsonProperty("username")]
        public string? Username { get; set; }

        /// <summary>
        /// SMTP authentication password. Optional (only if server requires auth).
        /// </summary>
        [JsonProperty("password")]
        public string? Password { get; set; }

        /// <summary>
        /// SMTP server port number. Numeric, required, default 25.
        /// Valid range: [1, 65535].
        /// </summary>
        [JsonProperty("port")]
        public decimal Port { get; set; }

        /// <summary>
        /// Connection security mode: "0" = None, "1" = Auto (default),
        /// "2" = SslOnConnect, "3" = StartTls, "4" = StartTlsWhenAvailable.
        /// Stored as varchar(200), required.
        /// </summary>
        [JsonProperty("connection_security")]
        public string ConnectionSecurity { get; set; } = "1";

        /// <summary>
        /// Default sender display name. Optional, searchable.
        /// </summary>
        [JsonProperty("default_from_name")]
        public string? DefaultFromName { get; set; }

        /// <summary>
        /// Default sender email address. Required, searchable.
        /// varchar(500), defaults to empty string.
        /// </summary>
        [JsonProperty("default_from_email")]
        public string DefaultFromEmail { get; set; } = "";

        /// <summary>
        /// Whether this SMTP service is currently active. Boolean, required.
        /// Default: true. Searchable.
        /// </summary>
        [JsonProperty("is_enabled")]
        public bool IsEnabled { get; set; } = true;
    }

    // =========================================================================
    // MailDbContext — EF Core DbContext + SharedKernel IDbContext
    // =========================================================================
    // Implements both Microsoft.EntityFrameworkCore.DbContext (for EF Core data
    // access) and WebVella.Erp.SharedKernel.Database.IDbContext (for backward
    // compatibility with SharedKernel's DbRepository and DbConnection patterns).
    //
    // Key architectural differences from monolith DbContext.Current:
    //   - NO static Current singleton (DI-scoped instead)
    //   - NO ConcurrentDictionary context registry
    //   - NO AsyncLocal context ID
    //   - EF Core manages connection lifecycle
    //   - IDbContext interface bridges to SharedKernel helpers
    // =========================================================================

    /// <summary>
    /// EF Core database context for the Mail/Notification microservice.
    /// Manages the rec_email and rec_smtp_service tables in the service's
    /// independent PostgreSQL database. Also implements the SharedKernel
    /// IDbContext interface to support legacy database helper patterns.
    /// </summary>
    public class MailDbContext : DbContext, IDbContext
    {
        // =====================================================================
        // Private fields for IDbContext implementation
        // =====================================================================
        private readonly object _lockObj = new object();
        private readonly Stack<DbConnection> _connectionStack = new Stack<DbConnection>();
        private NpgsqlTransaction? _transaction;

        // =====================================================================
        // Constructor — standard EF Core DI pattern
        // =====================================================================

        /// <summary>
        /// Initializes a new instance of <see cref="MailDbContext"/> with the
        /// specified EF Core options. Configuration is provided via dependency
        /// injection in Program.cs:
        /// <code>
        /// builder.Services.AddDbContext&lt;MailDbContext&gt;(options =&gt;
        ///     options.UseNpgsql(connectionString, npgsql =&gt;
        ///     {
        ///         npgsql.MinBatchSize(1);
        ///         npgsql.CommandTimeout(120);
        ///     }));
        /// </code>
        /// </summary>
        /// <param name="options">EF Core options configured with Npgsql provider.</param>
        public MailDbContext(DbContextOptions<MailDbContext> options) : base(options)
        {
        }

        // =====================================================================
        // DbSets — Entity collections
        // =====================================================================

        /// <summary>Email records in the rec_email table.</summary>
        public DbSet<Email> Emails { get; set; } = null!;

        /// <summary>SMTP service configuration records in the rec_smtp_service table.</summary>
        public DbSet<SmtpServiceEntity> SmtpServices { get; set; } = null!;

        // =====================================================================
        // OnConfiguring — connection resilience guard
        // =====================================================================

        /// <summary>
        /// Validates that the context has been properly configured via DI.
        /// Throws <see cref="InvalidOperationException"/> if no configuration
        /// was provided (i.e., the context was instantiated outside of DI).
        /// </summary>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                throw new InvalidOperationException(
                    "MailDbContext must be configured via dependency injection. " +
                    "Register it in Program.cs using builder.Services.AddDbContext<MailDbContext>().");
            }

            base.OnConfiguring(optionsBuilder);
        }

        // =====================================================================
        // OnModelCreating — Complete schema configuration
        // =====================================================================

        /// <summary>
        /// Configures the database schema for both mail entities using the Fluent
        /// API. All column types, names, defaults, constraints, and indexes are
        /// preserved exactly as they exist in the monolith's cumulative patch state.
        /// Table naming follows the WebVella ERP convention: rec_{entity_name}.
        /// </summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            ConfigureEmailEntity(modelBuilder);
            ConfigureSmtpServiceEntity(modelBuilder);
        }

        // =====================================================================
        // Email table configuration
        // =====================================================================

        /// <summary>
        /// Configures the rec_email table schema. All column types, defaults,
        /// and indexes match the monolith's cumulative state after patches
        /// 20190215, 20190419, and 20190529.
        /// </summary>
        private static void ConfigureEmailEntity(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Email>(entity =>
            {
                // Table name follows WebVella ERP convention: rec_{entityName}
                entity.ToTable("rec_email");

                // Primary key
                entity.HasKey(e => e.Id);

                // ----------------------------------------------------------
                // Column mappings — exact types from monolith schema
                // ----------------------------------------------------------

                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.Subject)
                    .HasColumnName("subject")
                    .HasColumnType("text")
                    .HasMaxLength(1000);

                entity.Property(e => e.ContentText)
                    .HasColumnName("content_text")
                    .HasColumnType("text");

                entity.Property(e => e.ContentHtml)
                    .HasColumnName("content_html")
                    .HasColumnType("text");

                entity.Property(e => e.SentOn)
                    .HasColumnName("sent_on")
                    .HasColumnType("timestamptz");

                entity.Property(e => e.CreatedOn)
                    .HasColumnName("created_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.Property(e => e.ServerError)
                    .HasColumnName("server_error")
                    .HasColumnType("text");

                entity.Property(e => e.RetriesCount)
                    .HasColumnName("retries_count")
                    .HasColumnType("numeric")
                    .IsRequired()
                    .HasDefaultValue(0m);

                entity.Property(e => e.ServiceId)
                    .HasColumnName("service_id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.Priority)
                    .HasColumnName("priority")
                    .HasColumnType("varchar(200)")
                    .IsRequired()
                    .HasDefaultValue("1");

                entity.Property(e => e.ReplyToEmail)
                    .HasColumnName("reply_to_email")
                    .HasColumnType("text");

                entity.Property(e => e.ScheduledOn)
                    .HasColumnName("scheduled_on")
                    .HasColumnType("timestamptz");

                entity.Property(e => e.Status)
                    .HasColumnName("status")
                    .HasColumnType("varchar(200)")
                    .IsRequired()
                    .HasDefaultValue("0");

                entity.Property(e => e.XSearch)
                    .HasColumnName("x_search")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("");

                entity.Property(e => e.Sender)
                    .HasColumnName("sender")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("[]");

                entity.Property(e => e.Recipients)
                    .HasColumnName("recipients")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("");

                entity.Property(e => e.Attachments)
                    .HasColumnName("attachments")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("[]");

                // ----------------------------------------------------------
                // Indexes — matching searchable fields from patch definitions
                // ----------------------------------------------------------

                entity.HasIndex(e => e.SentOn)
                    .HasDatabaseName("idx_rec_email_sent_on");

                entity.HasIndex(e => e.CreatedOn)
                    .HasDatabaseName("idx_rec_email_created_on");

                entity.HasIndex(e => e.ServiceId)
                    .HasDatabaseName("idx_rec_email_service_id");

                entity.HasIndex(e => e.Priority)
                    .HasDatabaseName("idx_rec_email_priority");

                entity.HasIndex(e => e.ScheduledOn)
                    .HasDatabaseName("idx_rec_email_scheduled_on");

                entity.HasIndex(e => e.Sender)
                    .HasDatabaseName("idx_rec_email_sender");

                entity.HasIndex(e => e.Recipients)
                    .HasDatabaseName("idx_rec_email_recipients");

                entity.HasIndex(e => e.XSearch)
                    .HasDatabaseName("idx_rec_email_x_search");
            });
        }

        // =====================================================================
        // SmtpService table configuration
        // =====================================================================

        /// <summary>
        /// Configures the rec_smtp_service table schema. All column types,
        /// defaults, and indexes match the monolith's Patch20190215 definition.
        /// No subsequent patches modified the smtp_service schema.
        /// </summary>
        private static void ConfigureSmtpServiceEntity(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SmtpServiceEntity>(entity =>
            {
                // Table name follows WebVella ERP convention: rec_{entityName}
                entity.ToTable("rec_smtp_service");

                // Primary key
                entity.HasKey(e => e.Id);

                // ----------------------------------------------------------
                // Column mappings — exact types from monolith schema
                // ----------------------------------------------------------

                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.DefaultReplyToEmail)
                    .HasColumnName("default_reply_to_email")
                    .HasColumnType("varchar(500)");

                entity.Property(e => e.MaxRetriesCount)
                    .HasColumnName("max_retries_count")
                    .HasColumnType("numeric")
                    .IsRequired()
                    .HasDefaultValue(3m);

                entity.Property(e => e.RetryWaitMinutes)
                    .HasColumnName("retry_wait_minutes")
                    .HasColumnType("numeric")
                    .IsRequired()
                    .HasDefaultValue(60m);

                entity.Property(e => e.IsDefault)
                    .HasColumnName("is_default")
                    .HasColumnType("boolean")
                    .IsRequired()
                    .HasDefaultValue(false);

                entity.Property(e => e.Name)
                    .HasColumnName("name")
                    .HasColumnType("text")
                    .HasMaxLength(100)
                    .IsRequired();

                entity.Property(e => e.Server)
                    .HasColumnName("server")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("smtp.domain.com");

                entity.Property(e => e.Username)
                    .HasColumnName("username")
                    .HasColumnType("text");

                entity.Property(e => e.Password)
                    .HasColumnName("password")
                    .HasColumnType("text");

                entity.Property(e => e.Port)
                    .HasColumnName("port")
                    .HasColumnType("numeric")
                    .IsRequired()
                    .HasDefaultValue(25m);

                entity.Property(e => e.ConnectionSecurity)
                    .HasColumnName("connection_security")
                    .HasColumnType("varchar(200)")
                    .IsRequired()
                    .HasDefaultValue("1");

                entity.Property(e => e.DefaultFromName)
                    .HasColumnName("default_from_name")
                    .HasColumnType("text");

                entity.Property(e => e.DefaultFromEmail)
                    .HasColumnName("default_from_email")
                    .HasColumnType("varchar(500)")
                    .IsRequired()
                    .HasDefaultValue("");

                entity.Property(e => e.IsEnabled)
                    .HasColumnName("is_enabled")
                    .HasColumnType("boolean")
                    .IsRequired()
                    .HasDefaultValue(true);

                // ----------------------------------------------------------
                // Indexes — unique constraint and searchable fields
                // ----------------------------------------------------------

                // Unique constraint on name (from Patch20190215: name.Unique = true)
                // This also serves as the search index for the name field.
                entity.HasIndex(e => e.Name)
                    .IsUnique()
                    .HasDatabaseName("idx_rec_smtp_service_name_unique");

                // Searchable fields from Patch20190215
                entity.HasIndex(e => e.DefaultFromName)
                    .HasDatabaseName("idx_rec_smtp_service_default_from_name");

                entity.HasIndex(e => e.DefaultFromEmail)
                    .HasDatabaseName("idx_rec_smtp_service_default_from_email");
            });
        }

        // =====================================================================
        // IDbContext Implementation — SharedKernel backward compatibility
        // =====================================================================
        // These methods bridge EF Core's connection management with the
        // SharedKernel's DbConnection/DbRepository ambient connection pattern.
        // This allows SharedKernel database helpers to work with the Mail
        // service's database context transparently.
        // =====================================================================

        /// <summary>
        /// Creates a new SharedKernel <see cref="DbConnection"/> using either the
        /// current shared transaction or a new connection from the EF Core
        /// managed connection string. The connection is pushed onto the internal
        /// connection stack for lifecycle tracking.
        /// </summary>
        /// <returns>A new <see cref="DbConnection"/> wrapper.</returns>
        public DbConnection CreateConnection()
        {
            DbConnection conn;

            if (_transaction != null)
            {
                // Reuse the existing transaction's connection
                conn = new DbConnection(_transaction, this);
            }
            else
            {
                // Create a new connection using the EF Core configured connection string
                var connectionString = Database.GetConnectionString()
                    ?? throw new InvalidOperationException(
                        "Cannot create a DbConnection: the MailDbContext connection string is not configured.");
                conn = new DbConnection(connectionString, this);
            }

            _connectionStack.Push(conn);

            Debug.WriteLine(
                $"MailDbContext CreateConnection | Stack count: {_connectionStack.Count} | Hash: {conn.GetHashCode()}");

            return conn;
        }

        /// <summary>
        /// Closes a SharedKernel <see cref="DbConnection"/> and removes it from
        /// the connection stack. Validates that the connection being closed is
        /// the one at the top of the stack (LIFO order enforcement).
        /// </summary>
        /// <param name="conn">The connection to close.</param>
        /// <returns>True if the connection stack is now empty (all connections closed).</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if <paramref name="conn"/> is not the top of the connection stack.
        /// </exception>
        public bool CloseConnection(DbConnection conn)
        {
            lock (_lockObj)
            {
                if (_connectionStack.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Cannot close connection: the connection stack is empty.");
                }

                var topConn = _connectionStack.Peek();
                if (topConn != conn)
                {
                    throw new InvalidOperationException(
                        "You are trying to close a connection before closing inner connections.");
                }

                _connectionStack.Pop();

                Debug.WriteLine(
                    $"MailDbContext CloseConnection | Stack count: {_connectionStack.Count} | Hash: {conn.GetHashCode()}");

                return _connectionStack.Count == 0;
            }
        }

        /// <summary>
        /// Enters transactional state by storing a reference to the shared
        /// <see cref="NpgsqlTransaction"/>. All subsequent calls to
        /// <see cref="CreateConnection"/> will reuse this transaction's connection.
        /// </summary>
        /// <param name="transaction">The NpgsqlTransaction to share across connections.</param>
        public void EnterTransactionalState(NpgsqlTransaction transaction)
        {
            _transaction = transaction;
        }

        /// <summary>
        /// Leaves transactional state by clearing the shared transaction reference.
        /// Subsequent calls to <see cref="CreateConnection"/> will create new
        /// independent connections.
        /// </summary>
        public void LeaveTransactionalState()
        {
            _transaction = null;
        }

        // =====================================================================
        // Dispose — cleanup IDbContext resources
        // =====================================================================

        /// <summary>
        /// Disposes the EF Core DbContext and cleans up IDbContext resources.
        /// If there is an active transaction when disposing, it indicates a
        /// programming error (transaction should have been committed or rolled
        /// back before disposal). Overrides <see cref="DbContext.Dispose()"/>
        /// to also clean up the IDbContext connection stack and transaction state.
        /// </summary>
        public override void Dispose()
        {
            if (_transaction != null)
            {
                Debug.WriteLine(
                    "MailDbContext Dispose: WARNING — disposing with active transaction. " +
                    "The transaction should have been committed or rolled back.");
                _transaction = null;
            }

            _connectionStack.Clear();

            base.Dispose();
        }
    }
}
