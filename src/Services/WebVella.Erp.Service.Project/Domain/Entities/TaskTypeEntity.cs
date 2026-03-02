using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Entities
{
    /// <summary>
    /// Strongly-typed domain entity class for the <c>task_type</c> lookup entity.
    /// Extracted from <c>NextPlugin.20190203.cs</c> (entity definition lines 2419–2695, seed data lines 11337–11495)
    /// and <c>NextPlugin.20190222.cs</c> (seed data updates with normalized labels, icons, colors, and sort values).
    ///
    /// The task_type entity is a lookup/classification table that categorizes task records
    /// in the Project microservice. Each task references a task_type via the <c>type_id</c> field
    /// through the <c>task_type_1n_task</c> one-to-many relation.
    ///
    /// Entity metadata from source:
    ///   Entity ID:    35999e55-821c-4798-8e8f-29d8c672c9b9
    ///   Name:         task_type
    ///   Label:        Task type / Task types
    ///   System:       true
    ///   Icon:         far fa-dot-circle
    ///   Color:        #f44336
    /// </summary>
    [Table("rec_task_type")]
    public class TaskTypeEntity
    {
        #region Entity Metadata Constants

        /// <summary>
        /// Well-known entity identifier for the task_type entity.
        /// Source: NextPlugin.20190203.cs line 2422.
        /// </summary>
        public static readonly Guid EntityId = new Guid("35999e55-821c-4798-8e8f-29d8c672c9b9");

        /// <summary>
        /// Entity name used in EQL queries and record manager operations.
        /// Source: NextPlugin.20190203.cs line 2423.
        /// </summary>
        public const string EntityName = "task_type";

        /// <summary>
        /// Display label for the entity (singular form).
        /// Source: NextPlugin.20190203.cs line 2424.
        /// </summary>
        public const string EntityLabel = "Task type";

        /// <summary>
        /// Display label for the entity (plural form).
        /// Source: NextPlugin.20190203.cs line 2425.
        /// </summary>
        public const string EntityLabelPlural = "Task types";

        /// <summary>
        /// Indicates this is a system-managed entity that cannot be deleted through the UI.
        /// Source: NextPlugin.20190203.cs line 2426.
        /// </summary>
        public const bool EntityIsSystem = true;

        /// <summary>
        /// CSS icon class used for entity representation in the admin UI.
        /// Source: NextPlugin.20190203.cs line 2427.
        /// </summary>
        public const string EntityIconName = "far fa-dot-circle";

        /// <summary>
        /// Hex color code used for entity representation in the admin UI.
        /// Source: NextPlugin.20190203.cs line 2428.
        /// </summary>
        public const string EntityColor = "#f44336";

        #endregion

        #region Field GUID Constants

        /// <summary>
        /// GUID for the system <c>id</c> primary key field.
        /// Source: NextPlugin.20190203.cs line 2421 (systemFieldIdDictionary["id"]).
        /// </summary>
        public static readonly Guid IdFieldId = new Guid("6ad6d228-1714-4c02-a8a5-b22ddaa6a97f");

        /// <summary>
        /// GUID for the <c>is_default</c> checkbox field.
        /// Source: NextPlugin.20190203.cs line 2456.
        /// </summary>
        public static readonly Guid IsDefaultFieldId = new Guid("ab08ae57-de06-4788-999d-32d42cd4b75e");

        /// <summary>
        /// GUID for the <c>l_scope</c> text field.
        /// Source: NextPlugin.20190203.cs line 2485.
        /// </summary>
        public static readonly Guid LScopeFieldId = new Guid("133f2fc0-b3ca-44f0-8624-724493ec4de5");

        /// <summary>
        /// GUID for the <c>label</c> text field. Also serves as the RecordScreenIdField for the entity.
        /// Source: NextPlugin.20190203.cs line 2515 (field), line 2429 (RecordScreenIdField).
        /// </summary>
        public static readonly Guid LabelFieldId = new Guid("ddbf3b6f-8f09-4e37-95e2-e71de7ca5d3c");

        /// <summary>
        /// GUID for the <c>sort_index</c> number field.
        /// Source: NextPlugin.20190203.cs line 2545.
        /// </summary>
        public static readonly Guid SortIndexFieldId = new Guid("ef3aa457-ef03-4942-81d5-57753e1fc226");

        /// <summary>
        /// GUID for the <c>is_system</c> checkbox field.
        /// Source: NextPlugin.20190203.cs line 2577.
        /// </summary>
        public static readonly Guid IsSystemFieldId = new Guid("6fa4d8a2-60ad-4882-9f06-e44dfc83266e");

        /// <summary>
        /// GUID for the <c>is_enabled</c> checkbox field.
        /// Source: NextPlugin.20190203.cs line 2606.
        /// </summary>
        public static readonly Guid IsEnabledFieldId = new Guid("22d47a37-7c03-4d6a-bb8a-0e76fd5a3371");

        /// <summary>
        /// GUID for the <c>icon_class</c> text field.
        /// Source: NextPlugin.20190203.cs line 2635.
        /// </summary>
        public static readonly Guid IconClassFieldId = new Guid("525efe80-eb47-42e7-8f34-910ab13afa29");

        /// <summary>
        /// GUID for the <c>color</c> text field.
        /// Source: NextPlugin.20190203.cs line 2665.
        /// </summary>
        public static readonly Guid ColorFieldId = new Guid("8026ba91-abb2-4bdf-aa5f-134285b4a959");

        #endregion

        #region Permission Constants

        /// <summary>
        /// Role GUIDs authorized to create task_type records.
        /// Contains RegularRoleId and AdministratorRoleId from <see cref="SystemIds"/>.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanCreateRoles = new List<Guid>
        {
            SystemIds.RegularRoleId,
            SystemIds.AdministratorRoleId
        }.AsReadOnly();

        /// <summary>
        /// Role GUIDs authorized to read task_type records.
        /// Contains RegularRoleId and AdministratorRoleId from <see cref="SystemIds"/>.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanReadRoles = new List<Guid>
        {
            SystemIds.RegularRoleId,
            SystemIds.AdministratorRoleId
        }.AsReadOnly();

        /// <summary>
        /// Role GUIDs authorized to update task_type records.
        /// Contains RegularRoleId and AdministratorRoleId from <see cref="SystemIds"/>.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanUpdateRoles = new List<Guid>
        {
            SystemIds.RegularRoleId,
            SystemIds.AdministratorRoleId
        }.AsReadOnly();

        /// <summary>
        /// Role GUIDs authorized to delete task_type records.
        /// Contains RegularRoleId and AdministratorRoleId from <see cref="SystemIds"/>.
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanDeleteRoles = new List<Guid>
        {
            SystemIds.RegularRoleId,
            SystemIds.AdministratorRoleId
        }.AsReadOnly();

        #endregion

        #region Seed Data Constants

        /// <summary>
        /// Default task type record ID used as the default value for <c>TaskEntity.TypeId</c>.
        /// Record label: "New Feature" (after 20190222 patch update).
        /// Source: NextPlugin.20190203.cs line 11461, updated in NextPlugin.20190222.cs line 19.
        /// </summary>
        public static readonly Guid DefaultTaskTypeId = new Guid("a0465e9f-5d5f-433d-acf1-1da0eaec78b4");

        /// <summary>
        /// Seed record ID for the "General" task type (sort_index=1.0, icon="fa fa-cog", color="#2196F3").
        /// Source: NextPlugin.20190203.cs line 11341, updated in NextPlugin.20190222.cs line 79.
        /// </summary>
        public static readonly Guid GeneralTaskTypeId = new Guid("da9bf72d-3655-4c51-9f99-047ef9297bf2");

        /// <summary>
        /// Seed record ID for the "Call" task type (sort_index=2.0, icon="fas fa-phone", color="#2196F3").
        /// Source: NextPlugin.20190203.cs line 11361, updated in NextPlugin.20190222.cs line 98.
        /// </summary>
        public static readonly Guid CallTaskTypeId = new Guid("7b191135-5fbb-4db9-bf24-1a5fc72d8cd5");

        /// <summary>
        /// Seed record ID for the "Email" task type (sort_index=3.0, icon="fa fa-envelope", color="#2196F3").
        /// Source: NextPlugin.20190203.cs line 11381, updated in NextPlugin.20190222.cs line 138.
        /// </summary>
        public static readonly Guid EmailTaskTypeId = new Guid("489b16e1-91b1-4a05-b247-50ed74f7aaaf");

        /// <summary>
        /// Seed record ID for the "Meeting" task type (sort_index=4.0, icon="fas fa-users", color="#2196F3").
        /// Source: NextPlugin.20190203.cs line 11401, updated in NextPlugin.20190222.cs line 118.
        /// </summary>
        public static readonly Guid MeetingTaskTypeId = new Guid("894ba1ef-1b31-440c-9b33-f301d047d8fb");

        /// <summary>
        /// Seed record ID for the "Send Quote" task type (sort_index=5.0).
        /// Source: NextPlugin.20190203.cs line 11421. Not updated in 20190222 patch.
        /// </summary>
        public static readonly Guid SendQuoteTaskTypeId = new Guid("ddb9c170-706d-4b17-a8ee-78ed3a544fa3");

        /// <summary>
        /// Seed record ID for the "New Feature" task type (sort_index=6.0, icon="fas fa-fw fa-plus-square", color="#4CAF50").
        /// This is the same record as <see cref="DefaultTaskTypeId"/> — it has is_default=true.
        /// Source: NextPlugin.20190203.cs line 11461, updated in NextPlugin.20190222.cs line 19.
        /// </summary>
        public static readonly Guid NewFeatureTaskTypeId = new Guid("a0465e9f-5d5f-433d-acf1-1da0eaec78b4");

        /// <summary>
        /// Seed record ID for the "Improvement" task type (sort_index=7.0, icon="far fa-fw fa-caret-square-up", color="#9C27B0").
        /// Source: NextPlugin.20190203.cs line 11441, updated in NextPlugin.20190222.cs line 39.
        /// </summary>
        public static readonly Guid ImprovementTaskTypeId = new Guid("6105dcf4-4115-435f-94bb-0190d45d1b87");

        /// <summary>
        /// Seed record ID for the "Bug" task type (sort_index=8.0, icon="fas fa-fw fa-bug", color="#F44336").
        /// Source: NextPlugin.20190203.cs line 11481, updated in NextPlugin.20190222.cs line 59.
        /// </summary>
        public static readonly Guid BugTaskTypeId = new Guid("c0a2554c-f59a-434e-be00-217a416f8efd");

        #endregion

        #region Relation Constants

        /// <summary>
        /// Relation ID for the <c>task_type_1n_task</c> one-to-many relationship.
        /// Origin: task_type.id → Target: task.type_id
        /// Relation type: OneToMany, System: true
        /// Source: NextPlugin.20190203.cs line 4936.
        /// </summary>
        public static readonly Guid TaskType1NTaskRelationId = new Guid("2925c7ea-72fe-4c12-a1f6-9baa9281141e");

        /// <summary>
        /// Relation name for the one-to-many relationship from task_type to task.
        /// Source: NextPlugin.20190203.cs line 4937.
        /// </summary>
        public const string TaskType1NTaskRelationName = "task_type_1n_task";

        #endregion

        #region Entity Properties

        /// <summary>
        /// Primary key identifier for the task type record.
        /// System field mapped to the <c>id</c> column in the <c>rec_task_type</c> table.
        /// Field GUID: 6ad6d228-1714-4c02-a8a5-b22ddaa6a97f
        /// </summary>
        [Key]
        [Column("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Indicates whether this is the default task type.
        /// Checkbox field — required: true, default: false, system: true.
        /// Field GUID: ab08ae57-de06-4788-999d-32d42cd4b75e
        /// </summary>
        [Column("is_default")]
        [JsonProperty(PropertyName = "is_default")]
        public bool IsDefault { get; set; }

        /// <summary>
        /// Scope filter for the task type (e.g., <c>["projects"]</c> for project-scoped types).
        /// Text field — required: false, searchable: false, system: true, default: null.
        /// Field GUID: 133f2fc0-b3ca-44f0-8624-724493ec4de5
        /// </summary>
        [Column("l_scope")]
        [JsonProperty(PropertyName = "l_scope")]
        public string LScope { get; set; }

        /// <summary>
        /// Display label for the task type (e.g., "Bug", "New Feature", "General").
        /// Text field — required: true, default: "label", system: true.
        /// Also serves as the RecordScreenIdField for the entity.
        /// Field GUID: ddbf3b6f-8f09-4e37-95e2-e71de7ca5d3c
        /// </summary>
        [Column("label")]
        [JsonProperty(PropertyName = "label")]
        public string Label { get; set; }

        /// <summary>
        /// Sort order index for display ordering of task types.
        /// Number field — required: true, default: 1.0, decimalPlaces: 0, system: true.
        /// Field GUID: ef3aa457-ef03-4942-81d5-57753e1fc226
        /// </summary>
        [Column("sort_index")]
        [JsonProperty(PropertyName = "sort_index")]
        public decimal SortIndex { get; set; }

        /// <summary>
        /// Indicates whether this is a system-managed task type record.
        /// Checkbox field — required: true, default: false, system: true.
        /// Field GUID: 6fa4d8a2-60ad-4882-9f06-e44dfc83266e
        /// </summary>
        [Column("is_system")]
        [JsonProperty(PropertyName = "is_system")]
        public bool IsSystem { get; set; }

        /// <summary>
        /// Indicates whether this task type is enabled and available for selection.
        /// Checkbox field — required: true, default: true, system: true.
        /// Field GUID: 22d47a37-7c03-4d6a-bb8a-0e76fd5a3371
        /// </summary>
        [Column("is_enabled")]
        [JsonProperty(PropertyName = "is_enabled")]
        public bool IsEnabled { get; set; }

        /// <summary>
        /// CSS icon class for visual representation (e.g., "fas fa-fw fa-bug", "fas fa-phone").
        /// Text field — required: false, system: true, default: null.
        /// Field GUID: 525efe80-eb47-42e7-8f34-910ab13afa29
        /// </summary>
        [Column("icon_class")]
        [JsonProperty(PropertyName = "icon_class")]
        public string IconClass { get; set; }

        /// <summary>
        /// Hex color code for visual representation (e.g., "#F44336", "#4CAF50").
        /// Text field — required: false, system: true, default: null.
        /// Field GUID: 8026ba91-abb2-4bdf-aa5f-134285b4a959
        /// </summary>
        [Column("color")]
        [JsonProperty(PropertyName = "color")]
        public string Color { get; set; }

        #endregion
    }
}
