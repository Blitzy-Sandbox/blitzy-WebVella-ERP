using System;

namespace WebVella.Erp.Service.Crm.Domain.Entities
{
    /// <summary>
    /// Static configuration class defining the CRM Account entity metadata,
    /// field identifiers, type options, default values, permission role assignments,
    /// and relation identifiers.
    ///
    /// Extracted from the monolith's Next plugin patch files:
    ///   - NextPlugin.20190203.cs (entity creation, name + l_scope fields, account_1n_case relation)
    ///   - NextPlugin.20190204.cs (all remaining fields, most relations)
    ///   - NextPlugin.20190206.cs (created_on, salutation_id fields replacing misspelled solutation_id,
    ///     salutation_1n_account relation replacing solutation_1n_account)
    ///
    /// All GUIDs are preserved character-for-character from the monolith source code to ensure
    /// zero-loss data migration compatibility during the monolith-to-microservices decomposition.
    ///
    /// The Account entity represents companies or persons in the CRM domain. Its type field
    /// differentiates between Company (value "1") and Person (value "2") records.
    /// </summary>
    public static class AccountEntity
    {
        // ---------------------------------------------------------------------------
        // Entity Metadata
        // ---------------------------------------------------------------------------
        // Core entity descriptor constants exactly as defined in NextPlugin.20190203.cs
        // lines 978–1017. These values drive entity registration, UI rendering,
        // and schema migration.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Unique identifier for the Account entity.
        /// Source: NextPlugin.20190203.cs line 985 — entity.Id = new Guid("2e22b50f-e444-4b62-a171-076e51246939")
        /// </summary>
        public static readonly Guid Id = new Guid("2e22b50f-e444-4b62-a171-076e51246939");

        /// <summary>
        /// Internal entity name used in database table naming (rec_account) and EQL queries.
        /// Source: NextPlugin.20190203.cs line 986 — entity.Name = "account"
        /// </summary>
        public const string Name = "account";

        /// <summary>
        /// Human-readable singular label for the Account entity.
        /// Source: NextPlugin.20190203.cs line 987 — entity.Label = "Account"
        /// </summary>
        public const string Label = "Account";

        /// <summary>
        /// Human-readable plural label for the Account entity.
        /// Source: NextPlugin.20190203.cs line 988 — entity.LabelPlural = "Accounts"
        /// </summary>
        public const string LabelPlural = "Accounts";

        /// <summary>
        /// Indicates the Account entity is a system entity that cannot be deleted by users.
        /// Source: NextPlugin.20190203.cs line 989 — entity.System = true
        /// </summary>
        public const bool IsSystem = true;

        /// <summary>
        /// Font Awesome icon class for the Account entity in the UI.
        /// Source: NextPlugin.20190203.cs line 990 — entity.IconName = "fas fa-user-tie"
        /// </summary>
        public const string IconName = "fas fa-user-tie";

        /// <summary>
        /// Theme color (hex) for the Account entity in the UI.
        /// Source: NextPlugin.20190203.cs line 991 — entity.Color = "#f44336"
        /// </summary>
        public const string Color = "#f44336";

        /// <summary>
        /// System-generated "id" field identifier from the systemFieldIdDictionary.
        /// Source: NextPlugin.20190203.cs line 984 — systemFieldIdDictionary["id"] = new Guid("4c0c80d0-8b01-445f-9913-0be18d9086d1")
        /// </summary>
        public static readonly Guid SystemFieldId = new Guid("4c0c80d0-8b01-445f-9913-0be18d9086d1");

        /// <summary>
        /// Field ID used as the record screen identifier (the "name" field).
        /// Source: NextPlugin.20190203.cs line 992 — entity.RecordScreenIdField = new Guid("b8be9afb-687c-411a-a274-ebe5d36a8100")
        /// </summary>
        public static readonly Guid RecordScreenIdField = new Guid("b8be9afb-687c-411a-a274-ebe5d36a8100");

