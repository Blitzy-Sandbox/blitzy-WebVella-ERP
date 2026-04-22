using Microsoft.Extensions.Logging;
using WebVellaErp.Identity.Models;

namespace WebVellaErp.Identity.Services
{
    /// <summary>
    /// Defines the entity-level record permission set for role-based access control.
    ///
    /// <para>
    /// Replaces the monolith's <c>Entity.RecordPermissions</c> object, which held
    /// lists of role IDs authorized for each CRUD operation on a given entity. In the
    /// microservices architecture, this DTO is passed from the Entity Management
    /// service or fetched from entity metadata, decoupling the permission check
    /// logic from the entity storage layer.
    /// </para>
    ///
    /// <para>
    /// Each property contains the <see cref="Guid"/> IDs of roles that are authorized
    /// for the corresponding operation. An empty list means no role (not even guest)
    /// is authorized for that operation.
    /// </para>
    /// </summary>
    public class EntityPermissionSet
    {
        /// <summary>
        /// Role IDs authorized to read records of this entity.
        /// Replaces <c>entity.RecordPermissions.CanRead</c> (SecurityContext.cs line 80).
        /// Default: empty list (no roles authorized).
        /// </summary>
        public List<Guid> CanRead { get; set; }

        /// <summary>
        /// Role IDs authorized to create records of this entity.
        /// Replaces <c>entity.RecordPermissions.CanCreate</c> (SecurityContext.cs line 82).
        /// Default: empty list (no roles authorized).
        /// </summary>
        public List<Guid> CanCreate { get; set; }

        /// <summary>
        /// Role IDs authorized to update records of this entity.
        /// Replaces <c>entity.RecordPermissions.CanUpdate</c> (SecurityContext.cs line 84).
        /// Default: empty list (no roles authorized).
        /// </summary>
        public List<Guid> CanUpdate { get; set; }

        /// <summary>
        /// Role IDs authorized to delete records of this entity.
        /// Replaces <c>entity.RecordPermissions.CanDelete</c> (SecurityContext.cs line 86).
        /// Default: empty list (no roles authorized).
        /// </summary>
        public List<Guid> CanDelete { get; set; }

        /// <summary>
        /// Initializes a new <see cref="EntityPermissionSet"/> with all permission
        /// lists set to empty (most restrictive default — no role has any permission).
        /// </summary>
        public EntityPermissionSet()
        {
            CanRead = new List<Guid>();
            CanCreate = new List<Guid>();
            CanUpdate = new List<Guid>();
            CanDelete = new List<Guid>();
        }
    }

    /// <summary>
    /// Defines the contract for stateless permission checking in the Identity &amp;
    /// Access Management bounded context.
    ///
    /// <para>
    /// Replaces the monolith's static <c>SecurityContext</c> class
    /// (<c>WebVella.Erp/Api/SecurityContext.cs</c> lines 11-168) which used
    /// <c>AsyncLocal&lt;SecurityContext&gt;</c> with a <c>Stack&lt;ErpUser&gt;</c>
    /// for ambient user scoping. This interface is <b>stateless</b> — all user
    /// context is passed as method parameters extracted from JWT claims.
    /// </para>
    ///
    /// <para><b>Design rationale:</b> In the monolith, <c>SecurityContext</c> acted
    /// as ambient state because the server processed requests in-process with middleware
    /// setting up scope. In Lambda, each invocation is independent — JWT claims from
    /// API Gateway provide the user context. The permission <i>logic</i> is identical;
    /// only the context delivery mechanism changes.</para>
    ///
    /// <para>Register as singleton in the DI container — the implementation is
    /// thread-safe and holds no mutable state.</para>
    /// </summary>
    public interface IPermissionService
    {
        /// <summary>
        /// Checks whether the specified user (or guest if <paramref name="user"/> is
        /// <c>null</c>) has the given entity-level permission based on the role ID
        /// lists in <paramref name="recordPermissions"/>.
        ///
        /// <para>Replaces <c>SecurityContext.HasEntityPermission(EntityPermission,
        /// Entity, ErpUser)</c> (source SecurityContext.cs lines 63-107).</para>
        /// </summary>
        /// <param name="permission">The CRUD operation to check.</param>
        /// <param name="recordPermissions">
        /// The entity's permission set containing role ID lists per operation.
        /// Must not be <c>null</c>.
        /// </param>
        /// <param name="user">
        /// The authenticated user, or <c>null</c> for unauthenticated (guest) access.
        /// </param>
        /// <returns>
        /// <c>true</c> if the user (or guest) is authorized for the operation;
        /// <c>false</c> otherwise.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="recordPermissions"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="permission"/> is not a recognized
        /// <see cref="EntityPermission"/> value.
        /// </exception>
        bool HasEntityPermission(EntityPermission permission, EntityPermissionSet recordPermissions, User? user);

