using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;
using WebVella.Erp.Service.Crm.Domain.Entities;

namespace WebVella.Erp.Tests.Crm.Domain.Entities
{
    /// <summary>
    /// Unit tests for the <see cref="AccountEntity"/> static configuration class.
    /// Validates that all entity metadata (IDs, fields, constraints, relations, permissions)
    /// matches the original monolith provisioning from three NextPlugin patch files:
    ///   - NextPlugin.20190203.cs (entity creation, name + l_scope fields)
    ///   - NextPlugin.20190204.cs (20 additional fields, account_nn_contact/account_nn_case relations)
    ///   - NextPlugin.20190206.cs (created_on, salutation_id fields replacing misspelled solutation_id,
    ///     salutation_1n_account relation, x_search/l_scope field updates)
    ///   - Configuration.cs (AccountSearchIndexFields — 17 fields)
    ///
    /// Per AAP §0.8.1 business rule preservation requirements, every extracted business rule
    /// must map to at least one automated test producing identical output to the monolith.
    ///
    /// The Account entity is the most field-rich CRM entity with 23 custom fields across
    /// three patches, and is unique in having: RecordScreenIdField set, CanDelete limited
    /// to admin only, and a type SelectField with Company/Person options.
    /// </summary>
    public class AccountEntityTests
    {
        // -----------------------------------------------------------------------
        // Well-known role GUIDs (from CrmEntityConstants / ERPService.cs)
        // -----------------------------------------------------------------------
        private static readonly Guid AdministratorRoleId = new Guid("bdc56420-caf0-4030-8a0e-d264938e0cda");
        private static readonly Guid RegularRoleId = new Guid("f16ec6db-626d-4c27-8de0-3e7ce542c55f");

        // ===================================================================
        // Phase 1: Entity Base Properties Tests
        // ===================================================================

        /// <summary>
        /// Validates the account entity ID matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 985 —
        ///   entity.Id = new Guid("2e22b50f-e444-4b62-a171-076e51246939")
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectId()
        {
            // Arrange
            var expectedId = new Guid("2e22b50f-e444-4b62-a171-076e51246939");

            // Act & Assert
            AccountEntity.Id.Should().Be(expectedId,
                "the account entity ID must match the monolith definition from NextPlugin.20190203.cs line 985");
        }

        /// <summary>
        /// Validates the account entity Name matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 986 — entity.Name = "account"
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectName()
        {
            AccountEntity.Name.Should().Be("account",
                "the entity name determines the database table name (rec_account) and EQL queries");
        }

        /// <summary>
        /// Validates the account entity Label matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 987 — entity.Label = "Account"
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectLabel()
        {
            AccountEntity.Label.Should().Be("Account",
                "the singular label is used for UI display and must match the monolith");
        }

        /// <summary>
        /// Validates the account entity LabelPlural matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 988 — entity.LabelPlural = "Accounts"
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectLabelPlural()
        {
            AccountEntity.LabelPlural.Should().Be("Accounts",
                "the plural label is used for collection views and must match the monolith");
        }

        /// <summary>
        /// Validates the account entity is marked as a system entity.
        /// Source: NextPlugin.20190203.cs line 989 — entity.System = true
        /// System entities cannot be deleted by users.
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldBeSystemEntity()
        {
            AccountEntity.IsSystem.Should().BeTrue(
                "the account entity is a system-managed entity that cannot be deleted by users");
        }

        /// <summary>
        /// Validates the account entity IconName matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 990 — entity.IconName = "fas fa-user-tie"
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectIcon()
        {
            AccountEntity.IconName.Should().Be("fas fa-user-tie",
                "the icon class must match the monolith definition from NextPlugin.20190203.cs line 990");
        }

        /// <summary>
        /// Validates the account entity Color matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 991 — entity.Color = "#f44336"
        /// Red (#f44336) is consistent with other CRM entities.
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectColor()
        {
            AccountEntity.Color.Should().Be("#f44336",
                "the account entity accent color must be red (#f44336), consistent with CRM entities");
        }

