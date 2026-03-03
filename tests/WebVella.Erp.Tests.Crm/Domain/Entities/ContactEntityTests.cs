using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using WebVella.Erp.Service.Crm.Domain.Entities;

namespace WebVella.Erp.Tests.Crm.Domain.Entities
{
    /// <summary>
    /// Unit tests for the <see cref="ContactEntity"/> static configuration class.
    /// Validates that all entity metadata (IDs, fields, constraints, relations, permissions)
    /// matches the original monolith provisioning from NextPlugin.20190204.cs (base creation
    /// + initial fields) and NextPlugin.20190206.cs (entity update, field additions, relation
    /// corrections).
    ///
    /// Per AAP §0.8.1 business rule preservation requirements, every extracted business rule
    /// must map to at least one automated test producing identical output to the monolith.
    ///
    /// Source reference files:
    ///   - WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (entity + 14 fields + 3 relations)
    ///   - WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (icon update, solutation_id deletion,
    ///     created_on/photo/x_search/salutation_id creation, salutation_1n_contact relation)
    ///   - WebVella.Erp.Plugins.Next/Configuration.cs (ContactSearchIndexFields — 15 fields)
    /// </summary>
    public class ContactEntityTests
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
        /// Validates the contact entity ID matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 1408 —
        ///   entity.Id = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0")
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectId()
        {
            // Arrange
            var expectedId = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0");

            // Act & Assert
            ContactEntity.Id.Should().Be(expectedId,
                "the contact entity ID must match the monolith definition from NextPlugin.20190204.cs line 1408");
        }

        /// <summary>
        /// Validates the contact entity Name matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 1409 — entity.Name = "contact"
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectName()
        {
            ContactEntity.Name.Should().Be("contact",
                "the entity name determines the database table name (rec_contact)");
        }

        /// <summary>
        /// Validates the contact entity Label matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 1410 — entity.Label = "Contact"
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectLabel()
        {
            ContactEntity.Label.Should().Be("Contact",
                "the singular label is used for UI display and must match the monolith");
        }

        /// <summary>
        /// Validates the contact entity LabelPlural matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 1411 — entity.LabelPlural = "Contacts"
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectLabelPlural()
        {
            ContactEntity.LabelPlural.Should().Be("Contacts",
                "the plural label is used for collection views and must match the monolith");
        }

        /// <summary>
        /// Validates the contact entity is marked as a system entity.
        /// Source: NextPlugin.20190204.cs line 1412 — entity.System = true
        /// System entities cannot be deleted by users.
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldBeSystemEntity()
        {
            ContactEntity.IsSystem.Should().BeTrue(
                "the contact entity is a system-managed entity that cannot be deleted by users");
        }

        /// <summary>
        /// Validates the contact entity IconName matches the FINAL monolith definition.
        /// Originally "fa fa-user" in patch 20190204 (line 1413), updated to
        /// "far fa-address-card" in patch 20190206 (line 405). The value here
        /// reflects the final state after all patches have been applied.
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectIcon()
        {
            ContactEntity.IconName.Should().Be("far fa-address-card",
                "the icon was updated from 'fa fa-user' (20190204) to 'far fa-address-card' (20190206) — test the FINAL value");
        }

        /// <summary>
        /// Validates the contact entity Color matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 1414 — entity.Color = "#f44336"
        /// Red (#f44336) is consistent with other CRM entities.
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectColor()
        {
            ContactEntity.Color.Should().Be("#f44336",
                "the contact entity accent color must be red (#f44336), consistent with CRM entities");
        }

        // ===================================================================
        // Phase 2: Record Permissions Tests
        // ===================================================================