        // ---------------------------------------------------------------------------
        // Field Identifiers
        // ---------------------------------------------------------------------------
        // Every field GUID for the Account entity, extracted from three patches.
        // The final state reflects all creates, updates, and deletions across patches.
        //
        // NOTE: The misspelled "solutation_id" field (4ace48d2-ece0-43a5-a04f-5a8e080c7428)
        // from 20190204 was DELETED in 20190206 and replaced with the correctly spelled
        // "salutation_id" field (dce30f5b-7c87-450e-a60a-757f758d9f62).
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Field identifier constants for every field on the Account entity.
        /// Each field ID is the exact GUID assigned during entity creation in the
        /// monolith's NextPlugin patch files.
        /// </summary>
        public static class Fields
        {
            /// <summary>
            /// Field: name (TextField, Required=true, System=true).
            /// The primary display name for the account record.
            /// Source: NextPlugin.20190203.cs line 1022
            /// </summary>
            public static readonly Guid NameFieldId = new Guid("b8be9afb-687c-411a-a274-ebe5d36a8100");

            /// <summary>
            /// Field: l_scope (TextField, Required=true after 20190206 update, System=true, Searchable=true).
            /// Scope classification field used for multi-tenant or context-based filtering.
            /// Source: NextPlugin.20190203.cs line 1052 (created), NextPlugin.20190206.cs line 182 (updated)
            /// </summary>
            public static readonly Guid LScopeFieldId = new Guid("fda3238e-52b5-48b7-82ad-558573c6e25c");

            /// <summary>
            /// Field: type (SelectField, Required=true, System=true, Searchable=true).
            /// Determines whether the account is a Company ("1") or Person ("2").
            /// Source: NextPlugin.20190204.cs line 19 — dropdownField.Id
            /// </summary>
            public static readonly Guid TypeFieldId = new Guid("7cab7793-1ae4-4c05-9191-4035a0d54bd1");

            /// <summary>
            /// Field: website (UrlField, Required=false, System=true).
            /// Account's website URL.
            /// Source: NextPlugin.20190204.cs line 53
            /// </summary>
            public static readonly Guid WebsiteFieldId = new Guid("df7114b5-49ad-400b-ae16-a6ed1daa8a0c");

            /// <summary>
            /// Field: street (TextField, Required=false, System=true).
            /// Primary street address line.
            /// Source: NextPlugin.20190204.cs line 84
            /// </summary>
            public static readonly Guid StreetFieldId = new Guid("1bc1ead8-2673-4cdd-b0f3-b99d4cf4fadc");

            /// <summary>
            /// Field: region (TextField, Required=false, System=true).
            /// Geographic region, state, or province.
            /// Source: NextPlugin.20190204.cs line 114
            /// </summary>
            public static readonly Guid RegionFieldId = new Guid("9c29b56d-2db2-47c6-bcf6-96cbe7187119");

            /// <summary>
            /// Field: post_code (TextField, Required=false, System=true).
            /// Postal or ZIP code.
            /// Source: NextPlugin.20190204.cs line 144
            /// </summary>
            public static readonly Guid PostCodeFieldId = new Guid("caaaf464-67b7-47b2-afec-beec03d90e4f");

            /// <summary>
            /// Field: fixed_phone (PhoneField, Required=false, System=true).
            /// Landline telephone number.
            /// Source: NextPlugin.20190204.cs line 174
            /// </summary>
            public static readonly Guid FixedPhoneFieldId = new Guid("f51f7451-b9f1-4a5a-a282-3d83525a9094");

            /// <summary>
            /// Field: mobile_phone (PhoneField, Required=false, System=true).
            /// Mobile telephone number.
            /// Source: NextPlugin.20190204.cs line 205
            /// </summary>
            public static readonly Guid MobilePhoneFieldId = new Guid("01e8d8e6-457b-49c8-9194-81f06bd9f8ed");

            /// <summary>
            /// Field: fax_phone (PhoneField, Required=false, System=true).
            /// Fax telephone number.
            /// Source: NextPlugin.20190204.cs line 237
            /// </summary>
            public static readonly Guid FaxPhoneFieldId = new Guid("8f6bbfac-8f10-4023-b2b0-af03d22b9cef");

            /// <summary>
            /// Field: notes (MultiLineTextField, Required=false, System=true).
            /// Free-text notes or description for the account.
            /// Source: NextPlugin.20190204.cs line 267
            /// </summary>
            public static readonly Guid NotesFieldId = new Guid("d2c7a984-c173-434f-a711-1f1efa07f0c1");

            /// <summary>
            /// Field: last_name (TextField, Required=true, System=true).
            /// Last name, primarily used when account type is Person.
            /// Source: NextPlugin.20190204.cs line 298
            /// </summary>
            public static readonly Guid LastNameFieldId = new Guid("c9da8e17-9511-4f2c-8576-8756f34a17b9");

