using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Extended abstraction for record search hook execution within the EQL engine.
	/// Extends <see cref="IEqlHookProvider"/> to provide backward compatibility with the
	/// monolith's <c>RecordHookManager</c> pattern.
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
	public interface IRecordSearchHookExecutor : IEqlHookProvider
	{
		// All methods are inherited from IEqlHookProvider:
		// - bool ContainsAnyHooksForEntity(string entityName)
		// - void ExecutePreSearchRecordHooks(string entityName, EqlSelectNode selectNode, List<EqlError> errors)
		// - void ExecutePostSearchRecordHooks(string entityName, EntityRecordList records)
	}
}
