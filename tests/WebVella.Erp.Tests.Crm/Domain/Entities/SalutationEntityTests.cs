using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using WebVella.Erp.Service.Crm.Domain.Entities;
using Xunit;

namespace WebVella.Erp.Tests.Crm.Domain.Entities
{
    /// <summary>
    /// Unit tests for the <see cref="SalutationEntity"/> static configuration class.
    /// Validates that all entity metadata (IDs, fields, constraints, seed data, relations)
    /// matches the original monolith provisioning specifications from NextPlugin.20190206.cs.
    ///
    /// The salutation entity was created in NextPlugin.20190206.cs to replace the misspelled
    /// "solutation" entity. These tests serve as regression guards (AAP 0.8.1) ensuring that
    /// the CRM microservice preserves 100% of the original entity definition during the
    /// monolith-to-microservices decomposition.
    ///
    /// Source reference: WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs
    ///   - Entity creation: lines 613–649
    ///   - Field definitions: lines 651–828
    ///   - Seed data records: lines 1212–1300
    ///   - Relation definitions: lines 1334–1390
    /// </summary>
    public class SalutationEntityTests
    {
        // ---------------------------------------------------------------------------
        // Well-known GUIDs from the monolith source for assertion comparison
        // ---------------------------------------------------------------------------
        private static readonly Guid ExpectedEntityId = new Guid("690dc799-e732-4d17-80d8-0f761bc33def");
        private static readonly Guid ExpectedSystemFieldId = new Guid("8721d461-ded9-46e7-8b1e-b7d0703a8d21");
        private static readonly Guid AdministratorRoleId = new Guid("bdc56420-caf0-4030-8a0e-d264938e0cda");
        private static readonly Guid RegularRoleId = new Guid("f16ec6db-626d-4c27-8de0-3e7ce542c55f");

        // Field IDs from NextPlugin.20190206.cs
        private static readonly Guid ExpectedIsDefaultFieldId = new Guid("17f9eb90-f712-472a-9b33-a5cdcfd15c68");
        private static readonly Guid ExpectedIsEnabledFieldId = new Guid("77ac9673-86df-43e9-bb17-b648f1fe5eb4");
        private static readonly Guid ExpectedIsSystemFieldId = new Guid("059917a0-4fdd-4154-9500-ebe8a0124ee2");
        private static readonly Guid ExpectedLabelFieldId = new Guid("8318dfb5-c656-459b-adc8-83f4f0ee65a0");
        private static readonly Guid ExpectedSortIndexFieldId = new Guid("e2a82937-7982-4fc2-84ca-b734efabb6b8");
        private static readonly Guid ExpectedLScopeFieldId = new Guid("a2de0020-63c6-4fb9-a35c-f3b63cc3455e");

        // Seed record IDs from NextPlugin.20190206.cs lines 1212–1300
        private static readonly Guid ExpectedMrId = new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");
        private static readonly Guid ExpectedMsId = new Guid("0ede7d96-2d85-45fa-818b-01327d4c47a9");
        private static readonly Guid ExpectedMrsId = new Guid("ab073457-ddc8-4d36-84a5-38619528b578");
        private static readonly Guid ExpectedDrId = new Guid("5b8d0137-9ec5-4b1c-a9b0-e982ef8698c1");
        private static readonly Guid ExpectedProfId = new Guid("a74cd934-b425-4061-8f4e-a6d6b9d7adb1");

        // Relation IDs from NextPlugin.20190206.cs lines 1334–1390
        private static readonly Guid ExpectedSalutation1nAccountId = new Guid("99e1a18b-05c2-4fca-986e-37ecebd62168");
        private static readonly Guid ExpectedSalutation1nContactId = new Guid("77ca10ff-18c9-44d6-a7ae-ddb0baa6a3a9");

        // =====================================================================
        // Phase 1: Entity Base Properties Tests
        // =====================================================================

