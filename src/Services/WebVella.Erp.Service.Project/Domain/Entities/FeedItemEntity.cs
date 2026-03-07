using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Entities
{
	/// <summary>
	/// Strongly-typed domain entity class for the <c>feed_item</c> entity, extracted from the
	/// monolith's <c>NextPlugin.20190203.cs</c> (lines 720–976). Feed items track activity and
	/// events within the Project microservice, including system changes, task updates, timelog
	/// entries, and comments.
	/// </summary>
	/// <remarks>
	/// <para><b>Database table:</b> <c>rec_feed_item</c></para>
	/// <para><b>Entity ID:</b> <c>db83b9b0-448c-4675-be71-640aca2e2a3a</c></para>
	/// <para><b>Cross-service references:</b></para>
	/// <list type="bullet">
	///   <item><c>CreatedBy</c> references User entity in the Core service — stored as UUID only,
	///   resolved via gRPC call on read (no FK constraint in database-per-service model).</item>
	/// </list>
	/// <para><b>Relations:</b></para>
	/// <list type="bullet">
	///   <item><c>user_1n_feed_item</c> — OneToMany from user to feed_item (creator). In the
	///   microservice architecture this becomes a cross-service UUID reference.</item>
	/// </list>
	/// </remarks>
	[Table("rec_feed_item")]
	public class FeedItemEntity
	{
		#region Entity Metadata Constants

		/// <summary>
		/// Well-known entity ID for the feed_item entity definition.
		/// Source: NextPlugin.20190203.cs, entity.Id = new Guid("db83b9b0-448c-4675-be71-640aca2e2a3a")
		/// </summary>
		public static readonly Guid EntityId = new Guid("db83b9b0-448c-4675-be71-640aca2e2a3a");

		/// <summary>
		/// Entity system name used in EQL queries, API routes, and database table naming (<c>rec_feed_item</c>).
		/// </summary>
		public const string EntityName = "feed_item";

		/// <summary>
		/// Human-readable singular label for UI display.
		/// Source: entity.Label = "Feed item"
		/// </summary>
		public const string EntityLabel = "Feed item";

		/// <summary>
		/// Human-readable plural label for UI display.
		/// Source: entity.LabelPlural = "Feed items"
		/// </summary>
		public const string EntityLabelPlural = "Feed items";

		/// <summary>
		/// FontAwesome icon class for entity visual representation in the UI.
		/// </summary>
		public const string EntityIconName = "fas fa-rss";

		/// <summary>
		/// Theme color (red) for entity visual representation in the UI.
		/// Source: entity.Color = "#f44336"
		/// </summary>
		public const string EntityColor = "#f44336";

		#endregion

		#region Field Definition GUIDs

		/// <summary>
		/// Field definition GUID for the system <c>id</c> field (primary key).
		/// Preserved from monolith schema for metadata consistency.
		/// </summary>
		public static readonly Guid FieldIdId = new Guid("d5eb3ce3-c68e-41b5-99de-4ab0b88064a4");

		/// <summary>
		/// Field definition GUID for the <c>created_by</c> field.
		/// Type: Guid | Required: true | Default: Guid.Empty | System: true
		/// Cross-service reference to User entity in Core service.
		/// </summary>
		public static readonly Guid FieldIdCreatedBy = new Guid("c6a5c78f-5ed0-4612-8e4e-c13b09cd26fc");

		/// <summary>
		/// Field definition GUID for the <c>created_on</c> field.
		/// Type: DateTime | Required: true | UseCurrentTimeAsDefault: true | Searchable: true
		/// Format: "yyyy-MMM-dd HH:mm" | System: true
		/// </summary>
		public static readonly Guid FieldIdCreatedOn = new Guid("5d3b46e0-d884-4025-adc0-7ae39085d36a");

		/// <summary>
		/// Field definition GUID for the <c>l_scope</c> field.
		/// Type: Text | Required: false | Searchable: false | System: true
		/// Label: "Scope"
		/// </summary>
		public static readonly Guid FieldIdLScope = new Guid("a38bc9f4-9073-47e6-9dc0-c8d193545825");

		/// <summary>
		/// Field definition GUID for the <c>subject</c> field.
		/// Type: Text | Required: false | System: true
		/// Label: "Subject"
		/// </summary>
		public static readonly Guid FieldIdSubject = new Guid("feb5c8ac-45dc-4b07-9cd9-38e18eb9bf31");

		/// <summary>
		/// Field definition GUID for the <c>body</c> field.
		/// Type: Text | Required: false | System: true
		/// Label: "Body" | Help: "text,html or json of the feed item content"
		/// </summary>
		public static readonly Guid FieldIdBody = new Guid("370f3f96-b008-449d-8a8e-ff29519bd295");

		/// <summary>
		/// Field definition GUID for the <c>type</c> field.
		/// Type: Select | Required: true | Default: "system" | System: true
		/// Options: system, task, case, timelog, comment
		/// </summary>
		public static readonly Guid FieldIdType = new Guid("ecc28658-571b-467d-9d85-51972de8b94d");

		/// <summary>
		/// Field definition GUID for the <c>l_related_records</c> field.
		/// Type: Text | Required: false | Searchable: true | System: true
		/// Label: "Related Record lookup"
		/// Description: "csv list of related parent primary key"
		/// </summary>
		public static readonly Guid FieldIdLRelatedRecords = new Guid("7411b977-ff65-493d-b00d-d09cb82e409e");

		#endregion

		#region Instance Properties (EF Core Mapped)

		/// <summary>
		/// Primary key identifier for the feed item record.
		/// Maps to the <c>id</c> column in the <c>rec_feed_item</c> table.
		/// </summary>
		[Key]
		[Column("id")]
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		/// <summary>
		/// UUID of the user who created this feed item.
		/// Cross-service reference to the User entity in the Core service — stored as UUID only,
		/// resolved via gRPC call on read. No foreign key constraint in the database-per-service model.
		/// Required: true | Default: Guid.Empty
		/// </summary>
		[Column("created_by")]
		[JsonProperty(PropertyName = "created_by")]
		public Guid CreatedBy { get; set; }

		/// <summary>
		/// Timestamp when the feed item was created.
		/// Uses the current server time as the default value when a new record is inserted.
		/// Required: true | Searchable: true | Format: "yyyy-MMM-dd HH:mm"
		/// </summary>
		[Column("created_on")]
		[JsonProperty(PropertyName = "created_on")]
		public DateTime CreatedOn { get; set; }

		/// <summary>
		/// Scope identifier for the feed item, used for filtering feed items
		/// within a specific context (e.g., project or task scope).
		/// Required: false | Searchable: false | System: true
		/// </summary>
		[Column("l_scope")]
		[JsonProperty(PropertyName = "l_scope")]
		public string LScope { get; set; }

		/// <summary>
		/// Subject line or title of the feed item.
		/// Required: false | System: true
		/// </summary>
		[Column("subject")]
		[JsonProperty(PropertyName = "subject")]
		public string Subject { get; set; }

		/// <summary>
		/// Body content of the feed item. Can contain text, HTML, or JSON content
		/// depending on the feed item type.
		/// Required: false | System: true | Help: "text,html or json of the feed item content"
		/// </summary>
		[Column("body")]
		[JsonProperty(PropertyName = "body")]
		public string Body { get; set; }

		/// <summary>
		/// Type classification of the feed item. Must be one of the values defined
		/// in <see cref="TypeOptions"/>: system, task, case, timelog, comment.
		/// Required: true | Default: "system" | System: true
		/// </summary>
		[Column("type")]
		[JsonProperty(PropertyName = "type")]
		public string Type { get; set; } = "system";

		/// <summary>
		/// Comma-separated list of related parent record primary keys. Used for
		/// looking up feed items associated with specific records across entity boundaries.
		/// Required: false | Searchable: true | System: true
		/// Label: "Related Record lookup" | Description: "csv list of related parent primary key"
		/// </summary>
		[Column("l_related_records")]
		[JsonProperty(PropertyName = "l_related_records")]
		public string LRelatedRecords { get; set; }

		#endregion

		#region Record Permission Role Lists

		/// <summary>
		/// Role IDs that are permitted to create feed item records.
		/// Includes RegularRoleId and AdministratorRoleId from the monolith's permission model.
		/// Source: entity.RecordPermissions.CanCreate (NextPlugin.20190203.cs lines 740-742)
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanCreateRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Role IDs that are permitted to read feed item records.
		/// Includes RegularRoleId and AdministratorRoleId from the monolith's permission model.
		/// Source: entity.RecordPermissions.CanRead (NextPlugin.20190203.cs lines 744-745)
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanReadRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Role IDs that are permitted to delete feed item records.
		/// Includes RegularRoleId and AdministratorRoleId from the monolith's permission model.
		/// Source: entity.RecordPermissions.CanDelete (NextPlugin.20190203.cs lines 748-749)
		/// Note: CanUpdate was empty in the original monolith source — feed items are immutable
		/// once created (no update permission roles defined).
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanDeleteRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		#endregion

		#region Feed Type Select Options

		/// <summary>
		/// Constant for the "system" feed item type — automatically generated system events.
		/// </summary>
		public const string TypeSystem = "system";

		/// <summary>
		/// Constant for the "task" feed item type — task-related activity entries.
		/// </summary>
		public const string TypeTask = "task";

		/// <summary>
		/// Constant for the "case" feed item type — case-related activity entries.
		/// </summary>
		public const string TypeCase = "case";

		/// <summary>
		/// Constant for the "timelog" feed item type — timelog-related activity entries.
		/// </summary>
		public const string TypeTimelog = "timelog";

		/// <summary>
		/// Constant for the "comment" feed item type — comment-related activity entries.
		/// </summary>
		public const string TypeComment = "comment";

		/// <summary>
		/// Complete list of available feed type select options, preserving the exact
		/// structure from the monolith's InputSelectField.Options definition.
		/// Source: NextPlugin.20190203.cs lines 926-933
		/// Each option includes Label, Value, IconClass, and Color matching the original
		/// <c>SelectOption</c> shape for API contract backward compatibility.
		/// </summary>
		public static readonly IReadOnlyList<FeedTypeSelectOption> TypeOptions = new List<FeedTypeSelectOption>
		{
			new FeedTypeSelectOption(TypeSystem, TypeSystem, string.Empty, string.Empty),
			new FeedTypeSelectOption(TypeTask, TypeTask, string.Empty, string.Empty),
			new FeedTypeSelectOption(TypeCase, TypeCase, string.Empty, string.Empty),
			new FeedTypeSelectOption(TypeTimelog, TypeTimelog, string.Empty, string.Empty),
			new FeedTypeSelectOption(TypeComment, TypeComment, string.Empty, string.Empty)
		};

		#endregion

		/// <summary>
		/// Initializes a new instance of the <see cref="FeedItemEntity"/> class with default values.
		/// Sets <see cref="CreatedBy"/> to <see cref="Guid.Empty"/> and <see cref="CreatedOn"/>
		/// to <see cref="DateTime.UtcNow"/>, matching the monolith's default behavior.
		/// </summary>
		public FeedItemEntity()
		{
			CreatedBy = Guid.Empty;
			CreatedOn = DateTime.UtcNow;
		}

		/// <summary>
		/// Represents a single select option for the feed item <see cref="Type"/> field,
		/// preserving the monolith's <c>SelectOption</c> shape (Label, Value, IconClass, Color)
		/// for API contract backward compatibility with the REST API v3 response shapes.
		/// </summary>
		public sealed class FeedTypeSelectOption
		{
			/// <summary>
			/// Display label for the select option shown in UI dropdowns.
			/// </summary>
			[JsonProperty(PropertyName = "label")]
			public string Label { get; }

			/// <summary>
			/// Stored value for the select option persisted in the database.
			/// </summary>
			[JsonProperty(PropertyName = "value")]
			public string Value { get; }

			/// <summary>
			/// Optional FontAwesome icon class for the select option.
			/// Empty string when no icon is assigned.
			/// </summary>
			[JsonProperty(PropertyName = "iconClass")]
			public string IconClass { get; }

			/// <summary>
			/// Optional color hex code for the select option.
			/// Empty string when no color is assigned.
			/// </summary>
			[JsonProperty(PropertyName = "color")]
			public string Color { get; }

			/// <summary>
			/// Creates a new feed type select option with the specified values.
			/// </summary>
			/// <param name="label">Display label for the option.</param>
			/// <param name="value">Stored value for the option.</param>
			/// <param name="iconClass">FontAwesome icon class (empty string if none).</param>
			/// <param name="color">Color hex code (empty string if none).</param>
			public FeedTypeSelectOption(string label, string value, string iconClass, string color)
			{
				Label = label ?? throw new ArgumentNullException(nameof(label));
				Value = value ?? throw new ArgumentNullException(nameof(value));
				IconClass = iconClass ?? string.Empty;
				Color = color ?? string.Empty;
			}
		}
	}
}
