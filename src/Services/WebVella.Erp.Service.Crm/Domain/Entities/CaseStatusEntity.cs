using System;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Static configuration/constant class defining the CRM Case Status lookup entity.
    /// Contains the entity GUID, all field IDs, seed data record identifiers, and
    /// role-based permission constants for the case_status entity.
    ///
    /// This is a lookup entity referenced by the Case entity via the status_id field.
    /// It tracks the workflow state of support cases (Open, On Hold, Escalated, Closed, etc.).
    ///
    /// Source: Extracted from WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (lines 1080–1383)
    /// with field updates from WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (lines 891–919).
    /// Seed data from NextPlugin.20190203.cs (lines 5393–5580).
    ///
    /// All GUIDs are preserved exactly from the monolith source code to ensure
    /// data migration compatibility during the monolith-to-microservices decomposition.
    /// </summary>
    public static class CaseStatusEntity
    {
        // ---------------------------------------------------------------------------
        // Entity Metadata
        // ---------------------------------------------------------------------------
        // Source: NextPlugin.20190203.cs lines 1083–1093
        // Entity definition for "case_status" — a system lookup entity used to
        // represent the workflow state of CRM cases.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique identifier for the case_status entity.
        /// Source: entity.Id = new Guid("960afdc1-cd78-41ab-8135-816f7f7b8a27")
        /// </summary>
        public static readonly Guid Id = new Guid("960afdc1-cd78-41ab-8135-816f7f7b8a27");

        /// <summary>
        /// Database entity name used for table naming (rec_case_status).
        /// </summary>
        public const string Name = "case_status";

        /// <summary>
        /// Human-readable singular label for UI display.
        /// </summary>
        public const string Label = "Case status";

        /// <summary>
        /// Human-readable plural label for UI display.
        /// </summary>
        public const string LabelPlural = "Case statuses";

        /// <summary>
        /// Indicates this is a system-managed entity that cannot be deleted by users.
        /// </summary>
        public const bool IsSystem = true;

        /// <summary>
        /// Font Awesome icon class used for entity representation in the UI.
        /// Uses the "far" (regular) prefix for the dot-circle icon.
        /// </summary>
        public const string IconName = "far fa-dot-circle";

        /// <summary>
        /// Entity accent color in hexadecimal format, used for UI theming.
        /// </summary>
        public const string Color = "#f44336";

        /// <summary>
        /// System-generated field identifier for the built-in "id" field.
        /// Source: systemFieldIdDictionary["id"] = new Guid("0e54a6c3-aa35-4048-bf8f-7d05afcc5eb3")
        /// </summary>
        public static readonly Guid SystemFieldId = new Guid("0e54a6c3-aa35-4048-bf8f-7d05afcc5eb3");

        /// <summary>
        /// Field used as the record screen identifier — points to the label field.
        /// This GUID matches <see cref="Fields.LabelFieldId"/> because the label field
        /// is used to display record identity in the UI.
        /// Source: entity.RecordScreenIdField = new Guid("f9082286-ff37-402b-b860-284d86dff1b6")
        /// </summary>
        public static readonly Guid RecordScreenIdField = new Guid("f9082286-ff37-402b-b860-284d86dff1b6");

        // ---------------------------------------------------------------------------
        // Field ID Constants (9 fields)
        // ---------------------------------------------------------------------------
        // All field identifiers extracted from NextPlugin.20190203.cs (lines 1117–1383).
        // The l_scope field was updated in NextPlugin.20190206.cs (lines 891–919)
        // to Required=true, Searchable=true, DefaultValue="".
        //
        // NOTE: The is_closed field is UNIQUE to case_status — it does not appear
        // in other lookup entities (case_type, industry, salutation).
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Field identifiers for all 9 fields on the case_status entity.
        /// Each field's GUID is preserved exactly from the monolith source.
        /// </summary>
        public static class Fields
        {
            /// <summary>
            /// CheckboxField: is_default — indicates the default case status for new cases.
            /// Required=true, System=true, DefaultValue=false.
            /// Source: NextPlugin.20190203.cs line 1120
            /// </summary>
            public static readonly Guid IsDefaultFieldId = new Guid("1ac9f589-f785-4046-ab73-13678afa007c");

            /// <summary>
            /// TextField: label — the display name of the case status.
            /// Required=true, Unique=true, System=true, DefaultValue="label".
            /// This field is also used as the RecordScreenIdField.
            /// Source: NextPlugin.20190203.cs line 1149
            /// </summary>
            public static readonly Guid LabelFieldId = new Guid("f9082286-ff37-402b-b860-284d86dff1b6");

            /// <summary>
            /// NumberField: sort_index — controls the display order of statuses.
            /// Required=true, System=true, DefaultValue=1.0, DecimalPlaces=0.
            /// Source: NextPlugin.20190203.cs line 1179
            /// </summary>
            public static readonly Guid SortIndexFieldId = new Guid("db39f9f2-f2e2-4dfb-80bd-75691983e5ce");

            /// <summary>
            /// CheckboxField: is_closed — indicates whether this status represents a
            /// closed/resolved state. Used to determine if a case is still active.
            /// Required=true, System=true, DefaultValue=false.
            /// NOTE: This field is UNIQUE to case_status — not present in other lookup entities.
            /// Source: NextPlugin.20190203.cs line 1211
            /// </summary>
            public static readonly Guid IsClosedFieldId = new Guid("1060aeda-8374-4d7e-a746-72fd082b120c");

            /// <summary>
            /// CheckboxField: is_system — marks system-provided status records that
            /// cannot be deleted or fundamentally altered by users.
            /// Required=true, System=true, DefaultValue=false.
            /// Source: NextPlugin.20190203.cs line 1240
            /// </summary>
            public static readonly Guid IsSystemFieldId = new Guid("4356d21a-5c7b-4716-af4c-571045e582f6");

            /// <summary>
            /// CheckboxField: is_enabled — controls whether this status is available
            /// for selection in the UI. Searchable=true.
            /// Required=true, Searchable=true, System=true, DefaultValue=true.
            /// Source: NextPlugin.20190203.cs line 1269
            /// </summary>
            public static readonly Guid IsEnabledFieldId = new Guid("ce3d169c-4eb2-488b-9b52-d15a800b4588");

            /// <summary>
            /// TextField: l_scope — scoping field used for multi-tenant or context-based filtering.
            /// UPDATED in patch 20190206: Required=true, Searchable=true, System=true, DefaultValue="".
            /// Original (20190203): Required=false, Searchable=false, DefaultValue=null.
            /// Source: NextPlugin.20190203.cs line 1298 (created),
            ///         NextPlugin.20190206.cs line 894 (updated to final values).
            /// </summary>
            public static readonly Guid LScopeFieldId = new Guid("21fa7a14-d953-47b0-a842-7516851197b9");

            /// <summary>
            /// TextField: icon_class — optional CSS icon class for visual representation.
            /// Required=false, System=true, DefaultValue=null, MaxLength=null.
            /// Source: NextPlugin.20190203.cs line 1328
            /// </summary>
            public static readonly Guid IconClassFieldId = new Guid("be916956-db6d-4f5b-9cf1-b892e3dafcca");

            /// <summary>
            /// TextField: color — optional hex color code for UI badge/label rendering.
            /// Required=false, System=true, DefaultValue=null, MaxLength=null.
            /// Source: NextPlugin.20190203.cs line 1358
            /// </summary>
            public static readonly Guid ColorFieldId = new Guid("3c822afe-764e-4144-9da2-06f2801883f7");
        }

        // ---------------------------------------------------------------------------
        // Seed Data Constants (9 records)
        // ---------------------------------------------------------------------------
        // Pre-populated case status records created during initial entity provisioning.
        // Source: NextPlugin.20190203.cs lines 5393–5580.
        //
        // All seed records share: l_scope="", icon_class=null, color=null,
        // is_system=true, is_enabled=true.
        //
        // Status workflow:
        //   Open (default) → Re-Open / On Hold / Wait for Customer / Escalated
        //   → Closed - Resolved / Closed - Rejected / Closed - No Response / Closed - Duplicate
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Seed data record identifiers for the 9 pre-populated case status records.
        /// These GUIDs are critical for data migration compatibility — they are referenced
        /// as default values in the Case entity's status_id field and by UI components.
        /// </summary>
        public static class SeedData
        {
            /// <summary>
            /// "Open" status — the default status for new cases.
            /// sort_index=1.0, is_default=true, is_closed=false.
            /// Source: NextPlugin.20190203.cs line 5397
            /// </summary>
            public static readonly Guid OpenId = new Guid("4f17785b-c430-4fea-9fa9-8cfef931c60e");

            /// <summary>
            /// "Re-Open" status — used when a previously closed case is reopened.
            /// sort_index=10.0, is_default=false, is_closed=false.
            /// Source: NextPlugin.20190203.cs line 5502
            /// </summary>
            public static readonly Guid ReOpenId = new Guid("fe9d8d44-996a-4e8a-8448-3d7731d4f278");

            /// <summary>
            /// "On Hold" status — case is paused pending internal action.
            /// sort_index=40.0, is_default=false, is_closed=false.
            /// Source: NextPlugin.20190203.cs line 5565
            /// </summary>
            public static readonly Guid OnHoldId = new Guid("ef18bf1e-314e-472f-887b-e348daef9676");

            /// <summary>
            /// "Wait for Customer" status — case is pending customer response.
            /// sort_index=50.0, is_default=false, is_closed=false.
            /// Source: NextPlugin.20190203.cs line 5523
            /// </summary>
            public static readonly Guid WaitForCustomerId = new Guid("508d9e1b-8896-46ed-a6fd-734197bdb1c8");

            /// <summary>
            /// "Escalated" status — case has been escalated to a higher support tier.
            /// sort_index=52.0, is_default=false, is_closed=false.
            /// Source: NextPlugin.20190203.cs line 5544
            /// </summary>
            public static readonly Guid EscalatedId = new Guid("95170be2-dcd9-4399-9ac4-7ecefb67ad2d");

            /// <summary>
            /// "Closed - Resolved" status — case has been successfully resolved.
            /// sort_index=100.0, is_default=false, is_closed=true.
            /// Source: NextPlugin.20190203.cs line 5460
            /// </summary>
            public static readonly Guid ClosedResolvedId = new Guid("2aac0c08-5e84-477d-add0-5bc60057eba4");

            /// <summary>
            /// "Closed - Rejected" status — case was rejected (not a valid issue).
            /// sort_index=101.0, is_default=false, is_closed=true.
            /// Source: NextPlugin.20190203.cs line 5481
            /// </summary>
            public static readonly Guid ClosedRejectedId = new Guid("61cba6d4-b175-4a89-94b6-6b700ce9adb9");

            /// <summary>
            /// "Closed - No Response" status — case closed due to customer non-response.
            /// sort_index=102.0, is_default=false, is_closed=true.
            /// Source: NextPlugin.20190203.cs line 5439
            /// </summary>
            public static readonly Guid ClosedNoResponseId = new Guid("b7368bd9-ea1c-4091-8c57-26e5c8360c29");

            /// <summary>
            /// "Closed - Duplicate" status — case was a duplicate of an existing case.
            /// sort_index=103.0, is_default=false, is_closed=true.
            /// Source: NextPlugin.20190203.cs line 5418
            /// </summary>
            public static readonly Guid ClosedDuplicateId = new Guid("c04d2a73-9fd3-4d00-b32e-9887e517f3bf");

            /// <summary>
            /// Default case status identifier — points to the "Open" status record.
            /// Used as the default value for Case entity's status_id field
            /// (DefaultValue = Guid.Parse("4f17785b-c430-4fea-9fa9-8cfef931c60e")).
            /// </summary>
            public static readonly Guid DefaultStatusId = OpenId;
        }

        // ---------------------------------------------------------------------------
        // Permission Constants
        // ---------------------------------------------------------------------------
        // Role-based access control constants for the case_status entity.
        // Source: NextPlugin.20190203.cs lines 1094–1106.
        //
        // Permission model:
        //   CanCreate: Administrator only
        //   CanRead:   Both Regular and Administrator roles
        //   CanUpdate: Administrator only
        //   CanDelete: No roles (empty) — case statuses cannot be deleted
        //
        // Role GUIDs are sourced from CrmEntityConstants to avoid duplication.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Role-based permission constants for the case_status entity.
        /// Defines which roles can perform CRUD operations on case status records.
        /// </summary>
        public static class Permissions
        {
            /// <summary>
            /// Roles permitted to create new case status records.
            /// Only the Administrator role can create case statuses.
            /// Source: entity.RecordPermissions.CanCreate.Add(new Guid("bdc56420-..."))
            /// </summary>
            public static readonly Guid[] CanCreate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to read case status records.
            /// Both Regular and Administrator roles can read case statuses.
            /// Source: entity.RecordPermissions.CanRead.Add(new Guid("f16ec6db-..."))
            ///         entity.RecordPermissions.CanRead.Add(new Guid("bdc56420-..."))
            /// </summary>
            public static readonly Guid[] CanRead = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to update existing case status records.
            /// Only the Administrator role can update case statuses.
            /// Source: entity.RecordPermissions.CanUpdate.Add(new Guid("bdc56420-..."))
            /// </summary>
            public static readonly Guid[] CanUpdate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to delete case status records.
            /// No roles are permitted to delete case statuses — this array is empty.
            /// Source: entity.RecordPermissions.CanDelete was initialized but no GUIDs were added.
            /// </summary>
            public static readonly Guid[] CanDelete = Array.Empty<Guid>();
        }
    }
}