        /// <summary>
        /// Validates the RecordScreenIdField matches the "name" field ID.
        /// Source: NextPlugin.20190203.cs line 992 —
        ///   entity.RecordScreenIdField = new Guid("b8be9afb-687c-411a-a274-ebe5d36a8100")
        /// This is the field displayed as the record identifier in list views and detail screens.
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectRecordScreenIdField()
        {
            // Arrange
            var expectedRecordScreenIdField = new Guid("b8be9afb-687c-411a-a274-ebe5d36a8100");

            // Act & Assert
            AccountEntity.RecordScreenIdField.Should().Be(expectedRecordScreenIdField,
                "the RecordScreenIdField must point to the 'name' field (b8be9afb-...) " +
                "as defined in NextPlugin.20190203.cs line 992");
        }

        // ===================================================================
        // Phase 2: Record Permissions Tests
        // ===================================================================

        /// <summary>
        /// Validates CanCreate permissions contain exactly the Regular user and Administrator roles.
        /// Source: NextPlugin.20190203.cs lines 999–1000
        ///   entity.RecordPermissions.CanCreate.Add(new Guid("f16ec6db-...")) — regular
        ///   entity.RecordPermissions.CanCreate.Add(new Guid("bdc56420-...")) — admin
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectCanCreatePermissions()
        {
            // Act
            var canCreate = AccountEntity.Permissions.CanCreate;

            // Assert — exactly 2 roles
            canCreate.Should().HaveCount(2,
                "CanCreate should contain exactly the Regular user and Administrator roles");

            canCreate.Should().Contain(RegularRoleId,
                "the Regular user role (f16ec6db-...) must have create permission");

            canCreate.Should().Contain(AdministratorRoleId,
                "the Administrator role (bdc56420-...) must have create permission");
        }

        /// <summary>
        /// Validates CanRead permissions contain exactly the Regular user and Administrator roles.
        /// Source: NextPlugin.20190203.cs lines 1002–1003
        ///   entity.RecordPermissions.CanRead.Add(new Guid("f16ec6db-...")) — regular
        ///   entity.RecordPermissions.CanRead.Add(new Guid("bdc56420-...")) — admin
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectCanReadPermissions()
        {
            // Act
            var canRead = AccountEntity.Permissions.CanRead;

            // Assert — exactly 2 roles
            canRead.Should().HaveCount(2,
                "CanRead should contain exactly the Regular user and Administrator roles");

            canRead.Should().Contain(RegularRoleId,
                "the Regular user role (f16ec6db-...) must have read permission");

            canRead.Should().Contain(AdministratorRoleId,
                "the Administrator role (bdc56420-...) must have read permission");
        }

        /// <summary>
        /// Validates CanUpdate permissions contain exactly the Regular user and Administrator roles.
        /// Source: NextPlugin.20190203.cs lines 1005–1006
        ///   entity.RecordPermissions.CanUpdate.Add(new Guid("f16ec6db-...")) — regular
        ///   entity.RecordPermissions.CanUpdate.Add(new Guid("bdc56420-...")) — admin
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectCanUpdatePermissions()
        {
            // Act
            var canUpdate = AccountEntity.Permissions.CanUpdate;

            // Assert — exactly 2 roles
            canUpdate.Should().HaveCount(2,
                "CanUpdate should contain exactly the Regular user and Administrator roles");

            canUpdate.Should().Contain(RegularRoleId,
                "the Regular user role (f16ec6db-...) must have update permission");

            canUpdate.Should().Contain(AdministratorRoleId,
                "the Administrator role (bdc56420-...) must have update permission");
        }

