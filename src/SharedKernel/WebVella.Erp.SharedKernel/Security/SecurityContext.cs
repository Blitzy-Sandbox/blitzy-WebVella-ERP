using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Security
{

	/// <summary>
	/// Provides ambient security context for user identity resolution across
	/// async call chains. Uses AsyncLocal&lt;SecurityContext&gt; with a
	/// Stack&lt;ErpUser&gt; to support nested user scopes via the IDisposable
	/// OpenScope pattern.
	///
	/// Migrated from <c>WebVella.Erp.Api.SecurityContext</c> to the SharedKernel
	/// to enable cross-service identity propagation. Extended with JWT claims
	/// extraction methods (OpenScope(ClaimsPrincipal), OpenScope(JwtSecurityToken),
	/// ExtractUserFromClaims) for microservice middleware to open security scopes
	/// directly from validated JWT tokens.
	///
	/// No database access or HttpContext dependency — pure in-memory identity management.
	/// </summary>
	public class SecurityContext : IDisposable
	{
		private static ErpUser systemUser;
		private static AsyncLocal<SecurityContext> current;
		private Stack<ErpUser> userStack;

		static SecurityContext()
		{
			systemUser = new ErpUser();
			systemUser.Id = SystemIds.SystemUserId;
			systemUser.FirstName = "Local";
			systemUser.LastName = "System";
			systemUser.Username = "system";
			systemUser.Email = "system@webvella.com";
			systemUser.Enabled = true;
			systemUser.Roles.Add(new ErpRole { Id = SystemIds.AdministratorRoleId, Name = "administrator" });
		}

		private SecurityContext()
		{
			userStack = new Stack<ErpUser>();
		}

		/// <summary>
		/// Returns the current user at the top of the scope stack for the
		/// current async execution flow. Returns null if no scope is open.
		/// </summary>
		public static ErpUser CurrentUser
		{
			get
			{
				if (current == null || current.Value == null)
					return null;

				return current.Value.userStack.Count > 0 ? current.Value.userStack.Peek() : null;
			}
		}

		/// <summary>
		/// Checks whether the current user holds any of the specified roles.
		/// Delegates to the Guid[] overload after extracting role IDs.
		/// </summary>
		/// <param name="roles">One or more ErpRole instances to check against.</param>
		/// <returns>True if the current user has at least one of the specified roles.</returns>
		public static bool IsUserInRole(params ErpRole[] roles)
		{
			var currentUser = CurrentUser;
			if (currentUser != null && roles != null && roles.Any())
				return IsUserInRole(roles.Select(x => x.Id).ToArray());

			return false;
		}

		/// <summary>
		/// Checks whether the current user holds any of the specified role IDs.
		/// </summary>
		/// <param name="roles">One or more role GUIDs to check against.</param>
		/// <returns>True if the current user has at least one of the specified role IDs.</returns>
		public static bool IsUserInRole(params Guid[] roles)
		{
			var currentUser = CurrentUser;
			if (currentUser != null && roles != null && roles.Any())
				return currentUser.Roles.Any(x => roles.Any(z => z == x.Id));

			return false;
		}

		/// <summary>
		/// Determines whether the specified (or current) user has the given CRUD
		/// permission on the specified entity, based on RecordPermissions role lists.
		/// The system user (SystemIds.SystemUserId) always has all permissions.
		/// When no user is available, guest role permissions are checked.
		/// </summary>
		/// <param name="permission">The CRUD permission type to check.</param>
		/// <param name="entity">The entity whose RecordPermissions are checked.</param>
		/// <param name="user">Optional user; defaults to CurrentUser if null.</param>
		/// <returns>True if the user (or guest) has the specified permission.</returns>
		public static bool HasEntityPermission(EntityPermission permission, Entity entity, ErpUser user = null)
		{
			if (entity == null)
				throw new ArgumentNullException("entity");

			if (user == null)
				user = CurrentUser;

			if (user != null)
			{
				//system user has unlimited permissions :)
				if (user.Id == SystemIds.SystemUserId)
					return true;

				switch (permission)
				{
					case EntityPermission.Read:
						return user.Roles.Any(x => entity.RecordPermissions.CanRead.Any(z => z == x.Id));
					case EntityPermission.Create:
						return user.Roles.Any(x => entity.RecordPermissions.CanCreate.Any(z => z == x.Id));
					case EntityPermission.Update:
						return user.Roles.Any(x => entity.RecordPermissions.CanUpdate.Any(z => z == x.Id));
					case EntityPermission.Delete:
						return user.Roles.Any(x => entity.RecordPermissions.CanDelete.Any(z => z == x.Id));
					default:
						throw new NotSupportedException("Entity permission type is not supported");
				}
			}
			else
			{
				switch (permission)
				{
					case EntityPermission.Read:
						return entity.RecordPermissions.CanRead.Any(z => z == SystemIds.GuestRoleId);
					case EntityPermission.Create:
						return entity.RecordPermissions.CanCreate.Any(z => z == SystemIds.GuestRoleId);
					case EntityPermission.Update:
						return entity.RecordPermissions.CanUpdate.Any(z => z == SystemIds.GuestRoleId);
					case EntityPermission.Delete:
						return entity.RecordPermissions.CanDelete.Any(z => z == SystemIds.GuestRoleId);
					default:
						throw new NotSupportedException("Entity permission type is not supported");
				}
			}
		}

		/// <summary>
		/// Determines whether the specified (or current) user has metadata
		/// management permission. Only administrators have this permission.
		/// </summary>
		/// <param name="user">Optional user; defaults to CurrentUser if null.</param>
		/// <returns>True if the user holds the Administrator role.</returns>
		public static bool HasMetaPermission(ErpUser user = null)
		{
			if (user == null)
				user = CurrentUser;

			if (user == null)
				return false;

			return user.Roles.Any(x => x.Id == SystemIds.AdministratorRoleId);
		}

		/// <summary>
		/// Opens a new security scope for the given user, pushing the user onto
		/// the scope stack. The returned IDisposable pops the user on Dispose,
		/// enabling nested using blocks for temporary identity elevation.
		/// </summary>
		/// <param name="user">The ErpUser to set as the current user for this scope.</param>
		/// <returns>An IDisposable that closes this scope on disposal.</returns>
		public static IDisposable OpenScope(ErpUser user)
		{
			if (current == null)
			{
				current = new AsyncLocal<SecurityContext>();
				current.Value = new SecurityContext();
			}
			if (current.Value == null)
				current.Value = new SecurityContext();

			current.Value.userStack.Push(user);
			return current.Value;
		}

		/// <summary>
		/// Opens a security scope for the built-in system user, which has
		/// unlimited permissions (Administrator role, bypasses all permission checks).
		/// Used by background jobs and service-internal operations.
		/// </summary>
		/// <returns>An IDisposable that closes the system scope on disposal.</returns>
		public static IDisposable OpenSystemScope()
		{
			return OpenScope(systemUser);
		}

		/// <summary>
		/// Opens a security scope by extracting user identity from a
		/// ClaimsPrincipal, typically populated by ASP.NET Core JWT Bearer
		/// authentication middleware. Enables microservices to establish
		/// security context directly from the incoming HTTP request identity.
		/// </summary>
		/// <param name="principal">The ClaimsPrincipal containing JWT claims.</param>
		/// <returns>An IDisposable that closes this scope on disposal.</returns>
		/// <exception cref="ArgumentNullException">Thrown when principal is null.</exception>
		/// <exception cref="ArgumentException">Thrown when user identity cannot be extracted from claims.</exception>
		public static IDisposable OpenScope(ClaimsPrincipal principal)
		{
			if (principal == null)
				throw new ArgumentNullException(nameof(principal));

			var user = ErpUser.FromClaims(principal.Claims);

			if (user == null || user.Id == Guid.Empty)
				throw new ArgumentException("Cannot extract valid user identity from ClaimsPrincipal. " +
					"Ensure the token contains a NameIdentifier claim with a valid GUID.", nameof(principal));

			return OpenScope(user);
		}

		/// <summary>
		/// Opens a security scope by extracting user identity from a validated
		/// JwtSecurityToken. Enables microservice middleware to establish
		/// security context directly from a decoded JWT token without needing
		/// a full ClaimsPrincipal.
		/// </summary>
		/// <param name="jwtToken">The validated JwtSecurityToken containing user claims.</param>
		/// <returns>An IDisposable that closes this scope on disposal.</returns>
		/// <exception cref="ArgumentNullException">Thrown when jwtToken is null.</exception>
		/// <exception cref="ArgumentException">Thrown when user identity cannot be extracted from token claims.</exception>
		public static IDisposable OpenScope(JwtSecurityToken jwtToken)
		{
			if (jwtToken == null)
				throw new ArgumentNullException(nameof(jwtToken));

			var user = ErpUser.FromClaims(jwtToken.Claims);

			if (user == null || user.Id == Guid.Empty)
				throw new ArgumentException("Cannot extract valid user identity from JwtSecurityToken. " +
					"Ensure the token contains a NameIdentifier claim with a valid GUID.", nameof(jwtToken));

			return OpenScope(user);
		}

		/// <summary>
		/// Extracts an ErpUser from a collection of JWT claims. This is a
		/// convenience utility for cross-service identity propagation where
		/// a downstream service needs to reconstruct the user from raw claims.
		/// Delegates to <see cref="ErpUser.FromClaims(IEnumerable{Claim})"/>.
		/// </summary>
		/// <param name="claims">The JWT claims to map into an ErpUser.</param>
		/// <returns>A populated ErpUser, or null if the NameIdentifier claim is missing or invalid.</returns>
		public static ErpUser ExtractUserFromClaims(IEnumerable<Claim> claims)
		{
			if (claims == null)
				return null;

			var user = ErpUser.FromClaims(claims);

			// Per specification: return null if NameIdentifier claim is missing,
			// meaning user.Id was never set and remains Guid.Empty (default).
			if (user.Id == Guid.Empty)
				return null;

			return user;
		}

		/// <summary>
		/// Closes the current scope by popping the top user from the stack.
		/// If the stack becomes empty, the AsyncLocal value is cleared.
		/// </summary>
		private static void CloseScope()
		{
			if (current != null && current.Value != null)
			{
				var stack = current.Value.userStack;
				if (stack.Count > 0)
				{
					var user = stack.Pop();
					if (stack.Count == 0)
						current.Value = null;
				}
			}
		}

		/// <summary>
		/// Disposes this SecurityContext instance, closing the current scope.
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Releases resources used by this SecurityContext instance.
		/// When disposing is true, closes the current user scope.
		/// </summary>
		/// <param name="disposing">True if called from Dispose(); false if from finalizer.</param>
		public void Dispose(bool disposing)
		{
			if (disposing)
				CloseScope();
		}
	}
}