            /// <summary>
            /// Field: first_name (TextField, Required=true, System=true).
            /// First name, primarily used when account type is Person.
            /// Source: NextPlugin.20190204.cs line 328
            /// </summary>
            public static readonly Guid FirstNameFieldId = new Guid("66de2df4-f42a-4bc9-817d-8960578a8302");

            /// <summary>
            /// Field: x_search (TextField, Required=true, Searchable=true, System=true).
            /// Composite search index field regenerated by SearchService.
            /// Source: NextPlugin.20190204.cs line 358 (created), NextPlugin.20190206.cs line 151 (updated)
            /// </summary>
            public static readonly Guid XSearchFieldId = new Guid("d8ce135d-f6c4-45b7-a543-c58e154c06df");

            /// <summary>
            /// Field: email (EmailField, Required=false, Searchable=true, System=true).
            /// Primary email address for the account.
            /// Source: NextPlugin.20190204.cs line 388
            /// </summary>
            public static readonly Guid EmailFieldId = new Guid("25dcf767-2e12-4413-b096-60d37700194f");

            /// <summary>
            /// Field: city (TextField, Required=false, System=true).
            /// City or town for the account's address.
            /// Source: NextPlugin.20190204.cs line 418
            /// </summary>
            public static readonly Guid CityFieldId = new Guid("4e18d041-0daf-4db4-9bd9-6d5b631af0bd");

            /// <summary>
            /// Field: country_id (GuidField, Required=false, System=true).
            /// Foreign key reference to the country entity (owned by Core service).
            /// Source: NextPlugin.20190204.cs line 448
            /// </summary>
            public static readonly Guid CountryIdFieldId = new Guid("76c1d754-8bf5-4a78-a2d7-bf771e1b032b");

            /// <summary>
            /// Field: tax_id (TextField, Required=false, System=true).
            /// Tax identification number for the account.
            /// Source: NextPlugin.20190204.cs line 478
            /// </summary>
            public static readonly Guid TaxIdFieldId = new Guid("c4bbc47c-2dc0-4c24-9159-1b5a6bfa8ed3");

            /// <summary>
            /// Field: street_2 (TextField, Required=false, System=true).
            /// Secondary street address line.
            /// Source: NextPlugin.20190204.cs line 538
            /// </summary>
            public static readonly Guid Street2FieldId = new Guid("8829ff72-2910-40a8-834d-5f05c51c8d2f");

            /// <summary>
            /// Field: language_id (GuidField, Required=false, System=true).
            /// Foreign key reference to the language entity (owned by Core service).
            /// Source: NextPlugin.20190204.cs line 568
            /// </summary>
            public static readonly Guid LanguageIdFieldId = new Guid("02b796b4-2b7a-4662-8a16-01dbffdd1ba1");

            /// <summary>
            /// Field: currency_id (GuidField, Required=false, System=true).
            /// Foreign key reference to the currency entity (owned by Core service).
            /// Source: NextPlugin.20190204.cs line 598
            /// </summary>
            public static readonly Guid CurrencyIdFieldId = new Guid("c2a2a490-951d-4395-b359-0dc88ad56c11");

            /// <summary>
            /// Field: created_on (DateTimeField, Required=true, System=true, UseCurrentTimeAsDefaultValue=true).
            /// Timestamp of account record creation.
            /// Source: NextPlugin.20190206.cs line 89
            /// </summary>
            public static readonly Guid CreatedOnFieldId = new Guid("48a33ffe-d5e4-4fa1-b74c-272733201652");

            /// <summary>
            /// Field: salutation_id (GuidField, Required=true, System=true).
            /// Foreign key reference to the salutation lookup entity.
            /// NOTE: This is the CORRECTED field replacing the misspelled "solutation_id"
            /// (4ace48d2-ece0-43a5-a04f-5a8e080c7428) which was deleted in NextPlugin.20190206.cs line 38.
            /// Source: NextPlugin.20190206.cs line 120
            /// </summary>
            public static readonly Guid SalutationIdFieldId = new Guid("dce30f5b-7c87-450e-a60a-757f758d9f62");
        }

        // ---------------------------------------------------------------------------
        // Type Select Field Options
        // ---------------------------------------------------------------------------
        // The "type" select field differentiates between Company and Person accounts.
        // Option values as defined in NextPlugin.20190204.cs lines 31–35.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// String option values for the Account "type" select field.
        /// These map directly to the SelectOption values defined in the monolith.
        /// </summary>
        public static class TypeOptions
        {
            /// <summary>
            /// Account type value representing a company/organization.
            /// SelectOption: Label = "Company", Value = "1"
            /// </summary>
            public const string Company = "1";

