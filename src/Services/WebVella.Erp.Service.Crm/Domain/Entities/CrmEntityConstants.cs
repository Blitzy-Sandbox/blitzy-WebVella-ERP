using System;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Central foundational constants for the CRM service domain.
    /// Contains shared role GUIDs, CRM plugin metadata, entity registry IDs,
    /// intra-service relation IDs, and cross-service relation IDs.
    ///
    /// All GUIDs are preserved exactly from the monolith source code to ensure
    /// data migration compatibility during the monolith-to-microservices decomposition.
    ///
    /// This is the foundational file that all other entity definition files in
    /// the CRM domain folder depend on for shared identifiers.
    /// </summary>
    public static class CrmEntityConstants
    {
        // ---------------------------------------------------------------------------
        // Permission Role GUIDs
        // ---------------------------------------------------------------------------
        // These role identifiers originate from WebVella.Erp/ERPService.cs system
        // entity initialization. They are used across ALL CRM entity definitions
        // for RecordPermissions (CanCreate, CanRead, CanUpdate, CanDelete).
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Regular (non-admin) user role identifier.
        /// Sourced from WebVella.Erp/ERPService.cs system entity setup.
        /// Used in RecordPermissions for standard CRUD access on CRM entities.
        /// </summary>
        public static readonly Guid RegularRoleId = new Guid("f16ec6db-626d-4c27-8de0-3e7ce542c55f");

        /// <summary>
        /// Administrator role identifier.
        /// Sourced from WebVella.Erp/ERPService.cs system entity setup.
        /// Used in RecordPermissions for elevated CRUD access on CRM entities.
        /// </summary>
        public static readonly Guid AdministratorRoleId = new Guid("bdc56420-caf0-4030-8a0e-d264938e0cda");

        // ---------------------------------------------------------------------------
        // CRM Plugin Metadata
        // ---------------------------------------------------------------------------
        // Plugin identifier and name preserved from the monolith CrmPlugin class
        // (WebVella.Erp.Plugins.Crm/CrmPlugin.cs). Used for migration tracking,
        // service registration, and plugin_data table references.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique identifier for the CRM plugin / CRM microservice.
        /// Used for service registration and migration version tracking.
        /// </summary>
        public static readonly Guid CrmPluginId = new Guid("a60df97c-c81e-4e22-b7c2-39eb1f2ddba4");

        /// <summary>
        /// Canonical name of the CRM plugin / CRM microservice.
        /// Matches the monolith CrmPlugin.Name property value.
        /// </summary>
        public const string CrmPluginName = "crm";

        // ---------------------------------------------------------------------------
        // Entity Registry — All CRM-Owned Entity Identifiers
        // ---------------------------------------------------------------------------
        // Every entity that belongs to the CRM service boundary is registered here.
        // These IDs correspond to the entity.Id values set in the monolith's
        // NextPlugin patch files (20190203, 20190204, 20190206) and the CrmPlugin.
        //
        // Entity sources:
        //   Account     — NextPlugin.20190203.cs (line ~985)
        //   Contact     — NextPlugin.20190204.cs (line ~1408)
        //   Case        — NextPlugin.20190203.cs (line ~1392)
        //   Address     — NextPlugin.20190204.cs (line ~1904)
        //   Salutation  — NextPlugin.20190206.cs (line ~620)
        //   CaseStatus  — NextPlugin.20190203.cs (line ~1086)
        //   CaseType    — NextPlugin.20190203.cs (line ~3412)
        //   Industry    — NextPlugin.20190206.cs (referenced at line ~1110)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Well-known entity identifiers for all CRM-owned entities.
        /// These GUIDs are used by entity definition files, database migrations,
        /// search indexing, and gRPC service implementations to reference
        /// CRM entities by their stable identifiers.
        /// </summary>
        public static class EntityIds
        {
            /// <summary>
            /// Account entity identifier.
            /// Represents companies or persons in the CRM.
            /// Source: NextPlugin.20190203.cs — entity.Id = new Guid("2e22b50f-e444-4b62-a171-076e51246939")
            /// </summary>
            public static readonly Guid Account = new Guid("2e22b50f-e444-4b62-a171-076e51246939");

            /// <summary>
            /// Contact entity identifier.
            /// Represents individual contacts associated with accounts.
            /// Source: NextPlugin.20190204.cs — entity.Id = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0")
            /// </summary>
            public static readonly Guid Contact = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0");

            /// <summary>
            /// Case entity identifier.
            /// Represents support cases or service tickets in the CRM.
            /// Source: NextPlugin.20190203.cs — entity.Id = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c")
            /// </summary>
            public static readonly Guid Case = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c");

            /// <summary>
            /// Address entity identifier.
            /// Represents physical addresses associated with accounts or contacts.
            /// Source: NextPlugin.20190204.cs — entity.Id = new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0")
            /// </summary>
            public static readonly Guid Address = new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0");

            /// <summary>
            /// Salutation entity identifier.
            /// Lookup entity for salutation prefixes (Mr., Ms., Mrs., Dr., Prof.).
            /// Source: NextPlugin.20190206.cs — entity.Id = new Guid("690dc799-e732-4d17-80d8-0f761bc33def")
            /// </summary>
            public static readonly Guid Salutation = new Guid("690dc799-e732-4d17-80d8-0f761bc33def");

            /// <summary>
            /// CaseStatus entity identifier.
            /// Lookup entity for case workflow statuses.
            /// Source: NextPlugin.20190203.cs — entity.Id = new Guid("960afdc1-cd78-41ab-8135-816f7f7b8a27")
            /// </summary>
            public static readonly Guid CaseStatus = new Guid("960afdc1-cd78-41ab-8135-816f7f7b8a27");

            /// <summary>
            /// CaseType entity identifier.
            /// Lookup entity for case classification types.
            /// Source: NextPlugin.20190203.cs — entity.Id = new Guid("0dfeba58-40bb-4205-a539-c16d5c0885ad")
            /// </summary>
            public static readonly Guid CaseType = new Guid("0dfeba58-40bb-4205-a539-c16d5c0885ad");

            /// <summary>
            /// Industry entity identifier.
            /// Lookup entity for account industry classifications.
            /// Source: NextPlugin.20190206.cs — referenced at line ~1110 with Guid("2c60e662-367e-475d-9fcb-3ead55178a56")
            /// </summary>
            public static readonly Guid Industry = new Guid("2c60e662-367e-475d-9fcb-3ead55178a56");
        }

        // ---------------------------------------------------------------------------
        // Intra-Service Relations (within the CRM service boundary)
        // ---------------------------------------------------------------------------
        // These relation identifiers represent entity-to-entity relationships
        // that are fully contained within the CRM service's database. Both the
        // origin and target entities are owned by the CRM service.
        //
        // Note: Country is owned by the Core service, but the country→CRM entity
        // relations are tracked here because the CRM service needs them for
        // its database schema and query construction.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Relation identifiers for entity-to-entity relationships within
        /// or closely associated with the CRM service boundary.
        /// These are used for database schema migrations, EQL query construction,
        /// and relation management operations.
        /// </summary>
        public static class Relations
        {
            /// <summary>
            /// Salutation → Account (1:N) relation identifier.
            /// Links salutation lookup to account records via account.salutation_id.
            /// Origin: Salutation entity (690dc799-...) → Target: Account entity (2e22b50f-...)
            /// Source: NextPlugin.20190206.cs — corrected from "solutation" typo in earlier patch.
            /// </summary>
            public static readonly Guid Salutation1NAccount = new Guid("99e1a18b-2d2f-4e48-b81d-77c6cd3a8de8");

            /// <summary>
            /// Salutation → Contact (1:N) relation identifier.
            /// Links salutation lookup to contact records via contact.salutation_id.
            /// Origin: Salutation entity (690dc799-...) → Target: Contact entity (39e1dd9b-...)
            /// Source: NextPlugin.20190206.cs — corrected from "solutation" typo in earlier patch.
            /// </summary>
            public static readonly Guid Salutation1NContact = new Guid("77ca10ff-7128-4f45-a364-5e4e53b0094d");

            /// <summary>
            /// Country → Account (1:N) relation identifier.
            /// Links Core service's country entity to CRM account records via account.country_id.
            /// Origin: Country entity (Core service) → Target: Account entity (2e22b50f-...)
            /// Note: Country entity is owned by Core service; this relation is tracked in CRM
            /// because the target entity (account) belongs to CRM.
            /// Source: NextPlugin.20190204.cs
            /// </summary>
            public static readonly Guid Country1NAccount = new Guid("2581abd7-c474-4880-8e96-d311e9560524");

            /// <summary>
            /// Country → Contact (1:N) relation identifier.
            /// Links Core service's country entity to CRM contact records via contact.country_id.
            /// Origin: Country entity (Core service) → Target: Contact entity (39e1dd9b-...)
            /// Note: Country entity is owned by Core service; this relation is tracked in CRM
            /// because the target entity (contact) belongs to CRM.
            /// Source: NextPlugin.20190204.cs
            /// </summary>
            public static readonly Guid Country1NContact = new Guid("0e26f249-f8d0-4feb-a9ea-ce34b35f7b36");
        }

        // ---------------------------------------------------------------------------
        // Cross-Service Relations (CRM ↔ Core service)
        // ---------------------------------------------------------------------------
        // These relation identifiers represent relationships that span service
        // boundaries. In the database-per-service model, these cannot be enforced
        // via foreign keys. Instead, the CRM service stores the related entity's
        // UUID and resolves it at read-time via gRPC calls to the Core service
        // or via denormalized event-driven projections.
        //
        // These IDs are preserved for migration compatibility and are used by
        // the API composition layer (Gateway) to stitch cross-service query results.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Cross-service relation identifiers for relationships between
        /// CRM entities and Core service entities.
        /// These are used for API composition, event-driven projections,
        /// and maintaining cross-service referential integrity.
        /// </summary>
        public static class CrossServiceRelations
        {
            /// <summary>
            /// User → Account (created_by) cross-service relation identifier.
            /// Links Core service's user entity to CRM account records via account.created_by.
            /// In the microservice architecture, this is resolved via JWT claims or
            /// gRPC call to the Core service's SecurityGrpcService.
            /// </summary>
            public static readonly Guid UserAccountCreatedBy = new Guid("c27eb5c6-03c1-45fa-aae0-c7f2d0b10e4e");

            /// <summary>
            /// User → Contact (created_by) cross-service relation identifier.
            /// Links Core service's user entity to CRM contact records via contact.created_by.
            /// In the microservice architecture, this is resolved via JWT claims or
            /// gRPC call to the Core service's SecurityGrpcService.
            /// </summary>
            public static readonly Guid UserContactCreatedBy = new Guid("3e94dae7-00b1-4b6d-987b-b4caf86fd77d");
        }
    }
}