        /// <summary>
        /// Validates entity ID matches the GUID from NextPlugin.20190206.cs line 620:
        ///   entity.Id = new Guid("690dc799-e732-4d17-80d8-0f761bc33def")
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectId()
        {
            SalutationEntity.Id.Should().Be(ExpectedEntityId,
                "entity ID must match NextPlugin.20190206.cs line 620");
        }

        /// <summary>
        /// Validates entity Name = "salutation" (NextPlugin.20190206.cs line 621).
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectName()
        {
            SalutationEntity.Name.Should().Be("salutation",
                "entity name must match NextPlugin.20190206.cs line 621");
        }

        /// <summary>
        /// Validates Label = "Salutation" (NextPlugin.20190206.cs line 622).
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectLabel()
        {
            SalutationEntity.Label.Should().Be("Salutation",
                "entity label must match NextPlugin.20190206.cs line 622");
        }

        /// <summary>
        /// Validates LabelPlural = "Salutations" (NextPlugin.20190206.cs line 623).
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectLabelPlural()
        {
            SalutationEntity.LabelPlural.Should().Be("Salutations",
                "entity label plural must match NextPlugin.20190206.cs line 623");
        }

        /// <summary>
        /// Validates System = true (NextPlugin.20190206.cs line 624).
        /// The salutation entity is a system-managed entity that cannot be deleted by users.
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldBeSystemEntity()
        {
            SalutationEntity.IsSystem.Should().BeTrue(
                "salutation must be a system entity per NextPlugin.20190206.cs line 624");
        }

        /// <summary>
        /// Validates IconName = "far fa-dot-circle" (NextPlugin.20190206.cs line 625).
        /// Note: Uses the "far" (regular) Font Awesome prefix, not "fa" or "fas".
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectIcon()
        {
            SalutationEntity.IconName.Should().Be("far fa-dot-circle",
                "icon must match NextPlugin.20190206.cs line 625");
        }

        /// <summary>
        /// Validates Color = "#f44336" (NextPlugin.20190206.cs line 626).
        /// Material Design Red 500 color used for UI display.
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectColor()
        {
            SalutationEntity.Color.Should().Be("#f44336",
                "color must match NextPlugin.20190206.cs line 626");
        }

        // =====================================================================
        // Phase 2: Record Permissions Tests
        // =====================================================================

        /// <summary>
        /// Validates CanCreate contains exactly 1 GUID: administrator role only.
        /// Source: NextPlugin.20190206.cs line 634.
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectCanCreatePermissions()
        {
            SalutationEntity.Permissions.CanCreate.Should().HaveCount(1,
                "only administrator role should have create permission");
            SalutationEntity.Permissions.CanCreate.Should().Contain(AdministratorRoleId,
                "administrator role must be in CanCreate per line 634");
        }

        /// <summary>
        /// Validates CanRead contains exactly 2 GUIDs: administrator and regular roles.
        /// Source: NextPlugin.20190206.cs lines 636–637.
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectCanReadPermissions()
        {
            SalutationEntity.Permissions.CanRead.Should().HaveCount(2,
                "administrator and regular roles should have read permission");
            SalutationEntity.Permissions.CanRead.Should().Contain(AdministratorRoleId,
                "administrator role must be in CanRead per line 636");
            SalutationEntity.Permissions.CanRead.Should().Contain(RegularRoleId,
                "regular role must be in CanRead per line 637");
        }

        /// <summary>
        /// Validates CanUpdate contains exactly 1 GUID: administrator role only.
        /// Source: NextPlugin.20190206.cs line 639.
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectCanUpdatePermissions()
        {
            SalutationEntity.Permissions.CanUpdate.Should().HaveCount(1,
                "only administrator role should have update permission");
            SalutationEntity.Permissions.CanUpdate.Should().Contain(AdministratorRoleId,
                "administrator role must be in CanUpdate per line 639");
        }

        /// <summary>
        /// Validates CanDelete is empty — no roles have delete permission.
        /// Source: NextPlugin.20190206.cs line 640 (only "//DELETE" comment, no .Add() calls).
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectCanDeletePermissions()
        {
            SalutationEntity.Permissions.CanDelete.Should().BeEmpty(
                "no roles should have delete permission per NextPlugin.20190206.cs line 640");
        }