            /// <summary>
            /// Account type value representing an individual person.
            /// SelectOption: Label = "Person", Value = "2"
            /// </summary>
            public const string Person = "2";
        }

        // ---------------------------------------------------------------------------
        // Default Values
        // ---------------------------------------------------------------------------
        // Default field values that reference lookup entity records.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Default value constants for Account fields that reference lookup entities.
        /// </summary>
        public static class Defaults
        {
            /// <summary>
            /// Default salutation record identifier applied to new account records.
            /// Corresponds to the default salutation lookup record (e.g., "Mr." or empty/generic).
            /// Source: NextPlugin.20190206.cs line 131 — guidField.DefaultValue = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698")
            /// </summary>
            public static readonly Guid DefaultSalutationId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");
        }

        // ---------------------------------------------------------------------------
        // Record Permissions
        // ---------------------------------------------------------------------------
        // Role-based access control arrays exactly as defined in NextPlugin.20190203.cs
        // lines 993–1008. Uses shared role GUIDs from CrmEntityConstants for
        // consistency across all CRM entity definitions.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Permission role arrays for Account record CRUD operations.
        /// CanCreate, CanRead, CanUpdate include both Regular and Administrator roles.
        /// CanDelete is restricted to Administrator role only.
        /// </summary>
        public static class Permissions
        {
            /// <summary>
            /// Roles allowed to create Account records.
            /// Source: NextPlugin.20190203.cs lines 999–1000
            /// </summary>
            public static readonly Guid[] CanCreate = new Guid[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles allowed to read Account records.
            /// Source: NextPlugin.20190203.cs lines 1002–1003
            /// </summary>
            public static readonly Guid[] CanRead = new Guid[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles allowed to update Account records.
            /// Source: NextPlugin.20190203.cs lines 1005–1006
            /// </summary>
            public static readonly Guid[] CanUpdate = new Guid[]
            {
                CrmEntityConstants.RegularRoleId,
                CrmEntityConstants.AdministratorRoleId
            };

            /// <summary>
            /// Roles allowed to delete Account records.
            /// Only Administrator role has delete permission.
            /// Source: NextPlugin.20190203.cs line 1008
            /// </summary>
            public static readonly Guid[] CanDelete = new Guid[]
            {
                CrmEntityConstants.AdministratorRoleId
            };
        }

        // ---------------------------------------------------------------------------
        // Relation Identifiers
        // ---------------------------------------------------------------------------
        // Every relation involving the Account entity across all three patches.
        // The final-state relations after applying all patches are:
        //
        //   - account_1n_case:        Created 20190203, deleted 20190204 — preserved for migration reference
        //   - account_nn_contact:     Created 20190204 (M:N between account and contact)
        //   - currency_1n_account:    Created 20190204 (currency → account via currency_id)
        //   - account_nn_case:        Created 20190204 (M:N replacement for the deleted 1:N)
        //   - address_nn_account:     Created 20190204 (M:N between address and account)
        //   - country_1n_account:     Created 20190204 (country → account via country_id)
        //   - language_1n_account:    Created 20190204 (language → account via language_id)
        //   - salutation_1n_account:  Created 20190206 (replaces deleted solutation_1n_account)
        //
        // Cross-service note: country, language, and currency entities are owned by the
        // Core service. These relation IDs are preserved here because the target entity
        // (account) belongs to the CRM service.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Relation identifier and name constants for all Account entity relationships.
        /// Each relation has an Id (GUID) and a Name (string) matching the monolith's
        /// EntityRelation.Name property.
        /// </summary>
        public static class Relations
        {
            /// <summary>
            /// Account → Case one-to-many relation identifier.
            /// Originally created in NextPlugin.20190203.cs line 5110 as a 1:N relation.
            /// Deleted in NextPlugin.20190204.cs line 2219 and replaced by account_nn_case (M:N).
            /// Preserved here as a migration-compatibility constant for historical data references.
            /// </summary>
            public static readonly Guid Account1nCaseId = new Guid("06d07760-41ba-408c-af61-a1fdc8493de3");

            /// <summary>
            /// Canonical name for the original account → case one-to-many relation.
            /// </summary>
            public const string Account1nCaseName = "account_1n_case";

            /// <summary>
            /// Account ↔ Contact many-to-many relation identifier.
            /// Links account records to contact records.
            /// Origin: Account entity (2e22b50f-...) ↔ Target: Contact entity (39e1dd9b-...)
            /// Source: NextPlugin.20190204.cs line 2243
            /// </summary>
            public static readonly Guid AccountNnContactId = new Guid("dd211c99-5415-4195-923a-cb5a56e5d544");

            /// <summary>
            /// Canonical name for the account ↔ contact many-to-many relation.
            /// </summary>
            public const string AccountNnContactName = "account_nn_contact";

            /// <summary>
            /// Currency → Account one-to-many relation identifier.
            /// Links Core service's currency entity to CRM account records via account.currency_id.
            /// Origin: Currency entity (4d049df9-...) → Target: Account entity (2e22b50f-...)
            /// Source: NextPlugin.20190204.cs line 2301
            /// </summary>
            public static readonly Guid Currency1nAccountId = new Guid("5e5c17df-2f50-4f88-82f1-d76cb7cd6156");

            /// <summary>
            /// Canonical name for the currency → account one-to-many relation.
            /// </summary>
            public const string Currency1nAccountName = "currency_1n_account";

            /// <summary>
            /// Account ↔ Case many-to-many relation identifier.
            /// Replaces the original account_1n_case (1:N) relation deleted in 20190204.
            /// Origin: Account entity (2e22b50f-...) ↔ Target: Case entity (0ebb3981-...)
            /// Source: NextPlugin.20190204.cs line 2359
            /// </summary>
            public static readonly Guid AccountNnCaseId = new Guid("3690c12e-40e1-4e8f-a0a8-27221c686b43");

            /// <summary>
            /// Canonical name for the account ↔ case many-to-many relation.
            /// </summary>
            public const string AccountNnCaseName = "account_nn_case";

            /// <summary>
            /// Address ↔ Account many-to-many relation identifier.
            /// Links address records to account records.
            /// Origin: Address entity (34a126ba-...) ↔ Target: Account entity (2e22b50f-...)
            /// Source: NextPlugin.20190204.cs line 2417
            /// </summary>
            public static readonly Guid AddressNnAccountId = new Guid("dcf76eb5-16cf-466d-b760-c0d8ae57da94");

            /// <summary>
            /// Canonical name for the address ↔ account many-to-many relation.
            /// </summary>
            public const string AddressNnAccountName = "address_nn_account";

            /// <summary>
            /// Country → Account one-to-many relation identifier.
            /// Links Core service's country entity to CRM account records via account.country_id.
            /// Origin: Country entity (54cfe9e9-...) → Target: Account entity (2e22b50f-...)
            /// Source: NextPlugin.20190204.cs line 2475
            /// </summary>
            public static readonly Guid Country1nAccountId = new Guid("66661380-49f8-4a50-b0d9-4d2a8d2f0990");

            /// <summary>
            /// Canonical name for the country → account one-to-many relation.
            /// </summary>
            public const string Country1nAccountName = "country_1n_account";

            /// <summary>
            /// Language → Account one-to-many relation identifier.
            /// Links Core service's language entity to CRM account records via account.language_id.
            /// Origin: Language entity (f22c806a-...) → Target: Account entity (2e22b50f-...)
            /// Source: NextPlugin.20190204.cs line 2504
            /// </summary>
            public static readonly Guid Language1nAccountId = new Guid("6e7f99d8-712c-451d-80fc-3a5fba4580f4");

            /// <summary>
            /// Canonical name for the language → account one-to-many relation.
            /// </summary>
            public const string Language1nAccountName = "language_1n_account";

            /// <summary>
            /// Salutation → Account one-to-many relation identifier.
            /// Links salutation lookup entity to CRM account records via account.salutation_id.
            /// Origin: Salutation entity (690dc799-...) → Target: Account entity (2e22b50f-...)
            /// NOTE: This is the CORRECTED relation replacing the misspelled "solutation_1n_account"
            /// (66f62cd6-174c-4a0b-b56f-6356b24bd73d) which was deleted in NextPlugin.20190206.cs line 28.
            /// Source: NextPlugin.20190206.cs line 1341
            /// </summary>
            public static readonly Guid Salutation1nAccountId = new Guid("99e1a18b-05c2-4fca-986e-37ecebd62168");

            /// <summary>
            /// Canonical name for the salutation → account one-to-many relation.
            /// </summary>
            public const string Salutation1nAccountName = "salutation_1n_account";
        }
    }
}
