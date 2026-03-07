using System;
using System.Collections.Generic;

namespace WebVella.Erp.Service.Crm.Domain.Entities;

/// <summary>
/// Static configuration class defining the CRM Industry lookup entity metadata.
/// Preserves all hard-coded GUIDs from the monolith source (NextPlugin.20190203.cs lines 3682–3957
/// and NextPlugin.20190206.cs lines 1108–1136) for data migration compatibility.
///
/// The Industry entity is a lookup entity referenced by the Account entity via its
/// <c>industry_id</c> field. Unlike CaseType, CaseStatus, and Salutation entities,
/// Industry has no seed data records — industry data is populated at runtime.
///
/// Key differences from similar lookup entities (CaseType/CaseStatus):
/// - No seed data records
/// - The <c>is_enabled</c> field is NOT searchable (Searchable=false)
/// - No <c>is_closed</c> field (that is exclusive to CaseStatus)
/// </summary>
public static class IndustryEntity
{
    // ──────────────────────────────────────────────────────────────────────
    //  Entity Metadata Constants
    //  Source: NextPlugin.20190203.cs lines 3682–3717
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unique identifier for the Industry entity.
    /// Source: <c>entity.Id = new Guid("2c60e662-367e-475d-9fcb-3ead55178a56")</c>
    /// </summary>
    public static readonly Guid Id = new("2c60e662-367e-475d-9fcb-3ead55178a56");

    /// <summary>
    /// Internal entity name used in EQL queries and database table naming (rec_industry).
    /// </summary>
    public const string Name = "industry";

    /// <summary>
    /// Display label for the entity in UI contexts.
    /// </summary>
    public const string Label = "Industry";

    /// <summary>
    /// Plural display label for the entity in UI contexts.
    /// </summary>
    public const string LabelPlural = "Industries";

    /// <summary>
    /// Indicates this is a system entity that cannot be deleted by end users.
    /// </summary>
    public const bool IsSystem = true;

    /// <summary>
    /// Font Awesome icon class for UI representation.
    /// Uses the "far" (regular) prefix with the dot-circle icon.
    /// </summary>
    public const string IconName = "far fa-dot-circle";

    /// <summary>
    /// Hex color code for UI representation (Material Design Red 500).
    /// </summary>
    public const string Color = "#f44336";

    /// <summary>
    /// GUID of the system-generated <c>id</c> field for the Industry entity.
    /// Source: <c>systemFieldIdDictionary["id"] = new Guid("c1dd4f43-ec95-4f08-b9b7-26d46d6f5305")</c>
    /// </summary>
    public static readonly Guid SystemFieldId = new("c1dd4f43-ec95-4f08-b9b7-26d46d6f5305");

    /// <summary>
    /// Field used as the record screen identifier. Points to the <c>label</c> field,
    /// so industry records are identified by their label in record-detail views.
    /// Source: <c>entity.RecordScreenIdField = new Guid("cdc0ddda-d38c-46fa-901c-71409c685dd1")</c>
    /// </summary>
    public static readonly Guid RecordScreenIdField = new("cdc0ddda-d38c-46fa-901c-71409c685dd1");

    // ──────────────────────────────────────────────────────────────────────
    //  Field ID Constants (8 fields)
    //  Source: NextPlugin.20190203.cs lines 3720–3957
    //  Update: NextPlugin.20190206.cs lines 1108–1136 (l_scope field)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nested static class containing all field GUIDs for the Industry entity.
    /// Each field preserves its original GUID from the monolith for data migration compatibility.
    /// </summary>
    public static class Fields
    {
        /// <summary>
        /// CheckboxField: Indicates whether this industry is the default selection.
        /// Required=true, System=true, DefaultValue=false.
        /// Source: NextPlugin.20190203.cs line 3723
        /// </summary>
        public static readonly Guid IsDefaultFieldId = new("24a63589-ecc8-4f33-84bf-7d2259357d7e");

        /// <summary>
        /// CheckboxField: Indicates whether this industry option is currently enabled/active.
        /// Required=true, Searchable=false, System=true, DefaultValue=true.
        /// NOTE: Unlike CaseType and CaseStatus entities, this field is NOT searchable.
        /// Source: NextPlugin.20190203.cs line 3752
        /// </summary>
        public static readonly Guid IsEnabledFieldId = new("2b679734-662d-4a3e-9823-1924d52de2d9");

