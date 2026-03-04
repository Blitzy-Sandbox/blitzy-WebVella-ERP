using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Security
{
    /// <summary>
    /// Comprehensive unit tests for the SecurityContext class validating AsyncLocal
    /// scope behavior, user stack push/pop, CurrentUser property, OpenSystemScope(),
    /// scope nesting, proper Dispose behavior restoring previous user, thread
    /// isolation via AsyncLocal, IsUserInRole/HasMetaPermission checks, and the new
    /// JWT token propagation adaptation methods added for microservice identity propagation.
    /// </summary>
    public class SecurityContextTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates a test ErpUser with the specified properties and optional roles.
        /// Each role name is mapped to an ErpRole with a random Id.
        /// </summary>
        private ErpUser CreateTestUser(
            Guid? id = null,
            string username = "testuser",
            string email = "test@test.com",
            params string[] roleNames)
        {
            var user = new ErpUser
            {
                Id = id ?? Guid.NewGuid(),
                Username = username,
                Email = email,
                FirstName = "Test",
                LastName = "User",
                Enabled = true
            };

            foreach (var roleName in roleNames)
            {
                user.Roles.Add(new ErpRole { Id = Guid.NewGuid(), Name = roleName });
            }

            return user;
        }

        /// <summary>
        /// Creates an ErpUser with the Administrator role (SystemIds.AdministratorRoleId)
        /// for testing HasMetaPermission and admin-related checks.
        /// </summary>
        private ErpUser CreateAdminUser()
        {
            var user = new ErpUser
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                Email = "admin@test.com",
                FirstName = "Admin",
                LastName = "User",
                Enabled = true
            };
            user.Roles.Add(new ErpRole { Id = SystemIds.AdministratorRoleId, Name = "administrator" });
            return user;
        }

        /// <summary>
        /// Creates a ClaimsPrincipal from an ErpUser for JWT propagation tests.
        /// Maps Id → NameIdentifier, Username → Name, Email → Email, FirstName → GivenName,
        /// LastName → Surname, and each role → Role claim + role_name claim.
        /// </summary>
        private ClaimsPrincipal CreateClaimsPrincipal(ErpUser user)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username ?? string.Empty),
                new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
                new Claim(ClaimTypes.GivenName, user.FirstName ?? string.Empty),
                new Claim(ClaimTypes.Surname, user.LastName ?? string.Empty)
            };

            foreach (var role in user.Roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role.Id.ToString()));
                if (!string.IsNullOrEmpty(role.Name))
                {
                    claims.Add(new Claim("role_name", role.Name));
                }
            }

            var identity = new ClaimsIdentity(claims, "TestAuth");
            return new ClaimsPrincipal(identity);
        }

        #endregion

        #region CurrentUser Property Tests

        [Fact]
        public void CurrentUser_WhenNoScopeOpened_ReturnsNull()
        {
            // Arrange & Act — no scope is opened
            var user = SecurityContext.CurrentUser;

            // Assert
            user.Should().BeNull();
        }

        [Fact]
        public void CurrentUser_AfterOpenScope_ReturnsUser()
        {
            // Arrange
            var testUser = CreateTestUser(username: "scopeuser", email: "scope@test.com");

            // Act & Assert
            using (SecurityContext.OpenScope(testUser))
            {
                var current = SecurityContext.CurrentUser;
                current.Should().NotBeNull();
                current.Id.Should().Be(testUser.Id);
                current.Username.Should().Be("scopeuser");
                current.Email.Should().Be("scope@test.com");
            }
        }

        #endregion

        #region OpenScope Tests

        [Fact]
        public void OpenScope_WithValidUser_PushesUserToStack()
        {
            // Arrange
            var testUser = CreateTestUser();

            // Act & Assert
            using (SecurityContext.OpenScope(testUser))
            {
                SecurityContext.CurrentUser.Should().BeSameAs(testUser);
            }
        }

        [Fact]
        public void OpenScope_WithMultipleUsers_PeeksLastPushed()
        {
            // Arrange
            var user1 = CreateTestUser(username: "user1");
            var user2 = CreateTestUser(username: "user2");

            // Act & Assert
            using (SecurityContext.OpenScope(user1))
            {
                using (SecurityContext.OpenScope(user2))
                {
                    SecurityContext.CurrentUser.Should().BeSameAs(user2);
                    SecurityContext.CurrentUser.Username.Should().Be("user2");
                }
            }
        }

        [Fact]
        public void OpenScope_ReturnsIDisposable()
        {
            // Arrange
            var testUser = CreateTestUser();

            // Act
            IDisposable scope = SecurityContext.OpenScope(testUser);

            // Assert
            try
            {
                scope.Should().NotBeNull();
                scope.Should().BeAssignableTo<IDisposable>();
            }
            finally
            {
                scope.Dispose();
            }
        }

        #endregion

        #region OpenSystemScope Tests

        [Fact]
        public void OpenSystemScope_SetsCurrentUserToSystemUser()
        {
            // Act & Assert
            using (SecurityContext.OpenSystemScope())
            {
                SecurityContext.CurrentUser.Should().NotBeNull();
                SecurityContext.CurrentUser.Id.Should().Be(SystemIds.SystemUserId);
            }
        }

        [Fact]
        public void OpenSystemScope_SystemUserHasCorrectProperties()
        {
            // Act & Assert
            using (SecurityContext.OpenSystemScope())
            {
                var currentUser = SecurityContext.CurrentUser;
                currentUser.Should().NotBeNull();
                currentUser.Id.Should().Be(new Guid("10000000-0000-0000-0000-000000000000"));
                currentUser.FirstName.Should().Be("Local");
                currentUser.LastName.Should().Be("System");
                currentUser.Username.Should().Be("system");
                currentUser.Email.Should().Be("system@webvella.com");
                currentUser.Enabled.Should().BeTrue();
            }
        }

        [Fact]
        public void OpenSystemScope_SystemUserHasAdministratorRole()
        {
            // Act & Assert
            using (SecurityContext.OpenSystemScope())
            {
                var currentUser = SecurityContext.CurrentUser;
                currentUser.Should().NotBeNull();
                currentUser.Roles.Should().HaveCount(1);

                var adminRole = currentUser.Roles.First();
                adminRole.Id.Should().Be(new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA"));
                adminRole.Name.Should().Be("administrator");
            }
        }

        #endregion

        #region Scope Nesting Tests

        [Fact]
        public void NestedScopes_InnerScopeOverridesCurrentUser()
        {
            // Arrange
            var userA = CreateTestUser(username: "userA");
            var userB = CreateTestUser(username: "userB");

            // Act & Assert
            using (SecurityContext.OpenScope(userA))
            {
                SecurityContext.CurrentUser.Should().BeSameAs(userA);

                using (SecurityContext.OpenScope(userB))
                {
                    SecurityContext.CurrentUser.Should().BeSameAs(userB);
                }
            }
        }

        [Fact]
        public void NestedScopes_DisposingInnerScopeRestoresPreviousUser()
        {
            // Arrange
            var userA = CreateTestUser(username: "userA");
            var userB = CreateTestUser(username: "userB");

            // Act & Assert
            using (SecurityContext.OpenScope(userA))
            {
                using (SecurityContext.OpenScope(userB))
                {
                    SecurityContext.CurrentUser.Should().BeSameAs(userB);
                }

                // After inner scope disposed, userA is restored to the top of the stack
                SecurityContext.CurrentUser.Should().BeSameAs(userA);
            }
        }

        [Fact]
        public void NestedScopes_DisposingAllScopesReturnsNull()
        {
            // Arrange
            var userA = CreateTestUser(username: "userA");
            var userB = CreateTestUser(username: "userB");

            // Act
            using (SecurityContext.OpenScope(userA))
            {
                using (SecurityContext.OpenScope(userB))
                {
                    SecurityContext.CurrentUser.Should().BeSameAs(userB);
                }

                SecurityContext.CurrentUser.Should().BeSameAs(userA);
            }

            // Assert — both scopes disposed, CurrentUser returns null
            SecurityContext.CurrentUser.Should().BeNull();
        }

        [Fact]
        public void NestedScopes_ThreeLevelsDeep()
        {
            // Arrange
            var userA = CreateTestUser(username: "userA");
            var userB = CreateTestUser(username: "userB");
            var userC = CreateTestUser(username: "userC");

            // Act & Assert — verify stack push/pop at each level
            using (SecurityContext.OpenScope(userA))
            {
                SecurityContext.CurrentUser.Should().BeSameAs(userA);

                using (SecurityContext.OpenScope(userB))
                {
                    SecurityContext.CurrentUser.Should().BeSameAs(userB);

                    using (SecurityContext.OpenScope(userC))
                    {
                        SecurityContext.CurrentUser.Should().BeSameAs(userC);
                    }

                    // C disposed, B is restored
                    SecurityContext.CurrentUser.Should().BeSameAs(userB);
                }

                // B disposed, A is restored
                SecurityContext.CurrentUser.Should().BeSameAs(userA);
            }

            // A disposed, null is restored
            SecurityContext.CurrentUser.Should().BeNull();
        }

        #endregion

        #region Dispose Behavior Tests

        [Fact]
        public void Dispose_RemovesCurrentUser()
        {
            // Arrange
            var testUser = CreateTestUser();
            var scope = SecurityContext.OpenScope(testUser);
            SecurityContext.CurrentUser.Should().NotBeNull();

            // Act
            scope.Dispose();

            // Assert
            SecurityContext.CurrentUser.Should().BeNull();
        }

        [Fact]
        public void Dispose_UsingPattern_RestoresCorrectUser()
        {
            // Arrange
            var userA = CreateTestUser(username: "userA");
            var userB = CreateTestUser(username: "userB");

            // Act & Assert — demonstrates correct stack behavior with using blocks
            using (SecurityContext.OpenScope(userA))
            {
                using (SecurityContext.OpenScope(userB))
                {
                    SecurityContext.CurrentUser.Should().BeSameAs(userB);
                }

                SecurityContext.CurrentUser.Should().BeSameAs(userA);
            }

            SecurityContext.CurrentUser.Should().BeNull();
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_DoesNotThrow()
        {
            // Arrange
            var testUser = CreateTestUser();
            var scope = SecurityContext.OpenScope(testUser);

            // Act — dispose once, then verify second dispose does not throw
            scope.Dispose();

            Action secondDispose = () => scope.Dispose();
            secondDispose.Should().NotThrow();
        }

        #endregion

        #region AsyncLocal Thread Isolation Tests

        [Fact]
        public async Task AsyncLocal_SeparateThreadsHaveIndependentContexts()
        {
            // Arrange
            var user1 = CreateTestUser(username: "threaduser");

            // Act — open scope inside a separate thread via Task.Run
            await Task.Run(() =>
            {
                using (SecurityContext.OpenScope(user1))
                {
                    SecurityContext.CurrentUser.Should().NotBeNull();
                    SecurityContext.CurrentUser.Id.Should().Be(user1.Id);
                }
            });

            // Assert — main async context is unaffected by the scope opened in Task.Run
            SecurityContext.CurrentUser.Should().BeNull();
        }

        [Fact]
        public async Task AsyncLocal_ConcurrentScopesAreIsolated()
        {
            // Arrange
            var user1 = CreateTestUser(username: "concurrent1");
            var user2 = CreateTestUser(username: "concurrent2");

            // Act — run two concurrent scopes on separate tasks with overlapping delays
            var task1 = Task.Run(async () =>
            {
                using (SecurityContext.OpenScope(user1))
                {
                    await Task.Delay(50);
                    SecurityContext.CurrentUser.Should().NotBeNull();
                    SecurityContext.CurrentUser.Id.Should().Be(user1.Id);
                    SecurityContext.CurrentUser.Username.Should().Be("concurrent1");
                }
            });

            var task2 = Task.Run(async () =>
            {
                using (SecurityContext.OpenScope(user2))
                {
                    await Task.Delay(50);
                    SecurityContext.CurrentUser.Should().NotBeNull();
                    SecurityContext.CurrentUser.Id.Should().Be(user2.Id);
                    SecurityContext.CurrentUser.Username.Should().Be("concurrent2");
                }
            });

            // Assert — both tasks complete without cross-contamination
            await Task.WhenAll(task1, task2);
        }

        #endregion

        #region IsUserInRole Tests

        [Fact]
        public void IsUserInRole_WithMatchingRole_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser();
            user.Roles.Add(new ErpRole { Id = roleId, Name = "testrole" });

            // Act & Assert
            using (SecurityContext.OpenScope(user))
            {
                SecurityContext.IsUserInRole(roleId).Should().BeTrue();
            }
        }

        [Fact]
        public void IsUserInRole_WithNonMatchingRole_ReturnsFalse()
        {
            // Arrange
            var user = CreateTestUser(roleNames: "somerole");
            var nonExistentRoleId = Guid.NewGuid();

            // Act & Assert
            using (SecurityContext.OpenScope(user))
            {
                SecurityContext.IsUserInRole(nonExistentRoleId).Should().BeFalse();
            }
        }

        [Fact]
        public void IsUserInRole_WithNoScope_ReturnsFalse()
        {
            // Act & Assert — no scope opened, CurrentUser is null
            SecurityContext.IsUserInRole(Guid.NewGuid()).Should().BeFalse();
        }

        [Fact]
        public void IsUserInRole_WithErpRoleArray_ReturnsTrue()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var user = CreateTestUser();
            user.Roles.Add(new ErpRole { Id = roleId, Name = "matchrole" });
            var checkRole = new ErpRole { Id = roleId, Name = "matchrole" };

            // Act & Assert — tests the ErpRole[] overload
            using (SecurityContext.OpenScope(user))
            {
                SecurityContext.IsUserInRole(checkRole).Should().BeTrue();
            }
        }

        #endregion

        #region HasMetaPermission Tests

        [Fact]
        public void HasMetaPermission_WithAdminRole_ReturnsTrue()
        {
            // Arrange
            var adminUser = CreateAdminUser();

            // Act & Assert
            using (SecurityContext.OpenScope(adminUser))
            {
                SecurityContext.HasMetaPermission().Should().BeTrue();
            }
        }

        [Fact]
        public void HasMetaPermission_WithoutAdminRole_ReturnsFalse()
        {
            // Arrange
            var regularUser = CreateTestUser(roleNames: "regular");

            // Act & Assert
            using (SecurityContext.OpenScope(regularUser))
            {
                SecurityContext.HasMetaPermission().Should().BeFalse();
            }
        }

        [Fact]
        public void HasMetaPermission_WithNoScope_ReturnsFalse()
        {
            // Act & Assert — no scope opened, CurrentUser is null
            SecurityContext.HasMetaPermission().Should().BeFalse();
        }

        #endregion

        #region JWT Token Propagation Adaptation Tests

        [Fact]
        public void OpenScope_WithClaimsPrincipal_ExtractsUserCorrectly()
        {
            // Arrange — create user with a role and build ClaimsPrincipal
            var testUser = CreateTestUser(roleNames: "editor");
            var principal = CreateClaimsPrincipal(testUser);

            // Act & Assert
            using (SecurityContext.OpenScope(principal))
            {
                var current = SecurityContext.CurrentUser;
                current.Should().NotBeNull();
                current.Id.Should().Be(testUser.Id);
                current.Email.Should().Be(testUser.Email);
                current.Username.Should().Be(testUser.Username);
                current.FirstName.Should().Be(testUser.FirstName);
                current.LastName.Should().Be(testUser.LastName);
                current.Roles.Should().HaveCount(1);
                current.Roles.First().Id.Should().Be(testUser.Roles.First().Id);
            }
        }

        [Fact]
        public void ExtractUserFromClaims_WithValidClaims_ReturnsUser()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var roleId = Guid.NewGuid();
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, "claims@test.com"),
                new Claim(ClaimTypes.Name, "claimsuser"),
                new Claim(ClaimTypes.GivenName, "Claims"),
                new Claim(ClaimTypes.Surname, "User"),
                new Claim(ClaimTypes.Role, roleId.ToString()),
                new Claim("role_name", "testrole")
            };

            // Act
            var user = SecurityContext.ExtractUserFromClaims(claims);

            // Assert
            user.Should().NotBeNull();
            user.Id.Should().Be(userId);
            user.Email.Should().Be("claims@test.com");
            user.Username.Should().Be("claimsuser");
            user.FirstName.Should().Be("Claims");
            user.LastName.Should().Be("User");
            user.Roles.Should().HaveCount(1);
            user.Roles.First().Id.Should().Be(roleId);
            user.Roles.First().Name.Should().Be("testrole");
        }

        [Fact]
        public void ExtractUserFromClaims_WithMissingNameIdentifier_ReturnsNull()
        {
            // Arrange — claims without NameIdentifier, so user.Id remains Guid.Empty
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Email, "no-id@test.com"),
                new Claim(ClaimTypes.Name, "noiduser")
            };

            // Act
            var user = SecurityContext.ExtractUserFromClaims(claims);

            // Assert — ExtractUserFromClaims returns null when Id is Guid.Empty
            user.Should().BeNull();
        }

        [Fact]
        public void ExtractUserFromClaims_WithMultipleRoles_ExtractsAll()
        {
            // Arrange — three roles with interleaved Role and role_name claims
            var userId = Guid.NewGuid();
            var roleId1 = Guid.NewGuid();
            var roleId2 = Guid.NewGuid();
            var roleId3 = Guid.NewGuid();
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, "multi@test.com"),
                new Claim(ClaimTypes.Role, roleId1.ToString()),
                new Claim("role_name", "role1"),
                new Claim(ClaimTypes.Role, roleId2.ToString()),
                new Claim("role_name", "role2"),
                new Claim(ClaimTypes.Role, roleId3.ToString()),
                new Claim("role_name", "role3")
            };

            // Act
            var user = SecurityContext.ExtractUserFromClaims(claims);

            // Assert
            user.Should().NotBeNull();
            user.Id.Should().Be(userId);
            user.Roles.Should().HaveCount(3);
            user.Roles.Select(r => r.Id).Should().Contain(roleId1);
            user.Roles.Select(r => r.Id).Should().Contain(roleId2);
            user.Roles.Select(r => r.Id).Should().Contain(roleId3);
            user.Roles.Any(r => r.Name == "role1").Should().BeTrue();
            user.Roles.Any(r => r.Name == "role2").Should().BeTrue();
            user.Roles.Any(r => r.Name == "role3").Should().BeTrue();
        }

        #endregion
    }
}
