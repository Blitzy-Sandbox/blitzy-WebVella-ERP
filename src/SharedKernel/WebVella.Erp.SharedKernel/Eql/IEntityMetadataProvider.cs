using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Abstraction for entity and relation metadata access required by the EQL engine.
	/// <para>
	/// In the monolith, <c>EqlBuilder</c> and <c>EqlBuilder.Sql</c> directly instantiated
	/// <c>EntityManager</c> and <c>EntityRelationManager</c> to read entity definitions and
	/// relation metadata from the shared database. In the microservice architecture, each service
	/// provides its own implementation that reads from its owned database, ensuring the EQL engine
	/// operates within service boundaries.
	/// </para>
	/// <para>
	/// The <see cref="ReadEntities"/> method corresponds to the monolith's
	/// <c>EntityManager.ReadEntities().Object</c> call used extensively in
	/// <c>EqlBuilder.Sql.cs</c> for entity resolution, field lookup, and relation traversal.
	/// The <see cref="ReadEntity"/> method corresponds to <c>EntityManager.ReadEntity(name).Object</c>
	/// used in <c>EqlCommand.cs</c> for permission checking during result mapping.
	/// The <see cref="ReadRelations"/> method corresponds to <c>EntityRelationManager.Read().Object</c>
	/// used for relation join generation and WHERE clause relation resolution.
	/// </para>
	/// </summary>
	public interface IEntityMetadataProvider
	{
		/// <summary>
		/// Reads all entity definitions owned by this service.
		/// Returns the same structure as monolith's <c>EntityManager.ReadEntities().Object</c>.
		/// Used by <c>EqlBuilder.Sql.BuildSql</c> for entity lookup by name and field resolution.
		/// </summary>
		/// <returns>List of all entities with their fields, or empty list if none exist.</returns>
		List<Entity> ReadEntities();

		/// <summary>
		/// Reads a single entity definition by name.
		/// Returns the same structure as monolith's <c>EntityManager.ReadEntity(name).Object</c>.
		/// Used by <c>EqlCommand.ConvertJObjectToEntityRecord</c> for permission checking.
		/// </summary>
		/// <param name="entityName">The name of the entity to read.</param>
		/// <returns>The entity definition, or null if not found.</returns>
		Entity ReadEntity(string entityName);

		/// <summary>
		/// Reads all entity relations owned by this service.
		/// Returns the same structure as monolith's <c>EntityRelationManager.Read().Object</c>.
		/// Used by <c>EqlBuilder.Sql.ProcessRelationField</c> and <c>ProcessWhereJoins</c>
		/// for relation traversal SQL generation.
		/// </summary>
		/// <returns>List of all entity relations, or empty list if none exist.</returns>
		List<EntityRelation> ReadRelations();
	}
}