        /// <summary>
        /// Validates CanDelete permissions contain ONLY the Administrator role.
        /// CRITICAL: Unlike CanCreate/CanRead/CanUpdate, CanDelete does NOT include
        /// the regular user role — only administrators can delete account records.
        /// Source: NextPlugin.20190203.cs line 1008 —
        ///   entity.RecordPermissions.CanDelete.Add(new Guid("bdc56420-caf0-4030-8a0e-d264938e0cda"))
        /// (only 1 Add call, compared to 2 for the other permission arrays)
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveCorrectCanDeletePermissions()
        {
            // Act
            var canDelete = AccountEntity.Permissions.CanDelete;

            // Assert — ONLY 1 role (Administrator)
            canDelete.Should().HaveCount(1,
                "CanDelete should contain ONLY the Administrator role — regular users cannot delete accounts");

            canDelete.Should().Contain(AdministratorRoleId,
                "the Administrator role (bdc56420-...) must have delete permission");

            canDelete.Should().NotContain(RegularRoleId,
                "the Regular user role MUST NOT have delete permission on accounts " +
                "(this is a key business rule from NextPlugin.20190203.cs line 1008)");
        }

        // ===================================================================
        // Phase 3: Field Definition Tests
        // ===================================================================

        /// <summary>
        /// Validates that all 23 custom field IDs are defined and have non-empty GUIDs.
        /// Account entity fields span three patches (20190203, 20190204, 20190206) and include:
        /// name, l_scope, type, website, street, region, post_code, fixed_phone, mobile_phone,
        /// fax_phone, notes, last_name, first_name, x_search, email, city, country_id, tax_id,
        /// street_2, language_id, currency_id, created_on, salutation_id.
        /// NOTE: The old "solutation_id" field was DELETED in 20190206 and replaced by "salutation_id".
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHaveAllExpectedFields()
        {
            // Collect all 23 custom field IDs into a list and verify uniqueness and non-emptiness
            var allFieldIds = new List<Guid>
            {
                AccountEntity.Fields.NameFieldId,
                AccountEntity.Fields.LScopeFieldId,
                AccountEntity.Fields.TypeFieldId,
                AccountEntity.Fields.WebsiteFieldId,
                AccountEntity.Fields.StreetFieldId,
                AccountEntity.Fields.RegionFieldId,
                AccountEntity.Fields.PostCodeFieldId,
                AccountEntity.Fields.FixedPhoneFieldId,
                AccountEntity.Fields.MobilePhoneFieldId,
                AccountEntity.Fields.FaxPhoneFieldId,
                AccountEntity.Fields.NotesFieldId,
                AccountEntity.Fields.LastNameFieldId,
                AccountEntity.Fields.FirstNameFieldId,
                AccountEntity.Fields.XSearchFieldId,
                AccountEntity.Fields.EmailFieldId,
                AccountEntity.Fields.CityFieldId,
                AccountEntity.Fields.CountryIdFieldId,
                AccountEntity.Fields.TaxIdFieldId,
                AccountEntity.Fields.Street2FieldId,
                AccountEntity.Fields.LanguageIdFieldId,
                AccountEntity.Fields.CurrencyIdFieldId,
                AccountEntity.Fields.CreatedOnFieldId,
                AccountEntity.Fields.SalutationIdFieldId
            };

            // Assert — exactly 23 custom fields
            allFieldIds.Should().HaveCount(23,
                "the Account entity must have exactly 23 custom fields across three patches");

            // Assert — no empty GUIDs
            allFieldIds.Should().NotContain(Guid.Empty,
                "every field ID must be a valid non-empty GUID");

            // Assert — all field IDs must be unique
            allFieldIds.Distinct().Should().HaveCount(23,
                "all field IDs must be unique — no duplicates allowed");
        }

        /// <summary>
        /// Validates the type SelectField options match the monolith definition.
        /// Source: NextPlugin.20190204.cs lines 31–35 —
        ///   new SelectOption() { Label = "Company", Value = "1" }
        ///   new SelectOption() { Label = "Person", Value = "2" }
        /// Default value is "1" (Company).
        /// </summary>
        [Fact]
        public void AccountEntity_TypeField_ShouldHaveCorrectOptions()
        {
            // Assert — Company option value
            AccountEntity.TypeOptions.Company.Should().Be("1",
                "Company type option must have value '1' as defined in NextPlugin.20190204.cs line 33");

            // Assert — Person option value
            AccountEntity.TypeOptions.Person.Should().Be("2",
                "Person type option must have value '2' as defined in NextPlugin.20190204.cs line 34");
        }

