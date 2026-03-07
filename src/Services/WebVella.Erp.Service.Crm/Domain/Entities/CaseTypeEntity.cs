using System;
using System.Collections.Generic;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Static configuration and constant class defining the CRM Case Type lookup entity.
    /// Contains entity metadata, field IDs, seed data record GUIDs, and permission definitions.
    /// Referenced by the Case entity via the type_id field (CaseEntity.Fields.TypeIdFieldId).
    ///
    /// All GUIDs are preserved exactly from the monolith source code to ensure
    /// data migration compatibility during the monolith-to-microservices decomposition.
    ///
    /// Source files:
    ///   Entity creation + fields: WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (lines 3405–3680)
    ///   l_scope field update:     WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (lines 1077–1105)
    ///   Seed data records:        WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (lines 5582–5680)
    /// </summary>
    public static class CaseTypeEntity
    {
        // ---------------------------------------------------------------------------
        // Entity Metadata
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique entity identifier for the case_type entity.
        /// Source: NextPlugin.20190203.cs line 3412 —
        ///   entity.Id = new Guid("0dfeba58-40bb-4205-a539-c16d5c0885ad")
        /// </summary>
        public static readonly Guid Id = new Guid("0dfeba58-40bb-4205-a539-c16d5c0885ad");

        /// <summary>
        /// Entity name used for database table naming (rec_case_type) and EQL queries.
        /// </summary>
        public const string Name = "case_type";

        /// <summary>
        /// Human-readable singular label for the entity.
        /// </summary>
        public const string Label = "Case type";

        /// <summary>
        /// Human-readable plural label for the entity.
        /// </summary>
        public const string LabelPlural = "Case types";

        /// <summary>
        /// Indicates this is a system entity that cannot be deleted by users.
        /// </summary>
        public const bool IsSystem = true;

        /// <summary>
        /// Font Awesome icon class used in the UI to represent case types.
        /// Uses the "far" (Font Awesome Regular) prefix.
        /// </summary>
        public const string IconName = "far fa-dot-circle";

        /// <summary>
        /// Hex color code used in the UI for case type visual identification.
        /// Material Design Red 500.
        /// </summary>
        public const string Color = "#f44336";

        /// <summary>
        /// System-generated field ID for the built-in 'id' field of this entity.
        /// Source: NextPlugin.20190203.cs line 3411 —
        ///   systemFieldIdDictionary["id"] = new Guid("d46667d4-8dc1-4834-884d-3578b717a5f1")
        /// </summary>
        public static readonly Guid SystemFieldId = new Guid("d46667d4-8dc1-4834-884d-3578b717a5f1");

        /// <summary>
        /// Field ID used as the record screen identifier (display column).
        /// Points to the 'label' field — matches <see cref="Fields.LabelFieldId"/>.
        /// Source: NextPlugin.20190203.cs line 3419 —
        ///   entity.RecordScreenIdField = new Guid("db0edb8f-a5f6-4baa-91f5-929fc732cc95")
        /// </summary>
        public static readonly Guid RecordScreenIdField = new Guid("db0edb8f-a5f6-4baa-91f5-929fc732cc95");

        // ---------------------------------------------------------------------------
        // Field ID Constants (8 fields)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Contains unique identifiers for all 8 fields belonging to the case_type entity.
        /// Field definitions extracted from NextPlugin.20190203.cs (lines 3443–3680)
        /// with l_scope field update from NextPlugin.20190206.cs (lines 1077–1105).
        /// </summary>
        public static class Fields
        {
            /// <summary>
            /// is_default field — CheckboxField.
            /// Indicates whether this case type is the default selection for new cases.
            /// Required=true, Searchable=false, System=true, DefaultValue=false.
            /// Source: NextPlugin.20190203.cs line 3446
            /// </summary>
            public static readonly Guid IsDefaultFieldId = new Guid("bcd41123-7264-4fde-bb2b-460a78d823d5");

            /// <summary>
            /// is_enabled field — CheckboxField.
            /// Controls whether this case type is available for selection in the UI.
            /// Required=true, Searchable=true, System=true, DefaultValue=true.
            /// Source: NextPlugin.20190203.cs line 3475
            /// </summary>
            public static readonly Guid IsEnabledFieldId = new Guid("892063aa-5007-4d0f-a584-65da00704bed");

            /// <summary>
            /// is_system field — CheckboxField.
            /// Indicates whether this case type is a system-managed record that
            /// cannot be modified or deleted by end users.
            /// Required=true, Searchable=false, System=true, DefaultValue=false.
            /// Source: NextPlugin.20190203.cs line 3504
            /// </summary>
            public static readonly Guid IsSystemFieldId = new Guid("d7e49c0e-f715-4789-90bf-d33557f549c1");

            /// <summary>
            /// l_scope field — TextField.
            /// Licensing/scope tag used for multi-tenant filtering of case type records.
            /// UPDATED in patch 20190206: Required=true, Searchable=true, System=true, DefaultValue="".
            /// Original (20190203) values were Required=false, Searchable=false, DefaultValue=null.
            /// Source: NextPlugin.20190203.cs line 3533 (created),
            ///         NextPlugin.20190206.cs line 1080 (updated)
            /// </summary>
            public static readonly Guid LScopeFieldId = new Guid("44b50db8-da41-45db-918f-d25599ab4673");

            /// <summary>
            /// sort_index field — NumberField.
            /// Determines the display ordering of case types in UI dropdowns and lists.
            /// Required=true, Searchable=false, System=true, DefaultValue=1.0, DecimalPlaces=0.
            /// Source: NextPlugin.20190203.cs line 3563
            /// </summary>
            public static readonly Guid SortIndexFieldId = new Guid("ac65a1e4-af93-4b96-9c49-902b0b1f7524");

            /// <summary>
            /// label field — TextField.
            /// Human-readable name of the case type displayed in the UI.
            /// Required=true, Unique=true, Searchable=false, System=true, DefaultValue="label".
            /// NOTE: This field is also used as the entity's RecordScreenIdField.
            /// Source: NextPlugin.20190203.cs line 3595
            /// </summary>
            public static readonly Guid LabelFieldId = new Guid("db0edb8f-a5f6-4baa-91f5-929fc732cc95");

            /// <summary>
            /// icon_class field — TextField.
            /// Font Awesome CSS class for the case type's icon representation.
            /// Required=false, Searchable=false, System=true, DefaultValue=null.
            /// Source: NextPlugin.20190203.cs line 3625
            /// </summary>
            public static readonly Guid IconClassFieldId = new Guid("991233b8-ff9c-4f32-9b9c-502425b41486");

            /// <summary>
            /// color field — TextField.
            /// Hex color code for the case type's visual representation in the UI.
            /// Required=false, Searchable=false, System=true, DefaultValue=null.
            /// Source: NextPlugin.20190203.cs line 3655
            /// </summary>
            public static readonly Guid ColorFieldId = new Guid("0bde42d0-adb0-4c70-894a-394d480685d7");
        }

        // ---------------------------------------------------------------------------
        // Seed Data Record GUIDs (5 records)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Contains unique identifiers for the 5 seed data records created during
        /// entity initialization. These are the built-in case type lookup values
        /// provisioned by the monolith's NextPlugin.Patch20190203 method.
        ///
        /// All seed records share: l_scope="", icon_class=null, color=null,
        /// is_system=true, is_enabled=true.
        ///
        /// Source: NextPlugin.20190203.cs (lines 5582–5680)
        /// </summary>
        public static class SeedData
        {
            /// <summary>
            /// "General" case type — the default case type (is_default=true).
            /// label="General", sort_index=1.0.
            /// Used as the default value for CaseEntity.Fields.TypeIdFieldId.
            /// </summary>
            public static readonly Guid GeneralId = new Guid("3298c9b3-560b-48b2-b148-997f9cbb3bec");

            /// <summary>
            /// "Problem" case type.
            /// label="Problem", sort_index=2.0, is_default=false.
            /// </summary>
            public static readonly Guid ProblemId = new Guid("f228d073-bd09-48ed-85c7-54c6231c9182");

            /// <summary>
            /// "Question" case type.
            /// label="Question", sort_index=3.0, is_default=false.
            /// </summary>
            public static readonly Guid QuestionId = new Guid("92b35547-f91b-492d-9c83-c29c3a4d132d");

            /// <summary>
            /// "Feature Request" case type.
            /// label="Feature Request", sort_index=4.0, is_default=false.
            /// </summary>
            public static readonly Guid FeatureRequestId = new Guid("15e7adc5-a3e7-47c5-ae54-252cffe82923");

            /// <summary>
            /// "Duplicate" case type.
            /// label="Duplicate", sort_index=5.0, is_default=false.
            /// </summary>
            public static readonly Guid DuplicateId = new Guid("dc4b7e9f-0790-47b5-a89c-268740aded38");

            /// <summary>
            /// Default case type ID — aliases <see cref="GeneralId"/>.
            /// Used as the default value for the Case entity's type_id field
            /// (CaseEntity.Defaults.DefaultTypeId should reference this value).
            /// </summary>
            public static readonly Guid DefaultTypeId = GeneralId;
        }

        // ---------------------------------------------------------------------------
        // Permission Definitions
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Defines role-based access control constants for the case_type entity.
        /// These match the RecordPermissions configured in the monolith source.
        ///
        /// Access policy:
        ///   CanCreate  — Administrator role only
        ///   CanRead    — Both Regular and Administrator roles
        ///   CanUpdate  — Administrator role only
        ///   CanDelete  — No roles (empty — deletion is not permitted)
        ///
        /// Role GUIDs are sourced from <see cref="CrmEntityConstants"/> to maintain
        /// a single source of truth for role identifiers across CRM entity files.
        ///
        /// Source: NextPlugin.20190203.cs lines 3420–3432
        /// </summary>
        public static class Permissions
        {
            /// <summary>
            /// Roles permitted to create case_type records.
            /// Only the Administrator role can create new case types.
            /// Source: entity.RecordPermissions.CanCreate.Add(new Guid("bdc56420-..."))
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanCreate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to read case_type records.
            /// Both Regular and Administrator roles can view case types.
            /// Source: entity.RecordPermissions.CanRead.Add for both role GUIDs
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanRead = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to update case_type records.
            /// Only the Administrator role can modify existing case types.
            /// Source: entity.RecordPermissions.CanUpdate.Add(new Guid("bdc56420-..."))
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanUpdate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to delete case_type records.
            /// No roles have delete permission — case type records cannot be deleted.
            /// The CanDelete list was initialized empty in the monolith source with
            /// no subsequent Add calls.
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanDelete = Array.Empty<Guid>();
        }
    }
}
