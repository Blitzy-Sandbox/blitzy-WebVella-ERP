// =============================================================================
// Definitions.cs — Shared Constants and Enums for Entity Management Service
// =============================================================================
// Consolidates well-known identifiers, system GUIDs, enums, and utility types
// previously split across WebVella.Erp/Api/Definitions.cs and
// WebVella.Erp/Api/Models/SelectOptionAttribute.cs in the monolith.
//
// Namespace Migration:
//   Old: WebVella.Erp.Api + WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json.Serialization [JsonPropertyName("...")]
//        (AOT-safe for .NET 9 Native AOT Lambda deployment)
//
// CRITICAL: SelectOptionAttribute is consumed by FieldType, QuerySortType,
// DataSourceType, and EntityRelationType enums across other model files in this
// bounded context. Changes to this file affect enum metadata decoration
// throughout the Entity Management service.
// =============================================================================

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Well-known system GUIDs used throughout the Entity Management bounded context.
    /// These identifiers are immutable and must match the original monolith values
    /// character-by-character to ensure data migration compatibility.
    /// Migrated from: WebVella.Erp.Api.SystemIds
    /// </summary>
    public static class SystemIds
    {
        /// <summary>
        /// The built-in system entity identifier.
        /// Original: WebVella.Erp/Api/Definitions.cs line 8
        /// </summary>
        public static Guid SystemEntityId { get { return new Guid("a5050ac8-5967-4ce1-95e7-a79b054f9d14"); } }

        /// <summary>
        /// The user entity identifier — represents the users table/entity.
        /// Used by Identity and Entity Management services for user record lookups.
        /// Original: WebVella.Erp/Api/Definitions.cs line 9
        /// </summary>
        public static Guid UserEntityId { get { return new Guid("b9cebc3b-6443-452a-8e34-b311a73dcc8b"); } }

        /// <summary>
        /// The role entity identifier — represents the roles table/entity.
        /// Used by Identity and Entity Management services for role management.
        /// Original: WebVella.Erp/Api/Definitions.cs line 10
        /// </summary>
        public static Guid RoleEntityId { get { return new Guid("c4541fee-fbb6-4661-929e-1724adec285a"); } }

        /// <summary>
        /// The area entity identifier — represents application areas/sections.
        /// Original: WebVella.Erp/Api/Definitions.cs line 11
        /// Note: In source this was a field, normalized to property getter for consistency.
        /// </summary>
        public static Guid AreaEntityId { get { return new Guid("cb434298-8583-4a96-bdbb-97b2c1764192"); } }

        /// <summary>
        /// The user-to-role many-to-many relation identifier.
        /// Establishes the link between user entities and role entities.
        /// Original: WebVella.Erp/Api/Definitions.cs line 13
        /// </summary>
        public static Guid UserRoleRelationId { get { return new Guid("0C4B119E-1D7B-4B40-8D2C-9E447CC656AB"); } }

        /// <summary>
        /// The built-in Administrator role identifier.
        /// Users assigned this role have full system access.
        /// Original: WebVella.Erp/Api/Definitions.cs line 15
        /// </summary>
        public static Guid AdministratorRoleId { get { return new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA"); } }

        /// <summary>
        /// The built-in Regular (standard) role identifier.
        /// Default role for authenticated users with standard permissions.
        /// Original: WebVella.Erp/Api/Definitions.cs line 16
        /// </summary>
        public static Guid RegularRoleId { get { return new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F"); } }

        /// <summary>
        /// The built-in Guest role identifier.
        /// Assigned to unauthenticated or limited-access users.
        /// Original: WebVella.Erp/Api/Definitions.cs line 17
        /// </summary>
        public static Guid GuestRoleId { get { return new Guid("987148B1-AFA8-4B33-8616-55861E5FD065"); } }

        /// <summary>
        /// The system user identifier — used for automated operations.
        /// This user has implicit full permissions and is used for internal
        /// service-to-service operations and background processing.
        /// Original: WebVella.Erp/Api/Definitions.cs line 19
        /// </summary>
        public static Guid SystemUserId { get { return new Guid("10000000-0000-0000-0000-000000000000"); } }

        /// <summary>
        /// The first human user identifier — typically the bootstrap admin user.
        /// Created during initial system setup (erp@webvella.com / erp).
        /// Original: WebVella.Erp/Api/Definitions.cs line 20
        /// </summary>
        public static Guid FirstUserId { get { return new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2"); } }
    }

    /// <summary>
    /// Defines the type of record list rendering mode.
    /// Used by UI components and API responses to determine list behavior.
    /// Migrated from: WebVella.Erp.Api.RecordsListTypes
    /// </summary>
    public enum RecordsListTypes
    {
        /// <summary>A popup search/selection list used in relation field pickers.</summary>
        SearchPopup = 1,

        /// <summary>A standard data-grid list view with sorting, filtering, and pagination.</summary>
        List,

        /// <summary>A custom list rendering mode defined by page builder configuration.</summary>
        Custom
    }

    /// <summary>
    /// Defines the set of filter operators available for query construction.
    /// Used by the QueryAdapter service to translate filter expressions into
    /// DynamoDB query/scan filter conditions (replacing monolith EQL WHERE clauses).
    /// Migrated from: WebVella.Erp.Api.FilterOperatorTypes
    /// </summary>
    public enum FilterOperatorTypes
    {
        /// <summary>Exact equality comparison (==).</summary>
        Equals = 1,

        /// <summary>Inequality comparison (!=).</summary>
        NotEqualTo,

        /// <summary>String prefix match (LIKE 'value%' equivalent).</summary>
        StartsWith,

        /// <summary>Substring containment match (LIKE '%value%' equivalent).</summary>
        Contains,

        /// <summary>Negated substring containment (NOT LIKE '%value%' equivalent).</summary>
        DoesNotContain,

        /// <summary>Less-than numeric or date comparison (&lt;).</summary>
        LessThan,

        /// <summary>Greater-than numeric or date comparison (&gt;).</summary>
        GreaterThan,

        /// <summary>Less-than-or-equal comparison (&lt;=).</summary>
        LessOrEqual,

        /// <summary>Greater-than-or-equal comparison (&gt;=).</summary>
        GreaterOrEqual,

        /// <summary>Set membership check — value is in a specified set.</summary>
        Includes,

        /// <summary>Set exclusion check — value is not in a specified set.</summary>
        Excludes,

        /// <summary>Geographic or range containment check.</summary>
        Within
    }

    /// <summary>
    /// Defines the column layout for record detail/edit views.
    /// Migrated from: WebVella.Erp.Api.RecordViewLayouts
    /// </summary>
    public enum RecordViewLayouts
    {
        /// <summary>Single-column layout — all fields in one column.</summary>
        OneColumn = 1,

        /// <summary>Two-column layout — fields distributed across left and right columns.</summary>
        TwoColumns
    }

    /// <summary>
    /// Identifies which column a field occupies in a two-column record view layout.
    /// Migrated from: WebVella.Erp.Api.RecordViewColumns
    /// </summary>
    public enum RecordViewColumns
    {
        /// <summary>The left column of a two-column layout.</summary>
        Left = 1,

        /// <summary>The right column of a two-column layout.</summary>
        Right
    }

    /// <summary>
    /// Controls the placement of a currency symbol relative to the numeric value.
    /// Migrated from: WebVella.Erp.Api.CurrencySymbolPlacement
    /// </summary>
    public enum CurrencySymbolPlacement
    {
        /// <summary>Symbol appears before the value (e.g., "$100").</summary>
        Before = 1,

        /// <summary>Symbol appears after the value (e.g., "100€").</summary>
        After
    }

    /// <summary>
    /// Represents a currency definition with symbol, naming, and formatting metadata.
    /// Used by CurrencyField type for locale-aware monetary value display.
    /// JSON property names are preserved for backward API compatibility.
    /// Migrated from: WebVella.Erp.Api.CurrencyType
    ///
    /// Serialization Migration:
    ///   Old: [JsonProperty(PropertyName = "...")] (Newtonsoft.Json)
    ///   New: [JsonPropertyName("...")] (System.Text.Json.Serialization)
    /// </summary>
    [Serializable]
    public class CurrencyType
    {
        /// <summary>
        /// The international currency symbol (e.g., "$", "€", "£").
        /// </summary>
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The native/local currency symbol (e.g., "$", "€", "лв.").
        /// May differ from Symbol for currencies with distinct local representations.
        /// </summary>
        [JsonPropertyName("symbolNative")]
        public string SymbolNative { get; set; } = string.Empty;

        /// <summary>
        /// The singular English name of the currency (e.g., "US Dollar", "Euro").
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The plural English name of the currency (e.g., "US dollars", "euros").
        /// </summary>
        [JsonPropertyName("namePlural")]
        public string NamePlural { get; set; } = string.Empty;

        /// <summary>
        /// The ISO 4217 three-letter currency code (e.g., "USD", "EUR", "GBP").
        /// </summary>
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// The number of decimal digits used for this currency (typically 0 or 2).
        /// </summary>
        [JsonPropertyName("decimalDigits")]
        public int DecimalDigits { get; set; }

        /// <summary>
        /// The rounding increment for this currency (0 for standard rounding).
        /// </summary>
        [JsonPropertyName("rounding")]
        public int Rounding { get; set; }

        /// <summary>
        /// Controls whether the currency symbol is placed before or after the value.
        /// Defaults to <see cref="CurrencySymbolPlacement.After"/>.
        /// </summary>
        [JsonPropertyName("symbolPlacement")]
        public CurrencySymbolPlacement SymbolPlacement { get; set; } = CurrencySymbolPlacement.After;
    }

    /// <summary>
    /// Defines the possible return types for a formula/calculated field.
    /// The formula engine evaluates expressions and casts results to the specified type.
    /// Migrated from: WebVella.Erp.Api.FormulaFieldReturnType
    /// </summary>
    public enum FormulaFieldReturnType
    {
        /// <summary>Boolean result — true/false checkbox display.</summary>
        Checkbox = 1,

        /// <summary>Monetary result — displayed with currency formatting.</summary>
        Currency,

        /// <summary>Date-only result — no time component.</summary>
        Date,

        /// <summary>Date and time result — full timestamp display.</summary>
        DateTime,

        /// <summary>Numeric result — decimal/integer display.</summary>
        Number,

        /// <summary>Percentage result — displayed with % suffix.</summary>
        Percent,

        /// <summary>Free-text string result.</summary>
        Text
    }

    /// <summary>
    /// Defines the permission types that can be granted or denied on an entity
    /// for a specific role. Used by the permission system to enforce access control
    /// at the entity level.
    /// Migrated from: WebVella.Erp.Api.EntityPermission
    /// </summary>
    public enum EntityPermission
    {
        /// <summary>Permission to read/query records of the entity.</summary>
        Read,

        /// <summary>Permission to create new records in the entity.</summary>
        Create,

        /// <summary>Permission to update existing records of the entity.</summary>
        Update,

        /// <summary>Permission to delete records from the entity.</summary>
        Delete
    }

    /// <summary>
    /// Custom attribute for decorating enum members with display metadata.
    /// Provides label text, icon CSS class, and color information for UI rendering.
    /// Used by FieldType, QuerySortType, DataSourceType, and EntityRelationType
    /// enums to carry presentation metadata alongside enum values.
    /// Migrated from: WebVella.Erp.Api.Models.SelectOptionAttribute
    ///
    /// Example usage:
    /// <code>
    /// public enum FieldType
    /// {
    ///     [SelectOption(Label = "Auto Number", IconClass = "fa fa-sort-numeric-asc", Color = "")]
    ///     AutoNumberField = 1,
    ///     ...
    /// }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    public class SelectOptionAttribute : Attribute
    {
        private string _label = string.Empty;
        private string _iconClass = string.Empty;
        private string _color = string.Empty;

        /// <summary>
        /// Gets or sets the human-readable display label for the enum member.
        /// Rendered as the visible text in select/dropdown UI components.
        /// </summary>
        public virtual string Label
        {
            get { return _label; }
            set { _label = value; }
        }

        /// <summary>
        /// Gets or sets the CSS icon class for the enum member.
        /// Typically a Font Awesome class (e.g., "fa fa-font") rendered alongside the label.
        /// </summary>
        public virtual string IconClass
        {
            get { return _iconClass; }
            set { _iconClass = value; }
        }

        /// <summary>
        /// Gets or sets the color code for the enum member.
        /// Used for visual differentiation in UI components (e.g., status badges).
        /// </summary>
        public virtual string Color
        {
            get { return _color; }
            set { _color = value; }
        }
    }
}