        /// <summary>
        /// Checks whether the specified user has entity metadata modification
        /// permission (administrator-only).
        ///
        /// <para>Replaces <c>SecurityContext.HasMetaPermission(ErpUser)</c>
        /// (source SecurityContext.cs lines 109-118).</para>
        /// </summary>
        /// <param name="user">
        /// The authenticated user, or <c>null</c> for unauthenticated access.
        /// </param>
        /// <returns>
        /// <c>true</c> if the user has the Administrator role; <c>false</c> otherwise
        /// (including when <paramref name="user"/> is <c>null</c>).
        /// </returns>
        bool HasMetaPermission(User? user);

        /// <summary>
        /// Checks whether the specified user holds any of the given role IDs.
        ///
        /// <para>Replaces <c>SecurityContext.IsUserInRole(params Guid[])</c>
        /// (source SecurityContext.cs lines 54-61). The user parameter replaces the
        /// ambient <c>CurrentUser</c> static property.</para>
        /// </summary>
        /// <param name="user">The user whose roles to check.</param>
        /// <param name="roleIds">One or more role IDs to check membership against.</param>
        /// <returns>
        /// <c>true</c> if the user holds at least one of the specified roles;
        /// <c>false</c> otherwise.
        /// </returns>
        bool IsUserInRole(User user, params Guid[] roleIds);

        /// <summary>
        /// Checks whether the specified user holds any of the given roles.
        ///
        /// <para>Replaces <c>SecurityContext.IsUserInRole(params ErpRole[])</c>
        /// (source SecurityContext.cs lines 45-52). Delegates to the
        /// <see cref="IsUserInRole(User, Guid[])"/> overload after extracting role IDs.</para>
        /// </summary>
        /// <param name="user">The user whose roles to check.</param>
        /// <param name="roles">One or more <see cref="Role"/> objects to check membership against.</param>
        /// <returns>
        /// <c>true</c> if the user holds at least one of the specified roles;
        /// <c>false</c> otherwise.
        /// </returns>
        bool IsUserInRole(User user, params Role[] roles);

        /// <summary>
        /// Checks whether the specified user is the well-known system user
        /// (<see cref="User.SystemUserId"/>: <c>10000000-0000-0000-0000-000000000000</c>).
        ///
        /// <para>Replaces inline <c>user.Id == SystemIds.SystemUserId</c> checks
        /// (source SecurityContext.cs line 74).</para>
        /// </summary>
        /// <param name="user">The user to check, or <c>null</c>.</param>
        /// <returns>
        /// <c>true</c> if the user is the system user; <c>false</c> if <paramref name="user"/>
        /// is <c>null</c> or has a different ID.
        /// </returns>
        bool IsSystemUser(User? user);
    }

