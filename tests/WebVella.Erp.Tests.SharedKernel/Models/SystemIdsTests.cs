using System;
using System.Linq;
using System.Reflection;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.SharedKernel.Models
{
    /// <summary>
    /// Unit tests for <see cref="SystemIds"/> class and shared enums
    /// (<see cref="RecordsListTypes"/>, <see cref="FilterOperatorTypes"/>, <see cref="RecordViewLayouts"/>)
    /// from the SharedKernel project.
    ///
    /// These tests are CRITICAL for backward compatibility — the GUID values and enum integer values
    /// are the foundation of the identity and query system across all microservices. If ANY value
    /// changes, the entire system will fail to locate entities, users, or roles.
    ///
    /// All expected GUID values are verified character-by-character against the original monolith
    /// source at WebVella.Erp/Api/Definitions.cs.
    /// </summary>
    public class SystemIdsTests
    {
        // ════════════════════════════════════════════════════════════════════════
        // SECTION 1: SystemIds — Exact GUID Value Verification
        // Each GUID is verified against the monolith Definitions.cs source.
        // These GUIDs are used across the entire application for entity/user/role
        // identification. If ANY value is wrong, the system will fail.
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void SystemEntityId_HasExactValue()
        {
            // Monolith source: new Guid("a5050ac8-5967-4ce1-95e7-a79b054f9d14")
            SystemIds.SystemEntityId.Should().Be(new Guid("a5050ac8-5967-4ce1-95e7-a79b054f9d14"));
        }

        [Fact]
        public void UserEntityId_HasExactValue()
        {
            // Monolith source: new Guid("b9cebc3b-6443-452a-8e34-b311a73dcc8b")
            SystemIds.UserEntityId.Should().Be(new Guid("b9cebc3b-6443-452a-8e34-b311a73dcc8b"));
        }

        [Fact]
        public void RoleEntityId_HasExactValue()
        {
            // Monolith source: new Guid("c4541fee-fbb6-4661-929e-1724adec285a")
            SystemIds.RoleEntityId.Should().Be(new Guid("c4541fee-fbb6-4661-929e-1724adec285a"));
        }

        [Fact]
        public void AreaEntityId_HasExactValue()
        {
            // Monolith source: new Guid("cb434298-8583-4a96-bdbb-97b2c1764192")
            // Note: This is a static field (not a property) in the monolith source
            SystemIds.AreaEntityId.Should().Be(new Guid("cb434298-8583-4a96-bdbb-97b2c1764192"));
        }

        [Fact]
        public void UserRoleRelationId_HasExactValue()
        {
            // Monolith source: new Guid("0C4B119E-1D7B-4B40-8D2C-9E447CC656AB")
            SystemIds.UserRoleRelationId.Should().Be(new Guid("0C4B119E-1D7B-4B40-8D2C-9E447CC656AB"));
        }

        [Fact]
        public void AdministratorRoleId_HasExactValue()
        {
            // Monolith source: new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA")
            // This GUID is specifically used by ErpUser.IsAdmin computed property
            SystemIds.AdministratorRoleId.Should().Be(new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA"));
        }

        [Fact]
        public void RegularRoleId_HasExactValue()
        {
            // Monolith source: new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F")
            SystemIds.RegularRoleId.Should().Be(new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F"));
        }

        [Fact]
        public void GuestRoleId_HasExactValue()
        {
            // Monolith source: new Guid("987148B1-AFA8-4B33-8616-55861E5FD065")
            SystemIds.GuestRoleId.Should().Be(new Guid("987148B1-AFA8-4B33-8616-55861E5FD065"));
        }

        [Fact]
        public void SystemUserId_HasExactValue()
        {
            // Monolith source: new Guid("10000000-0000-0000-0000-000000000000")
            SystemIds.SystemUserId.Should().Be(new Guid("10000000-0000-0000-0000-000000000000"));
        }

        [Fact]
        public void FirstUserId_HasExactValue()
        {
            // Monolith source: new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2")
            SystemIds.FirstUserId.Should().Be(new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2"));
        }

        // ════════════════════════════════════════════════════════════════════════
        // SECTION 2: SystemIds — Immutability and Structural Guarantees
        // Verify that all IDs are static, non-empty, unique, and consistent.
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void AllSystemIds_AreStatic()
        {
            // Verify all 10 SystemIds members are static members of the SystemIds class.
            // This is critical because these are accessed as SystemIds.PropertyName
            // across the entire codebase without instantiation.
            var systemIdsType = typeof(SystemIds);

            var staticMembers = systemIdsType
                .GetMembers(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.MemberType == MemberTypes.Property || m.MemberType == MemberTypes.Field)
                .Where(m =>
                {
                    if (m is PropertyInfo pi) return pi.PropertyType == typeof(Guid);
                    if (m is FieldInfo fi) return fi.FieldType == typeof(Guid);
                    return false;
                })
                .Select(m => m.Name)
                .ToList();

            // All 10 well-known IDs must be present as static members
            staticMembers.Should().Contain("SystemEntityId");
            staticMembers.Should().Contain("UserEntityId");
            staticMembers.Should().Contain("RoleEntityId");
            staticMembers.Should().Contain("AreaEntityId");
            staticMembers.Should().Contain("UserRoleRelationId");
            staticMembers.Should().Contain("AdministratorRoleId");
            staticMembers.Should().Contain("RegularRoleId");
            staticMembers.Should().Contain("GuestRoleId");
            staticMembers.Should().Contain("SystemUserId");
            staticMembers.Should().Contain("FirstUserId");

            staticMembers.Count.Should().BeGreaterThanOrEqualTo(10);
        }

        [Fact]
        public void SystemIds_AreNotEmpty()
        {
            // None of the system IDs should ever be Guid.Empty.
            // An empty GUID would indicate a configuration or initialization failure.
            SystemIds.SystemEntityId.Should().NotBe(Guid.Empty);
            SystemIds.UserEntityId.Should().NotBe(Guid.Empty);
            SystemIds.RoleEntityId.Should().NotBe(Guid.Empty);
            SystemIds.AreaEntityId.Should().NotBe(Guid.Empty);
            SystemIds.UserRoleRelationId.Should().NotBe(Guid.Empty);
            SystemIds.AdministratorRoleId.Should().NotBe(Guid.Empty);
            SystemIds.RegularRoleId.Should().NotBe(Guid.Empty);
            SystemIds.GuestRoleId.Should().NotBe(Guid.Empty);
            SystemIds.SystemUserId.Should().NotBe(Guid.Empty);
            SystemIds.FirstUserId.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public void AllSystemIds_AreUnique()
        {
            // All 10 system IDs must be distinct from each other.
            // Duplicate GUIDs would cause entity/user/role identity collisions.
            var allIds = new[]
            {
                SystemIds.SystemEntityId,
                SystemIds.UserEntityId,
                SystemIds.RoleEntityId,
                SystemIds.AreaEntityId,
                SystemIds.UserRoleRelationId,
                SystemIds.AdministratorRoleId,
                SystemIds.RegularRoleId,
                SystemIds.GuestRoleId,
                SystemIds.SystemUserId,
                SystemIds.FirstUserId
            };

            allIds.Distinct().Count().Should().Be(allIds.Length,
                "all 10 SystemIds GUIDs must be unique — duplicate IDs would cause identity collisions");
        }

        [Fact]
        public void SystemIds_ConsistentOnMultipleAccess()
        {
            // The monolith implementation uses `new Guid(...)` inside property getters,
            // which creates a new Guid instance on each access. Verify that the value
            // returned is consistent across multiple accesses (Guid is a value type,
            // so equality is value-based, not reference-based).
            var systemEntityId1 = SystemIds.SystemEntityId;
            var systemEntityId2 = SystemIds.SystemEntityId;
            systemEntityId1.Should().Be(systemEntityId2);

            var userEntityId1 = SystemIds.UserEntityId;
            var userEntityId2 = SystemIds.UserEntityId;
            userEntityId1.Should().Be(userEntityId2);

            var roleEntityId1 = SystemIds.RoleEntityId;
            var roleEntityId2 = SystemIds.RoleEntityId;
            roleEntityId1.Should().Be(roleEntityId2);

            var areaEntityId1 = SystemIds.AreaEntityId;
            var areaEntityId2 = SystemIds.AreaEntityId;
            areaEntityId1.Should().Be(areaEntityId2);

            var userRoleRelationId1 = SystemIds.UserRoleRelationId;
            var userRoleRelationId2 = SystemIds.UserRoleRelationId;
            userRoleRelationId1.Should().Be(userRoleRelationId2);

            var adminRoleId1 = SystemIds.AdministratorRoleId;
            var adminRoleId2 = SystemIds.AdministratorRoleId;
            adminRoleId1.Should().Be(adminRoleId2);

            var regularRoleId1 = SystemIds.RegularRoleId;
            var regularRoleId2 = SystemIds.RegularRoleId;
            regularRoleId1.Should().Be(regularRoleId2);

            var guestRoleId1 = SystemIds.GuestRoleId;
            var guestRoleId2 = SystemIds.GuestRoleId;
            guestRoleId1.Should().Be(guestRoleId2);

            var systemUserId1 = SystemIds.SystemUserId;
            var systemUserId2 = SystemIds.SystemUserId;
            systemUserId1.Should().Be(systemUserId2);

            var firstUserId1 = SystemIds.FirstUserId;
            var firstUserId2 = SystemIds.FirstUserId;
            firstUserId1.Should().Be(firstUserId2);
        }

        // ════════════════════════════════════════════════════════════════════════
        // SECTION 3: RecordsListTypes Enum Tests
        // Verify exact integer values and member count for backward compatibility.
        // Monolith source: enum RecordsListTypes { SearchPopup = 1, List, Custom }
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void RecordsListTypes_SearchPopup_HasValue1()
        {
            ((int)RecordsListTypes.SearchPopup).Should().Be(1);
        }

        [Fact]
        public void RecordsListTypes_List_HasValue2()
        {
            // Implicit increment from SearchPopup = 1
            ((int)RecordsListTypes.List).Should().Be(2);
        }

        [Fact]
        public void RecordsListTypes_Custom_HasValue3()
        {
            // Implicit increment from List = 2
            ((int)RecordsListTypes.Custom).Should().Be(3);
        }

        [Fact]
        public void RecordsListTypes_HasExactly3Members()
        {
            // Guard against accidental addition or removal of enum members
            Enum.GetValues(typeof(RecordsListTypes)).Length.Should().Be(3,
                "RecordsListTypes must have exactly 3 members: SearchPopup, List, Custom");
        }

        // ════════════════════════════════════════════════════════════════════════
        // SECTION 4: FilterOperatorTypes Enum Tests
        // Verify exact integer values for all 12 filter operators.
        // These values are serialized in saved views, list filters, and EQL queries
        // across the system — changing them would break all persisted filter configs.
        // Monolith source: enum FilterOperatorTypes { Equals = 1, NotEqualTo, ... Within }
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void FilterOperatorTypes_Equals_HasValue1()
        {
            ((int)FilterOperatorTypes.Equals).Should().Be(1);
        }

        [Fact]
        public void FilterOperatorTypes_NotEqualTo_HasValue2()
        {
            ((int)FilterOperatorTypes.NotEqualTo).Should().Be(2);
        }

        [Fact]
        public void FilterOperatorTypes_StartsWith_HasValue3()
        {
            ((int)FilterOperatorTypes.StartsWith).Should().Be(3);
        }

        [Fact]
        public void FilterOperatorTypes_Contains_HasValue4()
        {
            ((int)FilterOperatorTypes.Contains).Should().Be(4);
        }

        [Fact]
        public void FilterOperatorTypes_DoesNotContain_HasValue5()
        {
            ((int)FilterOperatorTypes.DoesNotContain).Should().Be(5);
        }

        [Fact]
        public void FilterOperatorTypes_LessThan_HasValue6()
        {
            ((int)FilterOperatorTypes.LessThan).Should().Be(6);
        }

        [Fact]
        public void FilterOperatorTypes_GreaterThan_HasValue7()
        {
            ((int)FilterOperatorTypes.GreaterThan).Should().Be(7);
        }

        [Fact]
        public void FilterOperatorTypes_LessOrEqual_HasValue8()
        {
            ((int)FilterOperatorTypes.LessOrEqual).Should().Be(8);
        }

        [Fact]
        public void FilterOperatorTypes_GreaterOrEqual_HasValue9()
        {
            ((int)FilterOperatorTypes.GreaterOrEqual).Should().Be(9);
        }

        [Fact]
        public void FilterOperatorTypes_Includes_HasValue10()
        {
            ((int)FilterOperatorTypes.Includes).Should().Be(10);
        }

        [Fact]
        public void FilterOperatorTypes_Excludes_HasValue11()
        {
            ((int)FilterOperatorTypes.Excludes).Should().Be(11);
        }

        [Fact]
        public void FilterOperatorTypes_Within_HasValue12()
        {
            ((int)FilterOperatorTypes.Within).Should().Be(12);
        }

        [Fact]
        public void FilterOperatorTypes_HasExactly12Members()
        {
            // Guard against accidental addition or removal of enum members
            Enum.GetValues(typeof(FilterOperatorTypes)).Length.Should().Be(12,
                "FilterOperatorTypes must have exactly 12 members matching the monolith definition");
        }

        // ════════════════════════════════════════════════════════════════════════
        // SECTION 5: RecordViewLayouts Enum Tests
        // Verify exact integer values for view layout types.
        // Monolith source: enum RecordViewLayouts { OneColumn = 1, TwoColumns }
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void RecordViewLayouts_OneColumn_HasValue1()
        {
            ((int)RecordViewLayouts.OneColumn).Should().Be(1);
        }

        [Fact]
        public void RecordViewLayouts_TwoColumns_HasValue2()
        {
            // Implicit increment from OneColumn = 1
            ((int)RecordViewLayouts.TwoColumns).Should().Be(2);
        }

        [Fact]
        public void RecordViewLayouts_HasExactly2Members()
        {
            // Guard against accidental addition or removal of enum members
            Enum.GetValues(typeof(RecordViewLayouts)).Length.Should().Be(2,
                "RecordViewLayouts must have exactly 2 members: OneColumn, TwoColumns");
        }
    }
}
