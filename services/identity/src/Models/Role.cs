using System;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Identity.Models
{
    /// <summary>
    /// Represents an entity-level permission type used for role-based access control.
    /// Extracted from the monolith's <c>WebVella.Erp.Api.EntityPermission</c> enum
    /// (<c>Definitions.cs</c> lines 103-109). Used by <c>PermissionService</c> to
    /// evaluate whether a user's roles grant a specific operation on an entity.
    /// Values are implicitly numbered 0-3 matching the original source exactly.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter<EntityPermission>))]
    public enum EntityPermission
    {
        /// <summary>Permission to read/query records of an entity.</summary>
        Read = 0,

        /// <summary>Permission to create new records of an entity.</summary>
        Create = 1,

        /// <summary>Permission to update existing records of an entity.</summary>
        Update = 2,

        /// <summary>Permission to delete records of an entity.</summary>
        Delete = 3
    }

    /// <summary>
    /// Represents a role in the Identity &amp; Access Management bounded context.
    /// Replaces the monolith's <c>WebVella.Erp.Api.Models.ErpRole</c> class and
    /// extends it with AWS Cognito group mapping for the serverless architecture.
    ///
    /// <para><b>Source mapping:</b> <c>WebVella.Erp/Api/Models/ErpRole.cs</c> (lines 6-17)</para>
    /// <para><b>System IDs source:</b> <c>WebVella.Erp/Api/Definitions.cs</c> (lines 8-17)</para>
    ///
    /// <para>
    /// JSON property names are preserved exactly from the source <c>ErpRole</c> class
    /// (<c>"id"</c>, <c>"name"</c>, <c>"description"</c>) for backward compatibility with
    /// existing API consumers. A dual-attribute serialization pattern is applied:
    /// <c>System.Text.Json.Serialization.JsonPropertyName</c> (primary, AOT-compatible) and
    /// <c>Newtonsoft.Json.JsonProperty</c> (backward compatibility).
    /// </para>
    ///
    /// <para>
    /// Well-known system role IDs and identity-related entity/relation IDs are defined as
    /// <c>static readonly</c> constants, preserving the exact GUID values from the monolith's
    /// <c>SystemIds</c> class to ensure backward compatibility during data migration.
    /// </para>
    /// </summary>
    public class Role
    {
        #region Well-Known System Role IDs

        /// <summary>
        /// Administrator role ID — grants full system access.
        /// Source: <c>Definitions.cs</c> line 15 — <c>SystemIds.AdministratorRoleId</c>.
        /// Maps to Cognito group <c>"administrator"</c>.
        /// </summary>
        public static readonly Guid AdministratorRoleId = new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA");

        /// <summary>
        /// Regular (authenticated) user role ID — grants standard permissions.
        /// Source: <c>Definitions.cs</c> line 16 — <c>SystemIds.RegularRoleId</c>.
        /// Maps to Cognito group <c>"regular"</c>.
        /// </summary>
        public static readonly Guid RegularRoleId = new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F");

        /// <summary>
        /// Guest (unauthenticated) role ID — grants minimal read-only permissions.
        /// Source: <c>Definitions.cs</c> line 17 — <c>SystemIds.GuestRoleId</c>.
        /// Maps to Cognito group <c>"guest"</c>.
        /// </summary>
        public static readonly Guid GuestRoleId = new Guid("987148B1-AFA8-4B33-8616-55861E5FD065");

        #endregion

        #region Identity-Related Entity and Relation IDs

        /// <summary>
        /// Entity ID for the User entity in the metadata system.
        /// Source: <c>Definitions.cs</c> line 9 — <c>SystemIds.UserEntityId</c>.
        /// Used for entity-level permission checks and user-entity references.
        /// </summary>
        public static readonly Guid UserEntityId = new Guid("b9cebc3b-6443-452a-8e34-b311a73dcc8b");

        /// <summary>
        /// Entity ID for the Role entity in the metadata system.
        /// Source: <c>Definitions.cs</c> line 10 — <c>SystemIds.RoleEntityId</c>.
        /// Used for entity-level permission checks and role-entity references.
        /// </summary>
        public static readonly Guid RoleEntityId = new Guid("c4541fee-fbb6-4661-929e-1724adec285a");

        /// <summary>
        /// Relation ID for the many-to-many User ↔ Role relationship.
        /// Source: <c>Definitions.cs</c> line 13 — <c>SystemIds.UserRoleRelationId</c>.
        /// Used to associate users with their assigned roles.
        /// </summary>
        public static readonly Guid UserRoleRelationId = new Guid("0C4B119E-1D7B-4B40-8D2C-9E447CC656AB");

        #endregion

        #region Instance Properties

        /// <summary>
        /// Unique identifier for this role.
        /// Source: <c>ErpRole.Id</c> (<c>ErpRole.cs</c> line 9-10).
        /// JSON property name <c>"id"</c> preserved for backward compatibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Display name of the role (e.g., "administrator", "regular", "guest").
        /// Source: <c>ErpRole.Name</c> (<c>ErpRole.cs</c> lines 12-13).
        /// JSON property name <c>"name"</c> preserved for backward compatibility.
        /// Initialized to <see cref="string.Empty"/> to avoid null reference issues.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable description of the role's purpose and scope.
        /// Source: <c>ErpRole.Description</c> (<c>ErpRole.cs</c> lines 15-16).
        /// JSON property name <c>"description"</c> preserved for backward compatibility.
        /// Initialized to <see cref="string.Empty"/> to avoid null reference issues.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("description")]
        [JsonProperty(PropertyName = "description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The AWS Cognito user pool group name that this role maps to.
        /// This is a new property (not present in the source <c>ErpRole</c>) that bridges
        /// the legacy role system with Cognito group-based authorization.
        ///
        /// <para>Well-known mappings:</para>
        /// <list type="bullet">
        ///   <item><description><see cref="AdministratorRoleId"/> → <c>"administrator"</c></description></item>
        ///   <item><description><see cref="RegularRoleId"/> → <c>"regular"</c></description></item>
        ///   <item><description><see cref="GuestRoleId"/> → <c>"guest"</c></description></item>
        /// </list>
        ///
        /// <para>
        /// Nullable because custom roles created after migration may not yet have
        /// a corresponding Cognito group assigned.
        /// </para>
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("cognito_group_name")]
        [JsonProperty(PropertyName = "cognito_group_name")]
        public string? CognitoGroupName { get; set; }

        #endregion
    }
}
