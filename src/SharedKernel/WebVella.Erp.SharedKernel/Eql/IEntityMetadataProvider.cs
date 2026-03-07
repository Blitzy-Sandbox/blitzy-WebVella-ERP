using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Composite abstraction for entity and relation metadata access required by the EQL engine.
	/// Extends <see cref="IEqlEntityProvider"/> for entity operations.
	/// <para>
	/// In the monolith, <c>EqlBuilder</c> and <c>EqlBuilder.Sql</c> directly instantiated
	/// <c>EntityManager</c> and <c>EntityRelationManager</c> to read entity definitions and
	/// relation metadata from the shared database. In the microservice architecture, each service
	/// provides its own implementation that reads from its owned database, ensuring the EQL engine
	/// operates within service boundaries.
	/// </para>
	/// <para>
	/// This interface provides a convenience abstraction combining entity and relation access in one interface
	/// for callers (like <see cref="EqlCommand"/>) that need both capabilities. The core EQL builder uses the
	/// more granular <see cref="IEqlEntityProvider"/> and <see cref="IEqlRelationProvider"/> interfaces.
	/// </para>
	/// </summary>
	public interface IEntityMetadataProvider : IEqlEntityProvider
	{
		/// <summary>
		/// Reads all entity relations owned by this service.
		/// Returns the same structure as monolith's <c>EntityRelationManager.Read().Object</c>.
		/// Used by <c>EqlBuilder.Sql.ProcessRelationField</c> and <c>ProcessWhereJoins</c>
		/// for relation traversal SQL generation.
		/// </summary>
		/// <returns>List of all entity relations, or empty list if none exist.</returns>
		List<EntityRelation> ReadRelations();
	}

	/// <summary>
	/// Internal adapter bridging <see cref="IEntityMetadataProvider.ReadRelations()"/> to the
	/// <see cref="IEqlRelationProvider"/> interface expected by <see cref="EqlBuilder"/>.
	/// Used by <see cref="EqlCommand"/> to pass relation metadata from an <see cref="IEntityMetadataProvider"/>
	/// to the <see cref="EqlBuilder"/> constructor.
	/// </summary>
	internal class MetadataRelationProviderAdapter : IEqlRelationProvider
	{
		private readonly IEntityMetadataProvider _provider;

		public MetadataRelationProviderAdapter(IEntityMetadataProvider provider)
		{
			_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		}

		public List<EntityRelation> Read() => _provider.ReadRelations();

		public EntityRelation Read(string name)
		{
			var relations = _provider.ReadRelations();
			return relations?.Find(r => r.Name == name);
		}

		public EntityRelation Read(Guid id)
		{
			var relations = _provider.ReadRelations();
			return relations?.Find(r => r.Id == id);
		}
	}
}