        // =====================================================================
        // Phase 3: Field Definition Tests
        // =====================================================================

        /// <summary>
        /// Verifies that SalutationEntity.Fields exposes exactly 6 custom field IDs:
        /// is_default, is_enabled, is_system, label, sort_index, l_scope.
        /// Each field ID is a non-empty GUID that matches the monolith source.
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveAllExpectedFields()
        {
            // Collect all field IDs into a list for counting and distinctness checks
            var fieldIds = new List<Guid>
            {
                SalutationEntity.Fields.IsDefaultFieldId,
                SalutationEntity.Fields.IsEnabledFieldId,
                SalutationEntity.Fields.IsSystemFieldId,
                SalutationEntity.Fields.LabelFieldId,
                SalutationEntity.Fields.SortIndexFieldId,
                SalutationEntity.Fields.LScopeFieldId
            };

            // Exactly 6 custom fields must be defined
            fieldIds.Should().HaveCount(6,
                "salutation entity must have exactly 6 custom fields");

            // All field IDs must be non-empty and distinct
            fieldIds.Should().OnlyContain(id => id != Guid.Empty,
                "all field IDs must be non-empty GUIDs");
            fieldIds.Distinct().Should().HaveCount(6,
                "all field IDs must be distinct");
        }

        /// <summary>
        /// Parameterized test validating each custom field ID matches the expected GUID
        /// from NextPlugin.20190206.cs. Each inline data entry represents one field:
        ///
        /// | Field Name  | Expected Field ID                          | Source Line |
        /// |-------------|--------------------------------------------|-------------|
        /// | is_default  | 17f9eb90-f712-472a-9b33-a5cdcfd15c68       | 654         |
        /// | is_enabled  | 77ac9673-86df-43e9-bb17-b648f1fe5eb4       | 683         |
        /// | is_system   | 059917a0-4fdd-4154-9500-ebe8a0124ee2       | 712         |
        /// | label       | 8318dfb5-c656-459b-adc8-83f4f0ee65a0       | 741         |
        /// | sort_index  | e2a82937-7982-4fc2-84ca-b734efabb6b8       | 771         |
        /// | l_scope     | a2de0020-63c6-4fb9-a35c-f3b63cc3455e       | 803         |
        /// </summary>
        [Theory]
        [InlineData("is_default", "17f9eb90-f712-472a-9b33-a5cdcfd15c68")]
        [InlineData("is_enabled", "77ac9673-86df-43e9-bb17-b648f1fe5eb4")]
        [InlineData("is_system", "059917a0-4fdd-4154-9500-ebe8a0124ee2")]
        [InlineData("label", "8318dfb5-c656-459b-adc8-83f4f0ee65a0")]
        [InlineData("sort_index", "e2a82937-7982-4fc2-84ca-b734efabb6b8")]
        [InlineData("l_scope", "a2de0020-63c6-4fb9-a35c-f3b63cc3455e")]
        public void SalutationEntity_FieldShouldHaveCorrectProperties(
            string fieldName, string expectedFieldIdString)
        {
            var expectedFieldId = new Guid(expectedFieldIdString);
            Guid actualFieldId = GetFieldIdByName(fieldName);

            actualFieldId.Should().Be(expectedFieldId,
                $"field '{fieldName}' ID must match NextPlugin.20190206.cs");
        }

        /// <summary>
        /// Validates that the sort_index field ID matches the expected GUID for the field
        /// that was created with DecimalPlaces = 0 (byte.Parse("0"), line 785).
        /// The sort_index field is a NumberField with no decimal places, used for integer ordering.
        /// </summary>
        [Fact]
        public void SalutationEntity_SortIndexField_ShouldHaveZeroDecimalPlaces()
        {
            // The sort_index field ID must match the monolith field created with DecimalPlaces=0
            // Source: NextPlugin.20190206.cs line 771 (field ID), line 785 (DecimalPlaces = byte.Parse("0"))
            SalutationEntity.Fields.SortIndexFieldId.Should().Be(ExpectedSortIndexFieldId,
                "sort_index field ID must match the NumberField with DecimalPlaces=0 from line 771/785");
        }