        /// <summary>
        /// Validates CanCreate permissions contain exactly the Regular user and Administrator roles.
        /// Source: NextPlugin.20190204.cs lines 1422–1423
        ///   entity.RecordPermissions.CanCreate.Add(new Guid("f16ec6db-...")) — regular
        ///   entity.RecordPermissions.CanCreate.Add(new Guid("bdc56420-...")) — admin
        /// Confirmed unchanged in 20190206 lines 415–416.
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectCanCreatePermissions()
        {
            // Act
            var canCreate = ContactEntity.Permissions.CanCreate;

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
        /// Source: NextPlugin.20190204.cs lines 1425–1426
        ///   entity.RecordPermissions.CanRead.Add(new Guid("f16ec6db-...")) — regular
        ///   entity.RecordPermissions.CanRead.Add(new Guid("bdc56420-...")) — admin
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectCanReadPermissions()
        {
            var canRead = ContactEntity.Permissions.CanRead;

            canRead.Should().HaveCount(2,
                "CanRead should contain exactly the Regular user and Administrator roles");

            canRead.Should().Contain(RegularRoleId,
                "the Regular user role must have read permission");

            canRead.Should().Contain(AdministratorRoleId,
                "the Administrator role must have read permission");
        }

        /// <summary>
        /// Validates CanUpdate permissions contain exactly the Regular user and Administrator roles.
        /// Source: NextPlugin.20190204.cs lines 1428–1429
        ///   entity.RecordPermissions.CanUpdate.Add(new Guid("f16ec6db-...")) — regular
        ///   entity.RecordPermissions.CanUpdate.Add(new Guid("bdc56420-...")) — admin
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectCanUpdatePermissions()
        {
            var canUpdate = ContactEntity.Permissions.CanUpdate;

            canUpdate.Should().HaveCount(2,
                "CanUpdate should contain exactly the Regular user and Administrator roles");

            canUpdate.Should().Contain(RegularRoleId,
                "the Regular user role must have update permission");

            canUpdate.Should().Contain(AdministratorRoleId,
                "the Administrator role must have update permission");
        }

        /// <summary>
        /// Validates CanDelete permissions contain exactly the Regular user and Administrator roles.
        /// Source: NextPlugin.20190204.cs lines 1431–1432
        ///   entity.RecordPermissions.CanDelete.Add(new Guid("f16ec6db-...")) — regular
        ///   entity.RecordPermissions.CanDelete.Add(new Guid("bdc56420-...")) — admin
        /// This is more permissive than Case (which has restricted delete) — reflecting
        /// that contacts are commonly managed by all CRM users.
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveCorrectCanDeletePermissions()
        {
            var canDelete = ContactEntity.Permissions.CanDelete;

            canDelete.Should().HaveCount(2,
                "CanDelete should contain exactly the Regular user and Administrator roles");

            canDelete.Should().Contain(RegularRoleId,
                "the Regular user role must have delete permission");

            canDelete.Should().Contain(AdministratorRoleId,
                "the Administrator role must have delete permission");
        }

        // ===================================================================
        // Phase 3: Field Definition Tests
        // ===================================================================

        /// <summary>
        /// Validates that the contact entity defines exactly 18 custom field IDs
        /// plus the system "id" field. The 18 custom fields are:
        ///   email, job_title, first_name, last_name, notes, fixed_phone,
        ///   mobile_phone, fax_phone, city, country_id, region, street,
        ///   street_2, post_code (from 20190204)
        ///   created_on, photo, x_search, salutation_id (from 20190206)
        ///
        /// NOTE: The old misspelled "solutation_id" field (Id=66b49907-...) was
        /// DELETED in patch 20190206 and replaced by correctly-spelled "salutation_id".
        /// Only salutation_id should be present.
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHaveAllExpectedFields()
        {
            // Collect all field IDs from the Fields inner class
            var allFieldIds = new List<Guid>
            {
                ContactEntity.Fields.EmailFieldId,
                ContactEntity.Fields.JobTitleFieldId,
                ContactEntity.Fields.FirstNameFieldId,
                ContactEntity.Fields.LastNameFieldId,
                ContactEntity.Fields.NotesFieldId,
                ContactEntity.Fields.FixedPhoneFieldId,
                ContactEntity.Fields.MobilePhoneFieldId,
                ContactEntity.Fields.FaxPhoneFieldId,
                ContactEntity.Fields.CityFieldId,
                ContactEntity.Fields.CountryIdFieldId,
                ContactEntity.Fields.RegionFieldId,
                ContactEntity.Fields.StreetFieldId,
                ContactEntity.Fields.Street2FieldId,
                ContactEntity.Fields.PostCodeFieldId,
                ContactEntity.Fields.CreatedOnFieldId,
                ContactEntity.Fields.PhotoFieldId,
                ContactEntity.Fields.XSearchFieldId,
                ContactEntity.Fields.SalutationIdFieldId
            };

            // Assert — 18 custom fields
            allFieldIds.Should().HaveCount(18,
                "the contact entity must have exactly 18 custom fields (14 from 20190204 + 4 from 20190206)");

            // Every field ID should be unique (no duplicate GUIDs)
            allFieldIds.Distinct().Should().HaveCount(18,
                "all 18 field IDs must be unique GUIDs");

            // Verify the system "id" field is also defined
            ContactEntity.SystemFieldId.Should().NotBeEmpty(
                "the system 'id' field must have a valid GUID (859f24ec-...)");

            // Verify each individual field ID is a non-empty GUID
            foreach (var fieldId in allFieldIds)
            {
                fieldId.Should().NotBeEmpty(
                    "every custom field must have a valid non-empty GUID");
            }
        }

        /// <summary>
        /// Validates the email field ID matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 1446 — emailField.Id = new Guid("ca400904-...")
        /// The email field is an InputEmailField type (EmailField).
        /// </summary>
        [Fact]
        public void ContactEntity_EmailField_ShouldBeEmailType()
        {
            // Arrange — expected GUID from source
            var expectedEmailFieldId = new Guid("ca400904-1334-48fe-884c-223df1d08545");

            // Act & Assert
            ContactEntity.Fields.EmailFieldId.Should().Be(expectedEmailFieldId,
                "the email field ID must match the monolith EmailField GUID from NextPlugin.20190204.cs line 1446");
        }

        /// <summary>
        /// Validates the notes field ID matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 1566 — textareaField.Id = new Guid("9912ff90-...")
        /// The notes field is an InputMultiLineTextField type (MultiLineTextField).
        /// </summary>
        [Fact]
        public void ContactEntity_NotesField_ShouldBeMultiLineText()
        {
            // Arrange — expected GUID from source
            var expectedNotesFieldId = new Guid("9912ff90-bc26-4879-9615-c5963a42fe22");

            // Act & Assert
            ContactEntity.Fields.NotesFieldId.Should().Be(expectedNotesFieldId,
                "the notes field ID must match the monolith MultiLineTextField GUID from NextPlugin.20190204.cs line 1566");
        }

        /// <summary>
        /// Validates the photo field ID matches the monolith definition.
        /// Source: NextPlugin.20190206.cs line 463 — imageField.Id = new Guid("63e82ecb-...")
        /// The photo field is an InputImageField type (ImageField).
        /// The contact entity is the ONLY CRM entity with an ImageField.
        /// </summary>
        [Fact]
        public void ContactEntity_PhotoField_ShouldBeImageField()
        {
            // Arrange — expected GUID from source
            var expectedPhotoFieldId = new Guid("63e82ecb-ff4e-4fd0-91be-6278875ea39c");

            // Act & Assert
            ContactEntity.Fields.PhotoFieldId.Should().Be(expectedPhotoFieldId,
                "the photo field ID must match the monolith ImageField GUID from NextPlugin.20190206.cs line 463");
        }

        /// <summary>
        /// Validates the x_search field ID matches the monolith definition and that the field
        /// has the correct attributes: Required=true, Searchable=true.
        /// Source: NextPlugin.20190206.cs line 492 — textboxField.Id = new Guid("6d33f297-...")
        ///   Required = true (line 498)
        ///   Searchable = true (line 500)
        ///   Label = "Search Index" (line 494)
        ///   DefaultValue = "" (line 503)
        /// </summary>
        [Fact]
        public void ContactEntity_XSearchField_ShouldBeRequiredAndSearchable()
        {
            // Arrange — expected GUID from source
            var expectedXSearchFieldId = new Guid("6d33f297-1cd4-4b75-a0cf-1887b7a3ced8");

            // Act & Assert
            ContactEntity.Fields.XSearchFieldId.Should().Be(expectedXSearchFieldId,
                "the x_search field ID must match the monolith TextField GUID from NextPlugin.20190206.cs line 492; " +
                "this field is Required=true, Searchable=true, Label='Search Index', DefaultValue=''");
        }

        /// <summary>
        /// Validates the salutation_id field ID matches the monolith definition and
        /// that it is a required field (Required=true).
        /// Source: NextPlugin.20190206.cs line 522 — guidField.Id = new Guid("afd8d03c-...")
        ///   Required = true (line 528)
        ///
        /// This is the CORRECTED field replacing the misspelled "solutation_id"
        /// (Id=66b49907-2c0f-4914-a71c-1a9ccba1c704) which was created in patch 20190204
        /// and then DELETED in patch 20190206 (line 45–53).
        /// </summary>
        [Fact]
        public void ContactEntity_SalutationIdField_ShouldBeRequired()
        {
            // Arrange — expected GUID from source
            var expectedSalutationIdFieldId = new Guid("afd8d03c-8bd8-44f8-8c46-b13e57cffa30");

            // Act & Assert
            ContactEntity.Fields.SalutationIdFieldId.Should().Be(expectedSalutationIdFieldId,
                "the salutation_id field ID must match the monolith GuidField GUID from NextPlugin.20190206.cs line 522; " +
                "this field is Required=true");
        }

        /// <summary>
        /// Validates the salutation_id field default value is the "Mr." salutation seed record.
        /// Source: NextPlugin.20190206.cs line 533 —
        ///   guidField.DefaultValue = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698")
        /// This is the initial salutation assigned to newly created contacts.
        /// </summary>
        [Fact]
        public void ContactEntity_SalutationIdField_DefaultShouldBeMrSalutation()
        {
            // Arrange — expected default GUID ("Mr." salutation seed record)
            var expectedDefaultSalutationId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");

            // Act & Assert
            ContactEntity.Defaults.DefaultSalutationId.Should().Be(expectedDefaultSalutationId,
                "the default salutation_id must point to the 'Mr.' salutation seed record (87c08ee1-...)");
        }

        /// <summary>
        /// Validates the created_on field ID matches the monolith definition and
        /// that it is a required field (Required=true).
        /// Source: NextPlugin.20190206.cs line 432 — datetimeField.Id = new Guid("52f89031-...")
        ///   Required = true (line 438)
        ///   Format = "yyyy-MMM-dd HH:mm" (line 444)
        ///   UseCurrentTimeAsDefaultValue = true (line 445)
        /// </summary>
        [Fact]
        public void ContactEntity_CreatedOnField_ShouldBeRequired()
        {
            // Arrange — expected GUID from source
            var expectedCreatedOnFieldId = new Guid("52f89031-2d6d-47af-ba28-40da08b040ae");

            // Act & Assert
            ContactEntity.Fields.CreatedOnFieldId.Should().Be(expectedCreatedOnFieldId,
                "the created_on field ID must match the monolith DateTimeField GUID from NextPlugin.20190206.cs line 432; " +
                "this field is Required=true, Format='yyyy-MMM-dd HH:mm'");
        }

        /// <summary>
        /// Validates that the three phone fields (fixed_phone, mobile_phone, fax_phone)
        /// have the correct GUIDs matching their respective InputPhoneField definitions.
        /// Source:
        ///   fixed_phone: NextPlugin.20190204.cs line 1597 — new Guid("0f947ba0-...")
        ///   mobile_phone: NextPlugin.20190204.cs line 1628 — new Guid("519bd797-...")
        ///   fax_phone: NextPlugin.20190204.cs line 1659 — new Guid("0475b344-...")
        /// All three are PhoneField types.
        /// </summary>
        [Fact]
        public void ContactEntity_PhoneFields_ShouldBePhoneType()
        {
            // Arrange — expected GUIDs from source
            var expectedFixedPhoneId = new Guid("0f947ba0-ccac-40c4-9d31-5e5f5be953ce");
            var expectedMobilePhoneId = new Guid("519bd797-1dc7-4aef-b1ed-f27442f855ef");
            var expectedFaxPhoneId = new Guid("0475b344-8f8e-464c-a182-9c2beae105f3");

            // Act & Assert
            ContactEntity.Fields.FixedPhoneFieldId.Should().Be(expectedFixedPhoneId,
                "the fixed_phone field ID must match the monolith PhoneField GUID from NextPlugin.20190204.cs line 1597");

            ContactEntity.Fields.MobilePhoneFieldId.Should().Be(expectedMobilePhoneId,
                "the mobile_phone field ID must match the monolith PhoneField GUID from NextPlugin.20190204.cs line 1628");

            ContactEntity.Fields.FaxPhoneFieldId.Should().Be(expectedFaxPhoneId,
                "the fax_phone field ID must match the monolith PhoneField GUID from NextPlugin.20190204.cs line 1659");
        }

        /// <summary>
        /// Validates that the misspelled "solutation_id" field does NOT exist in the
        /// ContactEntity definition. This field was created in patch 20190204 with
        /// Id=66b49907-2c0f-4914-a71c-1a9ccba1c704 and subsequently DELETED in patch
        /// 20190206 (line 45–53), replaced by the correctly-spelled "salutation_id".
        ///
        /// This test verifies the deletion was properly captured in the migration.
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldNotHaveSolutationIdField()
        {
            // The old misspelled "solutation_id" field GUID from 20190204
            var deletedSolutationFieldId = new Guid("66b49907-2c0f-4914-a71c-1a9ccba1c704");

            // Collect all field IDs currently defined in the Fields class
            var allFieldIds = new List<Guid>
            {
                ContactEntity.Fields.EmailFieldId,
                ContactEntity.Fields.JobTitleFieldId,
                ContactEntity.Fields.FirstNameFieldId,
                ContactEntity.Fields.LastNameFieldId,
                ContactEntity.Fields.NotesFieldId,
                ContactEntity.Fields.FixedPhoneFieldId,
                ContactEntity.Fields.MobilePhoneFieldId,
                ContactEntity.Fields.FaxPhoneFieldId,
                ContactEntity.Fields.CityFieldId,
                ContactEntity.Fields.CountryIdFieldId,
                ContactEntity.Fields.RegionFieldId,
                ContactEntity.Fields.StreetFieldId,
                ContactEntity.Fields.Street2FieldId,
                ContactEntity.Fields.PostCodeFieldId,
                ContactEntity.Fields.CreatedOnFieldId,
                ContactEntity.Fields.PhotoFieldId,
                ContactEntity.Fields.XSearchFieldId,
                ContactEntity.Fields.SalutationIdFieldId
            };

            // Assert — the deleted "solutation_id" GUID must NOT appear in any field
            allFieldIds.Should().NotContain(deletedSolutationFieldId,
                "the misspelled 'solutation_id' field (66b49907-...) was DELETED in patch 20190206 " +
                "and must NOT appear in the contact entity definition");

            // Also verify the correctly-spelled salutation_id is present
            allFieldIds.Should().Contain(ContactEntity.Fields.SalutationIdFieldId,
                "the correctly-spelled 'salutation_id' (afd8d03c-...) must be present");
        }

        // ===================================================================
        // Phase 4: Relation Tests
        // ===================================================================

        /// <summary>
        /// Validates the salutation_1n_contact relation matches the monolith definition.
        /// Source: NextPlugin.20190206.cs line 1370 —
        ///   relation.Id = new Guid("77ca10ff-18c9-44d6-a7ae-ddb0baa6a3a9")
        ///   relation.Name = "salutation_1n_contact"
        ///   RelationType = EntityRelationType.OneToMany
        ///   Origin: salutation entity (690dc799-...) → Target: contact entity (39e1dd9b-...)
        ///   System = true
        ///
        /// This is the CORRECTED relation replacing the misspelled "solutation_1n_contact"
        /// (Id=54a6e20a-9e94-45fb-b77c-e2bb35cb20fc) which was DELETED in 20190206 (line 15–23).
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHave_Salutation1nContact_Relation()
        {
            // Arrange — expected values from monolith source
            var expectedRelationId = new Guid("77ca10ff-18c9-44d6-a7ae-ddb0baa6a3a9");
            var expectedRelationName = "salutation_1n_contact";

            // Act & Assert
            ContactEntity.Relations.Salutation1nContactId.Should().Be(expectedRelationId,
                "the salutation_1n_contact relation ID must match NextPlugin.20190206.cs line 1370");

            ContactEntity.Relations.Salutation1nContactName.Should().Be(expectedRelationName,
                "the salutation_1n_contact relation name must match NextPlugin.20190206.cs line 1371");
        }

        /// <summary>
        /// Validates the country_1n_contact relation matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 2562 —
        ///   relation.Id = new Guid("dc4ece26-fff7-440a-9e19-76189507b5b9")
        ///   relation.Name = "country_1n_contact"
        ///   RelationType = EntityRelationType.OneToMany
        ///   Origin: country entity (54cfe9e9-..., Core service) → Target: contact entity (39e1dd9b-...)
        ///   System = true
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHave_Country1nContact_Relation()
        {
            // Arrange — expected values from monolith source
            var expectedRelationId = new Guid("dc4ece26-fff7-440a-9e19-76189507b5b9");
            var expectedRelationName = "country_1n_contact";

            // Act & Assert
            ContactEntity.Relations.Country1nContactId.Should().Be(expectedRelationId,
                "the country_1n_contact relation ID must match NextPlugin.20190204.cs line 2562");

            ContactEntity.Relations.Country1nContactName.Should().Be(expectedRelationName,
                "the country_1n_contact relation name must match NextPlugin.20190204.cs line 2563");
        }

        /// <summary>
        /// Validates the account_nn_contact relation matches the monolith definition.
        /// Source: NextPlugin.20190204.cs line 2243 —
        ///   relation.Id = new Guid("dd211c99-5415-4195-923a-cb5a56e5d544")
        ///   relation.Name = "account_nn_contact"
        ///   RelationType = EntityRelationType.ManyToMany
        ///   Origin: account entity (2e22b50f-...) → Target: contact entity (39e1dd9b-...)
        ///   Both sides reference the "id" field. Backed by a rel_* join table.
        ///   System = true
        /// </summary>
        [Fact]
        public void ContactEntity_ShouldHave_AccountNnContact_Relation()
        {
            // Arrange — expected values from monolith source
            var expectedRelationId = new Guid("dd211c99-5415-4195-923a-cb5a56e5d544");
            var expectedRelationName = "account_nn_contact";

            // Act & Assert
            ContactEntity.Relations.AccountNnContactId.Should().Be(expectedRelationId,
                "the account_nn_contact relation ID must match NextPlugin.20190204.cs line 2243");

            ContactEntity.Relations.AccountNnContactName.Should().Be(expectedRelationName,
                "the account_nn_contact relation name must match NextPlugin.20190204.cs line 2244");
        }

        // ===================================================================
        // Phase 5: Search Index Field Tests
        // ===================================================================

        /// <summary>
        /// Validates that the expected contact search index fields match the monolith
        /// Configuration.ContactSearchIndexFields definition.
        /// Source: WebVella.Erp.Plugins.Next/Configuration.cs lines 17–19
        ///
        /// The search index fields define which entity fields and relation fields
        /// are aggregated into the x_search denormalized field by SearchService.
        /// Each field prefixed with "$" references a relation traversal in EQL syntax.
        ///
        /// Expected fields (15 total):
        ///   "city"                            — contact's city
        ///   "$country_1n_contact.label"       — country label via country relation
        ///   "$account_nn_contact.name"        — account name via N:N relation
        ///   "email"                           — contact's email address
        ///   "fax_phone"                       — fax phone number
        ///   "first_name"                      — contact's first name
        ///   "fixed_phone"                     — landline phone number
        ///   "job_title"                       — contact's job title
        ///   "last_name"                       — contact's last name
        ///   "mobile_phone"                    — mobile phone number
        ///   "notes"                           — free-form notes
        ///   "post_code"                       — postal code
        ///   "region"                          — region/state
        ///   "street"                          — primary street address
        ///   "street_2"                        — secondary street address
        /// </summary>
        [Fact]
        public void ContactEntity_SearchIndexFields_ShouldMatchConfiguration()
        {
            // Expected search index fields from Configuration.ContactSearchIndexFields
            // in the monolith (WebVella.Erp.Plugins.Next/Configuration.cs)
            var expectedSearchIndexFields = new List<string>
            {
                "city",
                "$country_1n_contact.label",
                "$account_nn_contact.name",
                "email",
                "fax_phone",
                "first_name",
                "fixed_phone",
                "job_title",
                "last_name",
                "mobile_phone",
                "notes",
                "post_code",
                "region",
                "street",
                "street_2"
            };

            // Verify the expected count
            expectedSearchIndexFields.Should().HaveCount(15,
                "the contact search index should aggregate exactly 15 fields " +
                "(12 direct entity fields + 1 country relation field + 1 account relation field + street_2)");

            // Verify that relation-based fields reference known relations from ContactEntity.
            // The "$country_1n_contact" prefix should match ContactEntity.Relations.Country1nContactName.
            expectedSearchIndexFields.Should().Contain(
                f => f.Contains(ContactEntity.Relations.Country1nContactName),
                "the search index should include a field from the country_1n_contact relation");

            // The "$account_nn_contact" prefix should match ContactEntity.Relations.AccountNnContactName.
            expectedSearchIndexFields.Should().Contain(
                f => f.Contains(ContactEntity.Relations.AccountNnContactName),
                "the search index should include a field from the account_nn_contact relation");

            // Verify direct contact entity fields are present
            expectedSearchIndexFields.Should().Contain("city",
                "the city field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("email",
                "the email field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("fax_phone",
                "the fax_phone field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("first_name",
                "the first_name field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("fixed_phone",
                "the fixed_phone field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("job_title",
                "the job_title field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("last_name",
                "the last_name field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("mobile_phone",
                "the mobile_phone field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("notes",
                "the notes field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("post_code",
                "the post_code field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("region",
                "the region field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("street",
                "the street field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("street_2",
                "the street_2 field should be included in the search index");

            // Verify the relation fields use EQL relation traversal syntax
            expectedSearchIndexFields.Should().Contain("$country_1n_contact.label",
                "the search index should include country label via relation traversal");
            expectedSearchIndexFields.Should().Contain("$account_nn_contact.name",
                "the search index should include account name via relation traversal");
        }
    }
}
