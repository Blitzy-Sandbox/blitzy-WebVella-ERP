using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Abstraction for security context access within the EQL engine.
	/// <para>
	/// In the monolith, <c>EqlCommand.ConvertJObjectToEntityRecord</c> called
	/// <c>SecurityContext.HasEntityPermission(EntityPermission.Read, entity)</c> to check
	/// whether the current user has read access to the entity being queried.
	/// In the microservice architecture, each service provides its own implementation
	/// that checks permissions based on JWT claims propagated across service boundaries.
	/// </para>
	/// <para>
	/// When null is passed to <see cref="EqlCommand"/>, permission checking is skipped,
	/// which is appropriate for internal service-to-service calls that operate under
	/// system scope (equivalent to the monolith's <c>SecurityContext.OpenSystemScope()</c>).
	/// </para>
	/// </summary>
	public interface ISecurityContextAccessor
	{
		/// <summary>
		/// Checks whether the current user/context has the specified permission on the given entity.
		/// Corresponds to the monolith's <c>SecurityContext.HasEntityPermission(permission, entity)</c>.
		/// </summary>
		/// <param name="permission">The permission type to check (Read, Create, Update, Delete).</param>
		/// <param name="entity">The entity definition to check permissions against.</param>
		/// <returns>True if the current context has the specified permission; false otherwise.</returns>
		bool HasEntityPermission(EntityPermission permission, Entity entity);
	}
}
