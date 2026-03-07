using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Reporting.Fixtures
{
    #region Well-Known Test GUIDs

    /// <summary>
    /// Deterministic, well-known GUIDs used across all Reporting service test data
    /// and test assertions. Every ID is hardcoded (no Guid.NewGuid()) to ensure
    /// assertion stability across test runs.
    ///
    /// Naming convention:
    /// - a000000x: Accounts (cross-service CRM references)
    /// - b000000x: Projects
    /// - c000000x: Tasks
    /// - d000000x: Timelogs
    /// - e000000x: Report definitions
    /// - System/First user IDs match monolith SystemIds in Definitions.cs
    /// </summary>
    public static class TestIds
    {
        // Accounts (cross-service CRM references — no FK, UUID only per AAP 0.8.2)
        public static readonly Guid AccountAlphaId = new Guid("a0000001-0000-0000-0000-000000000001");
        public static readonly Guid AccountBetaId = new Guid("a0000002-0000-0000-0000-000000000002");

        // Projects
        public static readonly Guid ProjectAlphaId = new Guid("b0000001-0000-0000-0000-000000000001");
        public static readonly Guid ProjectBetaId = new Guid("b0000002-0000-0000-0000-000000000002");
        public static readonly Guid ProjectGammaId = new Guid("b0000003-0000-0000-0000-000000000003"); // No account

        // Tasks
        public static readonly Guid Task1Id = new Guid("c0000001-0000-0000-0000-000000000001");
        public static readonly Guid Task2Id = new Guid("c0000002-0000-0000-0000-000000000002"); // Multi-project
        public static readonly Guid Task3Id = new Guid("c0000003-0000-0000-0000-000000000003"); // Under project without account

        // Timelogs
        public static readonly Guid Timelog1Id = new Guid("d0000001-0000-0000-0000-000000000001");
        public static readonly Guid Timelog2Id = new Guid("d0000002-0000-0000-0000-000000000002");
        public static readonly Guid Timelog3Id = new Guid("d0000003-0000-0000-0000-000000000003");
        public static readonly Guid Timelog4Id = new Guid("d0000004-0000-0000-0000-000000000004");
        public static readonly Guid Timelog5Id = new Guid("d0000005-0000-0000-0000-000000000005");
        public static readonly Guid Timelog6Id = new Guid("d0000006-0000-0000-0000-000000000006");

        // Report Definitions
        public static readonly Guid MonthlyReportId = new Guid("e0000001-0000-0000-0000-000000000001");
        public static readonly Guid AccountReportId = new Guid("e0000002-0000-0000-0000-000000000002");

        // Users (from monolith SystemIds in Definitions.cs lines 6-21)
        public static readonly Guid SystemUserId = new Guid("10000000-0000-0000-0000-000000000000");
        public static readonly Guid FirstUserId = new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2");
    }

    #endregion

    #region TimelogProjectionBuilder

    /// <summary>
    /// Fluent builder for constructing <see cref="TimelogProjection"/> EF Core entity
    /// instances for test database seeding and assertion verification.
    ///
    /// Properties match the ReportingDbContext.TimelogProjections schema:
    /// Id (Guid), TaskId (Guid), ProjectId (Guid?), AccountId (Guid?),
    /// IsBillable (bool), Minutes (decimal), LoggedOn (DateTime), Scope (string),
    /// CreatedOn (DateTime), LastModifiedOn (DateTime).
    ///
    /// Source reference:
    /// - is_billable from monolith TimeLogService.Create() maps to IsBillable
    /// - l_related_records → TaskId (extracted from JSON List&lt;Guid&gt;, first element)
    /// - minutes field directly from timelog entity
    /// - logged_on used for month range filtering (ReportService.cs lines 27-30)
    /// - l_scope maps to Scope — used in l_scope CONTAINS @scope EQL filter
    /// </summary>
    public class TimelogProjectionBuilder
    {
        private Guid _id = Guid.NewGuid();
        private Guid _taskId = Guid.NewGuid();
        private Guid _projectId = Guid.NewGuid();
        private Guid? _accountId = null;
        private bool _isBillable = true;
        private int _minutes = 60;
        private DateTime _loggedOn = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private string _scope = "projects";
        private DateTime _createdOn = DateTime.UtcNow;
        private DateTime _lastModifiedOn = DateTime.UtcNow;

        /// <summary>Sets the timelog record UUID primary key.</summary>
        public TimelogProjectionBuilder WithId(Guid id)
        {
            _id = id;
            return this;
        }

        /// <summary>Sets the task UUID extracted from l_related_records JSON in the monolith.</summary>
        public TimelogProjectionBuilder WithTaskId(Guid taskId)
        {
            _taskId = taskId;
            return this;
        }

        /// <summary>Sets the project UUID denormalized from the task→project relation.</summary>
        public TimelogProjectionBuilder WithProjectId(Guid projectId)
        {
            _projectId = projectId;
            return this;
        }

        /// <summary>Sets the account UUID denormalized from the project→account relation. Nullable.</summary>
        public TimelogProjectionBuilder WithAccountId(Guid? accountId)
        {
            _accountId = accountId;
            return this;
        }

        /// <summary>Sets whether this timelog entry is billable (is_billable field).</summary>
        public TimelogProjectionBuilder WithIsBillable(bool isBillable)
        {
            _isBillable = isBillable;
            return this;
        }

        /// <summary>Sets the number of minutes logged (minutes field from timelog entity).</summary>
        public TimelogProjectionBuilder WithMinutes(int minutes)
        {
            _minutes = minutes;
            return this;
        }

        /// <summary>Sets the date/time when the work was logged (logged_on field). Used for date-range filtering.</summary>
        public TimelogProjectionBuilder WithLoggedOn(DateTime loggedOn)
        {
            _loggedOn = loggedOn;
            return this;
        }

        /// <summary>Sets the scope identifier (l_scope field). Defaults to "projects".</summary>
        public TimelogProjectionBuilder WithScope(string scope)
        {
            _scope = scope;
            return this;
        }

        /// <summary>Sets the audit field: when this projection record was created.</summary>
        public TimelogProjectionBuilder WithCreatedOn(DateTime createdOn)
        {
            _createdOn = createdOn;
            return this;
        }

        /// <summary>Sets the audit field: when this projection record was last modified.</summary>
        public TimelogProjectionBuilder WithLastModifiedOn(DateTime lastModifiedOn)
        {
            _lastModifiedOn = lastModifiedOn;
            return this;
        }

        /// <summary>
        /// Builds a <see cref="TimelogProjection"/> entity instance with all configured properties.
        /// The entity can be directly inserted into <see cref="ReportingDbContext.TimelogProjections"/>.
        /// </summary>
        public TimelogProjection Build()
        {
            return new TimelogProjection
            {
                Id = _id,
                TaskId = _taskId,
                ProjectId = _projectId,
                AccountId = _accountId,
                IsBillable = _isBillable,
                Minutes = _minutes,
                LoggedOn = _loggedOn,
                Scope = _scope,
                CreatedOn = _createdOn,
                LastModifiedOn = _lastModifiedOn
            };
        }

        /// <summary>
        /// Creates a builder pre-configured for a billable timelog entry.
        /// Defaults: IsBillable=true, Minutes=60.
        /// </summary>
        public static TimelogProjectionBuilder BillableTimelog()
        {
            return new TimelogProjectionBuilder()
                .WithIsBillable(true)
                .WithMinutes(60);
        }

        /// <summary>
        /// Creates a builder pre-configured for a non-billable timelog entry.
        /// Defaults: IsBillable=false, Minutes=30.
        /// </summary>
        public static TimelogProjectionBuilder NonBillableTimelog()
        {
            return new TimelogProjectionBuilder()
                .WithIsBillable(false)
                .WithMinutes(30);
        }
    }

    #endregion

    #region TaskProjectionBuilder

    /// <summary>
    /// Fluent builder for constructing <see cref="TaskProjection"/> EF Core entity
    /// instances for test database seeding and assertion verification.
    ///
    /// Properties match the ReportingDbContext.TaskProjections schema:
    /// Id (Guid), Subject (string), TaskTypeLabel (string), CreatedOn (DateTime),
    /// LastModifiedOn (DateTime).
    ///
    /// Source reference:
    /// - subject from task entity in EQL: SELECT id,subject,... (ReportService.cs line 46)
    /// - $task_type_1n_task.label maps to TaskTypeLabel (ReportService.cs line 47)
    /// </summary>
    public class TaskProjectionBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _subject = "Test Task";
        private string _taskTypeLabel = "Development";
        private DateTime _createdOn = DateTime.UtcNow;
        private DateTime _lastModifiedOn = DateTime.UtcNow;

        /// <summary>Sets the task record UUID primary key.</summary>
        public TaskProjectionBuilder WithId(Guid id)
        {
            _id = id;
            return this;
        }

        /// <summary>Sets the task subject text.</summary>
        public TaskProjectionBuilder WithSubject(string subject)
        {
            _subject = subject;
            return this;
        }

        /// <summary>Sets the task type label resolved from the $task_type_1n_task relation.</summary>
        public TaskProjectionBuilder WithTaskTypeLabel(string taskTypeLabel)
        {
            _taskTypeLabel = taskTypeLabel;
            return this;
        }

        /// <summary>Sets the audit field: when this projection record was created.</summary>
        public TaskProjectionBuilder WithCreatedOn(DateTime createdOn)
        {
            _createdOn = createdOn;
            return this;
        }

        /// <summary>Sets the audit field: when this projection record was last modified.</summary>
        public TaskProjectionBuilder WithLastModifiedOn(DateTime lastModifiedOn)
        {
            _lastModifiedOn = lastModifiedOn;
            return this;
        }

        /// <summary>
        /// Builds a <see cref="TaskProjection"/> entity instance with all configured properties.
        /// The entity can be directly inserted into <see cref="ReportingDbContext.TaskProjections"/>.
        /// </summary>
        public TaskProjection Build()
        {
            return new TaskProjection
            {
                Id = _id,
                Subject = _subject,
                TaskTypeLabel = _taskTypeLabel,
                CreatedOn = _createdOn,
                LastModifiedOn = _lastModifiedOn
            };
        }
    }

    #endregion

    #region ProjectProjectionBuilder

    /// <summary>
    /// Fluent builder for constructing <see cref="ProjectProjection"/> EF Core entity
    /// instances for test database seeding and assertion verification.
    ///
    /// Properties match the ReportingDbContext.ProjectProjections schema:
    /// Id (Guid), Name (string), AccountId (Guid?), CreatedOn (DateTime),
    /// LastModifiedOn (DateTime).
    ///
    /// Source reference:
    /// - $project_nn_task.name maps to Name (ReportService.cs line 47)
    /// - $project_nn_task.account_id maps to AccountId — CRITICAL: null AccountId
    ///   triggers "There is a project without an account" exception when accountId
    ///   filter is active (ReportService.cs lines 115-120)
    /// </summary>
    public class ProjectProjectionBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _name = "Test Project";
        private Guid? _accountId = null;
        private DateTime _createdOn = DateTime.UtcNow;
        private DateTime _lastModifiedOn = DateTime.UtcNow;

        /// <summary>Sets the project record UUID primary key.</summary>
        public ProjectProjectionBuilder WithId(Guid id)
        {
            _id = id;
            return this;
        }

        /// <summary>Sets the project name for display and grouping.</summary>
        public ProjectProjectionBuilder WithName(string name)
        {
            _name = name;
            return this;
        }

        /// <summary>Sets the account UUID from the project's account_id field. Nullable.</summary>
        public ProjectProjectionBuilder WithAccountId(Guid? accountId)
        {
            _accountId = accountId;
            return this;
        }

        /// <summary>Sets the audit field: when this projection record was created.</summary>
        public ProjectProjectionBuilder WithCreatedOn(DateTime createdOn)
        {
            _createdOn = createdOn;
            return this;
        }

        /// <summary>Sets the audit field: when this projection record was last modified.</summary>
        public ProjectProjectionBuilder WithLastModifiedOn(DateTime lastModifiedOn)
        {
            _lastModifiedOn = lastModifiedOn;
            return this;
        }

        /// <summary>
        /// Builds a <see cref="ProjectProjection"/> entity instance with all configured properties.
        /// The entity can be directly inserted into <see cref="ReportingDbContext.ProjectProjections"/>.
        /// </summary>
        public ProjectProjection Build()
        {
            return new ProjectProjection
            {
                Id = _id,
                Name = _name,
                AccountId = _accountId,
                CreatedOn = _createdOn,
                LastModifiedOn = _lastModifiedOn
            };
        }

        /// <summary>
        /// Creates a builder pre-configured with a non-null AccountId.
        /// Use for projects that have an associated CRM account.
        /// </summary>
        /// <param name="accountId">The CRM account UUID to associate with the project.</param>
        public static ProjectProjectionBuilder WithAccount(Guid accountId)
        {
            return new ProjectProjectionBuilder()
                .WithAccountId(accountId);
        }

        /// <summary>
        /// Creates a builder pre-configured with AccountId=null.
        /// Tests the "project without account" error scenario from
        /// ReportService.cs lines 115-120 where null AccountId triggers
        /// an exception when accountId filter is active.
        /// </summary>
        public static ProjectProjectionBuilder WithoutAccount()
        {
            return new ProjectProjectionBuilder()
                .WithAccountId(null);
        }
    }

    #endregion

    #region ReportDefinitionBuilder

    /// <summary>
    /// Fluent builder for constructing <see cref="ReportDefinition"/> EF Core entity
    /// instances for test database seeding and assertion verification.
    ///
    /// Properties match the ReportingDbContext.ReportDefinitions schema:
    /// Id (Guid), Name (string), Description (string), ReportType (string),
    /// ParametersJson (string), CreatedBy (Guid), CreatedOn (DateTime),
    /// LastModifiedOn (DateTime).
    ///
    /// Convenience factory methods serialize report parameters to JSON using
    /// Newtonsoft.Json (AAP 0.8.2 mandates Newtonsoft.Json, not System.Text.Json).
    /// </summary>
    public class ReportDefinitionBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _name = "Test Report";
        private string _description = "A test report definition";
        private string _reportType = "timelog_monthly";
        private string _parametersJson = "{}";
        private Guid _createdBy = TestIds.FirstUserId;
        private DateTime _createdOn = DateTime.UtcNow;
        private DateTime _lastModifiedOn = DateTime.UtcNow;

        /// <summary>Sets the report definition UUID primary key.</summary>
        public ReportDefinitionBuilder WithId(Guid id)
        {
            _id = id;
            return this;
        }

        /// <summary>Sets the human-readable report name.</summary>
        public ReportDefinitionBuilder WithName(string name)
        {
            _name = name;
            return this;
        }

        /// <summary>Sets the optional report description.</summary>
        public ReportDefinitionBuilder WithDescription(string description)
        {
            _description = description;
            return this;
        }

        /// <summary>Sets the report type identifier (e.g., "timelog_monthly", "timelog_account").</summary>
        public ReportDefinitionBuilder WithReportType(string reportType)
        {
            _reportType = reportType;
            return this;
        }

        /// <summary>Sets the serialized JSON parameters for the report.</summary>
        public ReportDefinitionBuilder WithParametersJson(string parametersJson)
        {
            _parametersJson = parametersJson;
            return this;
        }

        /// <summary>Sets the UUID of the user who created this report definition.</summary>
        public ReportDefinitionBuilder WithCreatedBy(Guid createdBy)
        {
            _createdBy = createdBy;
            return this;
        }

        /// <summary>Sets the audit field: when this report definition was created.</summary>
        public ReportDefinitionBuilder WithCreatedOn(DateTime createdOn)
        {
            _createdOn = createdOn;
            return this;
        }

        /// <summary>Sets the audit field: when this report definition was last modified.</summary>
        public ReportDefinitionBuilder WithLastModifiedOn(DateTime lastModifiedOn)
        {
            _lastModifiedOn = lastModifiedOn;
            return this;
        }

        /// <summary>
        /// Builds a <see cref="ReportDefinition"/> entity instance with all configured properties.
        /// The entity can be directly inserted into <see cref="ReportingDbContext.ReportDefinitions"/>.
        /// </summary>
        public ReportDefinition Build()
        {
            return new ReportDefinition
            {
                Id = _id,
                Name = _name,
                Description = _description,
                ReportType = _reportType,
                ParametersJson = _parametersJson,
                CreatedBy = _createdBy,
                CreatedOn = _createdOn,
                LastModifiedOn = _lastModifiedOn
            };
        }

        /// <summary>
        /// Creates a builder pre-configured for a monthly timelog report.
        /// ReportType="timelog_monthly", ParametersJson serialized via Newtonsoft.Json
        /// containing year and month parameters.
        /// </summary>
        /// <param name="year">The report year (e.g., 2024).</param>
        /// <param name="month">The report month (1-12).</param>
        public static ReportDefinitionBuilder MonthlyTimelogReport(int year, int month)
        {
            var parameters = new { year, month };
            return new ReportDefinitionBuilder()
                .WithReportType("timelog_monthly")
                .WithName("Monthly Timelog Report")
                .WithDescription($"Monthly timelog report for {year}-{month:D2}")
                .WithParametersJson(JsonConvert.SerializeObject(parameters));
        }

        /// <summary>
        /// Creates a builder pre-configured for an account-filtered timelog report.
        /// ReportType="timelog_account", ParametersJson serialized via Newtonsoft.Json
        /// containing year, month, and accountId parameters.
        /// </summary>
        /// <param name="year">The report year (e.g., 2024).</param>
        /// <param name="month">The report month (1-12).</param>
        /// <param name="accountId">The CRM account UUID to filter by.</param>
        public static ReportDefinitionBuilder AccountTimelogReport(int year, int month, Guid accountId)
        {
            var parameters = new { year, month, accountId = accountId.ToString() };
            return new ReportDefinitionBuilder()
                .WithReportType("timelog_account")
                .WithName("Account Timelog Report")
                .WithDescription($"Account timelog report for {year}-{month:D2}, account {accountId}")
                .WithParametersJson(JsonConvert.SerializeObject(parameters));
        }
    }

    #endregion

    #region EntityRecordBuilder

    /// <summary>
    /// Fluent builder for constructing <see cref="EntityRecord"/> instances matching
    /// the exact output format of ReportAggregationService.GetTimelogData().
    ///
    /// EntityRecord is the SharedKernel dynamic record type (Expando-based property bag)
    /// preserved from the monolith's WebVella.Erp.Api.Models.EntityRecord.
    ///
    /// Output fields from source ReportService.cs lines 97-108:
    /// - task_id (Guid)
    /// - project_id (Guid)
    /// - task_subject (string)
    /// - project_name (string)
    /// - task_type (string)
    /// - billable_minutes (decimal)
    /// - non_billable_minutes (decimal)
    ///
    /// These field names and types MUST match exactly for assertion-based tests
    /// to verify that the microservice produces identical output to the monolith.
    /// </summary>
    public class EntityRecordBuilder
    {
        private readonly Dictionary<string, object> _properties = new Dictionary<string, object>();

        /// <summary>
        /// Sets a generic property on the EntityRecord by key and value.
        /// Use this for custom fields not covered by the named convenience methods.
        /// </summary>
        /// <param name="key">The property key (field name).</param>
        /// <param name="value">The property value.</param>
        public EntityRecordBuilder WithProperty(string key, object value)
        {
            _properties[key] = value;
            return this;
        }

        /// <summary>Sets the "task_id" field (Guid). Maps to task["id"] in monolith (ReportService.cs line 107).</summary>
        public EntityRecordBuilder WithTaskId(Guid taskId)
        {
            _properties["task_id"] = taskId;
            return this;
        }

        /// <summary>Sets the "project_id" field (Guid). Maps to task["project"]["id"] in monolith (ReportService.cs line 108).</summary>
        public EntityRecordBuilder WithProjectId(Guid projectId)
        {
            _properties["project_id"] = projectId;
            return this;
        }

        /// <summary>Sets the "task_subject" field (string). Maps to task["subject"] in monolith (ReportService.cs line 109).</summary>
        public EntityRecordBuilder WithTaskSubject(string subject)
        {
            _properties["task_subject"] = subject;
            return this;
        }

        /// <summary>Sets the "project_name" field (string). Maps to task["project"]["name"] in monolith (ReportService.cs line 110).</summary>
        public EntityRecordBuilder WithProjectName(string name)
        {
            _properties["project_name"] = name;
            return this;
        }

        /// <summary>Sets the "task_type" field (string). Maps to $task_type_1n_task[0]["label"] in monolith (ReportService.cs line 111).</summary>
        public EntityRecordBuilder WithTaskType(string taskType)
        {
            _properties["task_type"] = taskType;
            return this;
        }

        /// <summary>Sets the "billable_minutes" field (decimal). Aggregated from billable timelogs (ReportService.cs lines 125-126).</summary>
        public EntityRecordBuilder WithBillableMinutes(decimal minutes)
        {
            _properties["billable_minutes"] = minutes;
            return this;
        }

        /// <summary>Sets the "non_billable_minutes" field (decimal). Aggregated from non-billable timelogs (ReportService.cs lines 127-128).</summary>
        public EntityRecordBuilder WithNonBillableMinutes(decimal minutes)
        {
            _properties["non_billable_minutes"] = minutes;
            return this;
        }

        /// <summary>
        /// Builds an <see cref="EntityRecord"/> instance with all configured properties.
        /// Properties are set via the EntityRecord string indexer (record[key] = value),
        /// which stores them in the underlying Expando Properties dictionary.
        /// </summary>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            foreach (var kvp in _properties)
            {
                record[kvp.Key] = kvp.Value;
            }
            return record;
        }

        /// <summary>
        /// Creates a builder pre-configured with all 7 fields matching the exact output
        /// format of ReportAggregationService.GetTimelogData() / monolith ReportService.GetTimelogData().
        /// </summary>
        /// <param name="taskId">Task UUID (task_id field).</param>
        /// <param name="projectId">Project UUID (project_id field).</param>
        /// <param name="taskSubject">Task subject text (task_subject field).</param>
        /// <param name="projectName">Project name text (project_name field).</param>
        /// <param name="taskType">Task type label (task_type field).</param>
        /// <param name="billableMinutes">Total billable minutes (billable_minutes field).</param>
        /// <param name="nonBillableMinutes">Total non-billable minutes (non_billable_minutes field).</param>
        public static EntityRecordBuilder TimelogReportRecord(
            Guid taskId, Guid projectId,
            string taskSubject, string projectName, string taskType,
            decimal billableMinutes, decimal nonBillableMinutes)
        {
            return new EntityRecordBuilder()
                .WithTaskId(taskId)
                .WithProjectId(projectId)
                .WithTaskSubject(taskSubject)
                .WithProjectName(projectName)
                .WithTaskType(taskType)
                .WithBillableMinutes(billableMinutes)
                .WithNonBillableMinutes(nonBillableMinutes);
        }
    }

    #endregion

    #region TestDataScenarios

    /// <summary>
    /// Static helper class providing pre-built test data scenarios that exercise
    /// ALL business rules from the monolith's ReportService.GetTimelogData()
    /// (source: WebVella.Erp.Plugins.Project/Services/ReportService.cs, 138 lines).
    ///
    /// Business rules covered by standard test data:
    /// 1. Month range filtering (Timelogs 1-5 Jan vs Timelog 6 Feb)
    /// 2. Billable/non-billable aggregation (Task 1: billable=120, non-billable=60)
    /// 3. Multi-project task splitting (Task 2: Project Alpha 90 min AND Project Beta 45 min)
    /// 4. Account filtering (Filter by AccountAlphaId returns only Project Alpha timelogs)
    /// 5. Missing account error (Project Gamma has null AccountId — triggers exception)
    /// 6. Result shape (7 fields: task_id, project_id, task_subject, project_name, task_type, billable_minutes, non_billable_minutes)
    /// 7. l_related_records → TaskId mapping (TimelogProjection.TaskId replaces JSON parsing)
    /// 8. $project_nn_task relation denormalization (ProjectProjection denormalizes project data)
    /// 9. $task_type_1n_task.label denormalization (TaskProjection.TaskTypeLabel)
    /// </summary>
    public static class TestDataScenarios
    {
        /// <summary>
        /// Builds the standard January 2024 test data set that exercises all business rules
        /// from ReportService.GetTimelogData(). Includes:
        /// - 3 projects (2 with accounts, 1 without)
        /// - 3 tasks (regular, multi-project, under accountless project)
        /// - 6 timelogs (billable/non-billable, different months, different projects)
        /// - 2 report definitions (monthly and account-filtered)
        /// </summary>
        /// <returns>
        /// A tuple containing lists of all test entities for database seeding.
        /// </returns>
        public static (
            List<ProjectProjection> Projects,
            List<TaskProjection> Tasks,
            List<TimelogProjection> Timelogs,
            List<ReportDefinition> Reports
        ) BuildStandardTestData()
        {
            // ----------------------------------------------------------------
            // Projects: 3 projects (2 with accounts, 1 without)
            // ----------------------------------------------------------------
            var projects = new List<ProjectProjection>
            {
                // Project Alpha — associated with Account Alpha
                new ProjectProjectionBuilder()
                    .WithId(TestIds.ProjectAlphaId)
                    .WithName("Project Alpha")
                    .WithAccountId(TestIds.AccountAlphaId)
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Project Beta — associated with Account Beta (different account)
                new ProjectProjectionBuilder()
                    .WithId(TestIds.ProjectBetaId)
                    .WithName("Project Beta")
                    .WithAccountId(TestIds.AccountBetaId)
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Project Gamma — NO account (null AccountId)
                // Tests the "project without account" error scenario
                // from ReportService.cs lines 115-120
                ProjectProjectionBuilder.WithoutAccount()
                    .WithId(TestIds.ProjectGammaId)
                    .WithName("Project Gamma")
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build()
            };

            // ----------------------------------------------------------------
            // Tasks: 3 tasks (regular, multi-project, under accountless project)
            // ----------------------------------------------------------------
            var tasks = new List<TaskProjection>
            {
                // Task 1 — regular task with Development type
                new TaskProjectionBuilder()
                    .WithId(TestIds.Task1Id)
                    .WithSubject("Implement feature X")
                    .WithTaskTypeLabel("Development")
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Task 2 — multi-project task (appears under both Alpha and Beta)
                // Tests task-project splitting logic from ReportService.cs lines 75-95
                new TaskProjectionBuilder()
                    .WithId(TestIds.Task2Id)
                    .WithSubject("Fix bug Y")
                    .WithTaskTypeLabel("Bug Fix")
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Task 3 — under project without account (Project Gamma)
                // Tests the missing account error scenario
                new TaskProjectionBuilder()
                    .WithId(TestIds.Task3Id)
                    .WithSubject("Design review")
                    .WithTaskTypeLabel("Review")
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build()
            };

            // ----------------------------------------------------------------
            // Timelogs: 6 entries (5 January 2024 + 1 February 2024)
            //
            // Business rules exercised:
            // - Billable vs non-billable: Timelog 1 (billable) + Timelog 2 (non-billable) for Task 1
            // - Multi-project task: Timelog 3 (Task 2 under Alpha) + Timelog 4 (Task 2 under Beta)
            // - Missing account: Timelog 5 (Task 3 under Gamma with null AccountId)
            // - Month filtering: Timelog 6 (February) excluded from January reports
            // ----------------------------------------------------------------
            var timelogs = new List<TimelogProjection>
            {
                // Timelog 1: Task 1, Project Alpha, billable, 120 min, Jan 15
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(TestIds.Timelog1Id)
                    .WithTaskId(TestIds.Task1Id)
                    .WithProjectId(TestIds.ProjectAlphaId)
                    .WithAccountId(TestIds.AccountAlphaId)
                    .WithMinutes(120)
                    .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Timelog 2: Task 1, Project Alpha, NON-billable, 60 min, Jan 15
                // Tests billable/non-billable splitting aggregation
                TimelogProjectionBuilder.NonBillableTimelog()
                    .WithId(TestIds.Timelog2Id)
                    .WithTaskId(TestIds.Task1Id)
                    .WithProjectId(TestIds.ProjectAlphaId)
                    .WithAccountId(TestIds.AccountAlphaId)
                    .WithMinutes(60)
                    .WithLoggedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Timelog 3: Task 2, Project Alpha (multi-project task, alpha side), billable, 90 min, Jan 20
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(TestIds.Timelog3Id)
                    .WithTaskId(TestIds.Task2Id)
                    .WithProjectId(TestIds.ProjectAlphaId)
                    .WithAccountId(TestIds.AccountAlphaId)
                    .WithMinutes(90)
                    .WithLoggedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Timelog 4: Task 2, Project Beta (multi-project task, beta side), billable, 45 min, Jan 20
                // Tests multi-project task splitting
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(TestIds.Timelog4Id)
                    .WithTaskId(TestIds.Task2Id)
                    .WithProjectId(TestIds.ProjectBetaId)
                    .WithAccountId(TestIds.AccountBetaId)
                    .WithMinutes(45)
                    .WithLoggedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Timelog 5: Task 3, Project Gamma (no account), billable, 30 min, Jan 25
                // Tests missing account error scenario
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(TestIds.Timelog5Id)
                    .WithTaskId(TestIds.Task3Id)
                    .WithProjectId(TestIds.ProjectGammaId)
                    .WithAccountId(null)
                    .WithMinutes(30)
                    .WithLoggedOn(new DateTime(2024, 1, 25, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(new DateTime(2024, 1, 25, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 25, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Timelog 6: Task 1, Project Alpha, billable, 180 min, FEBRUARY 10
                // Tests month range filtering — this entry should be EXCLUDED
                // from January 2024 reports (ReportService.cs lines 27-30)
                TimelogProjectionBuilder.BillableTimelog()
                    .WithId(TestIds.Timelog6Id)
                    .WithTaskId(TestIds.Task1Id)
                    .WithProjectId(TestIds.ProjectAlphaId)
                    .WithAccountId(TestIds.AccountAlphaId)
                    .WithMinutes(180)
                    .WithLoggedOn(new DateTime(2024, 2, 10, 0, 0, 0, DateTimeKind.Utc))
                    .WithCreatedOn(new DateTime(2024, 2, 10, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 2, 10, 0, 0, 0, DateTimeKind.Utc))
                    .Build()
            };

            // ----------------------------------------------------------------
            // Report Definitions: 2 stored report configurations
            // ----------------------------------------------------------------
            var reports = new List<ReportDefinition>
            {
                // Monthly timelog report for January 2024
                ReportDefinitionBuilder.MonthlyTimelogReport(2024, 1)
                    .WithId(TestIds.MonthlyReportId)
                    .WithCreatedBy(TestIds.FirstUserId)
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build(),

                // Account-filtered timelog report for January 2024, Account Alpha
                ReportDefinitionBuilder.AccountTimelogReport(2024, 1, TestIds.AccountAlphaId)
                    .WithId(TestIds.AccountReportId)
                    .WithCreatedBy(TestIds.FirstUserId)
                    .WithCreatedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .WithLastModifiedOn(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Build()
            };

            return (projects, tasks, timelogs, reports);
        }
    }

    #endregion
}