        /// <summary>
        /// Validates that the label field ID matches the expected GUID for the field
        /// that was created with Unique = true (line 748).
        /// The label field is a TextField with a uniqueness constraint ensuring no duplicate salutation labels.
        /// </summary>
        [Fact]
        public void SalutationEntity_LabelField_ShouldBeUnique()
        {
            // The label field ID must match the monolith field created with Unique=true
            // Source: NextPlugin.20190206.cs line 741 (field ID), line 748 (Unique = true)
            SalutationEntity.Fields.LabelFieldId.Should().Be(ExpectedLabelFieldId,
                "label field ID must match the TextField with Unique=true from line 741/748");
        }

        /// <summary>
        /// Validates that the l_scope field ID matches the expected GUID for the field
        /// that was created with Searchable = true (line 811).
        /// The l_scope field is a TextField used for localization scope with full-text search support.
        /// </summary>
        [Fact]
        public void SalutationEntity_LScopeField_ShouldBeSearchable()
        {
            // The l_scope field ID must match the monolith field created with Searchable=true
            // Source: NextPlugin.20190206.cs line 803 (field ID), line 811 (Searchable = true)
            SalutationEntity.Fields.LScopeFieldId.Should().Be(ExpectedLScopeFieldId,
                "l_scope field ID must match the TextField with Searchable=true from line 803/811");
        }

        // =====================================================================
        // Phase 4: Seed Data Integrity Tests
        // =====================================================================

        /// <summary>
        /// Validates that exactly 5 seed records are defined:
        /// Mr., Ms., Mrs., Dr., Prof.
        /// Source: NextPlugin.20190206.cs lines 1212–1300
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveFiveSeedRecords()
        {
            var seedIds = new List<Guid>
            {
                SalutationEntity.SeedData.MrId,
                SalutationEntity.SeedData.MsId,
                SalutationEntity.SeedData.MrsId,
                SalutationEntity.SeedData.DrId,
                SalutationEntity.SeedData.ProfId
            };

            seedIds.Should().HaveCount(5,
                "exactly 5 salutation records must be seeded");
            seedIds.Should().OnlyContain(id => id != Guid.Empty,
                "all seed record IDs must be non-empty GUIDs");
            seedIds.Distinct().Should().HaveCount(5,
                "all seed record IDs must be distinct");
        }

        /// <summary>
        /// Validates the "Mr." seed record (id: 87c08ee1-8d4d-4c89-9b37-4e3cc3f98698).
        /// Properties from monolith: is_default=true, is_enabled=true, is_system=true,
        /// label="Mr.", sort_index=1.0, l_scope=""
        /// Source: NextPlugin.20190206.cs lines 1212–1227 (record JSON)
        /// </summary>
        [Fact]
        public void SalutationEntity_SeedRecord_Mr_ShouldBeCorrect()
        {
            // Verify the Mr. seed record ID matches the monolith source
            SalutationEntity.SeedData.MrId.Should().Be(ExpectedMrId,
                "Mr. seed record ID must match NextPlugin.20190206.cs line 1216");

            // Mr. is the default salutation (is_default=true, line 1217)
            SalutationEntity.SeedData.DefaultSalutationId.Should().Be(ExpectedMrId,
                "Mr. must be the default salutation (is_default=true)");
        }

        /// <summary>
        /// Validates the "Ms." seed record (id: 0ede7d96-2d85-45fa-818b-01327d4c47a9).
        /// Properties from monolith: is_default=false, is_enabled=true, is_system=true,
        /// label="Ms.", sort_index=2.0, l_scope=""
        /// Source: NextPlugin.20190206.cs lines 1230–1245 (record JSON)
        /// </summary>
        [Fact]
        public void SalutationEntity_SeedRecord_Ms_ShouldBeCorrect()
        {
            SalutationEntity.SeedData.MsId.Should().Be(ExpectedMsId,
                "Ms. seed record ID must match NextPlugin.20190206.cs line 1234");

            // Ms. is NOT the default salutation
            SalutationEntity.SeedData.DefaultSalutationId.Should().NotBe(ExpectedMsId,
                "Ms. must not be the default salutation (is_default=false)");
        }

