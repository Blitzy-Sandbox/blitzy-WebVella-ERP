using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Entities
{
	/// <summary>
	/// Strongly-typed domain entity class for the <c>comment</c> entity,
	/// extracted from the monolith's <c>NextPlugin.Patch20190203</c> (lines 339-560).
	/// Comments are created within the Project service scope and linked to tasks,
	/// cases, and other entities via the <see cref="LRelatedRecords"/> field.
	///
	/// Relation metadata (documented for migration reference):
	///   - <c>user_1n_comment</c>: OneToMany from user to comment (creator).
	///     Cross-service: <see cref="CreatedBy"/> references User in Core service — UUID only, resolved via gRPC.
	///   - <c>comment_nn_attachment</c>: ManyToMany between comment and attachment (user_file).
	///
	/// The <see cref="ParentId"/> field enables threaded/nested comment trees
	/// through a self-referencing relationship.
	/// </summary>
	[Table("rec_comment")]
	public class CommentEntity
	{
		#region Entity Metadata Constants

		/// <summary>
		/// Entity registration GUID for the comment entity.
		/// Source: <c>entity.Id = new Guid("b1d218d5-68c2-41a5-bea5-1b4a78cbf91d")</c>
		/// </summary>
		public static readonly Guid EntityId = new Guid("b1d218d5-68c2-41a5-bea5-1b4a78cbf91d");

		/// <summary>
		/// Entity name as registered in the ERP system.
		/// Source: <c>entity.Name = "comment"</c>
		/// </summary>
		public const string EntityName = "comment";

		/// <summary>
		/// Singular display label for the comment entity.
		/// Source: <c>entity.Label = "Comment"</c>
		/// </summary>
		public const string EntityLabel = "Comment";

		/// <summary>
		/// Plural display label for the comment entity.
		/// Source: <c>entity.LabelPlural = "Comments"</c>
		/// </summary>
		public const string EntityLabelPlural = "Comments";

		/// <summary>
		/// FontAwesome icon class for the comment entity.
		/// Source: <c>entity.IconName = "far fa-comment"</c>
		/// </summary>
		public const string EntityIconName = "far fa-comment";

		/// <summary>
		/// Display color (hex) for the comment entity.
		/// Source: <c>entity.Color = "#f44336"</c>
		/// </summary>
		public const string EntityColor = "#f44336";

		#endregion

		#region Field ID Constants

		/// <summary>
		/// GUID for the system <c>id</c> field of the comment entity.
		/// Source: <c>systemFieldIdDictionary["id"] = new Guid("7ffcc5b7-e347-4923-af5f-e368549d7f16")</c>
		/// </summary>
		public static readonly Guid IdFieldId = new Guid("7ffcc5b7-e347-4923-af5f-e368549d7f16");

		/// <summary>
		/// GUID for the <c>body</c> field.
		/// Source: InputTextField, Required=true, DefaultValue="body", System=true, Searchable=false.
		/// </summary>
		public static readonly Guid BodyFieldId = new Guid("0a4195d1-aa37-4aea-9c56-52e8d22d6f13");

		/// <summary>
		/// GUID for the <c>created_by</c> field.
		/// Source: InputGuidField, Required=true, DefaultValue=Guid.Empty, System=true, Searchable=false.
		/// </summary>
		public static readonly Guid CreatedByFieldId = new Guid("8b2d1f1c-bcdd-4c1d-94df-884205c2bf9c");

		/// <summary>
		/// GUID for the <c>l_scope</c> field.
		/// Source: InputTextField, Required=false, Searchable=false, System=false.
		/// </summary>
		public static readonly Guid LScopeFieldId = new Guid("28ea1822-6030-4b63-9532-d7f846105a11");

		/// <summary>
		/// GUID for the <c>parent_id</c> field (self-referencing for threaded/nested comments).
		/// Source: InputGuidField, Required=false, System=true, Searchable=false.
		/// </summary>
		public static readonly Guid ParentIdFieldId = new Guid("4629bdb5-9a79-4c87-b764-74491b5b2cfa");

		/// <summary>
		/// GUID for the <c>l_related_records</c> field.
		/// Source: InputTextField, Required=false, Searchable=true, Label="Related Record lookup", System=true.
		/// </summary>
		public static readonly Guid LRelatedRecordsFieldId = new Guid("364b886e-850a-438d-8e8f-4a6719272bfc");

		/// <summary>
		/// GUID for the <c>created_on</c> field.
		/// Source: InputDateTimeField, Required=true, UseCurrentTimeAsDefaultValue=true,
		/// Searchable=true, Format="yyyy-MMM-dd HH:mm", System=true.
		/// </summary>
		public static readonly Guid CreatedOnFieldId = new Guid("4c61dbe0-04a6-4bca-b4cd-40689bd232f1");

		#endregion

		#region Permission Constants

		/// <summary>
		/// Role IDs with permission to create comment records.
		/// Order preserved from source: RegularRoleId first, then AdministratorRoleId.
		/// Source GUIDs: f16ec6db-626d-4c27-8de0-3e7ce542c55f, bdc56420-caf0-4030-8a0e-d264938e0cda
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanCreateRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Role IDs with permission to read comment records.
		/// Order preserved from source: RegularRoleId first, then AdministratorRoleId.
		/// Source GUIDs: f16ec6db-626d-4c27-8de0-3e7ce542c55f, bdc56420-caf0-4030-8a0e-d264938e0cda
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanReadRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Role IDs with permission to update comment records.
		/// Order preserved from source: RegularRoleId first, then AdministratorRoleId.
		/// Source GUIDs: f16ec6db-626d-4c27-8de0-3e7ce542c55f, bdc56420-caf0-4030-8a0e-d264938e0cda
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanUpdateRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Role IDs with permission to delete comment records.
		/// Order preserved from source: RegularRoleId first, then AdministratorRoleId.
		/// Source GUIDs: f16ec6db-626d-4c27-8de0-3e7ce542c55f, bdc56420-caf0-4030-8a0e-d264938e0cda
		/// </summary>
		public static readonly IReadOnlyList<Guid> CanDeleteRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		#endregion

		#region Entity Properties

		/// <summary>
		/// Primary key. Auto-generated unique identifier for the comment record.
		/// Maps to the system <c>id</c> field (field GUID: 7ffcc5b7-e347-4923-af5f-e368549d7f16).
		/// </summary>
		[Key]
		[Column("id")]
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		/// <summary>
		/// Comment body text content.
		/// Source: InputTextField, Required=true, DefaultValue="body", System=true, Searchable=false.
		/// Field GUID: 0a4195d1-aa37-4aea-9c56-52e8d22d6f13
		/// </summary>
		[Column("body")]
		[JsonProperty(PropertyName = "body")]
		public string Body { get; set; } = "body";

		/// <summary>
		/// UUID of the user who created the comment.
		/// Cross-service reference: references User entity in Core service — UUID only,
		/// resolved via gRPC call to Core service. No foreign key constraint in
		/// database-per-service model.
		/// Source: InputGuidField, Required=true, DefaultValue=Guid.Empty, System=true, Searchable=false.
		/// Field GUID: 8b2d1f1c-bcdd-4c1d-94df-884205c2bf9c
		/// </summary>
		[Column("created_by")]
		[JsonProperty(PropertyName = "created_by")]
		public Guid CreatedBy { get; set; } = Guid.Empty;

		/// <summary>
		/// Scope classification for the comment.
		/// Source: InputTextField, Required=false, Searchable=false, System=false.
		/// Field GUID: 28ea1822-6030-4b63-9532-d7f846105a11
		/// Note: This is the only non-system field on the comment entity.
		/// </summary>
		[Column("l_scope")]
		[JsonProperty(PropertyName = "l_scope")]
		public string LScope { get; set; }

		/// <summary>
		/// Self-referencing parent comment ID enabling threaded/nested comment trees.
		/// When null, the comment is a root-level comment. When set, it is a reply
		/// to the comment with the given ID.
		/// Source: InputGuidField, Required=false, System=true, Searchable=false.
		/// Field GUID: 4629bdb5-9a79-4c87-b764-74491b5b2cfa
		/// </summary>
		[Column("parent_id")]
		[JsonProperty(PropertyName = "parent_id")]
		public Guid? ParentId { get; set; }

		/// <summary>
		/// CSV list of related record primary keys. Used to link comments to tasks,
		/// cases, and other entities within or across service boundaries.
		/// Source: InputTextField, Required=false, Searchable=true, Label="Related Record lookup", System=true.
		/// Field GUID: 364b886e-850a-438d-8e8f-4a6719272bfc
		/// </summary>
		[Column("l_related_records")]
		[JsonProperty(PropertyName = "l_related_records")]
		public string LRelatedRecords { get; set; }

		/// <summary>
		/// Timestamp when the comment was created. Defaults to the current UTC time.
		/// Source: InputDateTimeField, Required=true, UseCurrentTimeAsDefaultValue=true,
		/// Searchable=true, Format="yyyy-MMM-dd HH:mm", System=true.
		/// Field GUID: 4c61dbe0-04a6-4bca-b4cd-40689bd232f1
		/// </summary>
		[Column("created_on")]
		[JsonProperty(PropertyName = "created_on")]
		public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

		#endregion
	}
}