    /// <summary>
    /// Stateless, thread-safe permission checking service for the Identity &amp;
    /// Access Management microservice.
    ///
    /// <para>
    /// Replaces the monolith's <c>SecurityContext</c> class
    /// (<c>WebVella.Erp/Api/SecurityContext.cs</c> lines 11-168). The original used
    /// <c>AsyncLocal&lt;SecurityContext&gt;</c> with a <c>Stack&lt;ErpUser&gt;</c>
    /// for ambient per-request user scoping. This implementation is <b>stateless</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>NO <c>AsyncLocal</c> — async-local ambient state is eliminated.</description></item>
    ///   <item><description>NO <c>Stack&lt;ErpUser&gt;</c> — no nested user scope stack.</description></item>
    ///   <item><description>NO <c>CurrentUser</c> static property — user comes from JWT claims,
    ///     passed as method parameter.</description></item>
    ///   <item><description>NO <c>OpenScope</c>/<c>CloseScope</c> — no scope management.</description></item>
    ///   <item><description>NO <c>IDisposable</c> — no resources to dispose.</description></item>
    /// </list>
    ///
    /// <para><b>Preserved behaviors (full functional parity):</b></para>
    /// <list type="bullet">
    ///   <item><description><see cref="HasEntityPermission"/> — exact same role-based entity
    ///     permission logic with authenticated + guest paths.</description></item>
    ///   <item><description><see cref="HasMetaPermission"/> — exact same administrator-only
    ///     metadata permission check.</description></item>
    ///   <item><description><see cref="IsUserInRole"/> — exact same role membership check
    ///     (both <see cref="Guid"/>[] and <see cref="Role"/>[] overloads).</description></item>
    ///   <item><description>System user unlimited permissions — <see cref="User.SystemUserId"/>
    ///     bypasses all permission checks.</description></item>
    ///   <item><description>Guest role fallback — unauthenticated requests check
    ///     <see cref="Role.GuestRoleId"/> permissions.</description></item>
    /// </list>
    ///
    /// <para>Safe for DI singleton registration — holds no mutable instance state.</para>
    /// </summary>
    public class PermissionService : IPermissionService
    {
        private readonly ILogger<PermissionService> _logger;