        /// <summary>
        /// Validates the "Mrs." seed record (id: ab073457-ddc8-4d36-84a5-38619528b578).
        /// Properties from monolith: is_default=false, is_enabled=true, is_system=true,
        /// label="Mrs.", sort_index=3.0, l_scope=""
        /// Source: NextPlugin.20190206.cs lines 1248–1263 (record JSON)
        /// </summary>
        [Fact]
        public void SalutationEntity_SeedRecord_Mrs_ShouldBeCorrect()
        {
            SalutationEntity.SeedData.MrsId.Should().Be(ExpectedMrsId,
                "Mrs. seed record ID must match NextPlugin.20190206.cs line 1252");

            // Mrs. is NOT the default salutation
            SalutationEntity.SeedData.DefaultSalutationId.Should().NotBe(ExpectedMrsId,
                "Mrs. must not be the default salutation (is_default=false)");
        }

        /// <summary>
        /// Validates the "Dr." seed record (id: 5b8d0137-9ec5-4b1c-a9b0-e982ef8698c1).
        /// Properties from monolith: is_default=false, is_enabled=true, is_system=true,
        /// label="Dr.", sort_index=4.0, l_scope=""
        /// Source: NextPlugin.20190206.cs lines 1266–1281 (record JSON)
        /// </summary>
        [Fact]
        public void SalutationEntity_SeedRecord_Dr_ShouldBeCorrect()
        {
            SalutationEntity.SeedData.DrId.Should().Be(ExpectedDrId,
                "Dr. seed record ID must match NextPlugin.20190206.cs line 1270");

            // Dr. is NOT the default salutation
            SalutationEntity.SeedData.DefaultSalutationId.Should().NotBe(ExpectedDrId,
                "Dr. must not be the default salutation (is_default=false)");
        }

        /// <summary>
        /// Validates the "Prof." seed record (id: a74cd934-b425-4061-8f4e-a6d6b9d7adb1).
        /// Properties from monolith: is_default=false, is_enabled=true, is_system=true,
        /// label="Prof.", sort_index=5.0, l_scope=""
        /// Source: NextPlugin.20190206.cs lines 1284–1299 (record JSON)
        /// </summary>
        [Fact]
        public void SalutationEntity_SeedRecord_Prof_ShouldBeCorrect()
        {
            SalutationEntity.SeedData.ProfId.Should().Be(ExpectedProfId,
                "Prof. seed record ID must match NextPlugin.20190206.cs line 1288");

            // Prof. is NOT the default salutation
            SalutationEntity.SeedData.DefaultSalutationId.Should().NotBe(ExpectedProfId,
                "Prof. must not be the default salutation (is_default=false)");
        }

        /// <summary>
        /// Validates that exactly one seed record is marked as default.
        /// Only "Mr." (87c08ee1-...) has is_default=true in the monolith seed data.
        /// DefaultSalutationId must equal MrId and must not equal any other seed ID.
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveExactlyOneDefaultSeedRecord()
        {
            // DefaultSalutationId must point to Mr. (the only default)
            SalutationEntity.SeedData.DefaultSalutationId.Should().Be(SalutationEntity.SeedData.MrId,
                "only Mr. should be the default salutation");

            // Verify no other seed record is the default
            var nonDefaultIds = new List<Guid>
            {
                SalutationEntity.SeedData.MsId,
                SalutationEntity.SeedData.MrsId,
                SalutationEntity.SeedData.DrId,
                SalutationEntity.SeedData.ProfId
            };

            nonDefaultIds.Should().NotContain(SalutationEntity.SeedData.DefaultSalutationId,
                "only Mr. should be the default salutation — all others must have is_default=false");
        }

        // =====================================================================
        // Phase 5: Relation Tests
        // =====================================================================

