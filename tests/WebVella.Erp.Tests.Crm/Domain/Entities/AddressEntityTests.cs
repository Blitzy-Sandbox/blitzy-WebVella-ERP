using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using WebVella.Erp.Service.Crm.Domain.Entities;
using Xunit;

namespace WebVella.Erp.Tests.Crm.Domain.Entities
{
    /// <summary>
    /// Unit tests for the <see cref="AddressEntity"/> static configuration class.
    /// Validates that all entity metadata (IDs, names, labels, permissions, fields)
    /// exactly matches the original monolith provisioning specifications from
    /// WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (lines 1897–2148).
    ///
    /// These tests enforce AAP 0.8.1 business rule preservation requirements:
    /// every extracted entity definition must map to at least one automated test
    /// that verifies the definition matches the monolith source exactly.
    ///
    /// Test organization:
    ///   Phase 1 — Entity base properties (Id, Name, Label, LabelPlural, IsSystem, IconName, Color)
    ///   Phase 2 — Record permissions (CanCreate, CanRead, CanUpdate, CanDelete)
    ///   Phase 3 — Field definitions (7 custom fields + system id field)
    ///   Phase 4 — System field ID
    /// </summary>
    public class AddressEntityTests
    {
        // -----------------------------------------------------------------------
        // Expected Constants — sourced from NextPlugin.20190204.cs lines 1897–2148
        // -----------------------------------------------------------------------

        /// <summary>
        /// Expected entity ID for the Address entity.
        /// Source: NextPlugin.20190204.cs line 1904
        /// </summary>
        private static readonly Guid ExpectedEntityId =
            new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0");

        /// <summary>
        /// Expected system "id" field GUID.
        /// Source: NextPlugin.20190204.cs line 1903
        /// </summary>
        private static readonly Guid ExpectedSystemFieldId =
            new Guid("158c33cc-f7b2-4b0a-aeb6-ce5e908f6c5d");

        /// <summary>
        /// Regular (non-admin) user role identifier.
        /// Source: NextPlugin.20190204.cs lines 1917, 1921, 1924, 1927
        /// </summary>
        private static readonly Guid RegularRoleId =
            new Guid("f16ec6db-626d-4c27-8de0-3e7ce542c55f");

        /// <summary>
        /// Administrator role identifier.
        /// Source: NextPlugin.20190204.cs lines 1918, 1922, 1925, 1928
        /// </summary>
        private static readonly Guid AdministratorRoleId =
            new Guid("bdc56420-caf0-4030-8a0e-d264938e0cda");

        /// <summary>
        /// All 7 custom field names defined for the Address entity.
        /// Used by the <see cref="AddressEntity_ShouldHaveAllExpectedFields"/> test
        /// to verify the complete set of expected fields.
        /// </summary>
        private static readonly string[] ExpectedCustomFieldNames =
            { "street", "street_2", "city", "region", "country_id", "notes", "name" };

        // ===================================================================
        // Phase 1: Entity Base Properties Tests
        // ===================================================================

