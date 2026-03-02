using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace WebVella.Erp.Service.Reporting.Database
{
    #region POCO Entity Classes — Projection Tables

    /// <summary>
    /// Denormalized timelog projection populated by MassTransit event subscribers.
    /// Contains timelog data with task/project/account references pre-joined
    /// from the monolith's ReportService.GetTimelogData() cross-entity EQL query pattern.
    /// Cross-service references (TaskId, ProjectId, AccountId) are stored as UUIDs
    /// without FK constraints per database-per-service model (AAP 0.8.2).
    /// </summary>
    public class TimelogProjection
    {
        /// <summary>Timelog record UUID — primary key.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Task UUID extracted from l_related_records JSON in the monolith's timelog entity.
        /// References the Project service's task entity (cross-service, no FK).
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// Project UUID denormalized from the task→project relation.
        /// Nullable because a timelog may reference a task with no project association.
        /// References the Project service's project entity (cross-service, no FK).
        /// </summary>
        public Guid? ProjectId { get; set; }

        /// <summary>
        /// Account UUID denormalized from the project→account relation.
        /// Nullable because a project may have no associated account.
        /// References the CRM service's account entity (cross-service, no FK).
        /// </summary>
        public Guid? AccountId { get; set; }

        /// <summary>Indicates whether this timelog entry is billable (is_billable field).</summary>
        public bool IsBillable { get; set; }

        /// <summary>Number of minutes logged (minutes field from timelog entity).</summary>
        public decimal Minutes { get; set; }

        /// <summary>
        /// Date/time when the work was logged (logged_on field).
        /// Used for date-range filtering in report queries — maps to the
        /// WHERE logged_on >= @from_date AND logged_on &lt;= @to_date pattern
        /// from ReportService.GetTimelogData() (source lines 42-45).
        /// </summary>
        public DateTime LoggedOn { get; set; }

        /// <summary>
        /// Scope identifier (l_scope field). Defaults to "projects".
        /// Used in filtering: WHERE l_scope CONTAINS @scope (source line 45).
        /// </summary>
        public string Scope { get; set; } = "projects";

        /// <summary>Audit field: when this projection record was created.</summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>Audit field: when this projection record was last modified.</summary>
        public DateTime LastModifiedOn { get; set; }
    }

    /// <summary>
    /// Task summary projection populated by MassTransit event subscribers.
    /// Contains task metadata used for report grouping and display.
    /// Derived from the task fields queried in ReportService.GetTimelogData() (source line 60):
    /// SELECT id, subject, $task_type_1n_task.label FROM task.
    /// </summary>
    public class TaskProjection
    {
        /// <summary>Task record UUID — primary key.</summary>
        public Guid Id { get; set; }

        /// <summary>Task subject text.</summary>
        public string Subject { get; set; } = "";

        /// <summary>
        /// Task type label resolved from the $task_type_1n_task relation.
        /// Nullable because a task may not have a type assigned.
        /// </summary>
        public string? TaskTypeLabel { get; set; }

        /// <summary>Audit field: when this projection record was created.</summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>Audit field: when this projection record was last modified.</summary>
        public DateTime LastModifiedOn { get; set; }
    }

    /// <summary>
    /// Project metadata projection populated by MassTransit event subscribers.
    /// Contains project information used for report grouping by project and account.
    /// Derived from the project fields queried in ReportService.GetTimelogData() (source line 60):
    /// $project_nn_task.id, $project_nn_task.name, $project_nn_task.account_id.
    /// </summary>
    public class ProjectProjection
    {
        /// <summary>Project record UUID — primary key.</summary>
        public Guid Id { get; set; }

        /// <summary>Project name for display and grouping.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Account UUID from the project's account_id field.
        /// Nullable because a project may not be associated with an account.
        /// References the CRM service's account entity (cross-service, no FK).
        /// </summary>
        public Guid? AccountId { get; set; }

        /// <summary>Audit field: when this projection record was created.</summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>Audit field: when this projection record was last modified.</summary>
        public DateTime LastModifiedOn { get; set; }
    }

    /// <summary>
    /// Stored report configuration allowing users to save and reuse report definitions.
    /// This is a new concept for the Reporting microservice — enables persistent
    /// report configurations that were previously ephemeral in the monolith's
    /// ReportService.GetTimelogData() method.
    /// </summary>
    public class ReportDefinition
    {
        /// <summary>Report definition UUID — primary key.</summary>
        public Guid Id { get; set; }

        /// <summary>Human-readable report name (max 500 characters).</summary>
        public string Name { get; set; } = "";

        /// <summary>Optional report description.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Report type identifier (e.g., "timelog", "project_summary").
        /// Defaults to "timelog" matching the primary report type from the monolith.
        /// </summary>
        public string ReportType { get; set; } = "timelog";

        /// <summary>
        /// Serialized JSON parameters for the report (year, month, accountId, filters).
        /// Defaults to empty JSON object.
        /// </summary>
        public string ParametersJson { get; set; } = "{}";

        /// <summary>
        /// UUID of the user who created this report definition.
        /// References the Core service's user entity (cross-service, no FK).
        /// </summary>
        public Guid CreatedBy { get; set; }

        /// <summary>Audit field: when this report definition was created.</summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>Audit field: when this report definition was last modified.</summary>
        public DateTime LastModifiedOn { get; set; }
    }

    #endregion

    /// <summary>
    /// EF Core DbContext for the Reporting microservice.
    /// Manages projection tables populated by event subscribers (MassTransit consumers):
    /// <list type="bullet">
    ///   <item><description>TimelogProjections: denormalized timelog data with task/project/account info</description></item>
    ///   <item><description>TaskProjections: task summaries with type labels</description></item>
    ///   <item><description>ProjectProjections: project metadata for report grouping</description></item>
    ///   <item><description>ReportDefinitions: stored report configurations</description></item>
    /// </list>
    ///
    /// Owns the <c>erp_reporting</c> PostgreSQL database exclusively.
    /// No other service reads from or writes to this database (AAP 0.8.2).
    ///
    /// This context uses the modern EF Core DI-registered, scoped approach
    /// (no static Current property, no connection stacks, no ConcurrentDictionary).
    /// The Reporting service is a pure CQRS reader/aggregator (AAP 0.4.3),
    /// so the simpler EF Core approach is appropriate without needing the
    /// legacy IDbContext compatibility layer used by CRM/Mail/Core services.
    /// </summary>
    public class ReportingDbContext : DbContext
    {
        /// <summary>
        /// Initializes a new instance of <see cref="ReportingDbContext"/> using the
        /// specified options. Configured via DI in Program.cs with UseNpgsql().
        /// </summary>
        /// <param name="options">The EF Core options configured for PostgreSQL.</param>
        public ReportingDbContext(DbContextOptions<ReportingDbContext> options)
            : base(options)
        {
        }

        #region DbSet Properties

        /// <summary>
        /// Denormalized timelog projections with pre-joined task/project/account data.
        /// Populated by event subscribers consuming timelog CRUD events from the Project service.
        /// </summary>
        public DbSet<TimelogProjection> TimelogProjections { get; set; } = null!;

        /// <summary>
        /// Task summary projections with subject and type label.
        /// Populated by event subscribers consuming task CRUD events from the Project service.
        /// </summary>
        public DbSet<TaskProjection> TaskProjections { get; set; } = null!;

        /// <summary>
        /// Project metadata projections for grouping and account association.
        /// Populated by event subscribers consuming project CRUD events from the Project service.
        /// </summary>
        public DbSet<ProjectProjection> ProjectProjections { get; set; } = null!;

        /// <summary>
        /// Stored report definitions allowing users to persist report configurations.
        /// Directly managed by the Reporting service's API (CRUD operations).
        /// </summary>
        public DbSet<ReportDefinition> ReportDefinitions { get; set; } = null!;

        #endregion

        #region Schema Configuration

        /// <summary>
        /// Configures the database schema for all projection tables and the report definitions table.
        /// Uses Fluent API for complete control over table names (snake_case), column mappings,
        /// PostgreSQL column types, default values, and indexes optimized for the aggregation
        /// query patterns from the monolith's ReportService.GetTimelogData().
        ///
        /// Cross-service UUID references (AccountId, TaskId, CreatedBy) have NO FK constraints
        /// per the database-per-service model (AAP 0.8.2, 0.7.1).
        /// </summary>
        /// <param name="modelBuilder">The EF Core model builder for Fluent API configuration.</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            ConfigureTimelogProjection(modelBuilder);
            ConfigureTaskProjection(modelBuilder);
            ConfigureProjectProjection(modelBuilder);
            ConfigureReportDefinition(modelBuilder);
        }

        /// <summary>
        /// Ensures the context is configured via DI. Throws if DI was not set up.
        /// This fallback prevents silent misconfiguration during EF Core migration tooling
        /// or direct instantiation without proper options.
        /// </summary>
        /// <param name="optionsBuilder">The options builder to validate.</param>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                throw new InvalidOperationException(
                    "ReportingDbContext must be configured via dependency injection. " +
                    "Register it in Program.cs using AddDbContext<ReportingDbContext>(options => options.UseNpgsql(...)).");
            }

            base.OnConfiguring(optionsBuilder);
        }

        #endregion

        #region Private Configuration Methods

        /// <summary>
        /// Configures the timelog_projections table schema.
        /// Indexes are optimized for the common date-range + scope filter pattern
        /// from ReportService.GetTimelogData() (source lines 42-45):
        /// WHERE logged_on >= @from_date AND logged_on &lt;= @to_date AND l_scope CONTAINS @scope
        /// </summary>
        private static void ConfigureTimelogProjection(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TimelogProjection>(entity =>
            {
                entity.ToTable("timelog_projections");
                entity.HasKey(e => e.Id);

                // Column mappings with PostgreSQL types matching monolith conventions
                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.TaskId)
                    .HasColumnName("task_id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.ProjectId)
                    .HasColumnName("project_id")
                    .HasColumnType("uuid");

                entity.Property(e => e.AccountId)
                    .HasColumnName("account_id")
                    .HasColumnType("uuid");

                entity.Property(e => e.IsBillable)
                    .HasColumnName("is_billable")
                    .HasColumnType("boolean")
                    .IsRequired();

                entity.Property(e => e.Minutes)
                    .HasColumnName("minutes")
                    .HasColumnType("numeric")
                    .IsRequired()
                    .HasDefaultValue(0m);

                entity.Property(e => e.LoggedOn)
                    .HasColumnName("logged_on")
                    .HasColumnType("timestamptz")
                    .IsRequired();

                entity.Property(e => e.Scope)
                    .HasColumnName("scope")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("projects");

                entity.Property(e => e.CreatedOn)
                    .HasColumnName("created_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.Property(e => e.LastModifiedOn)
                    .HasColumnName("last_modified_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                // Individual column indexes for flexible aggregation queries
                entity.HasIndex(e => e.TaskId)
                    .HasDatabaseName("idx_timelog_proj_task_id");

                entity.HasIndex(e => e.ProjectId)
                    .HasDatabaseName("idx_timelog_proj_project_id");

                entity.HasIndex(e => e.AccountId)
                    .HasDatabaseName("idx_timelog_proj_account_id");

                entity.HasIndex(e => e.LoggedOn)
                    .HasDatabaseName("idx_timelog_proj_logged_on");

                entity.HasIndex(e => e.Scope)
                    .HasDatabaseName("idx_timelog_proj_scope");

                // Composite index for the common date-range + scope filter
                // from ReportService.GetTimelogData() (source lines 42-45)
                entity.HasIndex(e => new { e.LoggedOn, e.Scope })
                    .HasDatabaseName("idx_timelog_proj_logged_on_scope");
            });
        }

        /// <summary>
        /// Configures the task_projections table schema.
        /// Subject index supports text-based searches on task names.
        /// </summary>
        private static void ConfigureTaskProjection(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TaskProjection>(entity =>
            {
                entity.ToTable("task_projections");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.Subject)
                    .HasColumnName("subject")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("");

                entity.Property(e => e.TaskTypeLabel)
                    .HasColumnName("task_type_label")
                    .HasColumnType("text");

                entity.Property(e => e.CreatedOn)
                    .HasColumnName("created_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.Property(e => e.LastModifiedOn)
                    .HasColumnName("last_modified_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.HasIndex(e => e.Subject)
                    .HasDatabaseName("idx_task_proj_subject");
            });
        }

        /// <summary>
        /// Configures the project_projections table schema.
        /// Indexes on AccountId and Name support grouping and filtering in reports.
        /// </summary>
        private static void ConfigureProjectProjection(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProjectProjection>(entity =>
            {
                entity.ToTable("project_projections");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.Name)
                    .HasColumnName("name")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("");

                entity.Property(e => e.AccountId)
                    .HasColumnName("account_id")
                    .HasColumnType("uuid");

                entity.Property(e => e.CreatedOn)
                    .HasColumnName("created_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.Property(e => e.LastModifiedOn)
                    .HasColumnName("last_modified_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.HasIndex(e => e.AccountId)
                    .HasDatabaseName("idx_project_proj_account_id");

                entity.HasIndex(e => e.Name)
                    .HasDatabaseName("idx_project_proj_name");
            });
        }

        /// <summary>
        /// Configures the report_definitions table schema.
        /// Indexes on Name, ReportType, and CreatedBy support common lookup patterns.
        /// Name has MaxLength(500) and ReportType uses varchar(200) for bounded storage.
        /// </summary>
        private static void ConfigureReportDefinition(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ReportDefinition>(entity =>
            {
                entity.ToTable("report_definitions");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.Name)
                    .HasColumnName("name")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasMaxLength(500);

                entity.Property(e => e.Description)
                    .HasColumnName("description")
                    .HasColumnType("text");

                entity.Property(e => e.ReportType)
                    .HasColumnName("report_type")
                    .HasColumnType("varchar(200)")
                    .IsRequired()
                    .HasDefaultValue("timelog");

                entity.Property(e => e.ParametersJson)
                    .HasColumnName("parameters_json")
                    .HasColumnType("text")
                    .IsRequired()
                    .HasDefaultValue("{}");

                entity.Property(e => e.CreatedBy)
                    .HasColumnName("created_by")
                    .HasColumnType("uuid")
                    .IsRequired();

                entity.Property(e => e.CreatedOn)
                    .HasColumnName("created_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.Property(e => e.LastModifiedOn)
                    .HasColumnName("last_modified_on")
                    .HasColumnType("timestamptz")
                    .IsRequired()
                    .HasDefaultValueSql("now()");

                entity.HasIndex(e => e.Name)
                    .HasDatabaseName("idx_report_def_name");

                entity.HasIndex(e => e.ReportType)
                    .HasDatabaseName("idx_report_def_report_type");

                entity.HasIndex(e => e.CreatedBy)
                    .HasDatabaseName("idx_report_def_created_by");
            });
        }

        #endregion
    }
}
