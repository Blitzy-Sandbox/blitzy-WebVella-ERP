using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Entities
{
	/// <summary>
	/// Strongly-typed domain entity class for the timelog entity in the Project microservice.
	/// Extracted from the monolith's dynamic entity system defined in NextPlugin.20190203.cs
	/// (entity creation, fields: body, created_by, created_on, is_billable, l_related_records,
	/// l_scope, minutes, logged_on) and NextPlugin.20190205.cs (minutes field update:
	/// Required=true, DefaultValue=0, Description="0 will not create timelog").
	///
	/// Maps to the <c>rec_timelog</c> PostgreSQL table in the Project service's dedicated database
	/// under the database-per-service model. All field GUIDs, labels, defaults, required flags,
	/// searchable flags, and system flags are preserved exactly from the source patches.
	///
	/// Cross-service references:
	/// - CreatedBy references User entity in the Core service via UUID only (no FK constraint).
	///   Resolution occurs at runtime via Core gRPC calls.
	/// - Relation: user_1n_timelog (OneToMany from user to timelog via created_by)
	/// </summary>
	[Table("rec_timelog")]
	public class TimelogEntity
	{
		#region Entity Metadata Constants

		/// <summary>
		/// Well-known entity ID for the timelog entity.
		/// Source: NextPlugin.20190203.cs — entity.Id = new Guid("750153c5-1df9-408f-b856-727078a525bc")
		/// </summary>
		public static readonly Guid EntityId = new Guid("750153c5-1df9-408f-b856-727078a525bc");

		/// <summary>
		/// Entity name as registered in the ERP system.
		/// Source: NextPlugin.20190203.cs — entity.Name = "timelog"
		/// </summary>
		public const string EntityName = "timelog";

		/// <summary>
		/// Singular display label for the timelog entity.
		/// Source: NextPlugin.20190203.cs — entity.Label = "Timelog"
		/// </summary>
		public const string EntityLabel = "Timelog";

		/// <summary>
		/// Plural display label for the timelog entity.
		/// Source: NextPlugin.20190203.cs — entity.LabelPlural = "Timelogs"
		/// </summary>
		public const string EntityLabelPlural = "Timelogs";

		/// <summary>
		/// Font Awesome icon class for UI representation of the timelog entity.
		/// Source: NextPlugin.20190203.cs — entity.IconName = "far fa-clock"
		/// </summary>
		public const string EntityIconName = "far fa-clock";

		/// <summary>
		/// Color hex code for UI representation of the timelog entity.
		/// Source: NextPlugin.20190203.cs — entity.Color = "#f44336"
		/// </summary>
		public const string EntityColor = "#f44336";

		#endregion

		#region Field ID Constants

		/// <summary>
		/// Field ID for the system 'id' field (primary key).
		/// Source: NextPlugin.20190203.cs — systemFieldIdDictionary["id"] = new Guid("829b8572-0084-40e2-b589-c4e8dc7cbbd7")
		/// </summary>
		public static readonly Guid IdFieldId = new Guid("829b8572-0084-40e2-b589-c4e8dc7cbbd7");

		/// <summary>
		/// Field ID for the 'body' multi-line text field.
		/// Type: InputMultiLineTextField, Required=false, Searchable=false, System=true
		/// </summary>
		public static readonly Guid BodyFieldId = new Guid("7e6f0cb0-3bae-4e64-9d2c-7e22c5bbf7a8");

		/// <summary>
		/// Field ID for the 'created_by' GUID field.
		/// Type: InputGuidField, Required=true, Default=Guid.Empty, System=true
		/// Cross-service reference to User entity in Core service.
		/// </summary>
		public static readonly Guid CreatedByFieldId = new Guid("bce4e825-7052-426d-b546-1bf7e9f1c542");

		/// <summary>
		/// Field ID for the 'created_on' datetime field.
		/// Type: InputDateTimeField, Required=true, UseCurrentTimeAsDefault=true, Searchable=true, Format="yyyy-MMM-dd HH:mm"
		/// </summary>
		public static readonly Guid CreatedOnFieldId = new Guid("02a0b46e-4b41-4ec8-b31e-f90ca4100003");

		/// <summary>
		/// Field ID for the 'is_billable' checkbox field.
		/// Type: InputCheckboxField, Required=true, Default=true, System=true
		/// </summary>
		public static readonly Guid IsBillableFieldId = new Guid("d62fcab9-24c8-473b-894f-a3b73e7f60ac");

		/// <summary>
		/// Field ID for the 'l_related_records' text field.
		/// Type: InputTextField, Required=false, Searchable=true, System=true
		/// </summary>
		public static readonly Guid LRelatedRecordsFieldId = new Guid("9a1b7025-2037-472e-983f-a57444dc44da");

		/// <summary>
		/// Field ID for the 'l_scope' text field.
		/// Type: InputTextField, Required=false, Searchable=true, System=true
		/// </summary>
		public static readonly Guid LScopeFieldId = new Guid("8cc443ae-d45c-4186-a7f7-fc20ce5fc31c");

		/// <summary>
		/// Field ID for the 'minutes' number field.
		/// Type: InputNumberField, Required=true, Default=0, DecimalPlaces=0, System=true
		/// Updated in NextPlugin.20190205.cs: Required=true, DefaultValue=Decimal.Parse("0.0"),
		/// Description="0 will not create timelog"
		/// </summary>
		public static readonly Guid MinutesFieldId = new Guid("879b14b7-bd32-4f6c-b3a0-9b2e3a1cdc3a");

		/// <summary>
		/// Field ID for the 'logged_on' date field.
		/// Type: InputDateField, Required=true, UseCurrentTimeAsDefault=true, Searchable=true, Format="yyyy-MMM-dd"
		/// </summary>
		public static readonly Guid LoggedOnFieldId = new Guid("480363dd-e296-4572-8be5-618c32388ba3");

		#endregion

		#region Entity Properties

		/// <summary>
		/// Primary key for the timelog record. System field, auto-generated GUID.
		/// Maps to the 'id' column in the rec_timelog table.
		/// </summary>
		[Key]
		[Column("id")]
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		/// <summary>
		/// Body/description of the timelog entry. Multi-line text field, optional.
		/// Source: NextPlugin.20190203.cs — InputMultiLineTextField, Required=false, System=true
		/// </summary>
		[Column("body")]
		[JsonProperty(PropertyName = "body")]
		public string Body { get; set; }

		/// <summary>
		/// GUID of the user who created this timelog entry.
		/// Cross-service reference to User entity in Core service — UUID only, no FK constraint.
		/// Resolved at runtime via Core gRPC call when user details are needed.
		/// Source: NextPlugin.20190203.cs — InputGuidField, Required=true, Default=Guid.Empty, System=true
		/// </summary>
		[Column("created_by")]
		[JsonProperty(PropertyName = "created_by")]
		public Guid CreatedBy { get; set; }

		/// <summary>
		/// Timestamp when the timelog record was created.
		/// Source: NextPlugin.20190203.cs — InputDateTimeField, Required=true,
		/// UseCurrentTimeAsDefaultValue=true, Searchable=true, Format="yyyy-MMM-dd HH:mm"
		/// </summary>
		[Column("created_on")]
		[JsonProperty(PropertyName = "created_on")]
		public DateTime CreatedOn { get; set; }

		/// <summary>
		/// Whether this timelog entry is billable.
		/// Source: NextPlugin.20190203.cs — InputCheckboxField, Required=true, Default=true, System=true
		/// </summary>
		[Column("is_billable")]
		[JsonProperty(PropertyName = "is_billable")]
		public bool IsBillable { get; set; } = true;

		/// <summary>
		/// Related records reference string for cross-entity linking. Optional, searchable.
		/// Source: NextPlugin.20190203.cs — InputTextField, Required=false, Searchable=true, System=true
		/// </summary>
		[Column("l_related_records")]
		[JsonProperty(PropertyName = "l_related_records")]
		public string LRelatedRecords { get; set; }

		/// <summary>
		/// Scope identifier for the timelog entry. Optional, searchable.
		/// Source: NextPlugin.20190203.cs — InputTextField, Required=false, Searchable=true, System=true
		/// </summary>
		[Column("l_scope")]
		[JsonProperty(PropertyName = "l_scope")]
		public string LScope { get; set; }

		/// <summary>
		/// Number of minutes logged for this timelog entry.
		/// Required=true, Default=0, DecimalPlaces=0, System=true.
		/// Updated in NextPlugin.20190205.cs: Required=true, DefaultValue=Decimal.Parse("0.0"),
		/// Description="0 will not create timelog".
		/// Uses decimal type matching the source InputNumberField numeric precision.
		/// </summary>
		[Column("minutes")]
		[JsonProperty(PropertyName = "minutes")]
		public decimal Minutes { get; set; }

		/// <summary>
		/// Date when the time was logged. Stored as DateTime but represents a date-only value.
		/// Source: NextPlugin.20190203.cs — InputDateField, Required=true,
		/// UseCurrentTimeAsDefaultValue=true, Searchable=true, Format="yyyy-MMM-dd"
		/// </summary>
		[Column("logged_on")]
		[JsonProperty(PropertyName = "logged_on")]
		public DateTime LoggedOn { get; set; }

		#endregion

		#region Permission Constants

		/// <summary>
		/// Roles that can create timelog records.
		/// Contains RegularRoleId and AdministratorRoleId matching the source permission definitions.
		/// Source: NextPlugin.20190203.cs — entity.RecordPermissions.CanCreate
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanCreateRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Roles that can read timelog records.
		/// Contains RegularRoleId and AdministratorRoleId matching the source permission definitions.
		/// Source: NextPlugin.20190203.cs — entity.RecordPermissions.CanRead
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanReadRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Roles that can update timelog records.
		/// Contains RegularRoleId and AdministratorRoleId matching the source permission definitions.
		/// Source: NextPlugin.20190203.cs — entity.RecordPermissions.CanUpdate
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanUpdateRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Roles that can delete timelog records.
		/// Contains RegularRoleId and AdministratorRoleId matching the source permission definitions.
		/// Source: NextPlugin.20190203.cs — entity.RecordPermissions.CanDelete
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanDeleteRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		#endregion
	}
}
