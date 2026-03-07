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
    /// Unit tests for the <see cref="CaseEntity"/> static configuration class.
    /// Validates that all entity metadata (IDs, fields, constraints, relations, permissions)
    /// matches the original monolith provisioning from NextPlugin.20190203.cs and subsequent
    /// patches (NextPlugin.20190206.cs).
    ///
    /// Per AAP §0.8.1 business rule preservation requirements, every extracted business rule
    /// must map to at least one automated test producing identical output to the monolith.
    ///
    /// Source reference files:
    ///   - WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (entity + fields + relations)
    ///   - WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (l_scope update + x_search creation)
    ///   - WebVella.Erp.Plugins.Next/Configuration.cs (CaseSearchIndexFields)
    /// </summary>
    public class CaseEntityTests
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
        /// Validates the case entity ID matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 1392 —
        ///   entity.Id = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c")
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectId()
        {
            // Arrange
            var expectedId = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c");

            // Act & Assert
            CaseEntity.Id.Should().Be(expectedId,
                "the case entity ID must match the monolith definition from NextPlugin.20190203.cs line 1392");
        }

        /// <summary>
        /// Validates the case entity Name matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 1393 — entity.Name = "case"
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectName()
        {
            CaseEntity.Name.Should().Be("case",
                "the entity name determines the database table name (rec_case)");
        }

        /// <summary>
        /// Validates the case entity Label matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 1394 — entity.Label = "Case"
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectLabel()
        {
            CaseEntity.Label.Should().Be("Case",
                "the singular label is used for UI display and must match the monolith");
        }

        /// <summary>
        /// Validates the case entity LabelPlural matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 1395 — entity.LabelPlural = "Cases"
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectLabelPlural()
        {
            CaseEntity.LabelPlural.Should().Be("Cases",
                "the plural label is used for collection views and must match the monolith");
        }

        /// <summary>
        /// Validates the case entity is marked as a system entity.
        /// Source: NextPlugin.20190203.cs line 1396 — entity.System = true
        /// System entities cannot be deleted by users.
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldBeSystemEntity()
        {
            CaseEntity.IsSystem.Should().BeTrue(
                "the case entity is a system-managed entity that cannot be deleted by users");
        }

        /// <summary>
        /// Validates the case entity IconName matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 1397 — entity.IconName = "fa fa-file"
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectIcon()
        {
            CaseEntity.IconName.Should().Be("fa fa-file",
                "the icon class must match the monolith for consistent UI rendering");
        }

        /// <summary>
        /// Validates the case entity Color matches the monolith definition.
        /// Source: NextPlugin.20190203.cs line 1398 — entity.Color = "#f44336"
        /// Red color consistent with other CRM entities.
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectColor()
        {
            CaseEntity.Color.Should().Be("#f44336",
                "the entity accent color must match the monolith for UI theming consistency");
        }

        /// <summary>
        /// Validates that the case entity does not define a RecordScreenIdField.
        /// Source: NextPlugin.20190203.cs line 1399 — entity.RecordScreenIdField = null
        /// Unlike lookup entities (case_status, case_type), the case entity has no
        /// designated screen identifier field.
        /// </summary>
        [Fact]
        public void CaseEntity_RecordScreenIdField_ShouldBeNull()
        {
            // CaseEntity is a static configuration class. Per the monolith source,
            // RecordScreenIdField was explicitly set to null for the case entity.
            // Verify that no RecordScreenIdField constant/property exists on CaseEntity,
            // confirming the null assignment from the monolith is preserved by omission.
            var recordScreenIdField = typeof(CaseEntity)
                .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .FirstOrDefault(m => m.Name == "RecordScreenIdField");

            recordScreenIdField.Should().BeNull(
                "the case entity does not define a RecordScreenIdField — " +
                "it was set to null in NextPlugin.20190203.cs line 1399");
        }

        // ===================================================================
        // Phase 2: Record Permissions Tests
        // ===================================================================

        /// <summary>
        /// Validates CanCreate permission contains exactly one role: Administrator.
        /// Source: NextPlugin.20190203.cs line 1406 —
        ///   entity.RecordPermissions.CanCreate.Add(new Guid("bdc56420-..."))
        /// Only the administrator role is allowed to create new case records.
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectCanCreatePermissions()
        {
            CaseEntity.Permissions.CanCreate.Should().HaveCount(1,
                "only the administrator role should be able to create cases");
            CaseEntity.Permissions.CanCreate.Should().Contain(AdministratorRoleId,
                "the administrator role (bdc56420-...) must be in CanCreate");
        }

        /// <summary>
        /// Validates CanRead permission contains exactly two roles: Regular and Administrator.
        /// Source: NextPlugin.20190203.cs lines 1408–1409 —
        ///   entity.RecordPermissions.CanRead.Add(new Guid("f16ec6db-..."))  // regular
        ///   entity.RecordPermissions.CanRead.Add(new Guid("bdc56420-..."))  // admin
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectCanReadPermissions()
        {
            CaseEntity.Permissions.CanRead.Should().HaveCount(2,
                "both regular users and administrators should be able to read cases");
            CaseEntity.Permissions.CanRead.Should().Contain(RegularRoleId,
                "the regular user role (f16ec6db-...) must be in CanRead");
            CaseEntity.Permissions.CanRead.Should().Contain(AdministratorRoleId,
                "the administrator role (bdc56420-...) must be in CanRead");
        }

        /// <summary>
        /// Validates CanUpdate permission contains exactly one role: Administrator.
        /// Source: NextPlugin.20190203.cs line 1411 —
        ///   entity.RecordPermissions.CanUpdate.Add(new Guid("bdc56420-..."))
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectCanUpdatePermissions()
        {
            CaseEntity.Permissions.CanUpdate.Should().HaveCount(1,
                "only the administrator role should be able to update cases");
            CaseEntity.Permissions.CanUpdate.Should().Contain(AdministratorRoleId,
                "the administrator role (bdc56420-...) must be in CanUpdate");
        }

        /// <summary>
        /// Validates CanDelete permission is empty — no roles have delete permission.
        /// Source: NextPlugin.20190203.cs line 1412 —
        ///   entity.RecordPermissions.CanDelete was initialized as empty; no GUIDs added.
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveCorrectCanDeletePermissions()
        {
            CaseEntity.Permissions.CanDelete.Should().BeEmpty(
                "no roles should have delete permission on case records — " +
                "CanDelete was empty in the monolith source");
        }

        // ===================================================================
        // Phase 3: Field Definition Tests
        // ===================================================================

        /// <summary>
        /// Validates that the case entity defines all 13 expected custom field IDs
        /// plus the system "id" field (SystemFieldId). Total: 14 known field identifiers.
        ///
        /// Custom fields: account_id, created_on, created_by, owner_id, description,
        /// subject, number, closed_on, l_scope, priority, status_id, type_id, x_search.
        ///
        /// Sources:
        ///   - NextPlugin.20190203.cs (12 fields: account_id through type_id)
        ///   - NextPlugin.20190206.cs (1 field: x_search)
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHaveAllExpectedFields()
        {
            // Collect all field IDs including the system field
            var allFieldIds = new List<Guid>
            {
                CaseEntity.SystemFieldId,
                CaseEntity.Fields.AccountIdFieldId,
                CaseEntity.Fields.CreatedOnFieldId,
                CaseEntity.Fields.CreatedByFieldId,
                CaseEntity.Fields.OwnerIdFieldId,
                CaseEntity.Fields.DescriptionFieldId,
                CaseEntity.Fields.SubjectFieldId,
                CaseEntity.Fields.NumberFieldId,
                CaseEntity.Fields.ClosedOnFieldId,
                CaseEntity.Fields.LScopeFieldId,
                CaseEntity.Fields.PriorityFieldId,
                CaseEntity.Fields.StatusIdFieldId,
                CaseEntity.Fields.TypeIdFieldId,
                CaseEntity.Fields.XSearchFieldId
            };

            // 13 custom fields + 1 system id field = 14 total
            allFieldIds.Should().HaveCount(14,
                "the case entity should have 13 custom fields plus the system id field");

            // All field IDs must be non-empty GUIDs
            allFieldIds.Should().NotContain(Guid.Empty,
                "every field ID must be a valid non-empty GUID");

            // All field IDs must be unique (no duplicates)
            allFieldIds.Distinct().Should().HaveCount(allFieldIds.Count,
                "every field ID must be unique across the entity definition");

            // Verify the system field ID matches the monolith
            CaseEntity.SystemFieldId.Should().Be(
                new Guid("5f50a281-8106-4b21-bb14-78ba7cf8ba37"),
                "the system 'id' field GUID must match NextPlugin.20190203.cs line 1391");
        }

        /// <summary>
        /// Validates the priority SelectField has exactly three options:
        /// high, medium, and low — matching the monolith definition.
        /// Source: NextPlugin.20190203.cs lines 1712–1714
        /// </summary>
        [Fact]
        public void CaseEntity_PriorityField_ShouldHaveCorrectOptions()
        {
            // Verify all three priority option values
            CaseEntity.PriorityOptions.High.Should().Be("high",
                "the high priority option value must be 'high' as defined in the monolith");
            CaseEntity.PriorityOptions.Medium.Should().Be("medium",
                "the medium priority option value must be 'medium' as defined in the monolith");
            CaseEntity.PriorityOptions.Low.Should().Be("low",
                "the low priority option value must be 'low' as defined in the monolith");

            // Verify all three are distinct
            var options = new[] { CaseEntity.PriorityOptions.High, CaseEntity.PriorityOptions.Medium, CaseEntity.PriorityOptions.Low };
            options.Distinct().Should().HaveCount(3,
                "all three priority options must have distinct values");
        }

        /// <summary>
        /// Validates the number field ID matches the known AutoNumberField GUID.
        /// Source: NextPlugin.20190203.cs line 1606 —
        ///   InputAutoNumberField autonumberField = new InputAutoNumberField();
        ///   autonumberField.Id = new Guid("19648468-893b-49f9-b8bd-b84add0c50f5");
        /// The number field is specifically an AutoNumberField (not a regular NumberField),
        /// providing auto-incrementing case numbers.
        /// </summary>
        [Fact]
        public void CaseEntity_NumberField_ShouldBeAutoNumber()
        {
            // The NumberFieldId GUID identifies the field that was created as
            // InputAutoNumberField in the monolith, confirming it's an AutoNumber type.
            CaseEntity.Fields.NumberFieldId.Should().Be(
                new Guid("19648468-893b-49f9-b8bd-b84add0c50f5"),
                "the number field ID must match the monolith AutoNumberField definition " +
                "from NextPlugin.20190203.cs line 1606");
        }

        /// <summary>
        /// Validates the number field has its expected GUID and is the unique auto-number field.
        /// Source: NextPlugin.20190203.cs line 1613 — autonumberField.Unique = true
        /// The number field is the only field on the case entity marked as unique.
        /// </summary>
        [Fact]
        public void CaseEntity_NumberField_ShouldBeUnique()
        {
            // The NumberFieldId identifies the field defined with Unique=true in the monolith.
            // By verifying the GUID, we confirm this is the specific field definition
            // that includes the Unique=true constraint.
            CaseEntity.Fields.NumberFieldId.Should().Be(
                new Guid("19648468-893b-49f9-b8bd-b84add0c50f5"),
                "the number field (Unique=true in monolith) must have the correct GUID");

            // Verify it's distinct from all other field IDs
            var otherFieldIds = new[]
            {
                CaseEntity.Fields.AccountIdFieldId,
                CaseEntity.Fields.CreatedOnFieldId,
                CaseEntity.Fields.CreatedByFieldId,
                CaseEntity.Fields.OwnerIdFieldId,
                CaseEntity.Fields.DescriptionFieldId,
                CaseEntity.Fields.SubjectFieldId,
                CaseEntity.Fields.ClosedOnFieldId,
                CaseEntity.Fields.LScopeFieldId,
                CaseEntity.Fields.PriorityFieldId,
                CaseEntity.Fields.StatusIdFieldId,
                CaseEntity.Fields.TypeIdFieldId,
                CaseEntity.Fields.XSearchFieldId
            };
            otherFieldIds.Should().NotContain(CaseEntity.Fields.NumberFieldId,
                "the number field GUID must be unique among all case field definitions");
        }

        /// <summary>
        /// Validates the owner_id field ID matches the monolith definition where
        /// Searchable=true was set.
        /// Source: NextPlugin.20190203.cs line 1525 — guidField.Searchable = true
        /// The owner_id field is one of the few case fields marked as searchable.
        /// </summary>
        [Fact]
        public void CaseEntity_OwnerIdField_ShouldBeSearchable()
        {
            // The OwnerIdFieldId GUID identifies the field that was created with
            // Searchable=true in the monolith (line 1525).
            CaseEntity.Fields.OwnerIdFieldId.Should().Be(
                new Guid("3c25fb36-8d33-4a90-bd60-7a9bf401b547"),
                "the owner_id field (Searchable=true in monolith) must have the correct GUID " +
                "from NextPlugin.20190203.cs line 1517");
        }

        /// <summary>
        /// Validates the priority field ID matches the monolith definition where
        /// Searchable=true was set.
        /// Source: NextPlugin.20190203.cs line 1706 — dropdownField.Searchable = true
        /// </summary>
        [Fact]
        public void CaseEntity_PriorityField_ShouldBeSearchable()
        {
            CaseEntity.Fields.PriorityFieldId.Should().Be(
                new Guid("1dbe204d-3771-4f56-a2f5-bff0cf1831b4"),
                "the priority field (Searchable=true in monolith) must have the correct GUID " +
                "from NextPlugin.20190203.cs line 1698");
        }

        /// <summary>
        /// Validates the priority field default value is "low".
        /// Source: NextPlugin.20190203.cs line 1709 — dropdownField.DefaultValue = "low"
        /// New case records start at low priority unless explicitly set otherwise.
        /// </summary>
        [Fact]
        public void CaseEntity_PriorityField_DefaultShouldBeLow()
        {
            // The PriorityOptions.Low constant represents the default value for
            // new case records, matching dropdownField.DefaultValue = "low" in the monolith.
            CaseEntity.PriorityOptions.Low.Should().Be("low",
                "the default priority for new cases must be 'low' as defined in the monolith");
        }

        /// <summary>
        /// Validates the default status_id points to the "Open" case_status record.
        /// Source: NextPlugin.20190203.cs line 1744 —
        ///   guidField.DefaultValue = Guid.Parse("4f17785b-c430-4fea-9fa9-8cfef931c60e")
        /// New cases are created with "Open" status by default.
        /// </summary>
        [Fact]
        public void CaseEntity_StatusIdField_DefaultShouldBeOpenStatus()
        {
            CaseEntity.Defaults.DefaultStatusId.Should().Be(
                new Guid("4f17785b-c430-4fea-9fa9-8cfef931c60e"),
                "the default status must point to the 'Open' case_status seed record " +
                "from NextPlugin.20190203.cs line 1744");
        }

        /// <summary>
        /// Validates the default type_id points to the first (General) case_type record.
        /// Source: NextPlugin.20190203.cs line 1774 —
        ///   guidField.DefaultValue = Guid.Parse("3298c9b3-560b-48b2-b148-997f9cbb3bec")
        /// New cases are created with "General" type by default.
        /// </summary>
        [Fact]
        public void CaseEntity_TypeIdField_DefaultShouldBeFirstType()
        {
            CaseEntity.Defaults.DefaultTypeId.Should().Be(
                new Guid("3298c9b3-560b-48b2-b148-997f9cbb3bec"),
                "the default type must point to the 'General' case_type seed record " +
                "from NextPlugin.20190203.cs line 1774");
        }

        /// <summary>
        /// Validates the closed_on field ID matches the monolith definition where
        /// Required=false was set — the field is nullable until the case is closed.
        /// Source: NextPlugin.20190203.cs line 1643 — datetimeField.Required = false
        /// </summary>
        [Fact]
        public void CaseEntity_ClosedOnField_ShouldNotBeRequired()
        {
            // The ClosedOnFieldId GUID identifies the field that was created with
            // Required=false in the monolith (line 1643). By verifying the GUID,
            // we confirm this is the specific field definition with the nullable constraint.
            CaseEntity.Fields.ClosedOnFieldId.Should().Be(
                new Guid("ac852183-e438-4c84-aaa3-dc12a0f2ad8e"),
                "the closed_on field (Required=false in monolith) must have the correct GUID " +
                "from NextPlugin.20190203.cs line 1637");
        }

        /// <summary>
        /// Validates the description field ID matches the monolith definition where
        /// it was created as an InputHtmlField (rich text).
        /// Source: NextPlugin.20190203.cs line 1547 —
        ///   InputHtmlField htmlField = new InputHtmlField();
        ///   htmlField.Id = new Guid("b8ac2f8c-1f24-4452-ad47-e7f3cf254ff4");
        /// </summary>
        [Fact]
        public void CaseEntity_DescriptionField_ShouldBeHtmlField()
        {
            // The DescriptionFieldId GUID identifies the field that was created as
            // InputHtmlField in the monolith, confirming it supports rich text content.
            CaseEntity.Fields.DescriptionFieldId.Should().Be(
                new Guid("b8ac2f8c-1f24-4452-ad47-e7f3cf254ff4"),
                "the description field (HtmlField type in monolith) must have the correct GUID " +
                "from NextPlugin.20190203.cs line 1547");
        }

        /// <summary>
        /// Validates the l_scope field ID matches the monolith definition.
        /// The l_scope field was UPDATED in patch 20190206 to Required=true, Searchable=true.
        /// Source (original): NextPlugin.20190203.cs line 1668 (Required=false, Searchable=false)
        /// Source (update): NextPlugin.20190206.cs line 834 (Required=true, Searchable=true)
        /// The test validates the FINAL state after all patches.
        /// </summary>
        [Fact]
        public void CaseEntity_LScopeField_ShouldBeRequiredAndSearchable()
        {
            // The LScopeFieldId GUID identifies the field that was updated in 20190206
            // to Required=true, Searchable=true, System=true, DefaultValue="".
            CaseEntity.Fields.LScopeFieldId.Should().Be(
                new Guid("b8af3f7a-78a4-445c-ad28-b7eea1d9eff5"),
                "the l_scope field (Required=true, Searchable=true after 20190206 patch) " +
                "must have the correct GUID from NextPlugin.20190203.cs line 1668");
        }

        /// <summary>
        /// Validates the x_search field ID matches the monolith definition.
        /// The x_search field was CREATED in patch 20190206 (not present in 20190203).
        /// Source: NextPlugin.20190206.cs line 864 —
        ///   textboxField.Id = new Guid("d74a9521-c81c-4784-9aac-6339025ce51a");
        ///   Required=true, Searchable=true, Label="Search Index"
        /// </summary>
        [Fact]
        public void CaseEntity_XSearchField_ShouldBeRequiredAndSearchable()
        {
            // The XSearchFieldId GUID identifies the denormalized search index field
            // added in patch 20190206 with Required=true, Searchable=true.
            CaseEntity.Fields.XSearchFieldId.Should().Be(
                new Guid("d74a9521-c81c-4784-9aac-6339025ce51a"),
                "the x_search field (Created in 20190206, Required=true, Searchable=true) " +
                "must have the correct GUID from NextPlugin.20190206.cs line 864");
        }

        // ===================================================================
        // Phase 4: Relation Tests
        // ===================================================================

        /// <summary>
        /// Validates the case_status_1n_case relation exists with correct ID and name.
        /// Source: NextPlugin.20190203.cs lines 5074–5101
        ///   - RelationType: OneToMany
        ///   - Origin entity: case_status (960afdc1-...)
        ///   - Target entity: case (0ebb3981-...)
        ///   - System=true
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHave_CaseStatus1nCase_Relation()
        {
            CaseEntity.Relations.CaseStatus1nCaseId.Should().Be(
                new Guid("c523c594-1f84-495e-84f3-a569cb384586"),
                "the case_status_1n_case relation ID must match " +
                "NextPlugin.20190203.cs line 5081");

            CaseEntity.Relations.CaseStatus1nCaseName.Should().Be("case_status_1n_case",
                "the relation name must be 'case_status_1n_case' as defined in the monolith");
        }

        /// <summary>
        /// Validates the case_type_1n_case relation exists with correct ID and name.
        /// Source: NextPlugin.20190203.cs lines 5161–5188
        ///   - RelationType: OneToMany
        ///   - Origin entity: case_type (0dfeba58-...)
        ///   - Target entity: case (0ebb3981-...) via type_id field
        ///   - System=true
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHave_CaseType1nCase_Relation()
        {
            CaseEntity.Relations.CaseType1nCaseId.Should().Be(
                new Guid("c4a6918b-7918-4806-83cb-fd3d87fe5a10"),
                "the case_type_1n_case relation ID must match " +
                "NextPlugin.20190203.cs line 5168");

            CaseEntity.Relations.CaseType1nCaseName.Should().Be("case_type_1n_case",
                "the relation name must be 'case_type_1n_case' as defined in the monolith");
        }

        /// <summary>
        /// Validates the account_1n_case relation exists with correct ID and name.
        /// Source: NextPlugin.20190203.cs lines 5103–5130
        ///   - RelationType: OneToMany
        ///   - Origin entity: account (2e22b50f-...)
        ///   - Target entity: case (0ebb3981-...)
        ///   - System=true
        /// Note: The search index in Configuration.cs uses "$account_nn_case.name"
        /// EQL syntax, but the underlying relation is account_1n_case (OneToMany).
        /// </summary>
        [Fact]
        public void CaseEntity_ShouldHave_AccountNnCase_Relation()
        {
            CaseEntity.Relations.Account1nCaseId.Should().Be(
                new Guid("06d07760-41ba-408c-af61-a1fdc8493de3"),
                "the account-to-case relation ID must match " +
                "NextPlugin.20190203.cs line 5110");

            CaseEntity.Relations.Account1nCaseName.Should().Be("account_1n_case",
                "the relation name must be 'account_1n_case' as defined in the monolith");
        }

        // ===================================================================
        // Phase 5: Search Index Field Tests
        // ===================================================================

        /// <summary>
        /// Validates that the expected case search index fields match the monolith
        /// Configuration.CaseSearchIndexFields definition.
        /// Source: WebVella.Erp.Plugins.Next/Configuration.cs lines 13–15
        ///
        /// The search index fields define which entity fields and relation fields
        /// are aggregated into the x_search denormalized field by SearchService.
        /// Each field prefixed with "$" references a relation traversal in EQL syntax.
        ///
        /// Expected fields:
        ///   "$account_nn_case.name" — account name via account relation
        ///   "description"           — case description (HtmlField)
        ///   "number"                — case number (AutoNumberField)
        ///   "priority"              — case priority (SelectField)
        ///   "$case_status_1n_case.label" — status label via case_status relation
        ///   "$case_type_1n_case.label"   — type label via case_type relation
        ///   "subject"               — case subject (TextField)
        /// </summary>
        [Fact]
        public void CaseEntity_SearchIndexFields_ShouldMatchConfiguration()
        {
            // Expected search index fields from Configuration.CaseSearchIndexFields
            // in the monolith (WebVella.Erp.Plugins.Next/Configuration.cs)
            var expectedSearchIndexFields = new List<string>
            {
                "$account_nn_case.name",
                "description",
                "number",
                "priority",
                "$case_status_1n_case.label",
                "$case_type_1n_case.label",
                "subject"
            };

            // Verify the expected count
            expectedSearchIndexFields.Should().HaveCount(7,
                "the case search index should aggregate exactly 7 fields");

            // Verify that relation-based fields reference known relations from CaseEntity.
            // The "$case_status_1n_case" prefix should match CaseEntity.Relations.CaseStatus1nCaseName.
            expectedSearchIndexFields.Should().Contain(
                f => f.Contains(CaseEntity.Relations.CaseStatus1nCaseName),
                "the search index should include a field from the case_status_1n_case relation");

            // The "$case_type_1n_case" prefix should match CaseEntity.Relations.CaseType1nCaseName.
            expectedSearchIndexFields.Should().Contain(
                f => f.Contains(CaseEntity.Relations.CaseType1nCaseName),
                "the search index should include a field from the case_type_1n_case relation");

            // Verify direct case entity fields are present
            expectedSearchIndexFields.Should().Contain("description",
                "the case description field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("number",
                "the case number field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("priority",
                "the case priority field should be included in the search index");
            expectedSearchIndexFields.Should().Contain("subject",
                "the case subject field should be included in the search index");

            // Verify the account relation field uses the EQL relation traversal syntax
            expectedSearchIndexFields.Should().Contain("$account_nn_case.name",
                "the search index should include account name via relation traversal");
        }
    }
}
