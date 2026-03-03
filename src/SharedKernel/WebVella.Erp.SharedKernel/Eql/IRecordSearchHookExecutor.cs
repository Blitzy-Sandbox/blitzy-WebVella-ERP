using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Abstraction for record search hook execution within the EQL engine.
	/// <para>
	/// In the monolith, <c>RecordHookManager.ContainsAnyHooksForEntity</c> and
	/// <c>RecordHookManager.ExecutePreSearchRecordHooks</c> were called directly during
	/// EQL building and execution. In the microservice architecture, hooks are replaced by
	/// domain events, but this interface preserves backward compatibility for services that
	/// still use the hook pattern internally.
	/// </para>
	/// <para>
	/// Services that do not use search hooks can pass <c>null</c> for this dependency in the
	/// <see cref="EqlBuilder"/> constructor, which disables hook execution entirely.
	/// </para>
	/// </summary>
	public interface IRecordSearchHookExecutor
	{
		/// <summary>
		/// Checks whether any search hooks are registered for the specified entity.
		/// Corresponds to monolith's <c>RecordHookManager.ContainsAnyHooksForEntity(entityName)</c>.
		/// </summary>
		/// <param name="entityName">The entity name to check for registered hooks.</param>
		/// <returns>True if any search hooks exist for the entity; false otherwise.</returns>
		bool ContainsAnyHooksForEntity(string entityName);

		/// <summary>
		/// Executes pre-search record hooks for the specified entity.
		/// Corresponds to monolith's <c>RecordHookManager.ExecutePreSearchRecordHooks(entityName, selectNode, errors)</c>.
		/// Hooks may modify the select node or add errors to cancel the search.
		/// </summary>
		/// <param name="entityName">The entity name to execute hooks for.</param>
		/// <param name="selectNode">The EQL select node that hooks can inspect or modify.</param>
		/// <param name="errors">Error list that hooks can append to for search cancellation.</param>
		void ExecutePreSearchRecordHooks(string entityName, EqlSelectNode selectNode, List<EqlError> errors);

		/// <summary>
		/// Executes post-search record hooks for the specified entity.
		/// Corresponds to monolith's <c>RecordHookManager.ExecutePostSearchRecordHooks(entityName, result)</c>.
		/// Hooks may modify the result set after query execution.
		/// </summary>
		/// <param name="entityName">The entity name to execute hooks for.</param>
		/// <param name="result">The entity record list that hooks can inspect or modify.</param>
		void ExecutePostSearchRecordHooks(string entityName, Models.EntityRecordList result);
	}
}