        /// <summary>
        /// Initializes a new <see cref="PermissionService"/> instance.
        ///
        /// <para><b>CRITICAL:</b> This constructor takes only an <see cref="ILogger{T}"/>
        /// — no database connections, no Cognito clients, no static mutable state.
        /// The service is fully stateless and safe for singleton DI registration.</para>
        /// </summary>
        /// <param name="logger">
        /// Logger for structured JSON logging with correlation-ID propagation
        /// (per AAP Section 0.8.5).
        /// </param>
        public PermissionService(ILogger<PermissionService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para><b>Behavioral parity with source</b>
        /// (<c>SecurityContext.HasEntityPermission</c>, lines 63-107):</para>
        ///
        /// <para><b>Authenticated path</b> (user ≠ null):</para>
        /// <list type="number">
        ///   <item><description>System user (<see cref="User.SystemUserId"/>) gets unlimited
        ///     permissions — returns <c>true</c> immediately.</description></item>
        ///   <item><description>Otherwise, checks if any of the user's role IDs appear in
        ///     the corresponding permission list (CanRead/CanCreate/CanUpdate/CanDelete).</description></item>
        /// </list>
        ///
        /// <para><b>Guest path</b> (user == null):</para>
        /// <list type="number">
        ///   <item><description>Checks if <see cref="Role.GuestRoleId"/>
        ///     (<c>987148B1-AFA8-4B33-8616-55861E5FD065</c>) appears in the corresponding
        ///     permission list.</description></item>
        /// </list>
        /// </remarks>
        public bool HasEntityPermission(EntityPermission permission, EntityPermissionSet recordPermissions, User? user)
        {
            // Step 1: Null check — preserves source SecurityContext.cs line 65-66
            if (recordPermissions == null)
                throw new ArgumentNullException(nameof(recordPermissions));

            // Step 2: Authenticated user path — source lines 71-89
            if (user != null)
            {
                // System user has unlimited permissions :)
                // Preserves source SecurityContext.cs line 73-75
                if (user.Id == User.SystemUserId)
                    return true;

                // Role-based permission check — source lines 77-89
                switch (permission)
                {
                    case EntityPermission.Read:
                        {
                            bool allowed = user.Roles.Any(x => recordPermissions.CanRead.Any(z => z == x.Id));
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied: UserId={UserId}, Permission=Read",
                                    user.Id);
                            }
                            return allowed;
                        }
                    case EntityPermission.Create:
                        {
                            bool allowed = user.Roles.Any(x => recordPermissions.CanCreate.Any(z => z == x.Id));
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied: UserId={UserId}, Permission=Create",
                                    user.Id);
                            }
                            return allowed;
                        }
                    case EntityPermission.Update:
                        {
                            bool allowed = user.Roles.Any(x => recordPermissions.CanUpdate.Any(z => z == x.Id));
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied: UserId={UserId}, Permission=Update",
                                    user.Id);
                            }
                            return allowed;
                        }
                    case EntityPermission.Delete:
                        {
                            bool allowed = user.Roles.Any(x => recordPermissions.CanDelete.Any(z => z == x.Id));
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied: UserId={UserId}, Permission=Delete",
                                    user.Id);
                            }
                            return allowed;
                        }
                    default:
                        throw new NotSupportedException("Entity permission type is not supported");
                }
            }
            else
            {
                // Step 3: Unauthenticated (guest) path — source lines 92-106
                // Uses Role.GuestRoleId (987148B1-AFA8-4B33-8616-55861E5FD065)
                switch (permission)
                {
                    case EntityPermission.Read:
                        {
                            bool allowed = recordPermissions.CanRead.Any(z => z == Role.GuestRoleId);
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied for guest: Permission=Read");
                            }
                            return allowed;
                        }
                    case EntityPermission.Create:
                        {
                            bool allowed = recordPermissions.CanCreate.Any(z => z == Role.GuestRoleId);
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied for guest: Permission=Create");
                            }
                            return allowed;
                        }
                    case EntityPermission.Update:
                        {
                            bool allowed = recordPermissions.CanUpdate.Any(z => z == Role.GuestRoleId);
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied for guest: Permission=Update");
                            }
                            return allowed;
                        }
                    case EntityPermission.Delete:
                        {
                            bool allowed = recordPermissions.CanDelete.Any(z => z == Role.GuestRoleId);
                            if (!allowed)
                            {
                                _logger.LogDebug(
                                    "Entity permission denied for guest: Permission=Delete");
                            }
                            return allowed;
                        }
                    default:
                        throw new NotSupportedException("Entity permission type is not supported");
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para><b>Behavioral parity with source</b>
        /// (<c>SecurityContext.HasMetaPermission</c>, lines 109-118):</para>
        /// <list type="number">
        ///   <item><description>If user is <c>null</c>, returns <c>false</c>.</description></item>
        ///   <item><description>Returns <c>true</c> only if the user holds the Administrator
        ///     role (<see cref="Role.AdministratorRoleId"/>:
        ///     <c>BDC56420-CAF0-4030-8A0E-D264938E0CDA</c>).</description></item>
        /// </list>
        /// <para>Only administrators can modify entity metadata (entity/field/relation
        /// definitions).</para>
        /// </remarks>
        public bool HasMetaPermission(User? user)
        {
            // Source SecurityContext.cs lines 114-115
            if (user == null)
            {
                _logger.LogDebug("Meta permission denied: user is null (unauthenticated)");
                return false;
            }

            // Source SecurityContext.cs line 117
            bool hasPermission = user.Roles.Any(x => x.Id == Role.AdministratorRoleId);

            _logger.LogInformation(
                "Meta permission check: UserId={UserId}, HasPermission={HasPermission}",
                user.Id,
                hasPermission);

            return hasPermission;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para><b>Behavioral parity with source</b>
        /// (<c>SecurityContext.IsUserInRole(params Guid[])</c>, lines 54-61):</para>
        /// <para>Returns <c>true</c> if the user holds at least one role whose
        /// <see cref="Role.Id"/> matches any of the supplied role IDs. Returns
        /// <c>false</c> if the user is <c>null</c>, <paramref name="roleIds"/> is
        /// <c>null</c>, or <paramref name="roleIds"/> is empty.</para>
        /// </remarks>
        public bool IsUserInRole(User user, params Guid[] roleIds)
        {
            // Source SecurityContext.cs lines 56-60
            if (user != null && roleIds != null && roleIds.Any())
                return user.Roles.Any(x => roleIds.Any(z => z == x.Id));

            return false;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para><b>Behavioral parity with source</b>
        /// (<c>SecurityContext.IsUserInRole(params ErpRole[])</c>, lines 45-52):</para>
        /// <para>Extracts role IDs from the <paramref name="roles"/> array and
        /// delegates to <see cref="IsUserInRole(User, Guid[])"/>.</para>
        /// </remarks>
        public bool IsUserInRole(User user, params Role[] roles)
        {
            // Source SecurityContext.cs lines 47-51
            if (user != null && roles != null && roles.Any())
                return IsUserInRole(user, roles.Select(x => x.Id).ToArray());

            return false;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>Convenience method replacing inline <c>user.Id == SystemIds.SystemUserId</c>
        /// checks (source SecurityContext.cs line 74). The system user ID is
        /// <c>10000000-0000-0000-0000-000000000000</c>.</para>
        /// </remarks>
        public bool IsSystemUser(User? user)
        {
            return user != null && user.Id == User.SystemUserId;
        }
    }
}