        /// <summary>
        /// CheckboxField: Indicates whether this industry record is a system-managed record.
        /// Required=true, System=true, DefaultValue=false.
        /// Source: NextPlugin.20190203.cs line 3781
        /// </summary>
        public static readonly Guid IsSystemFieldId = new("6924ac15-fbf6-4385-8ef4-7ecf2ee9391a");

        /// <summary>
        /// TextField: Localization scope identifier for multi-tenant scenarios.
        /// Originally created in patch 20190203 (Required=false, Searchable=false, DefaultValue=null).
        /// Updated in patch 20190206 to: Required=true, Searchable=true, System=true, DefaultValue="".
        /// The UPDATED values from patch 20190206 are the authoritative configuration.
        /// Source: NextPlugin.20190206.cs lines 1108–1136
        /// </summary>
        public static readonly Guid LScopeFieldId = new("99691c52-8bf5-4ccf-9efa-23906a5d6811");

        /// <summary>
        /// TextField: Display label for the industry record.
        /// Required=true, Unique=true, System=true, DefaultValue="label".
        /// This field is also the RecordScreenIdField — its GUID matches
        /// <see cref="IndustryEntity.RecordScreenIdField"/>.
        /// Source: NextPlugin.20190203.cs line 3840
        /// </summary>
        public static readonly Guid LabelFieldId = new("cdc0ddda-d38c-46fa-901c-71409c685dd1");

        /// <summary>
        /// NumberField: Sort order index for display ordering of industry options.
        /// Required=true, System=true, DefaultValue=1.0, DecimalPlaces=0.
        /// Source: NextPlugin.20190203.cs line 3870
        /// </summary>
        public static readonly Guid SortIndexFieldId = new("e3e4c409-cc40-4885-b208-df4af05ddfa6");

        /// <summary>
        /// TextField: CSS icon class for per-industry icon customization.
        /// Required=false, System=true, DefaultValue=null.
        /// Source: NextPlugin.20190203.cs line 3902
        /// </summary>
        public static readonly Guid IconClassFieldId = new("12054115-1434-444c-a669-97d8eee32910");

        /// <summary>
        /// TextField: Hex color code for per-industry color customization.
        /// Required=false, System=true, DefaultValue=null.
        /// Source: NextPlugin.20190203.cs line 3932
        /// </summary>
        public static readonly Guid ColorFieldId = new("69e2e024-e4e1-492a-9a39-c7ae24f74bd6");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Record Permissions
    //  Source: NextPlugin.20190203.cs lines 3697–3710
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nested static class defining role-based access control permissions for the Industry entity.
    /// Permission model:
    /// - Create: Administrator role only
    /// - Read:   Both Administrator and Regular User roles
    /// - Update: Administrator role only
    /// - Delete: No roles have delete permission (empty list)
    /// </summary>
    public static class Permissions
    {
        /// <summary>
        /// Administrator role GUID. Has Create, Read, and Update permissions on Industry records.
        /// Source: ERPService.cs system role initialization
        /// </summary>
        public static readonly Guid AdministratorRoleId = new("bdc56420-caf0-4030-8a0e-d264938e0cda");

        /// <summary>
        /// Regular user role GUID. Has Read-only permission on Industry records.
        /// Source: ERPService.cs system role initialization
        /// </summary>
        public static readonly Guid RegularRoleId = new("f16ec6db-626d-4c27-8de0-3e7ce542c55f");

        /// <summary>
        /// Roles that can create new Industry records. Administrator only.
        /// Source: <c>entity.RecordPermissions.CanCreate.Add(new Guid("bdc56420-..."))</c>
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanCreate = new List<Guid>
        {
            AdministratorRoleId
        }.AsReadOnly();

        /// <summary>
        /// Roles that can read Industry records. Both Regular User and Administrator.
        /// Source: <c>entity.RecordPermissions.CanRead.Add(...)</c> (two entries)
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanRead = new List<Guid>
        {
            RegularRoleId,
            AdministratorRoleId
        }.AsReadOnly();

        /// <summary>
        /// Roles that can update Industry records. Administrator only.
        /// Source: <c>entity.RecordPermissions.CanUpdate.Add(new Guid("bdc56420-..."))</c>
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanUpdate = new List<Guid>
        {
            AdministratorRoleId
        }.AsReadOnly();

        /// <summary>
        /// Roles that can delete Industry records. Empty — no role has delete permission.
        /// Source: <c>entity.RecordPermissions.CanDelete = new List&lt;Guid&gt;()</c> (no entries added)
        /// </summary>
        public static readonly IReadOnlyList<Guid> CanDelete = new List<Guid>().AsReadOnly();
    }
}
