using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Entities
{
    /// <summary>
    /// Strongly-typed domain entity representing the <c>task</c> entity in the Project
    /// microservice. Extracted from the monolith's dynamic entity system where tasks were
    /// created via <c>EntityManager.CreateEntity()</c> calls in
    /// <c>NextPlugin.20190203.cs</c> (lines 2696–3404) with additional fields added by
    /// <c>NextPlugin.20190205.cs</c> (recurrence_template field).
    ///
    /// <para>
    /// Maps to the PostgreSQL <c>rec_task</c> table in the Project service database using
    /// EF Core <see cref="TableAttribute"/> and <see cref="ColumnAttribute"/> annotations.
    /// JSON serialization uses <see cref="JsonPropertyAttribute"/> from Newtonsoft.Json to
    /// maintain backward compatibility with the monolith REST API v3 response shapes.
    /// </para>
    ///
    /// <para><b>Cross-Service References:</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="OwnerId"/> and <see cref="CreatedBy"/> reference the <c>user</c> entity
    ///     in the Core service — stored as UUID only, resolved via gRPC/HTTP at read time.
    ///   </description></item>
    ///   <item><description>
    ///     The <c>project_nn_task</c> relation crosses service boundaries when account data
    ///     is needed — project_id is denormalized locally and synced via domain events.
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>Entity ID:</b> 9386226e-381e-4522-b27b-fb5514d77902</para>
    /// </summary>
    [Table("rec_task")]
    public class TaskEntity
    {
        #region Entity Metadata Constants

        /// <summary>
        /// Well-known entity identifier for the task entity.
        /// Source: <c>entity.Id = new Guid("9386226e-381e-4522-b27b-fb5514d77902")</c>
        /// in NextPlugin.20190203.cs line 2699.
        /// </summary>
        public static readonly Guid EntityId = new Guid("9386226e-381e-4522-b27b-fb5514d77902");

        /// <summary>Entity system name used in EQL queries and record operations.</summary>
        public const string EntityName = "task";

        /// <summary>Singular display label for UI rendering.</summary>
        public const string EntityLabel = "Task";

        /// <summary>Plural display label for list views.</summary>
        public const string EntityLabelPlural = "Tasks";

        /// <summary>
        /// FontAwesome icon class for UI rendering.
        /// Updated from initial "fas fa-user-cog" via later patch to current value.
        /// </summary>
        public const string EntityIconName = "fas fa-tasks";

        /// <summary>
        /// Brand color (hex) for UI rendering of this entity.
        /// Updated from initial "#009688" via later patch to current value.
        /// </summary>
        public const string EntityColor = "#f44336";

        #endregion

        #region Field Identifier Constants

        /// <summary>
        /// Contains the well-known field GUIDs for all task entity fields,
        /// preserving the original identifiers from the monolith's entity system.
        /// These IDs are used for field-level metadata lookups and migration references.
        /// </summary>
        public static class FieldIds
        {
            /// <summary>System field ID for the 'id' primary key field.</summary>
            public static readonly Guid Id = new Guid("0e540cf8-2de6-419c-8ed1-42b8c637d191");

            /// <summary>Field ID for 'l_scope' — text field, optional, system.</summary>
            public static readonly Guid LScope = new Guid("94d069fd-04c1-4a3e-a735-867225df364d");

            /// <summary>Field ID for 'subject' — text field, required, system.</summary>
            public static readonly Guid Subject = new Guid("0b7a6ede-3439-4826-9438-05da4a428f98");

            /// <summary>Field ID for 'body' — HTML field, optional, system.</summary>
            public static readonly Guid Body = new Guid("e88fcb5d-f581-49d9-81e7-6840200ed3c1");

            /// <summary>Field ID for 'created_on' — datetime field, required, UseCurrentTimeAsDefault.</summary>
            public static readonly Guid CreatedOn = new Guid("0047b19a-d691-468f-9125-e7d29d3edcd1");

            /// <summary>Field ID for 'created_by' — guid field, required, default Guid.Empty.</summary>
            public static readonly Guid CreatedBy = new Guid("8f864e2e-8b03-4b82-b80b-b7f7537005cf");

            /// <summary>Field ID for 'completed_on' — datetime field, optional, system.</summary>
            public static readonly Guid CompletedOn = new Guid("ad426e80-56f9-45b5-bb48-5ced8b39918b");

            /// <summary>Field ID for 'number' — auto-number field, required, starting 1.0.</summary>
            public static readonly Guid Number = new Guid("b27da2eb-d872-44c8-ba54-58d2e05c298f");

            /// <summary>Field ID for 'parent_id' — guid field, optional, system.</summary>
            public static readonly Guid ParentId = new Guid("95a24e9c-4505-4b41-8a6d-55d13896504e");

            /// <summary>Field ID for 'status_id' — guid field, required, system.</summary>
            public static readonly Guid StatusId = new Guid("686dd7fd-6280-4dd3-bb20-1126179f261e");

            /// <summary>Field ID for 'key' — text field, required, unique, system.</summary>
            public static readonly Guid Key = new Guid("35de5afe-39d8-4c0c-9b11-4151d3fddb13");

            /// <summary>Field ID for 'x_search' — text field, optional, searchable, system.</summary>
            public static readonly Guid XSearch = new Guid("4141d7d2-a35c-418a-a13e-426fc6cc072f");

            /// <summary>Field ID for 'estimated_minutes' — number field, decimalPlaces=0.</summary>
            public static readonly Guid EstimatedMinutes = new Guid("873ef699-8921-434d-97ff-97c42d011b18");

            /// <summary>Field ID for 'x_billable_minutes' — number field, decimalPlaces=0.</summary>
            public static readonly Guid XBillableMinutes = new Guid("3398512d-1f0e-48a9-b5e8-ee36920c70ba");

            /// <summary>Field ID for 'x_nonbillable_minutes' — number field, decimalPlaces=0.</summary>
            public static readonly Guid XNonbillableMinutes = new Guid("9ba52854-fe79-483e-8142-4aa2ae85008b");

            /// <summary>Field ID for 'priority' — select field, required, default "1".</summary>
            public static readonly Guid Priority = new Guid("ce81430c-bf33-4d8d-bfcd-5c4be13700d3");

            /// <summary>Field ID for 'timelog_started_on' — datetime field, optional.</summary>
            public static readonly Guid TimelogStartedOn = new Guid("41266a06-98a0-4e48-9d47-a7fe20bc3c3f");

            /// <summary>Field ID for 'owner_id' — guid field, optional, default Guid.Empty.</summary>
            public static readonly Guid OwnerId = new Guid("aa486ab3-5510-4373-90b9-5285a6c6468f");

            /// <summary>Field ID for 'type_id' — guid field, required, default task type.</summary>
            public static readonly Guid TypeId = new Guid("955ed90c-c158-4423-a766-33646ce1d7e7");

            /// <summary>Field ID for 'start_time' — datetime field, optional, searchable.</summary>
            public static readonly Guid StartTime = new Guid("6f4a77ba-1ac2-4d9a-934e-ed5f9026102a");

            /// <summary>Field ID for 'end_time' — datetime field, optional.</summary>
            public static readonly Guid EndTime = new Guid("452dd069-6353-48af-ba37-6fa7672c59a4");

            /// <summary>Field ID for 'recurrence_id' — guid field, optional.</summary>
            public static readonly Guid RecurrenceId = new Guid("36a9a76e-9be7-4796-b5b9-e8485fe61c4a");

            /// <summary>Field ID for 'recurrence_template' — text field, optional (added in patch 20190205).</summary>
            public static readonly Guid RecurrenceTemplate = new Guid("9973acd9-86eb-41de-8c93-295b17876adb");

            /// <summary>Field ID for 'reserve_time' — checkbox field, required, default false.</summary>
            public static readonly Guid ReserveTime = new Guid("e1ba8fa3-1aba-4563-8baa-0a2fe409178d");
        }

        #endregion

        #region Instance Properties

        /// <summary>
        /// Primary key for the task record. Application-generated GUID, stored
        /// as UUID in PostgreSQL. System field with well-known ID
        /// <c>0e540cf8-2de6-419c-8ed1-42b8c637d191</c>.
        /// </summary>
        [Key]
        [Column("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Scope identifier for multi-project scoping. Stores a JSON array string
        /// (e.g., <c>'["projects"]'</c>) identifying which scope this task belongs to.
        /// Optional, not searchable, system field.
        /// </summary>
        [Column("l_scope")]
        [JsonProperty(PropertyName = "l_scope")]
        public string LScope { get; set; }

        /// <summary>
        /// Task subject/title. Required field.
        /// Source: TextField, Required=true, System=true, DefaultValue="subject".
        /// </summary>
        [Column("subject")]
        [JsonProperty(PropertyName = "subject")]
        public string Subject { get; set; }

        /// <summary>
        /// HTML body content of the task. Optional rich-text field.
        /// Source: HtmlField, Required=false, System=true.
        /// </summary>
        [Column("body")]
        [JsonProperty(PropertyName = "body")]
        public string Body { get; set; }

        /// <summary>
        /// Timestamp when the task was created. Required, with
        /// <c>UseCurrentTimeAsDefaultValue = true</c> in the monolith.
        /// Display format: "yyyy-MMM-dd HH:mm".
        /// </summary>
        [Column("created_on")]
        [JsonProperty(PropertyName = "created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Cross-service reference to the user who created this task.
        /// Points to the <c>user</c> entity in the Core service.
        /// Stored as UUID only — no FK constraint. Resolved via Core gRPC call at read time.
        /// Source: GuidField, Required=true, DefaultValue=Guid.Empty, System=true.
        /// </summary>
        [Column("created_by")]
        [JsonProperty(PropertyName = "created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// Timestamp when the task was completed. Null if not yet completed.
        /// Display format: "yyyy-MMM-dd HH:mm".
        /// Source: DateTimeField, Required=false, System=true.
        /// </summary>
        [Column("completed_on")]
        [JsonProperty(PropertyName = "completed_on")]
        public DateTime? CompletedOn { get; set; }

        /// <summary>
        /// Auto-incremented task number within scope. Stored as decimal to match
        /// the monolith's <c>AutoNumberField</c> type (starting value 1.0, format "{0}").
        /// Source: AutoNumberField, Required=true, StartingNumber=1.0.
        /// </summary>
        [Column("number")]
        [JsonProperty(PropertyName = "number")]
        public decimal Number { get; set; }

        /// <summary>
        /// Self-referencing parent task ID for task hierarchies.
        /// Null for root-level tasks.
        /// Source: GuidField, Required=false, System=true.
        /// </summary>
        [Column("parent_id")]
        [JsonProperty(PropertyName = "parent_id")]
        public Guid? ParentId { get; set; }

        /// <summary>
        /// Reference to the task's status via <c>task_status_1n_task</c> relation.
        /// Intra-service relation within the Project microservice.
        /// Source: GuidField, Required=true, System=true.
        /// </summary>
        [Column("status_id")]
        [JsonProperty(PropertyName = "status_id")]
        public Guid StatusId { get; set; }

        /// <summary>
        /// Human-readable task key (e.g., "PRJ-123"). Composed from project
        /// abbreviation and task number. Required, unique.
        /// Source: TextField, Required=true, Unique=true, System=true.
        /// </summary>
        [Column("key")]
        [JsonProperty(PropertyName = "key")]
        public string Key { get; set; }

        /// <summary>
        /// Full-text search index field. Regenerated by the SearchService to include
        /// denormalized data from related entities (owner name, project name, etc.).
        /// Searchable=true in the monolith for EQL CONTAINS queries.
        /// Source: TextField, Required=false, Searchable=true, System=true.
        /// </summary>
        [Column("x_search")]
        [JsonProperty(PropertyName = "x_search")]
        public string XSearch { get; set; }

        /// <summary>
        /// Estimated effort in minutes. DecimalPlaces=0 in the monolith.
        /// Null when no estimate is provided.
        /// Source: NumberField, DecimalPlaces=0, System=true.
        /// </summary>
        [Column("estimated_minutes")]
        [JsonProperty(PropertyName = "estimated_minutes")]
        public decimal? EstimatedMinutes { get; set; }

        /// <summary>
        /// Aggregated billable time in minutes logged against this task.
        /// DecimalPlaces=0 in the monolith. Recalculated from timelog records.
        /// Source: NumberField, DecimalPlaces=0, System=true.
        /// </summary>
        [Column("x_billable_minutes")]
        [JsonProperty(PropertyName = "x_billable_minutes")]
        public decimal? XBillableMinutes { get; set; }

        /// <summary>
        /// Aggregated non-billable time in minutes logged against this task.
        /// DecimalPlaces=0 in the monolith. Recalculated from timelog records.
        /// Source: NumberField, DecimalPlaces=0, System=true.
        /// </summary>
        [Column("x_nonbillable_minutes")]
        [JsonProperty(PropertyName = "x_nonbillable_minutes")]
        public decimal? XNonbillableMinutes { get; set; }

        /// <summary>
        /// Priority level as a select field value. Valid values: "1" (low),
        /// "2" (medium), "3" (high). See <see cref="PriorityOptions"/> for the
        /// complete option definitions including labels, icons, and colors.
        /// Source: SelectField, Required=true, DefaultValue="1", System=true.
        /// </summary>
        [Column("priority")]
        [JsonProperty(PropertyName = "priority")]
        public string Priority { get; set; }

        /// <summary>
        /// Timestamp when a timelog was started for this task. Null when no
        /// active timelog is in progress. Display format: "yyyy-MMM-dd HH:mm".
        /// Source: DateTimeField, Required=false, System=true.
        /// </summary>
        [Column("timelog_started_on")]
        [JsonProperty(PropertyName = "timelog_started_on")]
        public DateTime? TimelogStartedOn { get; set; }

        /// <summary>
        /// Cross-service reference to the user who owns/is assigned to this task.
        /// Points to the <c>user</c> entity in the Core service.
        /// Stored as UUID only — no FK constraint. Resolved via Core gRPC call at read time.
        /// Null when unassigned.
        /// Source: GuidField, Required=false, DefaultValue=Guid.Empty, System=true.
        /// </summary>
        [Column("owner_id")]
        [JsonProperty(PropertyName = "owner_id")]
        public Guid? OwnerId { get; set; }

        /// <summary>
        /// Reference to the task type via <c>task_type_1n_task</c> relation.
        /// Intra-service relation within the Project microservice.
        /// Default value <c>a0465e9f-5d5f-433d-acf1-1da0eaec78b4</c> represents the
        /// standard task type created during initial entity provisioning.
        /// Source: GuidField, Required=true, System=true.
        /// </summary>
        [Column("type_id")]
        [JsonProperty(PropertyName = "type_id")]
        public Guid TypeId { get; set; }

        /// <summary>
        /// Scheduled start date/time for the task. Searchable in the monolith
        /// for calendar and timeline queries. Display format: "yyyy-MMM-dd HH:mm".
        /// Source: DateTimeField, Required=false, Searchable=true, System=true.
        /// </summary>
        [Column("start_time")]
        [JsonProperty(PropertyName = "start_time")]
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// Scheduled end/due date/time for the task.
        /// Display format: "yyyy-MMM-dd HH:mm".
        /// Source: DateTimeField, Required=false, System=true.
        /// </summary>
        [Column("end_time")]
        [JsonProperty(PropertyName = "end_time")]
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// Reference to a recurrence plan (Ical.Net based). Null for non-recurring tasks.
        /// Source: GuidField, Required=false, System=true.
        /// </summary>
        [Column("recurrence_id")]
        [JsonProperty(PropertyName = "recurrence_id")]
        public Guid? RecurrenceId { get; set; }

        /// <summary>
        /// Template string for recurrence pattern configuration. Used alongside
        /// <see cref="RecurrenceId"/> for defining recurring task schedules.
        /// Added in patch 20190205.
        /// Source: TextField, Required=false, System=true.
        /// </summary>
        [Column("recurrence_template")]
        [JsonProperty(PropertyName = "recurrence_template")]
        public string RecurrenceTemplate { get; set; }

        /// <summary>
        /// Whether this task reserves time exclusively, preventing other tasks
        /// from being performed in the same time period. Used for scheduling
        /// and calendar blocking.
        /// Source: CheckboxField, Required=true, DefaultValue=false, System=true.
        /// HelpText: "Whether other tasks can be performed in the same time period".
        /// </summary>
        [Column("reserve_time")]
        [JsonProperty(PropertyName = "reserve_time")]
        public bool ReserveTime { get; set; }

        /// <summary>
        /// JSON-encoded list of related record references used for cross-entity
        /// linking within the project domain. Searchable=true for full-text search.
        /// Source: TextField, Required=false, Searchable=true, System=true.
        /// </summary>
        [Column("l_related_records")]
        [JsonProperty(PropertyName = "l_related_records")]
        public string LRelatedRecords { get; set; }

        #endregion

        #region Permission Constants

        /// <summary>
        /// Role IDs that have permission to create task records.
        /// Contains both Administrator and Regular role IDs from the monolith's
        /// <c>RecordPermissions.CanCreate</c> configuration.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanCreateRoles = new List<Guid>
        {
            SystemIds.AdministratorRoleId,
            SystemIds.RegularRoleId
        }.AsReadOnly();

        /// <summary>
        /// Role IDs that have permission to read task records.
        /// Contains both Administrator and Regular role IDs from the monolith's
        /// <c>RecordPermissions.CanRead</c> configuration.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanReadRoles = new List<Guid>
        {
            SystemIds.AdministratorRoleId,
            SystemIds.RegularRoleId
        }.AsReadOnly();

        /// <summary>
        /// Role IDs that have permission to update task records.
        /// Contains both Administrator and Regular role IDs from the monolith's
        /// <c>RecordPermissions.CanUpdate</c> configuration.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanUpdateRoles = new List<Guid>
        {
            SystemIds.AdministratorRoleId,
            SystemIds.RegularRoleId
        }.AsReadOnly();

        /// <summary>
        /// Role IDs that have permission to delete task records.
        /// Contains both Administrator and Regular role IDs from the monolith's
        /// <c>RecordPermissions.CanDelete</c> configuration.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanDeleteRoles = new List<Guid>
        {
            SystemIds.AdministratorRoleId,
            SystemIds.RegularRoleId
        }.AsReadOnly();

        #endregion

        #region Priority Options

        /// <summary>
        /// Represents a single priority select option with its display metadata.
        /// Mirrors the monolith's <c>SelectOption</c> definition from the priority
        /// <c>InputSelectField</c> in NextPlugin.20190203.cs (lines 3173-3178).
        /// </summary>
        public sealed class PriorityOption
        {
            /// <summary>Human-readable label (e.g., "low", "medium", "high").</summary>
            [JsonProperty(PropertyName = "label")]
            public string Label { get; }

            /// <summary>Stored value in the database (e.g., "1", "2", "3").</summary>
            [JsonProperty(PropertyName = "value")]
            public string Value { get; }

            /// <summary>FontAwesome icon CSS class for UI rendering.</summary>
            [JsonProperty(PropertyName = "iconClass")]
            public string IconClass { get; }

            /// <summary>Hex color code for UI rendering.</summary>
            [JsonProperty(PropertyName = "color")]
            public string Color { get; }

            /// <summary>
            /// Initializes a new priority option with all display metadata.
            /// </summary>
            /// <param name="label">Human-readable label.</param>
            /// <param name="value">Database-stored value.</param>
            /// <param name="iconClass">FontAwesome CSS class.</param>
            /// <param name="color">Hex color code.</param>
            public PriorityOption(string label, string value, string iconClass, string color)
            {
                Label = label ?? throw new ArgumentNullException(nameof(label));
                Value = value ?? throw new ArgumentNullException(nameof(value));
                IconClass = iconClass ?? throw new ArgumentNullException(nameof(iconClass));
                Color = color ?? throw new ArgumentNullException(nameof(color));
            }
        }

        /// <summary>
        /// Static list of all available priority options for the task entity,
        /// preserving the exact values from the monolith's <c>InputSelectField.Options</c>.
        /// <list type="bullet">
        ///   <item><description>Value "1" → low priority (green, arrow-circle-down)</description></item>
        ///   <item><description>Value "2" → medium priority (blue, minus-circle)</description></item>
        ///   <item><description>Value "3" → high priority (red, arrow-circle-up)</description></item>
        /// </list>
        /// </summary>
        public static readonly IReadOnlyList<PriorityOption> PriorityOptions = new List<PriorityOption>
        {
            new PriorityOption(label: "low", value: "1", iconClass: "fas fa-fw fa-arrow-circle-down", color: "#4CAF50"),
            new PriorityOption(label: "medium", value: "2", iconClass: "fa fa-fw fa-minus-circle", color: "#2196F3"),
            new PriorityOption(label: "high", value: "3", iconClass: "fas fa-fw fa-arrow-circle-up", color: "#F44336")
        }.AsReadOnly();

        #endregion

        #region Relation Metadata

        /// <summary>
        /// Contains well-known relation names that involve the task entity.
        /// These names correspond to the <c>EntityRelationManager.Create()</c> calls
        /// in the monolith's patch system and are used for EQL relation traversal
        /// queries and cross-service event routing.
        /// </summary>
        public static class Relations
        {
            /// <summary>
            /// ManyToMany relation between project and task entities.
            /// Manages which tasks belong to which projects.
            /// Cross-service note: project data may originate from CRM service events.
            /// </summary>
            public const string ProjectNnTask = "project_nn_task";

            /// <summary>
            /// ManyToMany relation between user and task entities (watchers).
            /// Cross-service reference: user records reside in Core service.
            /// </summary>
            public const string UserNnTaskWatchers = "user_nn_task_watchers";

            /// <summary>
            /// OneToMany relation from task_status to task.
            /// Intra-service relation within the Project microservice.
            /// </summary>
            public const string TaskStatus1nTask = "task_status_1n_task";

            /// <summary>
            /// OneToMany relation from task_type to task.
            /// Intra-service relation within the Project microservice.
            /// </summary>
            public const string TaskType1nTask = "task_type_1n_task";

            /// <summary>
            /// OneToMany relation from user to task (owner assignment).
            /// Cross-service reference: user records reside in Core service.
            /// </summary>
            public const string User1nTask = "user_1n_task";

            /// <summary>
            /// OneToMany relation from user to task (creator tracking).
            /// Cross-service reference: user records reside in Core service.
            /// </summary>
            public const string User1nTaskCreator = "user_1n_task_creator";

            /// <summary>
            /// ManyToMany relation between milestone and task entities.
            /// Intra-service relation within the Project microservice.
            /// </summary>
            public const string MilestoneNnTask = "milestone_nn_task";
        }

        #endregion

        #region EntityRecord Interop

        /// <summary>
        /// Converts this strongly-typed <see cref="TaskEntity"/> instance into an
        /// <see cref="EntityRecord"/> (Expando-based dynamic property bag) for
        /// interoperability with the ERP platform's dynamic record CRUD system.
        /// All property names match the monolith's field names exactly.
        /// </summary>
        /// <returns>
        /// A new <see cref="EntityRecord"/> with all task field values set as
        /// dynamic properties using their database column names as keys.
        /// </returns>
        public EntityRecord ToEntityRecord()
        {
            var record = new EntityRecord();
            record["id"] = Id;
            record["l_scope"] = LScope;
            record["subject"] = Subject;
            record["body"] = Body;
            record["created_on"] = CreatedOn;
            record["created_by"] = CreatedBy;
            record["completed_on"] = CompletedOn;
            record["number"] = Number;
            record["parent_id"] = ParentId;
            record["status_id"] = StatusId;
            record["key"] = Key;
            record["x_search"] = XSearch;
            record["estimated_minutes"] = EstimatedMinutes;
            record["x_billable_minutes"] = XBillableMinutes;
            record["x_nonbillable_minutes"] = XNonbillableMinutes;
            record["priority"] = Priority;
            record["timelog_started_on"] = TimelogStartedOn;
            record["owner_id"] = OwnerId;
            record["type_id"] = TypeId;
            record["start_time"] = StartTime;
            record["end_time"] = EndTime;
            record["recurrence_id"] = RecurrenceId;
            record["recurrence_template"] = RecurrenceTemplate;
            record["reserve_time"] = ReserveTime;
            record["l_related_records"] = LRelatedRecords;
            return record;
        }

        /// <summary>
        /// Creates a new <see cref="TaskEntity"/> instance from an
        /// <see cref="EntityRecord"/> (Expando-based dynamic property bag), performing
        /// safe type conversion for each field. Handles null values and missing keys
        /// gracefully — properties remain at their default values when the corresponding
        /// record key is absent or null.
        /// </summary>
        /// <param name="record">
        /// The dynamic record to convert. Must not be null.
        /// </param>
        /// <returns>
        /// A new <see cref="TaskEntity"/> populated from the record's dynamic properties.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="record"/> is null.
        /// </exception>
        public static TaskEntity FromEntityRecord(EntityRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            var task = new TaskEntity();

            // Primary key
            if (record["id"] is Guid id)
                task.Id = id;

            // String fields — safe cast with 'as' returns null for missing/wrong-typed values
            task.LScope = record["l_scope"] as string;
            task.Subject = record["subject"] as string;
            task.Body = record["body"] as string;
            task.Key = record["key"] as string;
            task.XSearch = record["x_search"] as string;
            task.Priority = record["priority"] as string;
            task.RecurrenceTemplate = record["recurrence_template"] as string;
            task.LRelatedRecords = record["l_related_records"] as string;

            // Required DateTime fields
            if (record["created_on"] is DateTime createdOn)
                task.CreatedOn = createdOn;

            // Required Guid fields
            if (record["created_by"] is Guid createdBy)
                task.CreatedBy = createdBy;

            if (record["status_id"] is Guid statusId)
                task.StatusId = statusId;

            if (record["type_id"] is Guid typeId)
                task.TypeId = typeId;

            // Required decimal field
            if (record["number"] is decimal number)
                task.Number = number;

            // Nullable DateTime fields
            if (record["completed_on"] is DateTime completedOn)
                task.CompletedOn = completedOn;

            if (record["timelog_started_on"] is DateTime timelogStartedOn)
                task.TimelogStartedOn = timelogStartedOn;

            if (record["start_time"] is DateTime startTime)
                task.StartTime = startTime;

            if (record["end_time"] is DateTime endTime)
                task.EndTime = endTime;

            // Nullable Guid fields
            if (record["parent_id"] is Guid parentId)
                task.ParentId = parentId;

            if (record["owner_id"] is Guid ownerId)
                task.OwnerId = ownerId;

            if (record["recurrence_id"] is Guid recurrenceId)
                task.RecurrenceId = recurrenceId;

            // Nullable decimal fields
            if (record["estimated_minutes"] is decimal estimatedMinutes)
                task.EstimatedMinutes = estimatedMinutes;

            if (record["x_billable_minutes"] is decimal billableMinutes)
                task.XBillableMinutes = billableMinutes;

            if (record["x_nonbillable_minutes"] is decimal nonbillableMinutes)
                task.XNonbillableMinutes = nonbillableMinutes;

            // Boolean field
            if (record["reserve_time"] is bool reserveTime)
                task.ReserveTime = reserveTime;

            return task;
        }

        #endregion
    }
}
