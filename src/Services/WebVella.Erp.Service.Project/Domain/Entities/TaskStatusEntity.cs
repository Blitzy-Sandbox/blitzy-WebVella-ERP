using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Entities
{
	/// <summary>
	/// Strongly-typed domain entity for the <c>task_status</c> lookup entity.
	/// Extracted from <c>NextPlugin.20190203.cs</c> lines 2109–2413.
	/// Task statuses are lookup records that define the lifecycle states of tasks
	/// (e.g., "not started", "in progress", "completed").
	/// Maps to the <c>rec_task_status</c> PostgreSQL table in the Project service database.
	///
	/// Relation metadata:
	///   - <c>task_status_1n_task</c>: OneToMany from task_status (origin) to task (target).
	///     Tasks reference task_status via <c>status_id</c>.
	///     This entity is the "one" side of the relationship.
	/// </summary>
	[Table("rec_task_status")]
	public class TaskStatusEntity
	{
		#region Entity Metadata Constants

		/// <summary>
		/// Well-known entity ID for the task_status entity.
		/// Source: NextPlugin.20190203.cs line 2116 —
		/// <c>entity.Id = new Guid("9221f095-f749-4b88-94e5-9fa485527ef7")</c>
		/// </summary>
		public static readonly Guid EntityId = new Guid("9221f095-f749-4b88-94e5-9fa485527ef7");

		/// <summary>
		/// Entity name as registered in the ERP dynamic entity system.
		/// </summary>
		public const string EntityName = "task_status";

		/// <summary>
		/// Singular display label for the entity.
		/// Source: <c>entity.Label = "Task status"</c>
		/// </summary>
		public const string EntityLabel = "Task status";

		/// <summary>
		/// Plural display label for the entity.
		/// Source: <c>entity.LabelPlural = "Task statuses"</c>
		/// </summary>
		public const string EntityLabelPlural = "Task statuses";

		/// <summary>
		/// Font Awesome icon class for entity UI display.
		/// Source: <c>entity.IconName = "far fa-dot-circle"</c>
		/// </summary>
		public const string EntityIconName = "far fa-dot-circle";

		/// <summary>
		/// Hex color code for entity UI display.
		/// Source: <c>entity.Color = "#f44336"</c>
		/// </summary>
		public const string EntityColor = "#f44336";

		#endregion

		#region Field ID Constants

		/// <summary>
		/// System field ID for the primary key "id" field.
		/// Source: NextPlugin.20190203.cs line 2115 —
		/// <c>systemFieldIdDictionary["id"] = new Guid("f4f9b011-b4d5-4651-8dc9-c608a0d216da")</c>
		/// </summary>
		public static readonly Guid IdFieldId = new Guid("f4f9b011-b4d5-4651-8dc9-c608a0d216da");

		/// <summary>
		/// Field ID for "is_closed" (checkbox).
		/// Source: NextPlugin.20190203.cs line 2150
		/// </summary>
		public static readonly Guid IsClosedFieldId = new Guid("7500864e-4106-4b36-ba9a-93f70c386c88");

		/// <summary>
		/// Field ID for "is_default" (checkbox).
		/// Source: NextPlugin.20190203.cs line 2179
		/// </summary>
		public static readonly Guid IsDefaultFieldId = new Guid("55bed50d-c263-4ebe-9ea7-2f79afb0d39b");

		/// <summary>
		/// Field ID for "l_scope" (text).
		/// Source: NextPlugin.20190203.cs line 2208
		/// </summary>
		public static readonly Guid LScopeFieldId = new Guid("4ccd4df3-860f-44d8-b486-5f47cb798451");

		/// <summary>
		/// Field ID for "label" (text). Also used as RecordScreenIdField.
		/// Source: NextPlugin.20190203.cs line 2238
		/// </summary>
		public static readonly Guid LabelFieldId = new Guid("6c242d1c-420e-4649-8a73-b891d5b508e0");

		/// <summary>
		/// Field ID for "sort_index" (number, 0 decimal places).
		/// Source: NextPlugin.20190203.cs line 2268
		/// </summary>
		public static readonly Guid SortIndexFieldId = new Guid("b16db646-3c63-4fb8-acac-499d1ddda5f9");

		/// <summary>
		/// Field ID for "is_system" (checkbox).
		/// Source: NextPlugin.20190203.cs line 2300
		/// </summary>
		public static readonly Guid IsSystemFieldId = new Guid("cd1217f7-0342-47e0-9017-7374a5419091");

		/// <summary>
		/// Field ID for "is_enabled" (checkbox).
		/// Source: NextPlugin.20190203.cs line 2329
		/// </summary>
		public static readonly Guid IsEnabledFieldId = new Guid("8ff595bc-edb9-4a07-8dca-9fd9a380ab35");

		/// <summary>
		/// Field ID for "icon_class" (text).
		/// Source: NextPlugin.20190203.cs line 2358
		/// </summary>
		public static readonly Guid IconClassFieldId = new Guid("2cbbd720-9bdc-48c4-a405-c2c2bcf81d35");

		/// <summary>
		/// Field ID for "color" (text).
		/// Source: NextPlugin.20190203.cs line 2388
		/// </summary>
		public static readonly Guid ColorFieldId = new Guid("0a3b6de4-1678-47a5-ab0e-512c9abd2f1b");

		#endregion

		#region Permission Constants

		/// <summary>
		/// Roles permitted to create task_status records.
		/// Source: NextPlugin.20190203.cs line 2130 — Administrator role only.
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanCreateRoles = new List<Guid>
		{
			SystemIds.AdministratorRoleId
		}.AsReadOnly();

		/// <summary>
		/// Roles permitted to read task_status records.
		/// Source: NextPlugin.20190203.cs lines 2132–2133 — Regular + Administrator roles.
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanReadRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		}.AsReadOnly();

		/// <summary>
		/// Roles permitted to update task_status records.
		/// Source: NextPlugin.20190203.cs line 2135 — Administrator role only.
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanUpdateRoles = new List<Guid>
		{
			SystemIds.AdministratorRoleId
		}.AsReadOnly();

		/// <summary>
		/// Roles permitted to delete task_status records.
		/// Source: NextPlugin.20190203.cs lines 2136–2137 — No roles; delete is not permitted.
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanDeleteRoles = new List<Guid>().AsReadOnly();

		#endregion

		#region Entity Properties

		/// <summary>
		/// Primary key identifier for the task status record.
		/// Column: id, Type: uuid
		/// System field with well-known field ID <see cref="IdFieldId"/>.
		/// </summary>
		[Key]
		[Column("id")]
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		/// <summary>
		/// Indicates whether this status represents a closed/completed state.
		/// When a task transitions to a status with <c>IsClosed = true</c>,
		/// it is considered finished and may trigger completion-related events.
		/// Field: is_closed, Type: checkbox, Required: true, Default: false, System: true
		/// </summary>
		[Column("is_closed")]
		[JsonProperty(PropertyName = "is_closed")]
		public bool IsClosed { get; set; }

		/// <summary>
		/// Indicates whether this is the default status assigned to newly created tasks.
		/// Exactly one task_status record should have <c>IsDefault = true</c> at any time.
		/// Field: is_default, Type: checkbox, Required: true, Default: false, System: true
		/// </summary>
		[Column("is_default")]
		[JsonProperty(PropertyName = "is_default")]
		public bool IsDefault { get; set; }

		/// <summary>
		/// Scope identifier for multi-tenant or context-scoped filtering.
		/// Allows different task status sets per organizational scope.
		/// Field: l_scope, Type: text, Required: false, Default: null, System: true, Searchable: false
		/// </summary>
		[Column("l_scope")]
		[JsonProperty(PropertyName = "l_scope")]
		public string LScope { get; set; }

		/// <summary>
		/// Human-readable display label for the task status (e.g., "Not started", "In progress", "Completed").
		/// Used as the <c>RecordScreenIdField</c> for this entity.
		/// Field: label, Type: text, Required: true, Default: "label", System: true
		/// </summary>
		[Column("label")]
		[JsonProperty(PropertyName = "label")]
		public string Label { get; set; }

		/// <summary>
		/// Numeric ordering index controlling the display sort order of statuses in dropdowns and lists.
		/// Lower values appear first. Uses 0 decimal places (whole numbers).
		/// Field: sort_index, Type: number, Required: true, Default: 1.0, DecimalPlaces: 0, System: true
		/// </summary>
		[Column("sort_index")]
		[JsonProperty(PropertyName = "sort_index")]
		public decimal SortIndex { get; set; }

		/// <summary>
		/// Indicates whether this status is a built-in system record that cannot be removed by users.
		/// System records are protected from deletion and certain modifications.
		/// Field: is_system, Type: checkbox, Required: true, Default: false, System: true
		/// </summary>
		[Column("is_system")]
		[JsonProperty(PropertyName = "is_system")]
		public bool IsSystem { get; set; }

		/// <summary>
		/// Indicates whether this status is active and available for selection in task workflows.
		/// Disabled statuses are hidden from user-facing dropdowns but preserved for historical records.
		/// Field: is_enabled, Type: checkbox, Required: true, Default: true, System: true
		/// </summary>
		[Column("is_enabled")]
		[JsonProperty(PropertyName = "is_enabled")]
		public bool IsEnabled { get; set; }

		/// <summary>
		/// CSS icon class (Font Awesome) for visual status representation in the UI.
		/// Example: "fas fa-check-circle" for completed status.
		/// Field: icon_class, Type: text, Required: false, Default: null, System: true
		/// </summary>
		[Column("icon_class")]
		[JsonProperty(PropertyName = "icon_class")]
		public string IconClass { get; set; }

		/// <summary>
		/// Hex color code for visual status differentiation in the UI (e.g., "#4CAF50" for green).
		/// Field: color, Type: text, Required: false, Default: null, System: true
		/// </summary>
		[Column("color")]
		[JsonProperty(PropertyName = "color")]
		public string Color { get; set; }

		#endregion
	}
}
