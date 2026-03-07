using System;
using System.Collections.Generic;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Static configuration and constant class defining the CRM Salutation lookup entity.
    /// Contains the entity GUID, field IDs, field configurations, permissions, seed data
    /// records, and relation metadata.
    ///
    /// This is the CORRECTED "salutation" entity (Id = 690dc799-e732-4d17-80d8-0f761bc33def)
    /// that replaces the misspelled "solutation" entity (Id = f0b64034-e0f6-452e-b82b-88186af6df88)
    /// which was deleted in NextPlugin patch 20190206 (lines 1201–1209).
    ///
    /// All GUIDs are preserved exactly from the monolith source code
    /// (WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs, lines 613–1390)
    /// to ensure data migration compatibility during the monolith-to-microservices decomposition.
    ///
    /// Source: NextPlugin.20190206.cs
    ///   - Entity creation: lines 613–649
    ///   - Field definitions: lines 651–828
    ///   - Seed data records: lines 1212–1300
    ///   - Relation definitions: lines 1334–1390
    /// </summary>
    public static class SalutationEntity
    {
        // ---------------------------------------------------------------------------
        // Entity Metadata
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique identifier for the Salutation entity.
        /// Source: NextPlugin.20190206.cs line 620 —
        ///   entity.Id = new Guid("690dc799-e732-4d17-80d8-0f761bc33def")
        /// </summary>
        public static readonly Guid Id = new Guid("690dc799-e732-4d17-80d8-0f761bc33def");

        /// <summary>
        /// Entity name used in EQL queries, record manager calls, and database table naming.
        /// Source: NextPlugin.20190206.cs line 621 — entity.Name = "salutation"
        /// </summary>
        public const string Name = "salutation";

        /// <summary>
        /// Display label for the entity in singular form.
        /// Source: NextPlugin.20190206.cs line 622 — entity.Label = "Salutation"
        /// </summary>
        public const string Label = "Salutation";

        /// <summary>
        /// Display label for the entity in plural form.
        /// Source: NextPlugin.20190206.cs line 623 — entity.LabelPlural = "Salutations"
        /// </summary>
        public const string LabelPlural = "Salutations";

        /// <summary>
        /// Indicates this is a system-managed entity that cannot be deleted by users.
        /// Source: NextPlugin.20190206.cs line 624 — entity.System = true
        /// </summary>
        public const bool IsSystem = true;

        /// <summary>
        /// Font Awesome icon class for the entity.
        /// Note: Uses the "far" (regular) prefix, not "fa" or "fas".
        /// Source: NextPlugin.20190206.cs line 625 — entity.IconName = "far fa-dot-circle"
        /// </summary>
        public const string IconName = "far fa-dot-circle";

        /// <summary>
        /// Display color for the entity in CSS hex format.
        /// Source: NextPlugin.20190206.cs line 626 — entity.Color = "#f44336"
        /// </summary>
        public const string Color = "#f44336";

        /// <summary>
        /// System-generated field ID for the entity's primary key ("id" field).
        /// Source: NextPlugin.20190206.cs line 619 —
        ///   systemFieldIdDictionary["id"] = new Guid("8721d461-ded9-46e7-8b1e-b7d0703a8d21")
        /// </summary>
        public static readonly Guid SystemFieldId = new Guid("8721d461-ded9-46e7-8b1e-b7d0703a8d21");

        // ---------------------------------------------------------------------------
        // Field ID Constants (6 fields)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Field identifiers for all custom fields on the Salutation entity.
        /// Each field ID is preserved exactly from the monolith source for migration compatibility.
        /// </summary>
        public static class Fields
        {
            /// <summary>
            /// Field: is_default (CheckboxField)
            /// Indicates whether this salutation is the default selection.
            /// Required=true, System=true, DefaultValue=false
            /// Source: NextPlugin.20190206.cs line 654
            /// </summary>
            public static readonly Guid IsDefaultFieldId = new Guid("17f9eb90-f712-472a-9b33-a5cdcfd15c68");

            /// <summary>
            /// Field: is_enabled (CheckboxField)
            /// Controls whether this salutation is available for selection.
            /// Required=true, System=true, DefaultValue=true
            /// Source: NextPlugin.20190206.cs line 683
            /// </summary>
            public static readonly Guid IsEnabledFieldId = new Guid("77ac9673-86df-43e9-bb17-b648f1fe5eb4");

            /// <summary>
            /// Field: is_system (CheckboxField)
            /// Marks this salutation as a system record that cannot be deleted by users.
            /// Required=true, System=true, DefaultValue=false
            /// Source: NextPlugin.20190206.cs line 712
            /// </summary>
            public static readonly Guid IsSystemFieldId = new Guid("059917a0-4fdd-4154-9500-ebe8a0124ee2");

            /// <summary>
            /// Field: label (TextField)
            /// The display text for the salutation (e.g., "Mr.", "Ms.", "Dr.").
            /// Required=true, Unique=true, System=true, DefaultValue="label", MaxLength=null
            /// Source: NextPlugin.20190206.cs line 741
            /// </summary>
            public static readonly Guid LabelFieldId = new Guid("8318dfb5-c656-459b-adc8-83f4f0ee65a0");

            /// <summary>
            /// Field: sort_index (NumberField)
            /// Numeric ordering value for display sorting.
            /// Required=true, System=true, DefaultValue=1.0, DecimalPlaces=0
            /// Source: NextPlugin.20190206.cs line 771
            /// </summary>
            public static readonly Guid SortIndexFieldId = new Guid("e2a82937-7982-4fc2-84ca-b734efabb6b8");

            /// <summary>
            /// Field: l_scope (TextField)
            /// Localization scope for multi-tenant or multi-language filtering.
            /// Required=true, Searchable=true, System=true, DefaultValue="", MaxLength=null
            /// Source: NextPlugin.20190206.cs line 803
            /// </summary>
            public static readonly Guid LScopeFieldId = new Guid("a2de0020-63c6-4fb9-a35c-f3b63cc3455e");
        }

        // ---------------------------------------------------------------------------
        // Seed Data Constants (5 records)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Seed data record identifiers for the five built-in salutation values.
        /// These GUIDs are critical for data migration compatibility — existing account
        /// and contact records reference these salutation IDs via their salutation_id field.
        ///
        /// Source: NextPlugin.20190206.cs lines 1212–1300
        /// </summary>
        public static class SeedData
        {
            /// <summary>
            /// Seed record: "Mr." — sort_index=1.0, is_default=true, is_enabled=true, is_system=true
            /// Source: NextPlugin.20190206.cs line 1216
            /// </summary>
            public static readonly Guid MrId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");

            /// <summary>
            /// Seed record: "Ms." — sort_index=2.0, is_default=false, is_enabled=true, is_system=true
            /// Source: NextPlugin.20190206.cs line 1234
            /// </summary>
            public static readonly Guid MsId = new Guid("0ede7d96-2d85-45fa-818b-01327d4c47a9");

            /// <summary>
            /// Seed record: "Mrs." — sort_index=3.0, is_default=false, is_enabled=true, is_system=true
            /// Source: NextPlugin.20190206.cs line 1252
            /// </summary>
            public static readonly Guid MrsId = new Guid("ab073457-ddc8-4d36-84a5-38619528b578");

            /// <summary>
            /// Seed record: "Dr." — sort_index=4.0, is_default=false, is_enabled=true, is_system=true
            /// Source: NextPlugin.20190206.cs line 1270
            /// </summary>
            public static readonly Guid DrId = new Guid("5b8d0137-9ec5-4b1c-a9b0-e982ef8698c1");

            /// <summary>
            /// Seed record: "Prof." — sort_index=5.0, is_default=false, is_enabled=true, is_system=true
            /// Source: NextPlugin.20190206.cs line 1288
            /// </summary>
            public static readonly Guid ProfId = new Guid("a74cd934-b425-4061-8f4e-a6d6b9d7adb1");

            /// <summary>
            /// The default salutation record ID. Points to the "Mr." record which has
            /// is_default=true in its seed data. Used as the default value for
            /// account.salutation_id and contact.salutation_id fields.
            /// </summary>
            public static readonly Guid DefaultSalutationId = MrId;
        }

        // ---------------------------------------------------------------------------
        // Relation Constants (2 relations)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Relation identifiers and names for the Salutation entity's one-to-many
        /// relationships with Account and Contact entities.
        /// Both relations are system-managed and link salutation.id to the target
        /// entity's salutation_id field.
        ///
        /// Source: NextPlugin.20190206.cs lines 1334–1390
        /// </summary>
        public static class Relations
        {
            /// <summary>
            /// Relation: salutation_1n_account (OneToMany)
            /// Origin: salutation.id (690dc799-...) → Target: account.salutation_id
            /// System=true
            /// Source: NextPlugin.20190206.cs line 1341
            /// </summary>
            public static readonly Guid Salutation1nAccountId = new Guid("99e1a18b-05c2-4fca-986e-37ecebd62168");

            /// <summary>
            /// Relation name for salutation → account (1:N).
            /// Source: NextPlugin.20190206.cs line 1342
            /// </summary>
            public const string Salutation1nAccountName = "salutation_1n_account";

            /// <summary>
            /// Relation: salutation_1n_contact (OneToMany)
            /// Origin: salutation.id (690dc799-...) → Target: contact.salutation_id
            /// System=true
            /// Source: NextPlugin.20190206.cs line 1370
            /// </summary>
            public static readonly Guid Salutation1nContactId = new Guid("77ca10ff-18c9-44d6-a7ae-ddb0baa6a3a9");

            /// <summary>
            /// Relation name for salutation → contact (1:N).
            /// Source: NextPlugin.20190206.cs line 1371
            /// </summary>
            public const string Salutation1nContactName = "salutation_1n_contact";
        }

        // ---------------------------------------------------------------------------
        // Permission Constants
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Record-level permission role assignments for the Salutation entity.
        /// These define which roles can perform CRUD operations on salutation records.
        ///
        /// Permission model (from NextPlugin.20190206.cs lines 629–640):
        ///   - CanCreate: Administrator only
        ///   - CanRead:   Administrator AND Regular user
        ///   - CanUpdate: Administrator only
        ///   - CanDelete: No roles (deletion not permitted)
        ///
        /// Role GUIDs are sourced from CrmEntityConstants to avoid duplication
        /// across entity files, consistent with CaseTypeEntity and CaseStatusEntity patterns.
        /// </summary>
        public static class Permissions
        {
            /// <summary>
            /// Roles permitted to create salutation records.
            /// Admin only: bdc56420-caf0-4030-8a0e-d264938e0cda
            /// Source: NextPlugin.20190206.cs line 634
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanCreate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to read salutation records.
            /// Both admin and regular roles.
            /// Source: NextPlugin.20190206.cs lines 636–637
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanRead = new[]
            {
                CrmEntityConstants.AdministratorRoleId,
                CrmEntityConstants.RegularRoleId
            };

            /// <summary>
            /// Roles permitted to update salutation records.
            /// Admin only: bdc56420-caf0-4030-8a0e-d264938e0cda
            /// Source: NextPlugin.20190206.cs line 639
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanUpdate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to delete salutation records.
            /// Empty — no roles have delete permission on salutation records.
            /// Source: NextPlugin.20190206.cs line 640 (CanDelete list remains empty)
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanDelete = Array.Empty<Guid>();
        }
    }
}