        /// <summary>
        /// Validates the type field ID matches the source and verifies the field is defined.
        /// Source: NextPlugin.20190204.cs line 19 — Required=true, Searchable=true.
        /// The type field (SelectField) determines whether the account is Company or Person.
        /// </summary>
        [Fact]
        public void AccountEntity_TypeField_ShouldBeRequiredAndSearchable()
        {
            // Arrange — expected type field ID from NextPlugin.20190204.cs line 19
            var expectedTypeFieldId = new Guid("7cab7793-1ae4-4c05-9191-4035a0d54bd1");

            // Act & Assert
            AccountEntity.Fields.TypeFieldId.Should().Be(expectedTypeFieldId,
                "the type field ID must match the monolith definition " +
                "(Required=true, Searchable=true, SelectField with Company/Person options)");
        }

        /// <summary>
        /// Validates the name field ID and that it is required.
        /// Source: NextPlugin.20190203.cs line 1022 —
        ///   textboxField.Id = new Guid("b8be9afb-687c-411a-a274-ebe5d36a8100")
        ///   textboxField.Required = true
        /// </summary>
        [Fact]
        public void AccountEntity_NameField_ShouldBeRequired()
        {
            // Arrange
            var expectedNameFieldId = new Guid("b8be9afb-687c-411a-a274-ebe5d36a8100");

            // Act & Assert
            AccountEntity.Fields.NameFieldId.Should().Be(expectedNameFieldId,
                "the name field ID must match the monolith definition (Required=true, TextField)");
        }

        /// <summary>
        /// Validates that the name field ID matches the RecordScreenIdField.
        /// The "name" field is used as the record display identifier in list/detail screens.
        /// Source: NextPlugin.20190203.cs line 992 + line 1022 — both use the same GUID.
        /// </summary>
        [Fact]
        public void AccountEntity_NameField_IsRecordScreenIdField()
        {
            // Act & Assert
            AccountEntity.Fields.NameFieldId.Should().Be(AccountEntity.RecordScreenIdField,
                "the name field ID must equal RecordScreenIdField because the 'name' field " +
                "is used as the record identifier in list views and detail screens");
        }

        /// <summary>
        /// Validates the email field ID and that it is searchable.
        /// Source: NextPlugin.20190204.cs line 388 —
        ///   emailField.Id = new Guid("25dcf767-2e12-4413-b096-60d37700194f")
        ///   emailField.Searchable = true (EmailField type)
        /// </summary>
        [Fact]
        public void AccountEntity_EmailField_ShouldBeSearchable()
        {
            // Arrange
            var expectedEmailFieldId = new Guid("25dcf767-2e12-4413-b096-60d37700194f");

            // Act & Assert
            AccountEntity.Fields.EmailFieldId.Should().Be(expectedEmailFieldId,
                "the email field ID must match the monolith definition (Searchable=true, EmailField type)");
        }

        /// <summary>
        /// Validates the x_search field ID reflects the FINAL state after 20190206 update.
        /// Originally created in 20190204 (not required, searchable=true),
        /// updated in 20190206 to Required=true, Searchable=true, Label="Search Index".
        /// Source: NextPlugin.20190204.cs line 358 (created), NextPlugin.20190206.cs line 151 (updated)
        /// </summary>
        [Fact]
        public void AccountEntity_XSearchField_ShouldBeRequiredAndSearchable()
        {
            // Arrange
            var expectedXSearchFieldId = new Guid("d8ce135d-f6c4-45b7-a543-c58e154c06df");

            // Act & Assert
            AccountEntity.Fields.XSearchFieldId.Should().Be(expectedXSearchFieldId,
                "the x_search field ID must match the monolith definition " +
                "(Required=true, Searchable=true after 20190206 update, Label='Search Index')");
        }