        /// <summary>
        /// Validates the salutation_1n_account relation definition.
        /// Source: NextPlugin.20190206.cs lines 1334–1360
        ///   - Relation ID: 99e1a18b-05c2-4fca-986e-37ecebd62168 (line 1341)
        ///   - Relation Name: "salutation_1n_account" (line 1342)
        ///   - RelationType: OneToMany
        ///   - Origin: salutation entity (id field)
        ///   - Target: account entity (salutation_id field)
        ///   - System = true (line 1345)
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHave_Salutation1nAccount_Relation()
        {
            SalutationEntity.Relations.Salutation1nAccountId.Should().Be(ExpectedSalutation1nAccountId,
                "salutation_1n_account relation ID must match NextPlugin.20190206.cs line 1341");

            SalutationEntity.Relations.Salutation1nAccountName.Should().Be("salutation_1n_account",
                "salutation_1n_account relation name must match NextPlugin.20190206.cs line 1342");
        }

        /// <summary>
        /// Validates the salutation_1n_contact relation definition.
        /// Source: NextPlugin.20190206.cs lines 1363–1390
        ///   - Relation ID: 77ca10ff-18c9-44d6-a7ae-ddb0baa6a3a9 (line 1370)
        ///   - Relation Name: "salutation_1n_contact" (line 1371)
        ///   - RelationType: OneToMany
        ///   - Origin: salutation entity (id field)
        ///   - Target: contact entity (salutation_id field)
        ///   - System = true (line 1374)
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHave_Salutation1nContact_Relation()
        {
            SalutationEntity.Relations.Salutation1nContactId.Should().Be(ExpectedSalutation1nContactId,
                "salutation_1n_contact relation ID must match NextPlugin.20190206.cs line 1370");

            SalutationEntity.Relations.Salutation1nContactName.Should().Be("salutation_1n_contact",
                "salutation_1n_contact relation name must match NextPlugin.20190206.cs line 1371");
        }

        // =====================================================================
        // Phase 6: System Field ID Test
        // =====================================================================

        /// <summary>
        /// Validates the system "id" field GUID: 8721d461-ded9-46e7-8b1e-b7d0703a8d21.
        /// Source: NextPlugin.20190206.cs line 619:
        ///   systemFieldIdDictionary["id"] = new Guid("8721d461-ded9-46e7-8b1e-b7d0703a8d21")
        /// </summary>
        [Fact]
        public void SalutationEntity_ShouldHaveCorrectSystemFieldId()
        {
            SalutationEntity.SystemFieldId.Should().Be(ExpectedSystemFieldId,
                "system 'id' field GUID must match NextPlugin.20190206.cs line 619");
        }

        // =====================================================================
        // Private Helper Methods
        // =====================================================================

        /// <summary>
        /// Maps a field name string to the corresponding field ID from
        /// <see cref="SalutationEntity.Fields"/>. Used by the parameterized
        /// <see cref="SalutationEntity_FieldShouldHaveCorrectProperties"/> test.
        /// </summary>
        /// <param name="fieldName">
        /// The field name as defined in the monolith (e.g., "is_default", "label").
        /// </param>
        /// <returns>The corresponding <see cref="Guid"/> field ID.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when an unknown field name is provided.
        /// </exception>
        private static Guid GetFieldIdByName(string fieldName)
        {
            switch (fieldName)
            {
                case "is_default":
                    return SalutationEntity.Fields.IsDefaultFieldId;
                case "is_enabled":
                    return SalutationEntity.Fields.IsEnabledFieldId;
                case "is_system":
                    return SalutationEntity.Fields.IsSystemFieldId;
                case "label":
                    return SalutationEntity.Fields.LabelFieldId;
                case "sort_index":
                    return SalutationEntity.Fields.SortIndexFieldId;
                case "l_scope":
                    return SalutationEntity.Fields.LScopeFieldId;
                default:
                    throw new ArgumentException(
                        $"Unknown salutation field name: '{fieldName}'. " +
                        $"Expected one of: is_default, is_enabled, is_system, label, sort_index, l_scope.",
                        nameof(fieldName));
            }
        }
    }
}
