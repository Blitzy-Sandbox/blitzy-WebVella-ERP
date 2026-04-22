using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Identity.Models
{
    /// <summary>
    /// AOT-compatible source-generated JSON serializer context used by
    /// <see cref="UserPreferences.Compare"/> for deep equality comparison via
    /// JSON serialization without reflection. Eliminates IL2026/IL3050 trimming
    /// warnings when <c>PublishAot=true</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = false)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(List<UserComponentUsage>))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
    internal partial class UserPreferencesSerializerContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }

    /// <summary>
    /// Tracks per-component SDK usage metadata within user preferences.
    /// Replaces the monolith's <c>WebVella.Erp.Api.Models.UserComponentUsage</c>
    /// (<c>UserComponentUsage.cs</c> lines 8-18).
    ///
    /// <para>
    /// Used within <see cref="UserPreferences.ComponentUsage"/> to record which SDK
    /// version each page-builder component was last configured with, supporting the
    /// component migration and versioning pipeline.
    /// </para>
    ///
    /// <para>
    /// JSON property names are preserved exactly from the source class for backward
    /// compatibility. A dual-attribute serialization pattern is applied:
    /// <c>System.Text.Json.Serialization.JsonPropertyName</c> (primary, AOT-compatible)
    /// and <c>Newtonsoft.Json.JsonProperty</c> (backward compatibility).
    /// </para>
    /// </summary>
    public class UserComponentUsage
    {
        /// <summary>
        /// Full component name identifier (e.g., "WebVella.Erp.Web.Components.PcFieldText").
        /// Source: <c>UserComponentUsage.Name</c> (line 11).
        /// Default: empty string.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// SDK version number when the component was last used or configured.
        /// Source: <c>UserComponentUsage.SdkUsed</c> (line 14).
        /// Default: 0 (never configured).
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("sdk_used")]
        [JsonProperty(PropertyName = "sdk_used")]
        public int SdkUsed { get; set; } = 0;

        /// <summary>
        /// Timestamp when the SDK was last used for this component.
        /// Source: <c>UserComponentUsage.SdkUsedOn</c> (line 17).
        /// Default: <see cref="DateTime.MinValue"/> (never used).
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("sdk_used_on")]
        [JsonProperty(PropertyName = "sdk_used_on")]
        public DateTime SdkUsedOn { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// User preferences model containing sidebar configuration, component usage
    /// tracking, and per-component data dictionaries.
    ///
    /// <para>
    /// Replaces the monolith's <c>WebVella.Erp.Api.Models.ErpUserPreferences</c>
    /// (<c>ErpUserPreferences.cs</c> lines 7-35). Key changes from the source:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>EntityRecord ComponentDataDictionary</c> simplified to
    ///     <c>Dictionary&lt;string, object&gt;</c> — <c>EntityRecord</c> was essentially
    ///     a dynamic dictionary; the explicit type removes the dependency on the core
    ///     engine while preserving the same JSON wire format.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Compare"/> method uses <c>System.Text.Json.JsonSerializer</c>
    ///     instead of <c>Newtonsoft.Json.JsonConvert</c> for Native AOT compatibility.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// JSON property names are preserved exactly from the source class for backward
    /// compatibility with existing API consumers.
    /// </para>
    /// </summary>
    public class UserPreferences
    {
        /// <summary>
        /// Sidebar display size preference (e.g., "sm", "lg", or empty for default).
        /// Source: <c>ErpUserPreferences.SidebarSize</c> (line 11).
        /// Default: empty string.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("sidebar_size")]
        [JsonProperty(PropertyName = "sidebar_size")]
        public string SidebarSize { get; set; } = string.Empty;

        /// <summary>
        /// Tracks SDK usage per page-builder component for versioning and migration.
        /// Source: <c>ErpUserPreferences.ComponentUsage</c> (line 14).
        /// Default: empty list.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("component_usage")]
        [JsonProperty(PropertyName = "component_usage")]
        public List<UserComponentUsage> ComponentUsage { get; set; } = new List<UserComponentUsage>();

        /// <summary>
        /// Per-component arbitrary data dictionary keyed by full component name.
        /// Source: <c>ErpUserPreferences.ComponentDataDictionary</c> (line 17).
        /// Simplified from <c>EntityRecord</c> (which was essentially a dynamic dictionary
        /// inheriting from <c>Dictionary&lt;string, object&gt;</c>) to a plain dictionary,
        /// removing the core engine dependency while preserving the same JSON wire format.
        /// Default: empty dictionary.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("component_data_dictionary")]
        [JsonProperty(PropertyName = "component_data_dictionary")]
        public Dictionary<string, object> ComponentDataDictionary { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Performs a deep equality comparison between this instance and another
        /// <see cref="UserPreferences"/> by serializing both to JSON and comparing
        /// the resulting strings.
        ///
        /// <para>
        /// Source: <c>ErpUserPreferences.Compare()</c> (lines 19-34).
        /// Migrated from <c>Newtonsoft.Json.JsonConvert.SerializeObject()</c> to
        /// <c>System.Text.Json.JsonSerializer.Serialize()</c> for Native AOT compatibility.
        /// </para>
        /// </summary>
        /// <param name="prefs">The other preferences instance to compare against.</param>
        /// <returns>
        /// <c>true</c> if all properties are deeply equal;
        /// <c>false</c> if <paramref name="prefs"/> is <c>null</c> or any property differs.
        /// </returns>
        public bool Compare(UserPreferences? prefs)
        {
            if (prefs == null)
                return false;

            if (SidebarSize != prefs.SidebarSize)
                return false;

            var ctx = UserPreferencesSerializerContext.Default;

            if (System.Text.Json.JsonSerializer.Serialize(ComponentUsage, ctx.ListUserComponentUsage)
                != System.Text.Json.JsonSerializer.Serialize(prefs.ComponentUsage, ctx.ListUserComponentUsage))
                return false;

            if (System.Text.Json.JsonSerializer.Serialize(ComponentDataDictionary, ctx.DictionaryStringObject)
                != System.Text.Json.JsonSerializer.Serialize(prefs.ComponentDataDictionary, ctx.DictionaryStringObject))
                return false;

            return true;
        }
    }

    /// <summary>
    /// Cognito-aware user domain model for the Identity &amp; Access Management service.
    ///
    /// <para>
    /// Replaces the monolith's <c>WebVella.Erp.Api.Models.ErpUser</c>
    /// (<c>ErpUser.cs</c> lines 8-68) with a model that maps AWS Cognito user pool
    /// attributes to domain properties while preserving backward compatibility with
    /// existing API consumers.
    /// </para>
    ///
    /// <para><b>Key changes from source <c>ErpUser</c>:</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     Renamed from <c>ErpUser</c> to <c>User</c> following bounded-context naming conventions.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Enabled"/> and <see cref="Verified"/> now included in JSON serialization
    ///     (were <c>[JsonIgnore]</c> in source) — the Identity service owns user status.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Roles"/> now included in JSON serialization (was <c>[JsonIgnore]</c> with
    ///     private setter in source) — the Identity service is the owning service for role assignments.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="IsAdmin"/> computed property references <see cref="Role.AdministratorRoleId"/>
    ///     instead of <c>SystemIds.AdministratorRoleId</c>.
    ///   </description></item>
    ///   <item><description>
    ///     Added <see cref="CognitoSub"/> and <see cref="EmailVerified"/> for AWS Cognito attribute mapping.
    ///   </description></item>
    ///   <item><description>
    ///     Added <see cref="SystemUserId"/> and <see cref="FirstUserId"/> constants
    ///     (extracted from <c>Definitions.cs</c> lines 19-20).
    ///   </description></item>
    ///   <item><description>
    ///     Uses dual JSON serialization attribute pattern for AOT + backward compatibility.
    ///   </description></item>
    ///   <item><description>
    ///     Preferences type changed from <c>ErpUserPreferences</c> to <see cref="UserPreferences"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>[Serializable]</c> attribute removed — not needed for System.Text.Json or Lambda serialization.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// All JSON property names are preserved exactly from the original <c>ErpUser</c>
    /// for backward-compatible API responses. The <see cref="Password"/> property is
    /// excluded from all JSON serialization for security — it is retained only for the
    /// MD5 → Cognito user migration flow.
    /// </para>
    /// </summary>
    public class User
    {
        #region Well-Known System User IDs

        /// <summary>
        /// System user ID — used for automated and background operations where no
        /// human user context is available. This user is assigned the Administrator role
        /// during bootstrap.
        /// Source: <c>Definitions.cs</c> line 19 — <c>SystemIds.SystemUserId</c>.
        /// GUID value must remain exact for backward compatibility with existing data.
        /// </summary>
        public static readonly Guid SystemUserId = new Guid("10000000-0000-0000-0000-000000000000");

        /// <summary>
        /// First (default) user ID — the initial administrative user seeded during
        /// system setup. Corresponds to the default <c>erp@webvella.com</c> account.
        /// Source: <c>Definitions.cs</c> line 20 — <c>SystemIds.FirstUserId</c>.
        /// GUID value must remain exact for backward compatibility with existing data.
        /// </summary>
        public static readonly Guid FirstUserId = new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2");

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new <see cref="User"/> instance with default values matching
        /// the source <c>ErpUser</c> constructor (<c>ErpUser.cs</c> lines 11-21):
        /// <c>Id = Guid.Empty</c>, strings to <c>string.Empty</c>,
        /// <c>Enabled = true</c>, <c>Verified = true</c>, <c>Roles = new List&lt;Role&gt;()</c>.
        /// </summary>
        public User()
        {
            Id = Guid.Empty;
            Username = string.Empty;
            Email = string.Empty;
            Password = string.Empty;
            FirstName = string.Empty;
            LastName = string.Empty;
            Enabled = true;
            Verified = true;
            Roles = new List<Role>();
        }

        #endregion

        #region Core Properties (from ErpUser)

        /// <summary>
        /// Unique identifier for the user record.
        /// Source: <c>ErpUser.Id</c> (lines 23-24).
        /// JSON property name <c>"id"</c> preserved for backward compatibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Username for display and authentication lookup.
        /// Source: <c>ErpUser.Username</c> (lines 26-27).
        /// JSON property name <c>"username"</c> preserved for backward compatibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("username")]
        [JsonProperty(PropertyName = "username")]
        public string Username { get; set; }

        /// <summary>
        /// Email address — also used as the Cognito <c>username</c> attribute.
        /// Source: <c>ErpUser.Email</c> (lines 29-30).
        /// JSON property name <c>"email"</c> preserved for backward compatibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("email")]
        [JsonProperty(PropertyName = "email")]
        public string Email { get; set; }

        /// <summary>
        /// Password hash — excluded from ALL JSON serialization for security.
        /// Retained as an internal property solely for the user migration flow
        /// (MD5 → Cognito migration trigger). After migration, Cognito manages
        /// credentials directly and this property is no longer used.
        /// Source: <c>ErpUser.Password</c> (lines 32-34).
        /// Both <c>System.Text.Json</c> and <c>Newtonsoft.Json</c> ignore this property.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [Newtonsoft.Json.JsonIgnore]
        public string Password { get; set; }

        /// <summary>
        /// User's first (given) name.
        /// Source: <c>ErpUser.FirstName</c> (lines 36-37).
        /// JSON property name <c>"firstName"</c> preserved for backward compatibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("firstName")]
        [JsonProperty(PropertyName = "firstName")]
        public string FirstName { get; set; }

        /// <summary>
        /// User's last (family) name.
        /// Source: <c>ErpUser.LastName</c> (lines 39-40).
        /// JSON property name <c>"lastName"</c> preserved for backward compatibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("lastName")]
        [JsonProperty(PropertyName = "lastName")]
        public string LastName { get; set; }

        /// <summary>
        /// URL or path to the user's profile image/avatar.
        /// Source: <c>ErpUser.Image</c> (lines 42-43).
        /// JSON property name <c>"image"</c> preserved for backward compatibility.
        /// Nullable — users may not have a profile image configured.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("image")]
        [JsonProperty(PropertyName = "image")]
        public string? Image { get; set; }

        /// <summary>
        /// Whether the user account is enabled/active.
        /// Source: <c>ErpUser.Enabled</c> (lines 45-47).
        /// <para>
        /// <b>Change from source:</b> Was <c>[JsonIgnore]</c> in the original — now included
        /// in JSON serialization because the Identity service owns user status and must
        /// expose it to API consumers and other services.
        /// </para>
        /// Default: <c>true</c>.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("enabled")]
        [JsonProperty(PropertyName = "enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Whether the user's email/identity has been verified.
        /// Source: <c>ErpUser.Verified</c> (lines 49-51).
        /// <para>
        /// <b>Change from source:</b> Was <c>[JsonIgnore]</c> in the original — now included
        /// in JSON serialization because the Identity service owns verification status.
        /// </para>
        /// Default: <c>true</c>.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("verified")]
        [JsonProperty(PropertyName = "verified")]
        public bool Verified { get; set; }

        /// <summary>
        /// Timestamp when the user record was created.
        /// Source: <c>ErpUser.CreatedOn</c> (lines 53-54).
        /// JSON property name <c>"createdOn"</c> preserved for backward compatibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("createdOn")]
        [JsonProperty(PropertyName = "createdOn")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Timestamp of the user's most recent successful login.
        /// Source: <c>ErpUser.LastLoggedIn</c> (lines 56-57).
        /// JSON property name <c>"lastLoggedIn"</c> preserved for backward compatibility.
        /// Nullable — <c>null</c> if the user has never logged in.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("lastLoggedIn")]
        [JsonProperty(PropertyName = "lastLoggedIn")]
        public DateTime? LastLoggedIn { get; set; }

        /// <summary>
        /// Collection of roles assigned to this user.
        /// Source: <c>ErpUser.Roles</c> (lines 59-61).
        /// <para>
        /// <b>Change from source:</b> Was <c>[JsonIgnore]</c> with a private setter — now
        /// included in JSON serialization with a public setter because the Identity service
        /// is the owning service for role assignments and must expose them.
        /// </para>
        /// Default: empty list (initialized in constructor).
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("roles")]
        [JsonProperty(PropertyName = "roles")]
        public List<Role> Roles { get; set; }

        /// <summary>
        /// Computed property indicating whether the user holds the Administrator role.
        /// Source: <c>ErpUser.IsAdmin</c> (line 64).
        /// <para>
        /// Adapted from <c>Roles.Any(x =&gt; x.Id == SystemIds.AdministratorRoleId)</c>
        /// to reference <see cref="Role.AdministratorRoleId"/> instead, keeping the
        /// Administrator role GUID within the <see cref="Role"/> class as the single
        /// source of truth.
        /// </para>
        /// Read-only (no setter) — always derived from current <see cref="Roles"/> collection.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("is_admin")]
        [JsonProperty(PropertyName = "is_admin")]
        public bool IsAdmin => Roles.Any(x => x.Id == Role.AdministratorRoleId);

        #endregion

        #region Cognito Mapping Properties (New)

        /// <summary>
        /// AWS Cognito user pool <c>sub</c> attribute — the unique Cognito user identifier.
        /// This is a new property not present in the source <c>ErpUser</c>.
        /// <para>
        /// Populated after user migration to Cognito (via the User Migration Lambda Trigger)
        /// or when a new user is created directly in Cognito. Used to correlate the
        /// domain user record with the Cognito identity.
        /// </para>
        /// Nullable — <c>null</c> for users not yet migrated to Cognito.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("cognito_sub")]
        [JsonProperty(PropertyName = "cognito_sub")]
        public string? CognitoSub { get; set; }

        /// <summary>
        /// Maps to the Cognito <c>email_verified</c> attribute, indicating whether
        /// the user's email address has been verified through Cognito's email verification flow.
        /// This is a new property not present in the source <c>ErpUser</c>.
        /// <para>
        /// Distinct from <see cref="Verified"/> which is the legacy application-level
        /// verification flag. This property tracks Cognito-specific email verification.
        /// </para>
        /// Default: <c>false</c> — requires explicit verification via Cognito flow.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("email_verified")]
        [JsonProperty(PropertyName = "email_verified")]
        public bool EmailVerified { get; set; }

        #endregion

        #region User Preferences

        /// <summary>
        /// User preferences including sidebar configuration, component usage tracking,
        /// and per-component data dictionaries.
        /// Source: <c>ErpUser.Preferences</c> (lines 66-67).
        /// <para>
        /// Type changed from <c>ErpUserPreferences</c> to <see cref="UserPreferences"/>
        /// following the bounded-context naming convention.
        /// </para>
        /// Nullable — preferences are optional and may not be set for all users.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("preferences")]
        [JsonProperty(PropertyName = "preferences")]
        public UserPreferences? Preferences { get; set; }

        #endregion
    }
}