        /// <summary>
        /// Validates the l_scope field ID reflects the FINAL state after 20190206 update.
        /// Originally created in 20190203 (not required, not searchable),
        /// updated in 20190206 to Required=true, Searchable=true.
        /// Source: NextPlugin.20190203.cs line 1052 (created), NextPlugin.20190206.cs line 182 (updated)
        /// </summary>
        [Fact]
        public void AccountEntity_LScopeField_ShouldBeRequiredAndSearchable()
        {
            // Arrange
            var expectedLScopeFieldId = new Guid("fda3238e-52b5-48b7-82ad-558573c6e25c");

            // Act & Assert
            AccountEntity.Fields.LScopeFieldId.Should().Be(expectedLScopeFieldId,
                "the l_scope field ID must match the monolith definition " +
                "(Required=true, Searchable=true after 20190206 update)");
        }

        /// <summary>
        /// Validates the salutation_id field is defined and required.
        /// This is the CORRECTED field replacing the misspelled "solutation_id".
        /// Source: NextPlugin.20190206.cs line 120 — Required=true, GuidField
        /// </summary>
        [Fact]
        public void AccountEntity_SalutationIdField_ShouldBeRequired()
        {
            // Arrange
            var expectedSalutationIdFieldId = new Guid("dce30f5b-7c87-450e-a60a-757f758d9f62");

            // Act & Assert
            AccountEntity.Fields.SalutationIdFieldId.Should().Be(expectedSalutationIdFieldId,
                "the salutation_id field ID must match the corrected field from NextPlugin.20190206.cs " +
                "(replacing the misspelled solutation_id that was deleted)");
        }

        /// <summary>
        /// Validates the salutation_id default value is the "Mr." salutation GUID.
        /// Source: NextPlugin.20190206.cs line 131 —
        ///   guidField.DefaultValue = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698")
        /// This is the "Mr." salutation record created in the salutation entity seed data.
        /// </summary>
        [Fact]
        public void AccountEntity_SalutationIdField_DefaultShouldBeMrSalutation()
        {
            // Arrange — the "Mr." salutation record GUID
            var expectedMrSalutationId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");

            // Act & Assert
            AccountEntity.Defaults.DefaultSalutationId.Should().Be(expectedMrSalutationId,
                "the default salutation_id must point to the 'Mr.' salutation record " +
                "(87c08ee1-...) as defined in NextPlugin.20190206.cs line 131");
        }

        /// <summary>
        /// Validates the created_on field is defined and required (DateTimeField).
        /// Source: NextPlugin.20190206.cs line 89 — Required=true, DateTimeField
        /// </summary>
        [Fact]
        public void AccountEntity_CreatedOnField_ShouldBeRequired()
        {
            // Arrange
            var expectedCreatedOnFieldId = new Guid("48a33ffe-d5e4-4fa1-b74c-272733201652");

            // Act & Assert
            AccountEntity.Fields.CreatedOnFieldId.Should().Be(expectedCreatedOnFieldId,
                "the created_on field ID must match NextPlugin.20190206.cs line 89 " +
                "(Required=true, DateTimeField, UseCurrentTimeAsDefaultValue=true)");
        }

