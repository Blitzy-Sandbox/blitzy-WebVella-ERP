using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Admin.Database
{
	#region Entity Models

	/// <summary>
	/// EF Core entity model mapped to the PostgreSQL <c>system_log</c> table.
	/// Preserves the exact column layout from the monolith's <c>WebVella.Erp.Diagnostics.Log</c> class.
	/// Columns: id (uuid), created_on (timestamp), type (int), source (text), message (text),
	/// details (text, nullable), notification_status (int).
	/// </summary>
	[Table("system_log")]
	public class SystemLogEntry
	{
		/// <summary>Primary key — UUID identifier for the log entry.</summary>
		[Column("id")]
		public Guid Id { get; set; }

		/// <summary>Timestamp when the log entry was created (UTC).</summary>
		[Column("created_on")]
		public DateTime CreatedOn { get; set; }

		/// <summary>Log type indicator (matches monolith LogType enum: Error=1, Info=2).</summary>
		[Column("type")]
		public int Type { get; set; }

		/// <summary>Source identifier for the log entry (e.g., class name or module name). Not null, defaults to empty string.</summary>
		[Column("source")]
		public string Source { get; set; }

		/// <summary>Human-readable log message. Not null, defaults to empty string.</summary>
		[Column("message")]
		public string Message { get; set; }

		/// <summary>Optional JSON-encoded details (stack traces, request info, inner exceptions). Nullable.</summary>
		[Column("details")]
		public string Details { get; set; }

		/// <summary>Notification dispatch state (matches monolith LogNotificationStatus enum: DoNotNotify=1, NotNotified=2, Notified=3, NotificationFailed=4).</summary>
		[Column("notification_status")]
		public int NotificationStatus { get; set; }
	}

	/// <summary>
	/// EF Core entity model mapped to the PostgreSQL <c>jobs</c> table.
	/// Preserves the exact column layout from the monolith's <c>WebVella.Erp.Jobs.Job</c> model
	/// and <c>JobDataService</c> persistence layer.
	/// Columns store Newtonsoft.Json serialized data in <c>attributes</c> and <c>result</c> text fields.
	/// </summary>
	[Table("jobs")]
	public class JobEntry
	{
		/// <summary>Primary key — UUID identifier for the job.</summary>
		[Column("id")]
		public Guid Id { get; set; }

		/// <summary>UUID of the registered job type (maps to the [Job] attribute Id).</summary>
		[Column("type_id")]
		public Guid TypeId { get; set; }

		/// <summary>Human-readable name of the job type.</summary>
		[Column("type_name")]
		public string TypeName { get; set; }

		/// <summary>Fully qualified class name of the job implementation (assembly-qualified).</summary>
		[Column("complete_class_name")]
		public string CompleteClassName { get; set; }

		/// <summary>Newtonsoft.Json serialized job attributes (TypeNameHandling.All). Nullable.</summary>
		[Column("attributes")]
		public string Attributes { get; set; }

		/// <summary>Job execution status (matches monolith JobStatus enum: Pending=1, Running=2, Canceled=3, Failed=4, Finished=5, Aborted=6).</summary>
		[Column("status")]
		public int Status { get; set; }

		/// <summary>Job scheduling priority (matches monolith JobPriority enum values). Higher = executed sooner.</summary>
		[Column("priority")]
		public int Priority { get; set; }

		/// <summary>Timestamp when the job started execution. Null if not yet started.</summary>
		[Column("started_on")]
		public DateTime? StartedOn { get; set; }

		/// <summary>Timestamp when the job finished execution. Null if not yet finished.</summary>
		[Column("finished_on")]
		public DateTime? FinishedOn { get; set; }

		/// <summary>UUID of the user who aborted the job. Null if not aborted.</summary>
		[Column("aborted_by")]
		public Guid? AbortedBy { get; set; }

		/// <summary>UUID of the user who canceled the job. Null if not canceled.</summary>
		[Column("canceled_by")]
		public Guid? CanceledBy { get; set; }

		/// <summary>Error message captured on job failure. Nullable.</summary>
		[Column("error_message")]
		public string ErrorMessage { get; set; }

		/// <summary>UUID of the schedule plan that triggered this job. Null for manually created jobs.</summary>
		[Column("schedule_plan_id")]
		public Guid? SchedulePlanId { get; set; }

		/// <summary>Timestamp when the job record was created (UTC).</summary>
		[Column("created_on")]
		public DateTime CreatedOn { get; set; }

		/// <summary>UUID of the user who created the job. Null for system-created jobs.</summary>
		[Column("created_by")]
		public Guid? CreatedBy { get; set; }

		/// <summary>Timestamp of the last modification to this job record (UTC).</summary>
		[Column("last_modified_on")]
		public DateTime LastModifiedOn { get; set; }

		/// <summary>UUID of the user who last modified the job. Null for system modifications.</summary>
		[Column("last_modified_by")]
		public Guid? LastModifiedBy { get; set; }

		/// <summary>Newtonsoft.Json serialized job result (TypeNameHandling.All, wrapped in JobResultWrapper). Nullable.</summary>
		[Column("result")]
		public string Result { get; set; }
	}

	/// <summary>
	/// EF Core entity model mapped to the PostgreSQL <c>schedule_plans</c> table.
	/// Preserves the exact column layout from the monolith's <c>WebVella.Erp.Jobs.SchedulePlan</c> model
	/// and <c>JobDataService</c> persistence layer.
	/// The <c>schedule_days</c> column stores a JSON-serialized <c>SchedulePlanDaysOfWeek</c> object.
	/// The <c>job_attributes</c> column stores Newtonsoft.Json serialized dynamic attributes.
	/// </summary>
	[Table("schedule_plans")]
	public class SchedulePlanEntry
	{
		/// <summary>Primary key — UUID identifier for the schedule plan.</summary>
		[Column("id")]
		public Guid Id { get; set; }

		/// <summary>Human-readable name of the schedule plan.</summary>
		[Column("name")]
		public string Name { get; set; }

		/// <summary>Schedule plan type (matches monolith SchedulePlanType enum: Interval=1, Daily=2, Weekly=3, Monthly=4).</summary>
		[Column("type")]
		public int Type { get; set; }

		/// <summary>Start date for the schedule plan validity window. Null if no start constraint.</summary>
		[Column("start_date")]
		public DateTime? StartDate { get; set; }

		/// <summary>End date for the schedule plan validity window. Null if no end constraint.</summary>
		[Column("end_date")]
		public DateTime? EndDate { get; set; }

		/// <summary>JSON-serialized day-of-week scheduling flags (SchedulePlanDaysOfWeek). Nullable for non-weekly plans.</summary>
		[Column("schedule_days")]
		public string ScheduledDays { get; set; }

		/// <summary>Interval in minutes between executions (for Interval-type plans). Null for other types.</summary>
		[Column("interval_in_minutes")]
		public int? IntervalInMinutes { get; set; }

		/// <summary>Start timespan boundary for daily/weekly plans (minutes from midnight). Null if unconstrained.</summary>
		[Column("start_timespan")]
		public int? StartTimespan { get; set; }

		/// <summary>End timespan boundary for daily/weekly plans (minutes from midnight). Null if unconstrained.</summary>
		[Column("end_timespan")]
		public int? EndTimespan { get; set; }

		/// <summary>Timestamp of the last trigger execution. Null if never triggered.</summary>
		[Column("last_trigger_time")]
		public DateTime? LastTriggerTime { get; set; }

		/// <summary>Computed next trigger timestamp. Null if plan is disabled or completed.</summary>
		[Column("next_trigger_time")]
		public DateTime? NextTriggerTime { get; set; }

		/// <summary>UUID of the job type to be instantiated when the schedule triggers.</summary>
		[Column("job_type_id")]
		public Guid JobTypeId { get; set; }

		/// <summary>Newtonsoft.Json serialized dynamic job attributes passed to each triggered job instance. Nullable.</summary>
		[Column("job_attributes")]
		public string JobAttributes { get; set; }

		/// <summary>Whether this schedule plan is active and will trigger jobs.</summary>
		[Column("enabled")]
		public bool Enabled { get; set; }

		/// <summary>UUID of the most recently started job from this plan. Null if no job has been started.</summary>
		[Column("last_started_job_id")]
		public Guid? LastStartedJobId { get; set; }

		/// <summary>Timestamp when the schedule plan was created (UTC).</summary>
		[Column("created_on")]
		public DateTime CreatedOn { get; set; }

		/// <summary>UUID of the user who last modified the schedule plan. Null for system modifications.</summary>
		[Column("last_modified_by")]
		public Guid? LastModifiedBy { get; set; }

		/// <summary>Timestamp of the last modification to this schedule plan (UTC).</summary>
		[Column("last_modified_on")]
		public DateTime LastModifiedOn { get; set; }
	}

	/// <summary>
	/// EF Core entity model mapped to the PostgreSQL <c>plugin_data</c> table.
	/// In the microservice architecture, this table stores admin service configuration
	/// and migration state data (replacing the monolith's cross-plugin plugin_data table).
	/// The <c>name</c> column has a unique constraint for plugin identification.
	/// </summary>
	[Table("plugin_data")]
	public class PluginDataEntry
	{
		/// <summary>Primary key — UUID identifier for the plugin data record.</summary>
		[Column("id")]
		public Guid Id { get; set; }

		/// <summary>Unique plugin name identifier (e.g., "webvella-admin"). Enforced via unique index.</summary>
		[Column("name")]
		public string Name { get; set; }

		/// <summary>JSON-serialized plugin settings/configuration data. Nullable.</summary>
		[Column("data")]
		public string Data { get; set; }
	}

	#endregion

	/// <summary>
	/// EF Core database context for the Admin/SDK microservice, targeting the independent
	/// <c>erp_admin</c> PostgreSQL database. Replaces the monolith's ambient static
	/// <c>DbContext.Current</c> pattern with proper ASP.NET Core dependency-injected EF Core.
	///
	/// <para><b>Database-per-service:</b> This context connects ONLY to the <c>erp_admin</c>
	/// database. No other microservice may access admin tables directly (AAP 0.8.1).</para>
	///
	/// <para><b>IDbContext compatibility:</b> Implements the SharedKernel <see cref="IDbContext"/>
	/// interface to support backward-compatible raw SQL operations used by LogService and
	/// CodeGenService. The primary data access pattern is EF Core; IDbContext is for legacy
	/// compatibility with the <c>DbRepository</c>/<c>DbConnection</c> pattern.</para>
	///
	/// <para><b>Tables owned:</b> system_log, jobs, schedule_plans, plugin_data.</para>
	///
	/// <para><b>Migrations:</b> Schema state is tracked via EF Core <c>__EFMigrationsHistory</c>,
	/// replacing the monolith's date-based <c>plugin_data</c> versioning system.</para>
	/// </summary>
	public class AdminDbContext : DbContext, IDbContext
	{
		#region Fields

		/// <summary>Synchronization object for thread-safe connection stack operations.</summary>
		private readonly object _lockObj = new object();

		/// <summary>
		/// LIFO connection stack mirroring the monolith's <c>DbContext.connectionStack</c> pattern.
		/// Connections must be closed in reverse order of creation (innermost first).
		/// </summary>
		private readonly Stack<SharedKernel.Database.DbConnection> _connectionStack;

		/// <summary>
		/// Shared transaction reference for the IDbContext transactional state.
		/// When set, all new connections created via <see cref="CreateConnection"/> share this transaction.
		/// </summary>
		private NpgsqlTransaction _transaction;

		/// <summary>
		/// Tracks whether the context has been disposed to prevent double-disposal issues.
		/// </summary>
		private bool _disposed;

		#endregion

		#region Constructor

		/// <summary>
		/// Initializes a new instance of <see cref="AdminDbContext"/> with the specified EF Core options.
		/// Connection string is injected via <c>DbContextOptions</c> configured in <c>Program.cs</c>
		/// (reading from <c>appsettings.json → ConnectionStrings:Default</c>).
		/// </summary>
		/// <param name="options">EF Core context options including the PostgreSQL connection string
		/// (expected format: <c>Host=...;Database=erp_admin;MinPoolSize=1;MaxPoolSize=100</c>).</param>
		public AdminDbContext(DbContextOptions<AdminDbContext> options) : base(options)
		{
			_connectionStack = new Stack<SharedKernel.Database.DbConnection>();
			_transaction = null;
			_disposed = false;
			// ConnectionString is extracted from the EF Core options for IDbContext raw SQL support.
			// Database.IsRelational() returns false for non-relational providers (e.g., InMemory
			// used in unit tests), which do not support GetConnectionString().
			ConnectionString = Database.IsRelational() ? Database.GetConnectionString() : null;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Connection string for the <c>erp_admin</c> database. Used by the IDbContext
		/// implementation when creating raw SQL connections for backward compatibility
		/// with LogService and CodeGenService operations.
		/// </summary>
		public string ConnectionString { get; }

		#endregion

		#region DbSet Properties

		/// <summary>
		/// Entity set for the <c>system_log</c> table. Contains diagnostic log entries
		/// (errors, info messages) written by the admin service.
		/// </summary>
		public DbSet<SystemLogEntry> SystemLogs { get; set; }

		/// <summary>
		/// Entity set for the <c>jobs</c> table. Contains background job records
		/// with status tracking, scheduling metadata, and execution results.
		/// </summary>
		public DbSet<JobEntry> Jobs { get; set; }

		/// <summary>
		/// Entity set for the <c>schedule_plans</c> table. Contains scheduled job
		/// execution plans (interval, daily, weekly, monthly triggers).
		/// </summary>
		public DbSet<SchedulePlanEntry> SchedulePlans { get; set; }

		/// <summary>
		/// Entity set for the <c>plugin_data</c> table. Stores admin service configuration
		/// and migration state data in JSON format.
		/// </summary>
		public DbSet<PluginDataEntry> PluginData { get; set; }

		#endregion

		#region OnModelCreating

		/// <summary>
		/// Configures the EF Core model using the fluent API. Maps entity classes to their
		/// exact PostgreSQL table and column names from the monolith, configures primary keys,
		/// column types, default values, indexes, and nullability constraints.
		/// All column names use snake_case to match the monolith's PostgreSQL schema exactly.
		/// </summary>
		/// <param name="modelBuilder">The EF Core model builder instance.</param>
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);

			ConfigureSystemLogEntry(modelBuilder);
			ConfigureJobEntry(modelBuilder);
			ConfigureSchedulePlanEntry(modelBuilder);
			ConfigurePluginDataEntry(modelBuilder);
		}

		/// <summary>
		/// Configures the <see cref="SystemLogEntry"/> entity mapping to the <c>system_log</c> table.
		/// </summary>
		private static void ConfigureSystemLogEntry(ModelBuilder modelBuilder)
		{
			var entity = modelBuilder.Entity<SystemLogEntry>();

			entity.ToTable("system_log");
			entity.HasKey(e => e.Id);

			entity.Property(e => e.Id)
				.HasColumnName("id")
				.HasColumnType("uuid")
				.HasDefaultValueSql("gen_random_uuid()")
				.IsRequired();

			entity.Property(e => e.CreatedOn)
				.HasColumnName("created_on")
				.HasColumnType("timestamp without time zone")
				.HasDefaultValueSql("now()")
				.IsRequired();

			entity.Property(e => e.Type)
				.HasColumnName("type")
				.HasColumnType("integer")
				.IsRequired();

			entity.Property(e => e.Source)
				.HasColumnName("source")
				.HasColumnType("text")
				.HasDefaultValue(string.Empty)
				.IsRequired();

			entity.Property(e => e.Message)
				.HasColumnName("message")
				.HasColumnType("text")
				.HasDefaultValue(string.Empty)
				.IsRequired();

			entity.Property(e => e.Details)
				.HasColumnName("details")
				.HasColumnType("text")
				.IsRequired(false);

			entity.Property(e => e.NotificationStatus)
				.HasColumnName("notification_status")
				.HasColumnType("integer")
				.HasDefaultValue(0)
				.IsRequired();

			// Index on created_on DESC for efficient log query pagination (ORDER BY created_on DESC)
			entity.HasIndex(e => e.CreatedOn)
				.HasDatabaseName("ix_system_log_created_on")
				.IsDescending(true);
		}

		/// <summary>
		/// Configures the <see cref="JobEntry"/> entity mapping to the <c>jobs</c> table.
		/// </summary>
		private static void ConfigureJobEntry(ModelBuilder modelBuilder)
		{
			var entity = modelBuilder.Entity<JobEntry>();

			entity.ToTable("jobs");
			entity.HasKey(e => e.Id);

			entity.Property(e => e.Id)
				.HasColumnName("id")
				.HasColumnType("uuid")
				.HasDefaultValueSql("gen_random_uuid()")
				.IsRequired();

			entity.Property(e => e.TypeId)
				.HasColumnName("type_id")
				.HasColumnType("uuid")
				.IsRequired();

			entity.Property(e => e.TypeName)
				.HasColumnName("type_name")
				.HasColumnType("text")
				.IsRequired();

			entity.Property(e => e.CompleteClassName)
				.HasColumnName("complete_class_name")
				.HasColumnType("text")
				.IsRequired();

			entity.Property(e => e.Attributes)
				.HasColumnName("attributes")
				.HasColumnType("text")
				.IsRequired(false);

			entity.Property(e => e.Status)
				.HasColumnName("status")
				.HasColumnType("integer")
				.IsRequired();

			entity.Property(e => e.Priority)
				.HasColumnName("priority")
				.HasColumnType("integer")
				.IsRequired();

			entity.Property(e => e.StartedOn)
				.HasColumnName("started_on")
				.HasColumnType("timestamp without time zone")
				.IsRequired(false);

			entity.Property(e => e.FinishedOn)
				.HasColumnName("finished_on")
				.HasColumnType("timestamp without time zone")
				.IsRequired(false);

			entity.Property(e => e.AbortedBy)
				.HasColumnName("aborted_by")
				.HasColumnType("uuid")
				.IsRequired(false);

			entity.Property(e => e.CanceledBy)
				.HasColumnName("canceled_by")
				.HasColumnType("uuid")
				.IsRequired(false);

			entity.Property(e => e.ErrorMessage)
				.HasColumnName("error_message")
				.HasColumnType("text")
				.IsRequired(false);

			entity.Property(e => e.SchedulePlanId)
				.HasColumnName("schedule_plan_id")
				.HasColumnType("uuid")
				.IsRequired(false);

			entity.Property(e => e.CreatedOn)
				.HasColumnName("created_on")
				.HasColumnType("timestamp without time zone")
				.HasDefaultValueSql("now()")
				.IsRequired();

			entity.Property(e => e.CreatedBy)
				.HasColumnName("created_by")
				.HasColumnType("uuid")
				.IsRequired(false);

			entity.Property(e => e.LastModifiedOn)
				.HasColumnName("last_modified_on")
				.HasColumnType("timestamp without time zone")
				.HasDefaultValueSql("now()")
				.IsRequired();

			entity.Property(e => e.LastModifiedBy)
				.HasColumnName("last_modified_by")
				.HasColumnType("uuid")
				.IsRequired(false);

			entity.Property(e => e.Result)
				.HasColumnName("result")
				.HasColumnType("text")
				.IsRequired(false);

			// Index on status for efficient pending/running job lookups (WHERE status = @status)
			entity.HasIndex(e => e.Status)
				.HasDatabaseName("ix_jobs_status");

			// Composite index for the common query pattern: ORDER BY priority DESC, created_on ASC
			entity.HasIndex(e => new { e.Priority, e.CreatedOn })
				.HasDatabaseName("ix_jobs_priority_created_on");

			// Index on created_on DESC for job listing pagination
			entity.HasIndex(e => e.CreatedOn)
				.HasDatabaseName("ix_jobs_created_on")
				.IsDescending(true);

			// Index on schedule_plan_id for efficient lookup of jobs by schedule plan
			entity.HasIndex(e => e.SchedulePlanId)
				.HasDatabaseName("ix_jobs_schedule_plan_id");
		}

		/// <summary>
		/// Configures the <see cref="SchedulePlanEntry"/> entity mapping to the <c>schedule_plans</c> table.
		/// </summary>
		private static void ConfigureSchedulePlanEntry(ModelBuilder modelBuilder)
		{
			var entity = modelBuilder.Entity<SchedulePlanEntry>();

			entity.ToTable("schedule_plans");
			entity.HasKey(e => e.Id);

			entity.Property(e => e.Id)
				.HasColumnName("id")
				.HasColumnType("uuid")
				.HasDefaultValueSql("gen_random_uuid()")
				.IsRequired();

			entity.Property(e => e.Name)
				.HasColumnName("name")
				.HasColumnType("text")
				.IsRequired();

			entity.Property(e => e.Type)
				.HasColumnName("type")
				.HasColumnType("integer")
				.IsRequired();

			entity.Property(e => e.StartDate)
				.HasColumnName("start_date")
				.HasColumnType("timestamp without time zone")
				.IsRequired(false);

			entity.Property(e => e.EndDate)
				.HasColumnName("end_date")
				.HasColumnType("timestamp without time zone")
				.IsRequired(false);

			entity.Property(e => e.ScheduledDays)
				.HasColumnName("schedule_days")
				.HasColumnType("text")
				.IsRequired(false);

			entity.Property(e => e.IntervalInMinutes)
				.HasColumnName("interval_in_minutes")
				.HasColumnType("integer")
				.IsRequired(false);

			entity.Property(e => e.StartTimespan)
				.HasColumnName("start_timespan")
				.HasColumnType("integer")
				.IsRequired(false);

			entity.Property(e => e.EndTimespan)
				.HasColumnName("end_timespan")
				.HasColumnType("integer")
				.IsRequired(false);

			entity.Property(e => e.LastTriggerTime)
				.HasColumnName("last_trigger_time")
				.HasColumnType("timestamp without time zone")
				.IsRequired(false);

			entity.Property(e => e.NextTriggerTime)
				.HasColumnName("next_trigger_time")
				.HasColumnType("timestamp without time zone")
				.IsRequired(false);

			entity.Property(e => e.JobTypeId)
				.HasColumnName("job_type_id")
				.HasColumnType("uuid")
				.IsRequired();

			entity.Property(e => e.JobAttributes)
				.HasColumnName("job_attributes")
				.HasColumnType("text")
				.IsRequired(false);

			entity.Property(e => e.Enabled)
				.HasColumnName("enabled")
				.HasColumnType("boolean")
				.HasDefaultValue(false)
				.IsRequired();

			entity.Property(e => e.LastStartedJobId)
				.HasColumnName("last_started_job_id")
				.HasColumnType("uuid")
				.IsRequired(false);

			entity.Property(e => e.CreatedOn)
				.HasColumnName("created_on")
				.HasColumnType("timestamp without time zone")
				.HasDefaultValueSql("now()")
				.IsRequired();

			entity.Property(e => e.LastModifiedBy)
				.HasColumnName("last_modified_by")
				.HasColumnType("uuid")
				.IsRequired(false);

			entity.Property(e => e.LastModifiedOn)
				.HasColumnName("last_modified_on")
				.HasColumnType("timestamp without time zone")
				.HasDefaultValueSql("now()")
				.IsRequired();

			// Composite index for the ready-for-execution query:
			// WHERE enabled = true AND next_trigger_time <= @utc_now AND start_date <= @utc_now
			entity.HasIndex(e => new { e.Enabled, e.NextTriggerTime })
				.HasDatabaseName("ix_schedule_plans_enabled_next_trigger");

			// Index on name for alphabetical listing (ORDER BY name)
			entity.HasIndex(e => e.Name)
				.HasDatabaseName("ix_schedule_plans_name");
		}

		/// <summary>
		/// Configures the <see cref="PluginDataEntry"/> entity mapping to the <c>plugin_data</c> table.
		/// </summary>
		private static void ConfigurePluginDataEntry(ModelBuilder modelBuilder)
		{
			var entity = modelBuilder.Entity<PluginDataEntry>();

			entity.ToTable("plugin_data");
			entity.HasKey(e => e.Id);

			entity.Property(e => e.Id)
				.HasColumnName("id")
				.HasColumnType("uuid")
				.HasDefaultValueSql("gen_random_uuid()")
				.IsRequired();

			entity.Property(e => e.Name)
				.HasColumnName("name")
				.HasColumnType("text")
				.IsRequired();

			entity.Property(e => e.Data)
				.HasColumnName("data")
				.HasColumnType("text")
				.IsRequired(false);

			// Unique index on name — each plugin/service has exactly one settings record
			entity.HasIndex(e => e.Name)
				.HasDatabaseName("ix_plugin_data_name")
				.IsUnique();
		}

		#endregion

		#region IDbContext Implementation

		/// <summary>
		/// Creates a new <see cref="SharedKernel.Database.DbConnection"/> for raw SQL operations.
		/// If a shared transaction is active (<see cref="EnterTransactionalState"/> was called),
		/// the new connection shares that transaction. Otherwise, a new physical connection is
		/// opened using <see cref="ConnectionString"/>.
		///
		/// <para>Connections are tracked on an internal LIFO stack and must be closed in reverse
		/// order of creation via <see cref="CloseConnection"/>.</para>
		/// </summary>
		/// <returns>A new <see cref="SharedKernel.Database.DbConnection"/> instance.</returns>
		/// <exception cref="InvalidOperationException">Thrown if the connection string is null or empty
		/// and no transaction is active.</exception>
		public SharedKernel.Database.DbConnection CreateConnection()
		{
			SharedKernel.Database.DbConnection conn;

			if (_transaction != null)
			{
				conn = new SharedKernel.Database.DbConnection(_transaction, this);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(ConnectionString))
				{
					throw new InvalidOperationException(
						"Cannot create a raw SQL connection: the AdminDbContext connection string is null or empty. " +
						"Ensure the connection string is configured in appsettings.json → ConnectionStrings:Default.");
				}
				conn = new SharedKernel.Database.DbConnection(ConnectionString, this);
			}

			_connectionStack.Push(conn);

			Debug.WriteLine(
				$"AdminDbContext CreateConnection | Stack count: {_connectionStack.Count} | Hash: {conn.GetHashCode()}");

			return conn;
		}

		/// <summary>
		/// Closes a previously created <see cref="SharedKernel.Database.DbConnection"/> and removes
		/// it from the connection stack. Connections must be closed in LIFO order (innermost first).
		/// </summary>
		/// <param name="conn">The connection to close. Must be the topmost connection on the stack.</param>
		/// <returns><c>true</c> if all connections have been closed (stack is empty); <c>false</c> otherwise.</returns>
		/// <exception cref="InvalidOperationException">Thrown if the connection stack is empty or
		/// if <paramref name="conn"/> is not the topmost connection (attempting to close out of order).</exception>
		public bool CloseConnection(SharedKernel.Database.DbConnection conn)
		{
			lock (_lockObj)
			{
				if (_connectionStack.Count == 0)
				{
					throw new InvalidOperationException(
						"Cannot close connection: the connection stack is empty. No connections are open.");
				}

				var topConn = _connectionStack.Peek();
				if (topConn != conn)
				{
					throw new InvalidOperationException(
						"You are trying to close a connection before closing inner connections. " +
						"Connections must be closed in LIFO order.");
				}

				_connectionStack.Pop();

				Debug.WriteLine(
					$"AdminDbContext CloseConnection | Stack count: {_connectionStack.Count} | Hash: {conn.GetHashCode()}");

				return _connectionStack.Count == 0;
			}
		}

		/// <summary>
		/// Enters transactional state — sets the shared <see cref="NpgsqlTransaction"/> that will be
		/// used by all subsequent connections created via <see cref="CreateConnection"/>.
		/// Called internally by <see cref="SharedKernel.Database.DbConnection.BeginTransaction"/>.
		/// </summary>
		/// <param name="transaction">The NpgsqlTransaction to share across connections.</param>
		public void EnterTransactionalState(NpgsqlTransaction transaction)
		{
			_transaction = transaction;
		}

		/// <summary>
		/// Leaves transactional state — clears the shared transaction reference.
		/// Called internally by <see cref="SharedKernel.Database.DbConnection.CommitTransaction"/>
		/// and <see cref="SharedKernel.Database.DbConnection.RollbackTransaction"/>.
		/// </summary>
		public void LeaveTransactionalState()
		{
			_transaction = null;
		}

		#endregion

		#region Dispose

		/// <summary>
		/// Disposes the AdminDbContext, cleaning up IDbContext resources (connection stack,
		/// transaction) before delegating to the EF Core base <see cref="DbContext.Dispose()"/>.
		/// If a transaction is still active, it is rolled back to prevent orphaned locks.
		/// </summary>
		public override void Dispose()
		{
			if (!_disposed)
			{
				_disposed = true;

				// Roll back any orphaned transaction to prevent database lock leaks
				if (_transaction != null)
				{
					try
					{
						_transaction.Rollback();
					}
					catch (Exception ex)
					{
						// Swallow rollback exceptions during disposal — the connection may
						// already be closed or the transaction already completed.
						Debug.WriteLine($"AdminDbContext Dispose: transaction rollback failed: {ex.Message}");
					}
					_transaction = null;
				}

				// Clear the connection stack — connections should have been closed properly,
				// but we clear the stack to prevent holding references.
				_connectionStack.Clear();
			}

			base.Dispose();
		}

		#endregion
	}
}
