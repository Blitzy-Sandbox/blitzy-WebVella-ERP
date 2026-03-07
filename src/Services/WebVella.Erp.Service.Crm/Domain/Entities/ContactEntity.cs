using System;
using System.Collections.Generic;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Static configuration/constant class defining the CRM Contact entity — its GUID,
    /// field IDs, default values, permissions, and relation metadata.
    ///
    /// Contacts represent individual people associated with CRM accounts. Each contact
    /// can be linked to one or more accounts (via account_nn_contact N:N relation),
    /// has a salutation (via salutation_id pointing to salutation entity), and an
    /// optional country reference (via country_id pointing to Core service's country entity).
    ///
    /// Source: Extracted from WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (lines 1401–1896)
    /// with entity update, field additions (created_on, photo, x_search, salutation_id),
    /// and relation corrections from NextPlugin.20190206.cs.
    ///
    /// All GUIDs are preserved exactly from the monolith source code to ensure
    /// data migration compatibility during the monolith-to-microservices decomposition.
    ///
    /// Cross-service references:
    ///   - created_by (audit field): Stores Core service user UUID, resolved via Core gRPC SecurityGrpcService.
    ///   - country_id: Stores Core service country entity UUID, resolved via Core gRPC or local denormalized projection.
    ///   - salutation_id: Stores CRM salutation UUID (intra-service, local FK).
    ///   - account (via N:N relation): Intra-CRM service relation.
    /// </summary>
    public static class ContactEntity
    {
        // ---------------------------------------------------------------------------
        // Entity Metadata
        // ---------------------------------------------------------------------------
        // Source: NextPlugin.20190204.cs lines 1401–1441 (entity creation)
        // Updated: NextPlugin.20190206.cs lines 397–426 (entity update — icon change)
        //
        // Entity definition for "contact" — a system entity representing individual
        // people in the CRM domain, associated with accounts.
        //
        // The entity was originally created in patch 20190204 with IconName="fa fa-user".
        // Patch 20190206 updated the entity, changing IconName to "far fa-address-card".
        // The values below reflect the FINAL state after all patches have been applied.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique identifier for the contact entity.
        /// Source: entity.Id = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0")
        /// </summary>
        public static readonly Guid Id = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0");

        /// <summary>
        /// Database entity name used for table naming (rec_contact).
        /// </summary>
        public const string Name = "contact";

        /// <summary>
        /// Human-readable singular label for UI display.
        /// </summary>
        public const string Label = "Contact";

        /// <summary>
        /// Human-readable plural label for UI display.
        /// </summary>
        public const string LabelPlural = "Contacts";

        /// <summary>
        /// Indicates this is a system-managed entity that cannot be deleted by users.
        /// </summary>
        public const bool IsSystem = true;

        /// <summary>
        /// Font Awesome icon class used for entity representation in the UI.
        /// Originally "fa fa-user" (patch 20190204), updated to "far fa-address-card" (patch 20190206).
        /// The value here reflects the final canonical state after all patches.
        /// </summary>
        public const string IconName = "far fa-address-card";

        /// <summary>
        /// Entity accent color in hexadecimal format, used for UI theming.
        /// Red (#f44336) — consistent with other CRM entities.
        /// </summary>
        public const string Color = "#f44336";

        /// <summary>
        /// System-generated field identifier for the built-in "id" field.
        /// Source: systemFieldIdDictionary["id"] = new Guid("859f24ec-4d3e-4597-9972-1d5a9cba918b")
        /// </summary>
        public static readonly Guid SystemFieldId = new Guid("859f24ec-4d3e-4597-9972-1d5a9cba918b");

        // NOTE: RecordScreenIdField is null for the contact entity — no field is designated
        // as the screen identifier. This is consistent with the case entity pattern.

        // ---------------------------------------------------------------------------
        // Field ID Constants (18 fields)
        // ---------------------------------------------------------------------------
        // Fields created in NextPlugin.20190204.cs (lines 1443–1895):
        //   email, job_title, first_name, last_name, notes, fixed_phone, mobile_phone,
        //   fax_phone, city, country_id, region, street, street_2, post_code
        //
        // Fields created in NextPlugin.20190206.cs (lines 429–547):
        //   created_on, photo, x_search, salutation_id
        //
        // The misspelled "solutation_id" field (Id=66b49907-...) was created in 20190204
        // and subsequently DELETED in 20190206 (line 45–53). It was replaced by the
        // correctly spelled "salutation_id" field (Id=afd8d03c-...) in 20190206.
        // Only the corrected salutation_id is included here.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Field identifiers for all 18 fields on the contact entity.
        /// Each field's GUID is preserved exactly from the monolith source.
        /// </summary>
        public static class Fields
        {
            /// <summary>
            /// EmailField: email — contact's email address.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1446
            /// </summary>
            public static readonly Guid EmailFieldId = new Guid("ca400904-1334-48fe-884c-223df1d08545");

            /// <summary>
            /// TextField: job_title — contact's job title or position.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1476
            /// </summary>
            public static readonly Guid JobTitleFieldId = new Guid("ddcc1807-6651-411d-9eed-668ee34d0c1b");

            /// <summary>
            /// TextField: first_name — contact's given name.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1506
            /// </summary>
            public static readonly Guid FirstNameFieldId = new Guid("6670c70c-c46e-4912-a70f-b1ad20816415");

            /// <summary>
            /// TextField: last_name — contact's family name.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1536
            /// </summary>
            public static readonly Guid LastNameFieldId = new Guid("4f711d55-11a7-464a-a4c3-3b3047c6c014");

            /// <summary>
            /// MultiLineTextField (TextareaField): notes — free-form notes about the contact.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1566
            /// </summary>
            public static readonly Guid NotesFieldId = new Guid("9912ff90-bc26-4879-9615-c5963a42fe22");

            /// <summary>
            /// PhoneField: fixed_phone — contact's landline telephone number.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1597
            /// </summary>
            public static readonly Guid FixedPhoneFieldId = new Guid("0f947ba0-ccac-40c4-9d31-5e5f5be953ce");

            /// <summary>
            /// PhoneField: mobile_phone — contact's mobile telephone number.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1628
            /// </summary>
            public static readonly Guid MobilePhoneFieldId = new Guid("519bd797-1dc7-4aef-b1ed-f27442f855ef");

            /// <summary>
            /// PhoneField: fax_phone — contact's fax telephone number.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1659
            /// </summary>
            public static readonly Guid FaxPhoneFieldId = new Guid("0475b344-8f8e-464c-a182-9c2beae105f3");

            /// <summary>
            /// TextField: city — contact's city of residence.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1720
            /// </summary>
            public static readonly Guid CityFieldId = new Guid("acc25b72-6e17-437f-bfaf-f514b0a7406f");

            /// <summary>
            /// GuidField: country_id — FK reference to the Core service's country entity.
            /// CROSS-SERVICE REFERENCE: Stores a Core service country entity UUID.
            /// In the microservice architecture, this is resolved via gRPC call to
            /// Core service or via denormalized event-driven projection.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1750
            /// </summary>
            public static readonly Guid CountryIdFieldId = new Guid("08a67742-21ef-4ecb-8872-54ac18b50bdc");

            /// <summary>
            /// TextField: region — contact's region/state/province.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1780
            /// </summary>
            public static readonly Guid RegionFieldId = new Guid("f5cab626-c215-4922-be4f-8931d0cf0b66");

            /// <summary>
            /// TextField: street — contact's primary street address.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1810
            /// </summary>
            public static readonly Guid StreetFieldId = new Guid("1147a14a-d9ae-4c88-8441-80f668676b1c");

            /// <summary>
            /// TextField: street_2 — contact's secondary street address line.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1840
            /// </summary>
            public static readonly Guid Street2FieldId = new Guid("2b1532c0-528c-4dfb-b40a-3d75ef1491fc");

            /// <summary>
            /// TextField: post_code — contact's postal/ZIP code.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// Source: NextPlugin.20190204.cs line 1870
            /// </summary>
            public static readonly Guid PostCodeFieldId = new Guid("c3433c76-dee9-4dce-94a0-ea5f03527ee6");

            /// <summary>
            /// DateTimeField: created_on — timestamp when the contact record was created.
            /// Required=true, System=true, Format="yyyy-MMM-dd HH:mm",
            /// UseCurrentTimeAsDefaultValue=true.
            /// NOTE: This field was CREATED in patch 20190206, not 20190204.
            /// Source: NextPlugin.20190206.cs line 432
            /// </summary>
            public static readonly Guid CreatedOnFieldId = new Guid("52f89031-2d6d-47af-ba28-40da08b040ae");

            /// <summary>
            /// ImageField: photo — contact's profile photo/avatar.
            /// Required=false, Unique=false, Searchable=false, System=true.
            /// NOTE: This field was CREATED in patch 20190206, not 20190204.
            /// Source: NextPlugin.20190206.cs line 463
            /// </summary>
            public static readonly Guid PhotoFieldId = new Guid("63e82ecb-ff4e-4fd0-91be-6278875ea39c");

            /// <summary>
            /// TextField: x_search — denormalized search index field for full-text search.
            /// Aggregates searchable values from the contact and related entities
            /// for efficient ILIKE/FTS queries. Regenerated by SearchService on record changes.
            /// Required=true, Searchable=true, System=true, DefaultValue="",
            /// PlaceholderText="Search contacts".
            /// NOTE: This field was CREATED in patch 20190206, not 20190204.
            /// Source: NextPlugin.20190206.cs line 492
            /// </summary>
            public static readonly Guid XSearchFieldId = new Guid("6d33f297-1cd4-4b75-a0cf-1887b7a3ced8");

            /// <summary>
            /// GuidField: salutation_id — FK reference to the CRM salutation lookup entity.
            /// Required=true, System=true.
            /// DefaultValue=Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698") — "Mr." salutation.
            /// See <see cref="Defaults.DefaultSalutationId"/>.
            ///
            /// IMPORTANT: This is the CORRECTED field replacing the misspelled "solutation_id"
            /// (Id=66b49907-2c0f-4914-a71c-1a9ccba1c704) which was created in patch 20190204
            /// and then DELETED in patch 20190206 (line 45–53).
            /// Source: NextPlugin.20190206.cs line 522
            /// </summary>
            public static readonly Guid SalutationIdFieldId = new Guid("afd8d03c-8bd8-44f8-8c46-b13e57cffa30");
        }

        // ---------------------------------------------------------------------------
        // Default Value Constants
        // ---------------------------------------------------------------------------
        // Default values for fields that reference lookup entity records.
        // The salutation_id field has a default pointing to a specific seed data
        // record in the salutation entity.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Default values for contact fields that reference lookup entity records.
        /// </summary>
        public static class Defaults
        {
            /// <summary>
            /// Default salutation_id value — points to the "Mr." salutation seed record.
            /// This is the initial salutation assigned to newly created contacts.
            /// Matches SalutationEntity seed data.
            /// Source: guidField.DefaultValue = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698")
            /// from NextPlugin.20190206.cs line 533
            /// </summary>
            public static readonly Guid DefaultSalutationId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");
        }

        // ---------------------------------------------------------------------------
        // Relation Constants
        // ---------------------------------------------------------------------------
        // Entity-to-entity relationship identifiers for relations where the contact
        // entity participates as the target entity.
        //
        // Relations:
        //   - account_nn_contact (N:N): Account ↔ Contact (bidirectional many-to-many)
        //   - salutation_1n_contact (1:N): Salutation → Contact (salutation lookup)
        //   - country_1n_contact (1:N): Country → Contact (country lookup, cross-service)
        //
        // The original "solutation_1n_contact" relation (Id=54a6e20a-...) was DELETED
        // in patch 20190206 and replaced by "salutation_1n_contact" (Id=77ca10ff-...).
        //
        // Sources:
        //   account_nn_contact: NextPlugin.20190204.cs lines 2236–2263
        //   salutation_1n_contact: NextPlugin.20190206.cs lines 1363–1390
        //   country_1n_contact: NextPlugin.20190204.cs lines 2555–2582
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Relation identifiers for entity-to-entity relationships involving the
        /// contact entity. Each relation includes both its GUID and canonical name.
        /// </summary>
        public static class Relations
        {
            /// <summary>
            /// Relation: account_nn_contact (ManyToMany)
            /// Origin: account entity (2e22b50f-...) → Target: contact entity (39e1dd9b-...)
            /// Links CRM accounts to their associated contacts in a many-to-many relationship.
            /// Both sides reference the "id" field. Backed by a rel_* join table.
            /// System=true.
            /// Source: NextPlugin.20190204.cs line 2243
            /// </summary>
            public static readonly Guid AccountNnContactId = new Guid("dd211c99-5415-4195-923a-cb5a56e5d544");

            /// <summary>
            /// Canonical name for the account ↔ contact (N:N) relation.
            /// Source: NextPlugin.20190204.cs line 2244
            /// </summary>
            public const string AccountNnContactName = "account_nn_contact";

            /// <summary>
            /// Relation: salutation_1n_contact (OneToMany)
            /// Origin: salutation entity (690dc799-...) → Target: contact entity (39e1dd9b-...)
            /// Links the salutation lookup entity to contact records via contact.salutation_id.
            /// System=true.
            ///
            /// This is the CORRECTED relation replacing the misspelled "solutation_1n_contact"
            /// (Id=54a6e20a-9e94-45fb-b77c-e2bb35cb20fc) which was created in patch 20190204
            /// and then DELETED in patch 20190206 (line 15–23).
            /// Source: NextPlugin.20190206.cs line 1370
            /// </summary>
            public static readonly Guid Salutation1nContactId = new Guid("77ca10ff-18c9-44d6-a7ae-ddb0baa6a3a9");

            /// <summary>
            /// Canonical name for the salutation → contact (1:N) relation.
            /// Source: NextPlugin.20190206.cs line 1371
            /// </summary>
            public const string Salutation1nContactName = "salutation_1n_contact";

            /// <summary>
            /// Relation: country_1n_contact (OneToMany)
            /// Origin: country entity (54cfe9e9-..., Core service) → Target: contact entity (39e1dd9b-...)
            /// Links the Core service's country entity to CRM contact records via contact.country_id.
            /// CROSS-SERVICE REFERENCE: Country entity is owned by the Core service; this relation
            /// is tracked in the CRM service because the target entity (contact) belongs to CRM.
            /// System=true.
            /// Source: NextPlugin.20190204.cs line 2562
            /// </summary>
            public static readonly Guid Country1nContactId = new Guid("dc4ece26-fff7-440a-9e19-76189507b5b9");

            /// <summary>
            /// Canonical name for the country → contact (1:N) relation.
            /// Source: NextPlugin.20190204.cs line 2563
            /// </summary>
            public const string Country1nContactName = "country_1n_contact";
        }

        // ---------------------------------------------------------------------------
        // Permission Constants
        // ---------------------------------------------------------------------------
        // Record-level permission role assignments for the Contact entity.
        // Source: NextPlugin.20190204.cs lines 1416–1432 (initial creation)
        // Confirmed: NextPlugin.20190206.cs lines 408–420 (entity update preserves same permissions)
        //
        // Permission model:
        //   - CanCreate: Regular user AND Administrator (both roles can create contacts)
        //   - CanRead:   Regular user AND Administrator (both roles can view contacts)
        //   - CanUpdate: Regular user AND Administrator (both roles can modify contacts)
        //   - CanDelete: Regular user AND Administrator (both roles can delete contacts)
        //
        // This is more permissive than the Case entity (where only Administrator can
        // create/update and no role can delete) — reflecting that contacts are commonly
        // managed by all CRM users.
        //
        // Role GUIDs are sourced from CrmEntityConstants to avoid duplication
        // across entity files, consistent with CaseEntity, CaseTypeEntity,
        // CaseStatusEntity, SalutationEntity, and AddressEntity patterns.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Record-level permission role assignments for the Contact entity.
        /// </summary>
        public static class Permissions
        {
            /// <summary>
            /// Roles permitted to create new contact records.
            /// Both the Regular user role and the Administrator role can create contacts.
            /// Source: entity.RecordPermissions.CanCreate lines 1422–1423
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanCreate = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to read/view contact records.
            /// Both the Regular user role and the Administrator role can read contacts.
            /// Source: entity.RecordPermissions.CanRead lines 1425–1426
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanRead = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to update existing contact records.
            /// Both the Regular user role and the Administrator role can update contacts.
            /// Source: entity.RecordPermissions.CanUpdate lines 1428–1429
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanUpdate = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles permitted to delete contact records.
            /// Both the Regular user role and the Administrator role can delete contacts.
            /// Source: entity.RecordPermissions.CanDelete lines 1431–1432
            /// </summary>
            public static readonly IReadOnlyList<Guid> CanDelete = new[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };
        }
    }
}
