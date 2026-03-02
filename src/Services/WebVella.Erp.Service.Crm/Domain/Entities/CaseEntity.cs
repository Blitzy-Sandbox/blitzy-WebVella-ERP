using System;
using System.Collections.Generic;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Static configuration/constant class defining the CRM Case entity — its GUID,
    /// field IDs, priority options, default values, permissions, and relation metadata.
    ///
    /// Cases represent support tickets or service requests in the CRM domain.
    /// Each case is linked to an account (via account_id), has a status (via status_id
    /// pointing to case_status entity), a type (via type_id pointing to case_type entity),
    /// and a priority (high/medium/low select field).
    ///
    /// Source: Extracted from WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (lines 1385–1788)
    /// with field additions and updates from NextPlugin.20190206.cs (lines 830–889).
    ///
    /// All GUIDs are preserved exactly from the monolith source code to ensure
    /// data migration compatibility during the monolith-to-microservices decomposition.
    ///
    /// Cross-service references:
    ///   - created_by: Stores Core service user UUID, resolved via Core gRPC SecurityGrpcService.
    ///   - owner_id: Stores Core service user UUID, resolved via Core gRPC SecurityGrpcService.
    ///   - account_id: Stores CRM account UUID (intra-service, local FK).
    ///   - status_id: Stores CRM case_status UUID (intra-service, local FK).
    ///   - type_id: Stores CRM case_type UUID (intra-service, local FK).
    /// </summary>
    public static class CaseEntity
    {
        // ---------------------------------------------------------------------------
        // Entity Metadata
        // ---------------------------------------------------------------------------
        // Source: NextPlugin.20190203.cs lines 1389–1399
        // Entity definition for "case" — a system entity representing support tickets
        // or service requests in the CRM domain.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique identifier for the case entity.
        /// Source: entity.Id = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c")
        /// </summary>
        public static readonly Guid Id = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c");

        /// <summary>
        /// Database entity name used for table naming (rec_case).
        /// </summary>
        public const string Name = "case";

        /// <summary>
        /// Human-readable singular label for UI display.
        /// </summary>
        public const string Label = "Case";

        /// <summary>
        /// Human-readable plural label for UI display.
        /// </summary>
        public const string LabelPlural = "Cases";

        /// <summary>
        /// Indicates this is a system-managed entity that cannot be deleted by users.
        /// </summary>
        public const bool IsSystem = true;

        /// <summary>
        /// Font Awesome icon class used for entity representation in the UI.
        /// Uses the "fa fa-file" icon class.
        /// </summary>
        public const string IconName = "fa fa-file";

        /// <summary>
        /// Entity accent color in hexadecimal format, used for UI theming.
        /// Red (#f44336) — consistent with other CRM entities.
        /// </summary>
        public const string Color = "#f44336";

        /// <summary>
        /// System-generated field identifier for the built-in "id" field.
        /// Source: systemFieldIdDictionary["id"] = new Guid("5f50a281-8106-4b21-bb14-78ba7cf8ba37")
        /// </summary>
        public static readonly Guid SystemFieldId = new Guid("5f50a281-8106-4b21-bb14-78ba7cf8ba37");

        // NOTE: RecordScreenIdField is null for the case entity — no field is designated
        // as the screen identifier. This differs from lookup entities (case_status, case_type)
        // which use their label field as the RecordScreenIdField.

        // ---------------------------------------------------------------------------
        // Field ID Constants (13 fields)
        // ---------------------------------------------------------------------------
        // All field identifiers extracted from NextPlugin.20190203.cs (lines 1423–1788)
        // with additions from NextPlugin.20190206.cs (lines 830–889).
        //
        // The l_scope field was originally created in 20190203 with Required=false,
        // Searchable=false, System=false. It was UPDATED in 20190206 to
        // Required=true, Searchable=true, System=true, DefaultValue="".
        //
        // The x_search field was CREATED in 20190206 (not present in 20190203).
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Field identifiers for all 13 fields on the case entity.
        /// Each field's GUID is preserved exactly from the monolith source.
        /// </summary>
        public static class Fields
        {
            /// <summary>
            /// GuidField: account_id — FK reference to the CRM account entity.
            /// Links the case to the customer account it belongs to.
            /// Required=true, Searchable=false, System=true, DefaultValue=Guid.Empty.
            /// Source: NextPlugin.20190203.cs line 1426
            /// </summary>
            public static readonly Guid AccountIdFieldId = new Guid("829fefbc-3578-4311-881c-33597d236830");

            /// <summary>
            /// DateTimeField: created_on — timestamp when the case record was created.
            /// Required=true, System=true, Format="yyyy-MMM-dd HH:mm",
            /// UseCurrentTimeAsDefaultValue=true.
            /// Source: NextPlugin.20190203.cs line 1456
            /// </summary>
            public static readonly Guid CreatedOnFieldId = new Guid("104ef526-773d-464a-98cd-774d184cc7de");

            /// <summary>
            /// GuidField: created_by — UUID of the user who created the case.
            /// CROSS-SERVICE REFERENCE: Stores a Core service user UUID.
            /// In the microservice architecture, this is resolved via gRPC call to
            /// Core service's SecurityGrpcService or via JWT claims.
            /// Required=true, System=true, DefaultValue=Guid.Empty.
            /// Source: NextPlugin.20190203.cs line 1487
            /// </summary>
            public static readonly Guid CreatedByFieldId = new Guid("c3d1aeb5-0d96-4be0-aa9e-d7732ca68709");

            /// <summary>
            /// GuidField: owner_id — UUID of the user who owns/is assigned this case.
            /// CROSS-SERVICE REFERENCE: Stores a Core service user UUID.
            /// In the microservice architecture, this is resolved via gRPC call to
            /// Core service's SecurityGrpcService or via JWT claims.
            /// Required=true, Searchable=true, System=true, DefaultValue=Guid.Empty.
            /// Source: NextPlugin.20190203.cs line 1517
            /// </summary>
            public static readonly Guid OwnerIdFieldId = new Guid("3c25fb36-8d33-4a90-bd60-7a9bf401b547");

            /// <summary>
            /// HtmlField: description — rich text description of the case.
            /// Required=true, System=true, DefaultValue="description".
            /// Source: NextPlugin.20190203.cs line 1547
            /// </summary>
            public static readonly Guid DescriptionFieldId = new Guid("b8ac2f8c-1f24-4452-ad47-e7f3cf254ff4");

            /// <summary>
            /// TextField: subject — brief title/summary of the case.
            /// Required=true, System=true, DefaultValue="subject", MaxLength=null.
            /// Source: NextPlugin.20190203.cs line 1576
            /// </summary>
            public static readonly Guid SubjectFieldId = new Guid("8f5477aa-0fc6-4c97-9192-b9dadadaf497");

            /// <summary>
            /// AutoNumberField: number — auto-incrementing case number for display.
            /// Required=true, Unique=true, System=true,
            /// DefaultValue=1.0, DisplayFormat="{0}", StartingNumber=1.0.
            /// Source: NextPlugin.20190203.cs line 1606
            /// </summary>
            public static readonly Guid NumberFieldId = new Guid("19648468-893b-49f9-b8bd-b84add0c50f5");

            /// <summary>
            /// DateTimeField: closed_on — timestamp when the case was closed.
            /// Required=false (nullable — not set until case is closed), System=true,
            /// Format="yyyy-MMM-dd HH:mm", UseCurrentTimeAsDefaultValue=false.
            /// Source: NextPlugin.20190203.cs line 1637
            /// </summary>
            public static readonly Guid ClosedOnFieldId = new Guid("ac852183-e438-4c84-aaa3-dc12a0f2ad8e");

            /// <summary>
            /// TextField: l_scope — scope/tenant marker for multi-tenancy filtering.
            /// Originally created in 20190203 with Required=false, Searchable=false, System=false.
            /// UPDATED in 20190206: Required=true, Searchable=true, System=true, DefaultValue="".
            /// The UPDATED values from 20190206 are the canonical current state.
            /// Source (original): NextPlugin.20190203.cs line 1668
            /// Source (update): NextPlugin.20190206.cs line 834
            /// </summary>
            public static readonly Guid LScopeFieldId = new Guid("b8af3f7a-78a4-445c-ad28-b7eea1d9eff5");

            /// <summary>
            /// SelectField: priority — case priority level (high/medium/low).
            /// Required=true, Searchable=true, System=true, DefaultValue="low".
            /// Options: [{Label="high",Value="high"}, {Label="medium",Value="medium"}, {Label="low",Value="low"}]
            /// Source: NextPlugin.20190203.cs line 1698
            /// </summary>
            public static readonly Guid PriorityFieldId = new Guid("1dbe204d-3771-4f56-a2f5-bff0cf1831b4");

            /// <summary>
            /// GuidField: status_id — FK reference to the case_status lookup entity.
            /// Required=true, System=true.
            /// DefaultValue=Guid.Parse("4f17785b-c430-4fea-9fa9-8cfef931c60e") — "Open" status.
            /// See <see cref="Defaults.DefaultStatusId"/>.
            /// Source: NextPlugin.20190203.cs line 1733
            /// </summary>
            public static readonly Guid StatusIdFieldId = new Guid("05b97041-7a65-4d27-8c06-fc154d2fcbf5");

            /// <summary>
            /// GuidField: type_id — FK reference to the case_type lookup entity.
            /// Required=true, System=true.
            /// DefaultValue=Guid.Parse("3298c9b3-560b-48b2-b148-997f9cbb3bec") — "General" type.
            /// See <see cref="Defaults.DefaultTypeId"/>.
            /// Source: NextPlugin.20190203.cs line 1763
            /// </summary>
            public static readonly Guid TypeIdFieldId = new Guid("0b1f1244-6090-41e7-9684-53d2968bb33a");

            /// <summary>
            /// TextField: x_search — denormalized search index field for full-text search.
            /// Aggregates searchable values from the case and related entities
            /// for efficient ILIKE/FTS queries. Regenerated by SearchService on record changes.
            /// Required=true, Searchable=true, System=true, DefaultValue="".
            /// NOTE: This field was CREATED in patch 20190206, not 20190203.
            /// Source: NextPlugin.20190206.cs line 864
            /// </summary>
            public static readonly Guid XSearchFieldId = new Guid("d74a9521-c81c-4784-9aac-6339025ce51a");
        }

        // ---------------------------------------------------------------------------
        // Priority Select Field Options
        // ---------------------------------------------------------------------------
        // String constants for the priority SelectField's allowed values.
        // Source: NextPlugin.20190203.cs lines 1710–1715
        // ---------------------------------------------------------------------------

        /// <summary>
        /// String constants for the priority field's allowed select options.
        /// These values are stored as-is in the database and used for filtering,
        /// display, and validation.
        /// </summary>
        public static class PriorityOptions
        {
            /// <summary>
            /// High priority value. Label="high", Value="high".
            /// </summary>
            public const string High = "high";

            /// <summary>
            /// Medium priority value. Label="medium", Value="medium".
            /// </summary>
            public const string Medium = "medium";

            /// <summary>
            /// Low priority value (default). Label="low", Value="low".
            /// This is the default value for the priority field on new case records.
            /// </summary>
            public const string Low = "low";
        }

        // ---------------------------------------------------------------------------
        // Default Value Constants
        // ---------------------------------------------------------------------------
        // Default values for fields that reference lookup entity records.
        // These GUIDs point to specific seed data records in the case_status
        // and case_type entities.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Default values for case fields that reference lookup entity records.
        /// </summary>
        public static class Defaults
        {
            /// <summary>
            /// Default status_id value — points to the "Open" case_status seed record.
            /// This is the initial status assigned to newly created cases.
            /// Matches CaseStatusEntity.SeedData.OpenId.
            /// Source: guidField.DefaultValue = Guid.Parse("4f17785b-c430-4fea-9fa9-8cfef931c60e")
            /// from NextPlugin.20190203.cs line 1744
            /// </summary>
            public static readonly Guid DefaultStatusId = new Guid("4f17785b-c430-4fea-9fa9-8cfef931c60e");

            /// <summary>
            /// Default type_id value — points to the "General" case_type seed record.
            /// This is the initial type assigned to newly created cases.
            /// Matches CaseTypeEntity.SeedData.GeneralId.
            /// Source: guidField.DefaultValue = Guid.Parse("3298c9b3-560b-48b2-b148-997f9cbb3bec")
            /// from NextPlugin.20190203.cs line 1774
            /// </summary>
            public static readonly Guid DefaultTypeId = new Guid("3298c9b3-560b-48b2-b148-997f9cbb3bec");
        }

        // ---------------------------------------------------------------------------
        // Relation Constants
        // ---------------------------------------------------------------------------
        // Entity-to-entity relationship identifiers for relations where the case
        // entity participates as the target (child) entity.
        //
        // All three relations are OneToMany (1:N) with System=true:
        //   - case_status → case (status lookup)
        //   - account → case (customer linkage)
        //   - case_type → case (type classification)
        //
        // Source: NextPlugin.20190203.cs lines 5074–5188
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Relation identifiers for entity-to-entity relationships involving the
        /// case entity. Each relation includes both its GUID and canonical name.
        /// </summary>
        public static class Relations
        {
            /// <summary>
            /// Relation: case_status_1n_case (OneToMany)
            /// Origin: case_status entity (960afdc1-...) → Target: case entity (0ebb3981-...)
            /// Links the case_status lookup to case records via the case's status_id field.
            /// System=true.
            /// Source: NextPlugin.20190203.cs line 5081
            /// </summary>
            public static readonly Guid CaseStatus1nCaseId = new Guid("c523c594-1f84-495e-84f3-a569cb384586");

            /// <summary>
            /// Canonical name for the case_status → case (1:N) relation.
            /// Source: NextPlugin.20190203.cs line 5082
            /// </summary>
            public const string CaseStatus1nCaseName = "case_status_1n_case";

            /// <summary>
            /// Relation: account_1n_case (OneToMany)
            /// Origin: account entity (2e22b50f-...) → Target: case entity (0ebb3981-...)
            /// Links CRM account records to their associated support cases.
            /// System=true.
            /// Source: NextPlugin.20190203.cs line 5110
            /// </summary>
            public static readonly Guid Account1nCaseId = new Guid("06d07760-41ba-408c-af61-a1fdc8493de3");

            /// <summary>
            /// Canonical name for the account → case (1:N) relation.
            /// Source: NextPlugin.20190203.cs line 5111
            /// </summary>
            public const string Account1nCaseName = "account_1n_case";

            /// <summary>
            /// Relation: case_type_1n_case (OneToMany)
            /// Origin: case_type entity (0dfeba58-...) → Target: case entity (0ebb3981-...)
            /// Links the case_type lookup to case records via the case's type_id field.
            /// System=true.
            /// Source: NextPlugin.20190203.cs line 5168
            /// </summary>
            public static readonly Guid CaseType1nCaseId = new Guid("c4a6918b-7918-4806-83cb-fd3d87fe5a10");

            /// <summary>
            /// Canonical name for the case_type → case (1:N) relation.
            /// Source: NextPlugin.20190203.cs line 5169
            /// </summary>
            public const string CaseType1nCaseName = "case_type_1n_case";
        }

        // ---------------------------------------------------------------------------
        // Permission Constants
        // ---------------------------------------------------------------------------
        // Record-level permission role assignments for the Case entity.
        // Source: NextPlugin.20190203.cs lines 1400–1412
        //
        // Permission model:
        //   - CanCreate: Administrator only (admin can create new cases)
        //   - CanRead:   Administrator AND Regular user (both can view cases)
        //   - CanUpdate: Administrator only (admin can modify cases)
        //   - CanDelete: No roles (deletion not permitted)
        //
        // Role GUIDs are sourced from CrmEntityConstants to avoid duplication
        // across entity files, consistent with CaseTypeEntity, CaseStatusEntity,
        // SalutationEntity, and AddressEntity patterns.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Record-level permission role assignments for the Case entity.
        /// </summary>
        public static class Permissions
        {
            /// <summary>
            /// Roles permitted to create new case records.
            /// Only the Administrator role can create cases.
            /// Source: entity.RecordPermissions.CanCreate.Add(new Guid("bdc56420-..."))
            /// from NextPlugin.20190203.cs line 1406
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanCreate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to read/view case records.
            /// Both the Regular user role and the Administrator role can read cases.
            /// Source: entity.RecordPermissions.CanRead lines 1408–1409
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanRead = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to update existing case records.
            /// Only the Administrator role can update cases.
            /// Source: entity.RecordPermissions.CanUpdate.Add(new Guid("bdc56420-..."))
            /// from NextPlugin.20190203.cs line 1411
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanUpdate = new[]
            {
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to delete case records.
            /// Empty — no roles have delete permission on case records.
            /// Source: entity.RecordPermissions.CanDelete was initialized as an empty list
            /// and no GUIDs were added. (NextPlugin.20190203.cs line 1412)
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanDelete = Array.Empty<Guid>();
        }
    }
}
