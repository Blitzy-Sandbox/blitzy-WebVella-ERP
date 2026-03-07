using System;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Static configuration/constant class defining the CRM Address entity — its GUID,
    /// field IDs, field configurations, and RBAC permissions.
    ///
    /// Extracted from the monolith's Next plugin patch file:
    ///   WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (lines 1897–2148)
    ///
    /// The Address entity represents physical addresses associated with accounts or
    /// contacts in the CRM domain. It is the most permissive CRM entity — both the
    /// regular user role and administrator role have full CRUD access.
    ///
    /// All GUIDs are preserved exactly from the monolith source code to ensure
    /// data migration compatibility during the monolith-to-microservices decomposition.
    ///
    /// Cross-service note: The country_id field references the Core service's country
    /// entity. In the database-per-service model, this is resolved at read-time via
    /// gRPC calls to the Core service or via denormalized event-driven projections.
    /// </summary>
    public static class AddressEntity
    {
        // ---------------------------------------------------------------------------
        // Entity Metadata
        // ---------------------------------------------------------------------------
        // Source: NextPlugin.20190204.cs, lines 1897–1937
        // Entity creation block: entity.Id, entity.Name, entity.Label, etc.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique identifier for the Address entity.
        /// Source: NextPlugin.20190204.cs — entity.Id = new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0")
        /// </summary>
        public static readonly Guid Id = new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0");

        /// <summary>
        /// Internal system name of the Address entity.
        /// Used for EQL queries, database table naming (rec_address), and API references.
        /// </summary>
        public const string Name = "address";

        /// <summary>
        /// Human-readable singular label for the Address entity.
        /// Displayed in the admin UI entity designer and form headers.
        /// </summary>
        public const string Label = "Address";

        /// <summary>
        /// Human-readable plural label for the Address entity.
        /// Displayed in list views and navigation menus.
        /// </summary>
        public const string LabelPlural = "Addresses";

        /// <summary>
        /// Indicates this is a system entity that cannot be deleted by end users.
        /// System entities are provisioned during initial database setup.
        /// </summary>
        public const bool IsSystem = true;

        /// <summary>
        /// Font Awesome icon class for the Address entity.
        /// Uses the "fas" (Font Awesome Solid) prefix as specified in the monolith source.
        /// Displayed in the admin UI entity list and navigation sidebar.
        /// </summary>
        public const string IconName = "fas fa-building";

        /// <summary>
        /// Accent color (hex) for the Address entity in the admin UI.
        /// Material Design red (#f44336) — used for entity badges, headers, and highlights.
        /// </summary>
        public const string Color = "#f44336";

        /// <summary>
        /// Well-known GUID for the system-generated "id" field of the Address entity.
        /// This is the primary key field auto-created by the entity framework during
        /// entity provisioning. Corresponds to systemFieldIdDictionary["id"] in the source.
        /// Source: NextPlugin.20190204.cs — systemFieldIdDictionary["id"] = new Guid("158c33cc-...")
        /// </summary>
        public static readonly Guid SystemFieldId = new Guid("158c33cc-f7b2-4b0a-aeb6-ce5e908f6c5d");

        // ---------------------------------------------------------------------------
        // Field Identifiers
        // ---------------------------------------------------------------------------
        // All 7 custom fields created for the Address entity in the monolith.
        // Source: NextPlugin.20190204.cs, lines 1939–2148
        //
        // Common field properties across all Address fields:
        //   Required = false, Searchable = false, Auditable = false,
        //   System = true, EnableSecurity = false
        //
        // Field types:
        //   street, street_2, city, region, name — InputTextField
        //   country_id — InputGuidField (GenerateNewId = false)
        //   notes — InputMultiLineTextField (VisibleLineNumber = null)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Contains the well-known GUIDs for all custom fields of the Address entity.
        /// Each field ID is preserved exactly from the monolith source to ensure
        /// schema migration compatibility and EQL query field resolution.
        /// </summary>
        public static class Fields
        {
            /// <summary>
            /// Field ID for the "street" field (InputTextField).
            /// Primary street address line.
            /// Required=false, Searchable=false, System=true, DefaultValue=null, MaxLength=null.
            /// Source: NextPlugin.20190204.cs, line 1942
            /// </summary>
            public static readonly Guid StreetFieldId = new Guid("79e7a689-6407-4a03-8580-5bdb20e2337d");

            /// <summary>
            /// Field ID for the "street_2" field (InputTextField).
            /// Secondary/supplementary street address line.
            /// Required=false, Searchable=false, System=true, DefaultValue=null, MaxLength=null.
            /// Source: NextPlugin.20190204.cs, line 1972
            /// </summary>
            public static readonly Guid Street2FieldId = new Guid("3aeb73d9-8879-4f25-93e9-0b22944a5bba");

            /// <summary>
            /// Field ID for the "city" field (InputTextField).
            /// City/municipality component of the address.
            /// Required=false, Searchable=false, System=true, DefaultValue=null, MaxLength=null.
            /// Source: NextPlugin.20190204.cs, line 2002
            /// </summary>
            public static readonly Guid CityFieldId = new Guid("6b8150d5-ea81-4a74-b35a-b6c888665fe5");

            /// <summary>
            /// Field ID for the "region" field (InputTextField).
            /// State/province/region component of the address.
            /// Required=false, Searchable=false, System=true, DefaultValue=null, MaxLength=null.
            /// Source: NextPlugin.20190204.cs, line 2032
            /// </summary>
            public static readonly Guid RegionFieldId = new Guid("6225169e-fcde-4c66-9066-d08bbe9a7b1b");

            /// <summary>
            /// Field ID for the "country_id" field (InputGuidField).
            /// References the Core service's country entity by UUID.
            /// In the database-per-service model, this cross-service reference is resolved
            /// at read-time via gRPC calls to the Core service's EntityGrpcService.
            /// Required=false, Searchable=false, System=true, DefaultValue=null, GenerateNewId=false.
            /// Source: NextPlugin.20190204.cs, line 2062
            /// </summary>
            public static readonly Guid CountryIdFieldId = new Guid("c40192ea-c81c-4140-9c7b-6134184f942c");

            /// <summary>
            /// Field ID for the "notes" field (InputMultiLineTextField / Textarea).
            /// Free-text notes or comments associated with the address.
            /// Required=false, Searchable=false, System=true, DefaultValue=null,
            /// MaxLength=null, VisibleLineNumber=null.
            /// Source: NextPlugin.20190204.cs, line 2092
            /// </summary>
            public static readonly Guid NotesFieldId = new Guid("a977b2af-78ea-4df0-97dc-652d82cee2df");

            /// <summary>
            /// Field ID for the "name" field (InputTextField).
            /// Display name or label for the address (e.g., "Main Office", "Warehouse").
            /// Required=false, Searchable=false, System=true, DefaultValue=null, MaxLength=null.
            /// Source: NextPlugin.20190204.cs, line 2123
            /// </summary>
            public static readonly Guid NameFieldId = new Guid("487d6795-6cec-4598-bbeb-094bcbeadcf6");
        }

        // ---------------------------------------------------------------------------
        // Record Permissions (Role-Based Access Control)
        // ---------------------------------------------------------------------------
        // The Address entity is the most permissive CRM entity — both the regular
        // user role and the administrator role have FULL CRUD access for all four
        // permission operations (Create, Read, Update, Delete).
        //
        // Source: NextPlugin.20190204.cs, lines 1912–1928
        //   entity.RecordPermissions.CanCreate.Add(RegularRoleId)
        //   entity.RecordPermissions.CanCreate.Add(AdministratorRoleId)
        //   entity.RecordPermissions.CanRead.Add(RegularRoleId)
        //   entity.RecordPermissions.CanRead.Add(AdministratorRoleId)
        //   entity.RecordPermissions.CanUpdate.Add(RegularRoleId)
        //   entity.RecordPermissions.CanUpdate.Add(AdministratorRoleId)
        //   entity.RecordPermissions.CanDelete.Add(RegularRoleId)
        //   entity.RecordPermissions.CanDelete.Add(AdministratorRoleId)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Role-based access control permission constants for the Address entity.
        /// Address is the most permissive CRM entity — both regular users and
        /// administrators have full CRUD access for all four permission operations.
        ///
        /// These permission arrays use the shared role GUIDs from
        /// <see cref="CrmEntityConstants.RegularRoleId"/> and
        /// <see cref="CrmEntityConstants.AdministratorRoleId"/>.
        /// </summary>
        public static class Permissions
        {
            /// <summary>
            /// Roles authorized to create Address records.
            /// Both regular and administrator roles have create permission.
            /// </summary>
            public static readonly Guid[] CanCreate = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles authorized to read Address records.
            /// Both regular and administrator roles have read permission.
            /// </summary>
            public static readonly Guid[] CanRead = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles authorized to update Address records.
            /// Both regular and administrator roles have update permission.
            /// </summary>
            public static readonly Guid[] CanUpdate = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles authorized to delete Address records.
            /// Both regular and administrator roles have delete permission.
            /// </summary>
            public static readonly Guid[] CanDelete = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };
        }
    }
}