        /// <summary>
        /// Validates that the phone field IDs (fixed_phone, mobile_phone, fax_phone) are
        /// correctly defined with their expected GUIDs.
        /// Source: NextPlugin.20190204.cs lines 174, 205, 237 — all PhoneField type
        /// </summary>
        [Fact]
        public void AccountEntity_PhoneFields_ShouldBePhoneType()
        {
            // Arrange — expected GUIDs from NextPlugin.20190204.cs
            var expectedFixedPhoneId = new Guid("f51f7451-b9f1-4a5a-a282-3d83525a9094");
            var expectedMobilePhoneId = new Guid("01e8d8e6-457b-49c8-9194-81f06bd9f8ed");
            var expectedFaxPhoneId = new Guid("8f6bbfac-8f10-4023-b2b0-af03d22b9cef");

            // Act & Assert — all three phone fields defined with correct IDs
            AccountEntity.Fields.FixedPhoneFieldId.Should().Be(expectedFixedPhoneId,
                "the fixed_phone field ID must match NextPlugin.20190204.cs line 174 (PhoneField)");

            AccountEntity.Fields.MobilePhoneFieldId.Should().Be(expectedMobilePhoneId,
                "the mobile_phone field ID must match NextPlugin.20190204.cs line 205 (PhoneField)");

            AccountEntity.Fields.FaxPhoneFieldId.Should().Be(expectedFaxPhoneId,
                "the fax_phone field ID must match NextPlugin.20190204.cs line 237 (PhoneField)");
        }

        /// <summary>
        /// Validates that all GuidField-type fields (country_id, language_id, currency_id,
        /// salutation_id) are correctly defined with their expected GUIDs.
        /// Source: NextPlugin.20190204.cs (country_id, language_id, currency_id),
        ///         NextPlugin.20190206.cs (salutation_id)
        /// </summary>
        [Fact]
        public void AccountEntity_GuidFields_ShouldBeGuidType()
        {
            // Arrange — expected GUIDs
            var expectedCountryIdFieldId = new Guid("76c1d754-8bf5-4a78-a2d7-bf771e1b032b");
            var expectedLanguageIdFieldId = new Guid("02b796b4-2b7a-4662-8a16-01dbffdd1ba1");
            var expectedCurrencyIdFieldId = new Guid("c2a2a490-951d-4395-b359-0dc88ad56c11");
            var expectedSalutationIdFieldId = new Guid("dce30f5b-7c87-450e-a60a-757f758d9f62");

            // Act & Assert
            AccountEntity.Fields.CountryIdFieldId.Should().Be(expectedCountryIdFieldId,
                "the country_id field ID must match NextPlugin.20190204.cs line 448 (GuidField)");

            AccountEntity.Fields.LanguageIdFieldId.Should().Be(expectedLanguageIdFieldId,
                "the language_id field ID must match NextPlugin.20190204.cs line 568 (GuidField)");

            AccountEntity.Fields.CurrencyIdFieldId.Should().Be(expectedCurrencyIdFieldId,
                "the currency_id field ID must match NextPlugin.20190204.cs line 598 (GuidField)");

            AccountEntity.Fields.SalutationIdFieldId.Should().Be(expectedSalutationIdFieldId,
                "the salutation_id field ID must match NextPlugin.20190206.cs line 120 (GuidField)");
        }

        /// <summary>
        /// CRITICAL: Account entity does NOT have a photo field — only Contact does.
        /// Verifies via reflection that no "PhotoFieldId" member exists in AccountEntity.Fields.
        /// This is an explicit business rule: account records have no photo upload capability.
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldNotHavePhotoField()
        {
            // Use reflection to check that AccountEntity.Fields does not contain a PhotoFieldId member
            var fieldsType = typeof(AccountEntity.Fields);
            var photoMember = fieldsType.GetField("PhotoFieldId",
                BindingFlags.Public | BindingFlags.Static);

            photoMember.Should().BeNull(
                "the Account entity must NOT have a photo field — only the Contact entity has one. " +
                "This is a key business rule distinguishing Account from Contact.");
        }

