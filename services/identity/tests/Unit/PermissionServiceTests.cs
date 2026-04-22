using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Identity.Models;
using WebVellaErp.Identity.Services;
using Xunit;

namespace WebVellaErp.Identity.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="PermissionService"/> — the stateless permission checking
    /// service extracted from the monolith's <c>SecurityContext</c> class
    /// (<c>WebVella.Erp/Api/SecurityContext.cs</c> lines 45-118).
    ///
    /// <para>
    /// Tests achieve EXACT behavioral parity with the original <c>SecurityContext</c>
    /// static methods. <see cref="PermissionService"/> is a pure logic service with no
    /// AWS dependencies — only an <see cref="ILogger{T}"/> mock is required.
    /// </para>
    ///
    /// <para>Coverage target: &gt;80% per AAP Section 0.8.4.</para>
    /// </summary>
    public class PermissionServiceTests
    {
        private readonly PermissionService _sut;
        private readonly Mock<ILogger<PermissionService>> _mockLogger;

        /// <summary>
        /// Constructor — creates the system under test with a mocked logger.
        /// PermissionService is fully stateless; the mock logger is the only dependency.
        /// </summary>
        public PermissionServiceTests()
        {
            _mockLogger = new Mock<ILogger<PermissionService>>();
            _sut = new PermissionService(_mockLogger.Object);
        }

        #region Helper Methods

        /// <summary>
        /// Creates a test <see cref="User"/> with the specified ID and role assignments.
        /// Populates <see cref="User.Roles"/> by converting each role ID to a
        /// <see cref="Role"/> instance via LINQ Select/ToList.
        /// </summary>
        /// <param name="userId">The user's unique identifier.</param>
        /// <param name="roleIds">Zero or more role IDs to assign to the user.</param>
        /// <returns>A fully constructed <see cref="User"/> with the specified roles.</returns>
        private static User CreateTestUser(Guid userId, params Guid[] roleIds)
        {
            return new User
            {
                Id = userId,
                Roles = roleIds.Select(id => new Role { Id = id }).ToList()
            };
        }

        /// <summary>
        /// Creates an <see cref="EntityPermissionSet"/> with the specified role ID lists
        /// for each CRUD operation. Null lists are replaced with empty lists for safety.
        /// </summary>
        /// <param name="canRead">Role IDs authorized for Read operations.</param>
        /// <param name="canCreate">Role IDs authorized for Create operations.</param>
        /// <param name="canUpdate">Role IDs authorized for Update operations.</param>
        /// <param name="canDelete">Role IDs authorized for Delete operations.</param>
        /// <returns>A fully constructed <see cref="EntityPermissionSet"/>.</returns>
        private static EntityPermissionSet CreateTestPermissions(
            List<Guid>? canRead = null,
            List<Guid>? canCreate = null,
            List<Guid>? canUpdate = null,
            List<Guid>? canDelete = null)
        {
            return new EntityPermissionSet
            {
                CanRead = canRead ?? new List<Guid>(),
                CanCreate = canCreate ?? new List<Guid>(),
                CanUpdate = canUpdate ?? new List<Guid>(),
                CanDelete = canDelete ?? new List<Guid>()
            };
        }

        #endregion

        #region Phase 2: HasEntityPermission — Authenticated User Tests

        /// <summary>
        /// Verifies: Authenticated user with a role that appears in CanRead returns true.
        /// Source: SecurityContext.cs line 80 —
        /// <c>return user.Roles.Any(x =&gt; entity.RecordPermissions.CanRead.Any(z =&gt; z == x.Id));</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_Read_AuthenticatedUserWithMatchingRole_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), roleId);
            var permissions = CreateTestPermissions(canRead: new List<Guid> { roleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Read, permissions, user);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: Authenticated user whose role does NOT appear in CanRead returns false.
        /// Source: SecurityContext.cs line 80 — no matching role ID in CanRead list.
        /// </summary>
        [Fact]
        public void HasEntityPermission_Read_AuthenticatedUserWithoutMatchingRole_ReturnsFalse()
        {
            // Arrange
            var userRoleId = Guid.NewGuid();
            var otherRoleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), userRoleId);
            var permissions = CreateTestPermissions(canRead: new List<Guid> { otherRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Read, permissions, user);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: Authenticated user with a role that appears in CanCreate returns true.
        /// Source: SecurityContext.cs line 82 —
        /// <c>user.Roles.Any(x =&gt; entity.RecordPermissions.CanCreate.Any(z =&gt; z == x.Id))</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_Create_AuthenticatedUserWithMatchingRole_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), roleId);
            var permissions = CreateTestPermissions(canCreate: new List<Guid> { roleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Create, permissions, user);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: Authenticated user whose role does NOT appear in CanCreate returns false.
        /// Source: SecurityContext.cs line 82 — no matching role ID in CanCreate list.
        /// </summary>
        [Fact]
        public void HasEntityPermission_Create_AuthenticatedUserWithoutMatchingRole_ReturnsFalse()
        {
            // Arrange
            var userRoleId = Guid.NewGuid();
            var otherRoleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), userRoleId);
            var permissions = CreateTestPermissions(canCreate: new List<Guid> { otherRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Create, permissions, user);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: Authenticated user with a role that appears in CanUpdate returns true.
        /// Source: SecurityContext.cs line 84 —
        /// <c>user.Roles.Any(x =&gt; entity.RecordPermissions.CanUpdate.Any(z =&gt; z == x.Id))</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_Update_AuthenticatedUserWithMatchingRole_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), roleId);
            var permissions = CreateTestPermissions(canUpdate: new List<Guid> { roleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Update, permissions, user);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: Authenticated user whose role does NOT appear in CanUpdate returns false.
        /// Source: SecurityContext.cs line 84 — no matching role ID in CanUpdate list.
        /// </summary>
        [Fact]
        public void HasEntityPermission_Update_AuthenticatedUserWithoutMatchingRole_ReturnsFalse()
        {
            // Arrange
            var userRoleId = Guid.NewGuid();
            var otherRoleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), userRoleId);
            var permissions = CreateTestPermissions(canUpdate: new List<Guid> { otherRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Update, permissions, user);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: Authenticated user with a role that appears in CanDelete returns true.
        /// Source: SecurityContext.cs line 86 —
        /// <c>user.Roles.Any(x =&gt; entity.RecordPermissions.CanDelete.Any(z =&gt; z == x.Id))</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_Delete_AuthenticatedUserWithMatchingRole_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), roleId);
            var permissions = CreateTestPermissions(canDelete: new List<Guid> { roleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Delete, permissions, user);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: Authenticated user whose role does NOT appear in CanDelete returns false.
        /// Source: SecurityContext.cs line 86 — no matching role ID in CanDelete list.
        /// </summary>
        [Fact]
        public void HasEntityPermission_Delete_AuthenticatedUserWithoutMatchingRole_ReturnsFalse()
        {
            // Arrange
            var userRoleId = Guid.NewGuid();
            var otherRoleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), userRoleId);
            var permissions = CreateTestPermissions(canDelete: new List<Guid> { otherRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Delete, permissions, user);

            // Assert
            result.Should().BeFalse();
        }

        #endregion

        #region Phase 3: HasEntityPermission — System User Tests

        /// <summary>
        /// Verifies: System user (Id == SystemUserId) always returns true for Read,
        /// even with empty permission lists.
        /// Source: SecurityContext.cs lines 73-75 —
        /// <c>if (user.Id == SystemIds.SystemUserId) return true;</c>
        /// Comment: "system user has unlimited permissions :)"
        /// </summary>
        [Fact]
        public void HasEntityPermission_SystemUser_AlwaysReturnsTrue_ForRead()
        {
            // Arrange — system user with no roles, empty permission lists
            var systemUser = CreateTestUser(User.SystemUserId);
            var permissions = CreateTestPermissions(); // all empty

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Read, permissions, systemUser);

            // Assert — system user bypasses ALL permission checks
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: System user always returns true for Create regardless of permissions.
        /// Source: SecurityContext.cs lines 73-75.
        /// </summary>
        [Fact]
        public void HasEntityPermission_SystemUser_AlwaysReturnsTrue_ForCreate()
        {
            // Arrange
            var systemUser = CreateTestUser(User.SystemUserId);
            var permissions = CreateTestPermissions();

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Create, permissions, systemUser);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: System user always returns true for Update regardless of permissions.
        /// Source: SecurityContext.cs lines 73-75.
        /// </summary>
        [Fact]
        public void HasEntityPermission_SystemUser_AlwaysReturnsTrue_ForUpdate()
        {
            // Arrange
            var systemUser = CreateTestUser(User.SystemUserId);
            var permissions = CreateTestPermissions();

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Update, permissions, systemUser);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: System user always returns true for Delete regardless of permissions.
        /// Source: SecurityContext.cs lines 73-75.
        /// </summary>
        [Fact]
        public void HasEntityPermission_SystemUser_AlwaysReturnsTrue_ForDelete()
        {
            // Arrange
            var systemUser = CreateTestUser(User.SystemUserId);
            var permissions = CreateTestPermissions();

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Delete, permissions, systemUser);

            // Assert
            result.Should().BeTrue();
        }

        #endregion

        #region Phase 4: HasEntityPermission — Guest (Null User) Tests

        /// <summary>
        /// Verifies: When user is null, Read permission checks GuestRoleId in CanRead.
        /// Source: SecurityContext.cs line 96 —
        /// <c>entity.RecordPermissions.CanRead.Any(z =&gt; z == SystemIds.GuestRoleId)</c>
        /// GuestRoleId = 987148B1-AFA8-4B33-8616-55861E5FD065 (Definitions.cs line 17).
        /// </summary>
        [Fact]
        public void HasEntityPermission_NullUser_Read_ChecksGuestRoleId()
        {
            // Arrange
            var permissions = CreateTestPermissions(
                canRead: new List<Guid> { Role.GuestRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Read, permissions, null);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: When user is null and GuestRoleId is NOT in CanRead, returns false.
        /// Source: SecurityContext.cs line 96 — no matching GuestRoleId.
        /// </summary>
        [Fact]
        public void HasEntityPermission_NullUser_Read_GuestNotInCanRead_ReturnsFalse()
        {
            // Arrange — CanRead contains a different role, not GuestRoleId
            var permissions = CreateTestPermissions(
                canRead: new List<Guid> { Guid.NewGuid() });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Read, permissions, null);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: When user is null, Create permission checks GuestRoleId in CanCreate.
        /// Source: SecurityContext.cs line 98 —
        /// <c>entity.RecordPermissions.CanCreate.Any(z =&gt; z == SystemIds.GuestRoleId)</c>
        /// Verify GuestRoleId = Guid("987148B1-AFA8-4B33-8616-55861E5FD065") from Definitions.cs line 17.
        /// </summary>
        [Fact]
        public void HasEntityPermission_NullUser_Create_ChecksGuestRoleId()
        {
            // Arrange
            var permissions = CreateTestPermissions(
                canCreate: new List<Guid> { Role.GuestRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Create, permissions, null);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: When user is null, Update permission checks GuestRoleId in CanUpdate.
        /// Source: SecurityContext.cs line 100 —
        /// <c>entity.RecordPermissions.CanUpdate.Any(z =&gt; z == SystemIds.GuestRoleId)</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_NullUser_Update_ChecksGuestRoleId()
        {
            // Arrange
            var permissions = CreateTestPermissions(
                canUpdate: new List<Guid> { Role.GuestRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Update, permissions, null);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: When user is null, Delete permission checks GuestRoleId in CanDelete.
        /// Source: SecurityContext.cs line 102 —
        /// <c>entity.RecordPermissions.CanDelete.Any(z =&gt; z == SystemIds.GuestRoleId)</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_NullUser_Delete_ChecksGuestRoleId()
        {
            // Arrange
            var permissions = CreateTestPermissions(
                canDelete: new List<Guid> { Role.GuestRoleId });

            // Act
            var result = _sut.HasEntityPermission(EntityPermission.Delete, permissions, null);

            // Assert
            result.Should().BeTrue();
        }

        #endregion

        #region Phase 5: HasEntityPermission — Error Cases

        /// <summary>
        /// Verifies: Null recordPermissions throws ArgumentNullException.
        /// Source: SecurityContext.cs lines 65-66 —
        /// <c>if (entity == null) throw new ArgumentNullException("entity");</c>
        /// In new service: <c>if (recordPermissions == null) throw new ArgumentNullException("recordPermissions");</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_NullRecordPermissions_ThrowsArgumentNullException()
        {
            // Arrange
            var user = CreateTestUser(Guid.NewGuid(), Role.RegularRoleId);

            // Act
            Action act = () => _sut.HasEntityPermission(EntityPermission.Read, null!, user);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("recordPermissions");
        }

        /// <summary>
        /// Verifies: Unsupported EntityPermission enum value throws NotSupportedException.
        /// Source: SecurityContext.cs line 88 —
        /// <c>throw new NotSupportedException("Entity permission type is not supported")</c>
        /// </summary>
        [Fact]
        public void HasEntityPermission_UnsupportedPermission_ThrowsNotSupportedException()
        {
            // Arrange — cast an invalid integer to EntityPermission to hit the default branch
            var invalidPermission = (EntityPermission)99;
            var user = CreateTestUser(Guid.NewGuid(), Role.RegularRoleId);
            var permissions = CreateTestPermissions();

            // Act
            Action act = () => _sut.HasEntityPermission(invalidPermission, permissions, user);

            // Assert
            act.Should().Throw<NotSupportedException>()
                .WithMessage("Entity permission type is not supported");
        }

        #endregion

        #region Phase 6: HasMetaPermission Tests

        /// <summary>
        /// Verifies: Null user returns false for meta permission.
        /// Source: SecurityContext.cs lines 113-115 —
        /// <c>if (user == null) return false;</c>
        /// </summary>
        [Fact]
        public void HasMetaPermission_NullUser_ReturnsFalse()
        {
            // Act
            var result = _sut.HasMetaPermission(null);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: Non-admin user (RegularRoleId only) returns false for meta permission.
        /// Source: SecurityContext.cs line 117 —
        /// <c>return user.Roles.Any(x =&gt; x.Id == SystemIds.AdministratorRoleId);</c>
        /// </summary>
        [Fact]
        public void HasMetaPermission_NonAdminUser_ReturnsFalse()
        {
            // Arrange — user with RegularRoleId only, not Administrator
            var user = CreateTestUser(Guid.NewGuid(), Role.RegularRoleId);

            // Act
            var result = _sut.HasMetaPermission(user);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: Admin user (AdministratorRoleId) returns true for meta permission.
        /// Source: SecurityContext.cs line 117.
        /// AdministratorRoleId = BDC56420-CAF0-4030-8A0E-D264938E0CDA (Definitions.cs line 15).
        /// </summary>
        [Fact]
        public void HasMetaPermission_AdminUser_ReturnsTrue()
        {
            // Arrange
            var user = CreateTestUser(Guid.NewGuid(), Role.AdministratorRoleId);

            // Act
            var result = _sut.HasMetaPermission(user);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: User with multiple roles including admin returns true.
        /// Source: SecurityContext.cs line 117 — Any() returns true if at least one role matches.
        /// </summary>
        [Fact]
        public void HasMetaPermission_UserWithMultipleRolesIncludingAdmin_ReturnsTrue()
        {
            // Arrange — user with both regular and admin roles
            var user = CreateTestUser(Guid.NewGuid(), Role.RegularRoleId, Role.AdministratorRoleId);

            // Act
            var result = _sut.HasMetaPermission(user);

            // Assert
            result.Should().BeTrue();
        }

        #endregion

        #region Phase 7: IsUserInRole — Role[] Overload Tests

        /// <summary>
        /// Verifies: Null user returns false for IsUserInRole(Role[]).
        /// Source: SecurityContext.cs line 48 — checks <c>currentUser != null</c>.
        /// </summary>
        [Fact]
        public void IsUserInRole_ByRoles_NullUser_ReturnsFalse()
        {
            // Arrange
            var roles = new[] { new Role { Id = Role.AdministratorRoleId, Name = "administrator" } };

            // Act
            var result = _sut.IsUserInRole(null!, roles);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: Empty roles array returns false for IsUserInRole(Role[]).
        /// Source: SecurityContext.cs line 48 — checks <c>roles.Any()</c>.
        /// </summary>
        [Fact]
        public void IsUserInRole_ByRoles_EmptyRoles_ReturnsFalse()
        {
            // Arrange
            var user = CreateTestUser(Guid.NewGuid(), Role.RegularRoleId);

            // Act
            var result = _sut.IsUserInRole(user, Array.Empty<Role>());

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: User has role A, check against Role[] containing role A — returns true.
        /// Source: SecurityContext.cs lines 45-52.
        /// </summary>
        [Fact]
        public void IsUserInRole_ByRoles_MatchingRole_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), roleId);
            var rolesToCheck = new[] { new Role { Id = roleId, Name = "test-role" } };

            // Act
            var result = _sut.IsUserInRole(user, rolesToCheck);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: User has role A, check against Role[] containing only role B — returns false.
        /// Source: SecurityContext.cs lines 45-52 — no matching role IDs.
        /// </summary>
        [Fact]
        public void IsUserInRole_ByRoles_NoMatchingRole_ReturnsFalse()
        {
            // Arrange
            var userRoleId = Guid.NewGuid();
            var otherRoleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), userRoleId);
            var rolesToCheck = new[] { new Role { Id = otherRoleId, Name = "other-role" } };

            // Act
            var result = _sut.IsUserInRole(user, rolesToCheck);

            // Assert
            result.Should().BeFalse();
        }

        #endregion

        #region Phase 8: IsUserInRole — Guid[] Overload Tests

        /// <summary>
        /// Verifies: Null user returns false for IsUserInRole(Guid[]).
        /// Source: SecurityContext.cs line 57 —
        /// <c>if (currentUser != null &amp;&amp; roles != null &amp;&amp; roles.Any())</c>
        /// </summary>
        [Fact]
        public void IsUserInRole_ByGuids_NullUser_ReturnsFalse()
        {
            // Act
            var result = _sut.IsUserInRole(null!, Role.AdministratorRoleId);

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: Empty Guid array returns false for IsUserInRole(Guid[]).
        /// Source: SecurityContext.cs line 57 — <c>roles.Any()</c> is false for empty array.
        /// </summary>
        [Fact]
        public void IsUserInRole_ByGuids_EmptyGuids_ReturnsFalse()
        {
            // Arrange
            var user = CreateTestUser(Guid.NewGuid(), Role.RegularRoleId);

            // Act
            var result = _sut.IsUserInRole(user, Array.Empty<Guid>());

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies: User has a role, check against Guid[] containing that role's ID — returns true.
        /// Source: SecurityContext.cs line 58 —
        /// <c>return currentUser.Roles.Any(x =&gt; roles.Any(z =&gt; z == x.Id));</c>
        /// </summary>
        [Fact]
        public void IsUserInRole_ByGuids_MatchingGuid_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), roleId);

            // Act
            var result = _sut.IsUserInRole(user, roleId);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies: User has a role, check against Guid[] with a different ID — returns false.
        /// Source: SecurityContext.cs line 58 — no matching role ID.
        /// </summary>
        [Fact]
        public void IsUserInRole_ByGuids_NoMatchingGuid_ReturnsFalse()
        {
            // Arrange
            var userRoleId = Guid.NewGuid();
            var otherRoleId = Guid.NewGuid();
            var user = CreateTestUser(Guid.NewGuid(), userRoleId);

            // Act
            var result = _sut.IsUserInRole(user, otherRoleId);

            // Assert
            result.Should().BeFalse();
        }

        #endregion

        #region Phase 9: Well-Known GUID Constant Verification

        /// <summary>
        /// Verifies exact GUID values for all well-known system IDs match the original
        /// <c>Definitions.cs</c> (lines 6-21). These values MUST remain exact for
        /// backward compatibility with existing data.
        /// </summary>
        [Fact]
        public void SystemIds_CorrectValues()
        {
            // Verify User.SystemUserId — Definitions.cs line 19
            User.SystemUserId.Should().Be(new Guid("10000000-0000-0000-0000-000000000000"));

            // Verify Role.AdministratorRoleId — Definitions.cs line 15
            Role.AdministratorRoleId.Should().Be(new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA"));

            // Verify Role.RegularRoleId — Definitions.cs line 16
            Role.RegularRoleId.Should().Be(new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F"));

            // Verify Role.GuestRoleId — Definitions.cs line 17
            Role.GuestRoleId.Should().Be(new Guid("987148B1-AFA8-4B33-8616-55861E5FD065"));
        }

        #endregion
    }
}