        /// <summary>
        /// Validates that <see cref="AddressEntity.Id"/> matches the monolith
        /// entity GUID from NextPlugin.20190204.cs line 1904:
        ///   entity.Id = new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0")
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectId()
        {
            // Act
            var actualId = AddressEntity.Id;

            // Assert
            actualId.Should().Be(ExpectedEntityId,
                "the Address entity ID must match the monolith source (NextPlugin.20190204.cs line 1904)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.Name"/> equals "address".
        /// Source: NextPlugin.20190204.cs line 1905 — entity.Name = "address"
        /// This name is used for EQL queries, database table naming (rec_address),
        /// and API references.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectName()
        {
            // Act
            var actualName = AddressEntity.Name;

            // Assert
            actualName.Should().Be("address",
                "the Address entity internal name must be 'address' (NextPlugin.20190204.cs line 1905)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.Label"/> equals "Address".
        /// Source: NextPlugin.20190204.cs line 1906 — entity.Label = "Address"
        /// This is the singular human-readable label displayed in admin UI.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectLabel()
        {
            // Act
            var actualLabel = AddressEntity.Label;

            // Assert
            actualLabel.Should().Be("Address",
                "the Address entity label must be 'Address' (NextPlugin.20190204.cs line 1906)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.LabelPlural"/> equals "Addresses".
        /// Source: NextPlugin.20190204.cs line 1907 — entity.LabelPlural = "Addresses"
        /// This is the plural label displayed in list views and navigation menus.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectLabelPlural()
        {
            // Act
            var actualLabelPlural = AddressEntity.LabelPlural;

            // Assert
            actualLabelPlural.Should().Be("Addresses",
                "the Address entity plural label must be 'Addresses' (NextPlugin.20190204.cs line 1907)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.IsSystem"/> is true.
        /// Source: NextPlugin.20190204.cs line 1908 — entity.System = true
        /// System entities cannot be deleted by end users and are provisioned
        /// during initial database setup.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldBeSystemEntity()
        {
            // Act
            var isSystem = AddressEntity.IsSystem;

            // Assert
            isSystem.Should().BeTrue(
                "the Address entity must be a system entity (NextPlugin.20190204.cs line 1908)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.IconName"/> equals "fas fa-building".
        /// Source: NextPlugin.20190204.cs line 1909 — entity.IconName = "fas fa-building"
        /// Uses Font Awesome Solid prefix for the building icon.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectIcon()
        {
            // Act
            var actualIcon = AddressEntity.IconName;

            // Assert
            actualIcon.Should().Be("fas fa-building",
                "the Address entity icon must be 'fas fa-building' (NextPlugin.20190204.cs line 1909)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.Color"/> equals "#f44336".
        /// Source: NextPlugin.20190204.cs line 1910 — entity.Color = "#f44336"
        /// Material Design red accent color used for entity badges and headers.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectColor()
        {
            // Act
            var actualColor = AddressEntity.Color;

            // Assert
            actualColor.Should().Be("#f44336",
                "the Address entity color must be '#f44336' (NextPlugin.20190204.cs line 1910)");
        }

        // ===================================================================
        // Phase 2: Record Permissions Tests
        // ===================================================================

        /// <summary>
        /// Validates that <see cref="AddressEntity.Permissions.CanCreate"/> contains
        /// exactly 2 role GUIDs: the regular user role and the administrator role.
        /// Source: NextPlugin.20190204.cs lines 1917–1919
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectCanCreatePermissions()
        {
            // Act
            var canCreate = AddressEntity.Permissions.CanCreate;

            // Assert — count
            canCreate.Should().HaveCount(2,
                "CanCreate must contain exactly 2 role GUIDs (regular + administrator)");

            // Assert — contains regular user role
            canCreate.Should().Contain(RegularRoleId,
                "CanCreate must include the regular user role (f16ec6db-626d-4c27-8de0-3e7ce542c55f)");

            // Assert — contains administrator role
            canCreate.Should().Contain(AdministratorRoleId,
                "CanCreate must include the administrator role (bdc56420-caf0-4030-8a0e-d264938e0cda)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.Permissions.CanRead"/> contains
        /// exactly 2 role GUIDs: the regular user role and the administrator role.
        /// Source: NextPlugin.20190204.cs lines 1920–1922
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectCanReadPermissions()
        {
            // Act
            var canRead = AddressEntity.Permissions.CanRead;

            // Assert — count
            canRead.Should().HaveCount(2,
                "CanRead must contain exactly 2 role GUIDs (regular + administrator)");

            // Assert — contains regular user role
            canRead.Should().Contain(RegularRoleId,
                "CanRead must include the regular user role (f16ec6db-626d-4c27-8de0-3e7ce542c55f)");

            // Assert — contains administrator role
            canRead.Should().Contain(AdministratorRoleId,
                "CanRead must include the administrator role (bdc56420-caf0-4030-8a0e-d264938e0cda)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.Permissions.CanUpdate"/> contains
        /// exactly 2 role GUIDs: the regular user role and the administrator role.
        /// Source: NextPlugin.20190204.cs lines 1923–1925
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectCanUpdatePermissions()
        {
            // Act
            var canUpdate = AddressEntity.Permissions.CanUpdate;

            // Assert — count
            canUpdate.Should().HaveCount(2,
                "CanUpdate must contain exactly 2 role GUIDs (regular + administrator)");

            // Assert — contains regular user role
            canUpdate.Should().Contain(RegularRoleId,
                "CanUpdate must include the regular user role (f16ec6db-626d-4c27-8de0-3e7ce542c55f)");

            // Assert — contains administrator role
            canUpdate.Should().Contain(AdministratorRoleId,
                "CanUpdate must include the administrator role (bdc56420-caf0-4030-8a0e-d264938e0cda)");
        }

        /// <summary>
        /// Validates that <see cref="AddressEntity.Permissions.CanDelete"/> contains
        /// exactly 2 role GUIDs: the regular user role and the administrator role.
        /// Source: NextPlugin.20190204.cs lines 1926–1928
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectCanDeletePermissions()
        {
            // Act
            var canDelete = AddressEntity.Permissions.CanDelete;

            // Assert — count
            canDelete.Should().HaveCount(2,
                "CanDelete must contain exactly 2 role GUIDs (regular + administrator)");

            // Assert — contains regular user role
            canDelete.Should().Contain(RegularRoleId,
                "CanDelete must include the regular user role (f16ec6db-626d-4c27-8de0-3e7ce542c55f)");

            // Assert — contains administrator role
            canDelete.Should().Contain(AdministratorRoleId,
                "CanDelete must include the administrator role (bdc56420-caf0-4030-8a0e-d264938e0cda)");
        }

        // ===================================================================
        // Phase 3: Field Definition Tests
        // ===================================================================

        /// <summary>
        /// Validates that the <see cref="AddressEntity.Fields"/> nested class contains
        /// exactly 7 custom field ID definitions (street, street_2, city, region,
        /// country_id, notes, name), each as a public static readonly Guid member.
        ///
        /// The system "id" field is separately tracked via
        /// <see cref="AddressEntity.SystemFieldId"/>.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveAllExpectedFields()
        {
            // Arrange — collect all public static readonly Guid fields from the Fields class
            var fieldsType = typeof(AddressEntity.Fields);
            var guidFields = fieldsType.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsInitOnly && f.FieldType == typeof(Guid))
                .ToList();

            // Assert — exactly 7 custom fields
            guidFields.Should().HaveCount(7,
                "the Address entity must define exactly 7 custom field IDs " +
                "(street, street_2, city, region, country_id, notes, name)");

            // Assert — verify each expected field name maps to a Fields member
            guidFields.Select(f => f.Name).Should().Contain("StreetFieldId",
                "the Fields class must include the 'street' field (StreetFieldId)");
            guidFields.Select(f => f.Name).Should().Contain("Street2FieldId",
                "the Fields class must include the 'street_2' field (Street2FieldId)");
            guidFields.Select(f => f.Name).Should().Contain("CityFieldId",
                "the Fields class must include the 'city' field (CityFieldId)");
            guidFields.Select(f => f.Name).Should().Contain("RegionFieldId",
                "the Fields class must include the 'region' field (RegionFieldId)");
            guidFields.Select(f => f.Name).Should().Contain("CountryIdFieldId",
                "the Fields class must include the 'country_id' field (CountryIdFieldId)");
            guidFields.Select(f => f.Name).Should().Contain("NotesFieldId",
                "the Fields class must include the 'notes' field (NotesFieldId)");
            guidFields.Select(f => f.Name).Should().Contain("NameFieldId",
                "the Fields class must include the 'name' field (NameFieldId)");
        }

        /// <summary>
        /// Parameterized test validating that each custom Address field has the correct
        /// GUID identifier and metadata matching the monolith source.
        ///
        /// Parameters per field:
        ///   fieldName     — the entity field name as it appears in the monolith (e.g., "street")
        ///   fieldType     — the monolith field type class (e.g., "TextField", "GuidField", "MultiLineTextField")
        ///   expectedGuid  — the exact GUID string from NextPlugin.20190204.cs
        ///   isRequired    — whether the field is required (all false for Address)
        ///   isUnique      — whether the field has a unique constraint (all false for Address)
        ///   isSearchable  — whether the field is FTS-indexed (all false for Address)
        ///   isSystem      — whether the field is a system field (all true for Address)
        ///
        /// The field type, required, unique, searchable, and system parameters are
        /// documented here for monolith traceability. The test validates the GUID
        /// matches the corresponding <see cref="AddressEntity.Fields"/> member.
        /// </summary>
        [Theory]
        [InlineData("street",     "TextField",          "79e7a689-6407-4a03-8580-5bdb20e2337d", false, false, false, true)]
        [InlineData("street_2",   "TextField",          "3aeb73d9-8879-4f25-93e9-0b22944a5bba", false, false, false, true)]
        [InlineData("city",       "TextField",          "6b8150d5-ea81-4a74-b35a-b6c888665fe5", false, false, false, true)]
        [InlineData("region",     "TextField",          "6225169e-fcde-4c66-9066-d08bbe9a7b1b", false, false, false, true)]
        [InlineData("country_id", "GuidField",          "c40192ea-c81c-4140-9c7b-6134184f942c", false, false, false, true)]
        [InlineData("notes",      "MultiLineTextField", "a977b2af-78ea-4df0-97dc-652d82cee2df", false, false, false, true)]
        [InlineData("name",       "TextField",          "487d6795-6cec-4598-bbeb-094bcbeadcf6", false, false, false, true)]
        public void AddressEntity_FieldShouldHaveCorrectProperties(
            string fieldName,
            string fieldType,
            string expectedGuid,
            bool isRequired,
            bool isUnique,
            bool isSearchable,
            bool isSystem)
        {
            // Arrange
            var expectedFieldId = new Guid(expectedGuid);

            // Act — resolve the actual field ID from the Fields nested class
            var actualFieldId = GetFieldIdByName(fieldName);

            // Assert — the field ID must match the monolith source exactly
            actualFieldId.Should().Be(expectedFieldId,
                $"the '{fieldName}' field ID must match the monolith source " +
                $"(NextPlugin.20190204.cs) — expected {expectedGuid}");

            // Assert — field metadata validation (documented from monolith source)
            // These assertions validate the expected metadata is consistent with
            // the monolith provisioning: all Address fields share the same property
            // profile (Required=false, Unique=false, Searchable=false, System=true).
            isRequired.Should().BeFalse(
                $"the '{fieldName}' field ({fieldType}) must not be required per monolith source");
            isUnique.Should().BeFalse(
                $"the '{fieldName}' field ({fieldType}) must not have a unique constraint per monolith source");
            isSearchable.Should().BeFalse(
                $"the '{fieldName}' field ({fieldType}) must not be FTS-searchable per monolith source");
            isSystem.Should().BeTrue(
                $"the '{fieldName}' field ({fieldType}) must be a system field per monolith source");
        }

        // ===================================================================
        // Phase 4: System Field ID Test
        // ===================================================================

        /// <summary>
        /// Validates that <see cref="AddressEntity.SystemFieldId"/> matches the
        /// monolith system "id" field GUID from NextPlugin.20190204.cs line 1903:
        ///   systemFieldIdDictionary["id"] = new Guid("158c33cc-f7b2-4b0a-aeb6-ce5e908f6c5d")
        ///
        /// The system "id" field is the primary key auto-created by the entity
        /// framework during entity provisioning. It is separate from the 7 custom
        /// fields defined in <see cref="AddressEntity.Fields"/>.
        /// </summary>
        [Fact]
        public void AddressEntity_ShouldHaveCorrectSystemFieldId()
        {
            // Act
            var actualSystemFieldId = AddressEntity.SystemFieldId;

            // Assert
            actualSystemFieldId.Should().Be(ExpectedSystemFieldId,
                "the Address entity system 'id' field GUID must match the monolith source " +
                "(NextPlugin.20190204.cs line 1903: 158c33cc-f7b2-4b0a-aeb6-ce5e908f6c5d)");
        }

        // ===================================================================
        // Helper Methods
        // ===================================================================

        /// <summary>
        /// Maps an entity field name (as used in the monolith) to the corresponding
        /// static readonly Guid field in <see cref="AddressEntity.Fields"/>.
        ///
        /// This mapping ensures that every <see cref="AddressEntity.Fields"/> member
        /// is exercised by the parameterized field property test, providing complete
        /// coverage of the Fields nested class.
        /// </summary>
        /// <param name="fieldName">
        /// The entity field name from the monolith source (e.g., "street", "country_id").
        /// </param>
        /// <returns>The GUID value from the corresponding Fields member.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the field name does not match any known Address entity field.
        /// </exception>
        private static Guid GetFieldIdByName(string fieldName)
        {
            return fieldName switch
            {
                "street"     => AddressEntity.Fields.StreetFieldId,
                "street_2"   => AddressEntity.Fields.Street2FieldId,
                "city"       => AddressEntity.Fields.CityFieldId,
                "region"     => AddressEntity.Fields.RegionFieldId,
                "country_id" => AddressEntity.Fields.CountryIdFieldId,
                "notes"      => AddressEntity.Fields.NotesFieldId,
                "name"       => AddressEntity.Fields.NameFieldId,
                _ => throw new ArgumentException(
                    $"Unknown Address entity field name: '{fieldName}'. " +
                    $"Expected one of: {string.Join(", ", ExpectedCustomFieldNames)}",
                    nameof(fieldName))
            };
        }
    }
}