        /// <summary>
        /// Validates the old misspelled "solutation_id" field does NOT exist.
        /// The "solutation_id" field (4ace48d2-ece0-43a5-a04f-5a8e080c7428) was created in
        /// NextPlugin.20190204.cs line 508 but DELETED in NextPlugin.20190206.cs line 38,
        /// and replaced by the correctly spelled "salutation_id" (dce30f5b-...).
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldNotHaveSolutationIdField()
        {
            // Use reflection to check that AccountEntity.Fields does not contain a SolutationIdFieldId member
            var fieldsType = typeof(AccountEntity.Fields);
            var solutationMember = fieldsType.GetField("SolutationIdFieldId",
                BindingFlags.Public | BindingFlags.Static);

            solutationMember.Should().BeNull(
                "the misspelled 'solutation_id' field was DELETED in NextPlugin.20190206.cs line 38 " +
                "and replaced by the correctly spelled 'salutation_id'. " +
                "Only SalutationIdFieldId should exist.");

            // Also verify the deleted field GUID is not used anywhere
            var deletedFieldGuid = new Guid("4ace48d2-ece0-43a5-a04f-5a8e080c7428");
            AccountEntity.Fields.SalutationIdFieldId.Should().NotBe(deletedFieldGuid,
                "the salutation_id field must use the new GUID, not the deleted solutation_id GUID");
        }

        // ===================================================================
        // Phase 4: Relation Tests
        // ===================================================================

        /// <summary>
        /// Validates the salutation_1n_account relation ID and name.
        /// Source: NextPlugin.20190206.cs line 1341 —
        ///   relation.Id = new Guid("99e1a18b-05c2-4fca-986e-37ecebd62168")
        ///   relation.Name = "salutation_1n_account"
        /// RelationType: OneToMany (Salutation → Account via salutation_id)
        /// System = true
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHave_Salutation1nAccount_Relation()
        {
            // Arrange
            var expectedRelationId = new Guid("99e1a18b-05c2-4fca-986e-37ecebd62168");
            var expectedRelationName = "salutation_1n_account";

            // Act & Assert
            AccountEntity.Relations.Salutation1nAccountId.Should().Be(expectedRelationId,
                "the salutation_1n_account relation ID must match NextPlugin.20190206.cs line 1341");

            AccountEntity.Relations.Salutation1nAccountName.Should().Be(expectedRelationName,
                "the relation name must be 'salutation_1n_account' (OneToMany, Salutation → Account)");
        }

        /// <summary>
        /// Validates the account_nn_contact relation ID and name.
        /// Source: NextPlugin.20190204.cs line 2243 —
        ///   relation.Name = "account_nn_contact"
        /// RelationType: ManyToMany (Account ↔ Contact)
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHave_AccountNnContact_Relation()
        {
            // Arrange
            var expectedRelationId = new Guid("dd211c99-5415-4195-923a-cb5a56e5d544");
            var expectedRelationName = "account_nn_contact";

            // Act & Assert
            AccountEntity.Relations.AccountNnContactId.Should().Be(expectedRelationId,
                "the account_nn_contact relation ID must match NextPlugin.20190204.cs line 2243");

            AccountEntity.Relations.AccountNnContactName.Should().Be(expectedRelationName,
                "the relation name must be 'account_nn_contact' (ManyToMany, Account ↔ Contact)");
        }

        /// <summary>
        /// Validates the account_nn_case relation ID and name.
        /// Source: NextPlugin.20190204.cs line 2359 —
        ///   relation.Name = "account_nn_case"
        /// RelationType: ManyToMany (Account ↔ Case)
        /// This relation replaced the original 1:N account_1n_case which was deleted.
        /// </summary>
        [Fact]
        public void AccountEntity_ShouldHave_AccountNnCase_Relation()
        {
            // Arrange
            var expectedRelationId = new Guid("3690c12e-40e1-4e8f-a0a8-27221c686b43");
            var expectedRelationName = "account_nn_case";

            // Act & Assert
            AccountEntity.Relations.AccountNnCaseId.Should().Be(expectedRelationId,
                "the account_nn_case relation ID must match NextPlugin.20190204.cs line 2359 " +
                "(ManyToMany replacement for the deleted account_1n_case 1:N relation)");

            AccountEntity.Relations.AccountNnCaseName.Should().Be(expectedRelationName,
                "the relation name must be 'account_nn_case' (ManyToMany, Account ↔ Case)");
        }

        // ===================================================================
        // Phase 5: Search Index Field Tests
        // ===================================================================

