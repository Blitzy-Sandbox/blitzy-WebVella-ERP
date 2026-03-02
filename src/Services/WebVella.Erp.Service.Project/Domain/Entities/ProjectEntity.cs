using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Entities
{
	/// <summary>
	/// Strongly-typed domain entity class for the 'project' entity, extracted from the
	/// WebVella ERP monolith (NextPlugin.20190203.cs lines 3960-4433) with updates applied
	/// from NextPlugin.20190204.cs (entity update — color, permissions) and
	/// NextPlugin.20190206.cs (l_scope field update — Required=true, Searchable=true).
	///
	/// Projects are the top-level organizational unit in the Project microservice,
	/// linking tasks, timelogs, milestones, and billing configurations.
	///
	/// Cross-service references:
	///   - AccountId references the 'account' entity in the CRM service (UUID only, resolved via gRPC/event).
	///   - OwnerId references the 'user' entity in the Core service (UUID only, resolved via gRPC).
	///
	/// Maps to the PostgreSQL 'rec_project' table in the Project microservice database
	/// under the database-per-service model.
	/// </summary>
	[Table("rec_project")]
	public class ProjectEntity
	{
		#region Entity Metadata Constants

		/// <summary>
		/// Well-known entity ID for the 'project' entity.
		/// Source: NextPlugin.20190203.cs line 3966.
		/// </summary>
		public static readonly Guid EntityId = new Guid("2d9b2d1d-e32b-45e1-a013-91d92a9ce792");

		/// <summary>
		/// Entity name as registered in the ERP system.
		/// </summary>
		public const string EntityName = "project";

		/// <summary>
		/// Singular display label for the entity.
		/// </summary>
		public const string EntityLabel = "Project";

		/// <summary>
		/// Plural display label for the entity.
		/// </summary>
		public const string EntityLabelPlural = "Projects";

		/// <summary>
		/// FontAwesome icon class for UI representation.
		/// Source: NextPlugin.20190204.cs line 2186 (confirmed unchanged from initial creation).
		/// </summary>
		public const string EntityIconName = "fas fa-cogs";

		/// <summary>
		/// Entity color for UI theming.
		/// Initially set to '#9c27b0' in patch 20190203, then updated to '#f44336' in patch 20190204.
		/// Source: NextPlugin.20190204.cs line 2187.
		/// </summary>
		public const string EntityColor = "#f44336";

		#endregion

		#region Entity Properties

		/// <summary>
		/// Primary key for the project record.
		/// System field ID GUID: 51990f1b-fbe9-4700-9b11-0822b149edd1.
		/// Source: NextPlugin.20190203.cs line 3965.
		/// </summary>
		[Key]
		[Column("id")]
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		/// <summary>
		/// Scope field for multi-tenant/scope filtering.
		/// Field ID: d19e0db3-6a35-4cc6-bb84-a213ecc2b3a5.
		/// Source: NextPlugin.20190203.cs line 4000, updated in NextPlugin.20190206.cs line 1142.
		/// Updated in patch 20190206: Required=true, Searchable=true, Default="".
		/// </summary>
		[Column("l_scope")]
		[JsonProperty(PropertyName = "l_scope")]
		public string LScope { get; set; } = "";

		/// <summary>
		/// Reference to account entity in CRM service (cross-service reference).
		/// Field ID: b30b4f7a-d5b0-423c-b341-76d2bcfff290.
		/// Source: NextPlugin.20190203.cs line 4030.
		/// CROSS-SERVICE: References 'account' entity (ID: 2e22b50f-e444-4b62-a171-076e51246939) in CRM service.
		/// UUID only — no foreign key constraint in database-per-service model.
		/// Resolved via gRPC call or event-sourced projection at read time.
		/// </summary>
		[Column("account_id")]
		[JsonProperty(PropertyName = "account_id")]
		public Guid? AccountId { get; set; }

		/// <summary>
		/// Project description (HTML content field).
		/// Field ID: a84c650d-67db-40f1-8cdd-14f735de6124.
		/// Source: NextPlugin.20190203.cs line 4060.
		/// Optional field, no default value.
		/// </summary>
		[Column("description")]
		[JsonProperty(PropertyName = "description")]
		public string Description { get; set; }

		/// <summary>
		/// Project name. Required field.
		/// Field ID: 2d8e82c2-38c6-4aa9-94c8-119bcda02db4.
		/// Source: NextPlugin.20190203.cs line 4089.
		/// Default: "name".
		/// </summary>
		[Column("name")]
		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; } = "name";

		/// <summary>
		/// Reference to user entity in Core service (cross-service reference) — the project owner.
		/// Field ID: 44a0a125-fab5-4be0-825b-24187946be21.
		/// Source: NextPlugin.20190203.cs line 4119.
		/// CROSS-SERVICE: References 'user' entity (ID: b9cebc3b-6443-452a-8e34-b311a73dcc8b) in Core service.
		/// UUID only — no foreign key constraint in database-per-service model.
		/// Source: Required=true, DefaultValue=Guid.Empty. Nullable in microservice context for flexibility.
		/// Related via 'user_1n_project_owner' relation (ID: 2f0ff495-54a0-4343-a4e5-67f5ca552519).
		/// </summary>
		[Column("owner_id")]
		[JsonProperty(PropertyName = "owner_id")]
		public Guid? OwnerId { get; set; } = Guid.Empty;

		/// <summary>
		/// Hourly rate for the project billing calculations.
		/// Field ID: 34a76b35-227d-404a-ba82-4575ca6679bc.
		/// Source: NextPlugin.20190203.cs line 4149.
		/// DecimalPlaces: 2. Optional field, no default value.
		/// </summary>
		[Column("hour_rate")]
		[JsonProperty(PropertyName = "hour_rate")]
		public decimal? HourRate { get; set; }

		/// <summary>
		/// Project start date.
		/// Field ID: 90bdb090-763b-4fb3-a79b-0652dfe170a1.
		/// Source: NextPlugin.20190203.cs line 4181.
		/// Format: "yyyy-MMM-dd HH:mm". Optional field, no default value.
		/// </summary>
		[Column("start_date")]
		[JsonProperty(PropertyName = "start_date")]
		public DateTime? StartDate { get; set; }

		/// <summary>
		/// Project end date.
		/// Field ID: b26ada8b-3ac4-4992-9ec6-f0764b9bdb68.
		/// Source: NextPlugin.20190203.cs line 4212.
		/// Format: "yyyy-MMM-dd HH:mm". Optional field, no default value.
		/// </summary>
		[Column("end_date")]
		[JsonProperty(PropertyName = "end_date")]
		public DateTime? EndDate { get; set; }

		/// <summary>
		/// Budget type classification for the project.
		/// Field ID: d558a173-0bfe-4be2-86a3-2b22e60cd9c4.
		/// Source: NextPlugin.20190203.cs line 4243.
		/// Required=true. Default: "none".
		/// Valid options: "none", "on amount", "on duration" (see <see cref="BudgetTypeOptions"/>).
		/// </summary>
		[Column("budget_type")]
		[JsonProperty(PropertyName = "budget_type")]
		public string BudgetType { get; set; } = "none";

		/// <summary>
		/// Project lifecycle status.
		/// Field ID: 683f6289-b642-4d8c-bf97-fd950d1bbd35.
		/// Source: NextPlugin.20190203.cs line 4278.
		/// Required=true. Default: "draft".
		/// Valid options: "draft", "published", "archived" (see <see cref="StatusOptions"/>).
		/// </summary>
		[Column("status")]
		[JsonProperty(PropertyName = "status")]
		public string Status { get; set; } = "draft";

		/// <summary>
		/// Project abbreviation used to identify tasks within the project scope.
		/// Field ID: 041ef7cc-7a70-4d90-bd78-63d513acd879.
		/// Source: NextPlugin.20190203.cs line 4313.
		/// Required=true. Default: "NXT".
		/// Help: "used to better identify the tasks".
		/// </summary>
		[Column("abbr")]
		[JsonProperty(PropertyName = "abbr")]
		public string Abbr { get; set; } = "NXT";

		/// <summary>
		/// Budget amount expressed in money or hours depending on <see cref="BudgetType"/>.
		/// Field ID: 4e654797-5661-4003-a64f-f9abb0e8d95a.
		/// Source: NextPlugin.20190203.cs line 4343.
		/// DecimalPlaces: 2. Optional field, no default value.
		/// Help: "money or hours".
		/// </summary>
		[Column("budget_amount")]
		[JsonProperty(PropertyName = "budget_amount")]
		public decimal? BudgetAmount { get; set; }

		/// <summary>
		/// Whether the project is billable. Determines default task billable status.
		/// Field ID: 93032d98-309d-4564-9244-5354ff284381.
		/// Source: NextPlugin.20190203.cs line 4375.
		/// Required=true. Default: true.
		/// Help: "default task billable status".
		/// </summary>
		[Column("is_billable")]
		[JsonProperty(PropertyName = "is_billable")]
		public bool IsBillable { get; set; } = true;

		/// <summary>
		/// Billing calculation method for the project.
		/// Field ID: f64e585c-dab4-4d51-9e73-5a769389aaa2.
		/// Source: NextPlugin.20190203.cs line 4404.
		/// Required=true. Default: "project_hour_cost".
		/// Valid options: "project_hour_cost", "user_hour_cost" (see <see cref="BillingMethodOptions"/>).
		/// </summary>
		[Column("billing_method")]
		[JsonProperty(PropertyName = "billing_method")]
		public string BillingMethod { get; set; } = "project_hour_cost";

		#endregion

		#region Permission Constants

		/// <summary>
		/// Roles allowed to create project records.
		/// Source: NextPlugin.20190204.cs line 2196 (final state after entity update).
		/// Administrator role only.
		/// </summary>
		public static readonly List<Guid> CanCreateRoles = new List<Guid>
		{
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Roles allowed to read project records.
		/// Source: NextPlugin.20190204.cs lines 2194-2195 (final state after entity update).
		/// Regular and Administrator roles.
		/// </summary>
		public static readonly List<Guid> CanReadRoles = new List<Guid>
		{
			SystemIds.RegularRoleId,
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Roles allowed to update project records.
		/// Source: NextPlugin.20190204.cs line 2197 (final state after entity update).
		/// Administrator role only.
		/// </summary>
		public static readonly List<Guid> CanUpdateRoles = new List<Guid>
		{
			SystemIds.AdministratorRoleId
		};

		/// <summary>
		/// Roles allowed to delete project records.
		/// Source: NextPlugin.20190204.cs (final state after entity update).
		/// No roles assigned — delete is not permitted via standard record permissions.
		/// </summary>
		public static readonly List<Guid> CanDeleteRoles = new List<Guid>();

		#endregion

		#region Select Option Definitions

		/// <summary>
		/// Available options for the <see cref="BudgetType"/> field.
		/// Source: NextPlugin.20190203.cs lines 4256-4260.
		/// </summary>
		public static readonly List<SelectOption> BudgetTypeOptions = new List<SelectOption>
		{
			new SelectOption("none", "none", "", ""),
			new SelectOption("on amount", "on amount", "", ""),
			new SelectOption("on duration", "on duration", "", "")
		};

		/// <summary>
		/// Available options for the <see cref="Status"/> field.
		/// Source: NextPlugin.20190203.cs lines 4291-4295.
		/// </summary>
		public static readonly List<SelectOption> StatusOptions = new List<SelectOption>
		{
			new SelectOption("draft", "draft", "", ""),
			new SelectOption("published", "published", "", ""),
			new SelectOption("archived", "archived", "", "")
		};

		/// <summary>
		/// Available options for the <see cref="BillingMethod"/> field.
		/// Source: NextPlugin.20190203.cs lines 4417-4420.
		/// </summary>
		public static readonly List<SelectOption> BillingMethodOptions = new List<SelectOption>
		{
			new SelectOption("project_hour_cost", "Project hour cost", "", ""),
			new SelectOption("user_hour_cost", "User hour cost", "", "")
		};

		#endregion

		#region Relation Metadata Constants

		/// <summary>
		/// Well-known field GUIDs for all fields on this entity, preserving the original
		/// identifiers from the monolith's entity/field registry.
		/// </summary>
		public static class FieldIds
		{
			/// <summary>System-generated primary key field ID.</summary>
			public static readonly Guid Id = new Guid("51990f1b-fbe9-4700-9b11-0822b149edd1");

			/// <summary>Field ID for l_scope.</summary>
			public static readonly Guid LScope = new Guid("d19e0db3-6a35-4cc6-bb84-a213ecc2b3a5");

			/// <summary>Field ID for account_id.</summary>
			public static readonly Guid AccountId = new Guid("b30b4f7a-d5b0-423c-b341-76d2bcfff290");

			/// <summary>Field ID for description.</summary>
			public static readonly Guid Description = new Guid("a84c650d-67db-40f1-8cdd-14f735de6124");

			/// <summary>Field ID for name.</summary>
			public static readonly Guid Name = new Guid("2d8e82c2-38c6-4aa9-94c8-119bcda02db4");

			/// <summary>Field ID for owner_id.</summary>
			public static readonly Guid OwnerId = new Guid("44a0a125-fab5-4be0-825b-24187946be21");

			/// <summary>Field ID for hour_rate.</summary>
			public static readonly Guid HourRate = new Guid("34a76b35-227d-404a-ba82-4575ca6679bc");

			/// <summary>Field ID for start_date.</summary>
			public static readonly Guid StartDate = new Guid("90bdb090-763b-4fb3-a79b-0652dfe170a1");

			/// <summary>Field ID for end_date.</summary>
			public static readonly Guid EndDate = new Guid("b26ada8b-3ac4-4992-9ec6-f0764b9bdb68");

			/// <summary>Field ID for budget_type.</summary>
			public static readonly Guid BudgetType = new Guid("d558a173-0bfe-4be2-86a3-2b22e60cd9c4");

			/// <summary>Field ID for status.</summary>
			public static readonly Guid Status = new Guid("683f6289-b642-4d8c-bf97-fd950d1bbd35");

			/// <summary>Field ID for abbr.</summary>
			public static readonly Guid Abbr = new Guid("041ef7cc-7a70-4d90-bd78-63d513acd879");

			/// <summary>Field ID for budget_amount.</summary>
			public static readonly Guid BudgetAmount = new Guid("4e654797-5661-4003-a64f-f9abb0e8d95a");

			/// <summary>Field ID for is_billable.</summary>
			public static readonly Guid IsBillable = new Guid("93032d98-309d-4564-9244-5354ff284381");

			/// <summary>Field ID for billing_method.</summary>
			public static readonly Guid BillingMethod = new Guid("f64e585c-dab4-4d51-9e73-5a769389aaa2");
		}

		/// <summary>
		/// Well-known relation IDs involving the project entity, preserving the original
		/// identifiers from the monolith's entity relation registry.
		/// </summary>
		public static class RelationIds
		{
			/// <summary>
			/// ManyToMany relation between project and task entities.
			/// Source: NextPlugin.20190203.cs line 5023.
			/// Origin: project (id) → Target: task (id, entity ID: 9386226e-381e-4522-b27b-fb5514d77902).
			/// </summary>
			public static readonly Guid ProjectTaskRelation = new Guid("b1db4466-7423-44e9-b6b9-3063222c9e15");

			/// <summary>
			/// ManyToMany relation between project and milestone entities.
			/// Source: NextPlugin.20190203.cs line 5052.
			/// Origin: project (id) → Target: milestone (id, entity ID: c15f030a-9d94-4767-89aa-c55a09f8b83e).
			/// </summary>
			public static readonly Guid ProjectMilestoneRelation = new Guid("55c8d6e2-f26d-4689-9d1b-a8c1b9de1672");

			/// <summary>
			/// OneToMany relation from user to project (owner relationship).
			/// Source: NextPlugin.20190203.cs line 5197.
			/// Origin: user (id, entity ID: b9cebc3b-6443-452a-8e34-b311a73dcc8b) → Target: project (owner_id).
			/// CROSS-SERVICE: User entity resides in Core service; resolved via gRPC.
			/// </summary>
			public static readonly Guid UserProjectOwnerRelation = new Guid("2f0ff495-54a0-4343-a4e5-67f5ca552519");
		}

		#endregion
	}
}