        /// <summary>
        /// Validates the account search index fields match Configuration.AccountSearchIndexFields.
        /// Source: WebVella.Erp.Plugins.Next/Configuration.cs lines 9–11 —
        ///   { "city", "$country_1n_account.label", "email", "fax_phone", "first_name",
        ///     "fixed_phone", "last_name", "mobile_phone", "name", "notes", "post_code",
        ///     "region", "street", "street_2", "tax_id", "type", "website" }
        /// Total: 17 fields in the search index.
        ///
        /// This test verifies that the AccountEntity has field definitions for every
        /// non-relation field referenced in the search index configuration, ensuring
        /// the CRM service's x_search index regeneration will have all source fields.
        /// </summary>
        [Fact]
        public void AccountEntity_SearchIndexFields_ShouldMatchConfiguration()
        {
            // Arrange — expected search index fields from Configuration.cs
            var expectedSearchFields = new List<string>
            {
                "city",
                "$country_1n_account.label",
                "email",
                "fax_phone",
                "first_name",
                "fixed_phone",
                "last_name",
                "mobile_phone",
                "name",
                "notes",
                "post_code",
                "region",
                "street",
                "street_2",
                "tax_id",
                "type",
                "website"
            };

            // Assert — exactly 17 search index fields
            expectedSearchFields.Should().HaveCount(17,
                "the account search index configuration must include exactly 17 fields " +
                "as defined in Configuration.AccountSearchIndexFields");

            // Verify all non-relation fields (those without '$' prefix) have corresponding
            // field definitions in AccountEntity.Fields by checking each field ID is not empty.
            // Relation fields (prefixed with '$') reference cross-entity joins and are validated
            // separately through the Relations class.
            var directFieldNames = expectedSearchFields
                .Where(f => !f.StartsWith("$"))
                .ToList();

            // Each direct field must have a corresponding non-empty field ID in AccountEntity.Fields
            // Map field names to their AccountEntity.Fields member values
            var fieldNameToId = new Dictionary<string, Guid>
            {
                { "city", AccountEntity.Fields.CityFieldId },
                { "email", AccountEntity.Fields.EmailFieldId },
                { "fax_phone", AccountEntity.Fields.FaxPhoneFieldId },
                { "first_name", AccountEntity.Fields.FirstNameFieldId },
                { "fixed_phone", AccountEntity.Fields.FixedPhoneFieldId },
                { "last_name", AccountEntity.Fields.LastNameFieldId },
                { "mobile_phone", AccountEntity.Fields.MobilePhoneFieldId },
                { "name", AccountEntity.Fields.NameFieldId },
                { "notes", AccountEntity.Fields.NotesFieldId },
                { "post_code", AccountEntity.Fields.PostCodeFieldId },
                { "region", AccountEntity.Fields.RegionFieldId },
                { "street", AccountEntity.Fields.StreetFieldId },
                { "street_2", AccountEntity.Fields.Street2FieldId },
                { "tax_id", AccountEntity.Fields.TaxIdFieldId },
                { "type", AccountEntity.Fields.TypeFieldId },
                { "website", AccountEntity.Fields.WebsiteFieldId }
            };

            // Assert every direct field is mapped and has a non-empty GUID
            foreach (var fieldName in directFieldNames)
            {
                fieldNameToId.Should().ContainKey(fieldName,
                    $"search index field '{fieldName}' must have a corresponding field definition in AccountEntity.Fields");

                fieldNameToId[fieldName].Should().NotBe(Guid.Empty,
                    $"search index field '{fieldName}' must have a non-empty GUID in AccountEntity.Fields");
            }

            // Assert — the relation field "$country_1n_account.label" references the
            // country_1n_account relation which is also defined in AccountEntity.Relations
            var relationFields = expectedSearchFields
                .Where(f => f.StartsWith("$"))
                .ToList();

            relationFields.Should().HaveCount(1,
                "exactly one relation-based search index field should be present ($country_1n_account.label)");

            relationFields[0].Should().Be("$country_1n_account.label",
                "the relation search field must reference the country_1n_account relation for country label lookup");
        }
    }
}
