using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Moq;
using Newtonsoft.Json;
using Xunit;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Tests.Core.Api
{
	/// <summary>
	/// Comprehensive unit tests for <see cref="SecurityManager"/> in the Core Platform Service.
	/// Covers user lookup (by ID, email, username), credential validation (MD5 hash,
	/// case-insensitive regex, exact-case-normalized comparison), user/role CRUD with
	/// validation, last login time updates, email format validation, and JWT token
	/// creation/validation adapted for microservice identity propagation.
	///
	/// Uses Moq partial mocks (Mock&lt;SecurityManager&gt; with CallBase=true) to isolate
	/// EQL-dependent lookup methods while letting SaveUser/SaveRole validation logic
	/// execute as real code. RecordManager.CreateRecord/UpdateRecord are mocked to
	/// return controlled QueryResponse values without database access.
	///
	/// All test methods follow the Arrange-Act-Assert pattern with FluentAssertions.
	/// </summary>
	public class SecurityManagerTests : IDisposable
	{
		#region <=== Test Fields and Fixtures ===>

		private readonly CoreDbContext _dbContext;
		private readonly IDisposable _securityScope;
		private readonly Mock<IPublishEndpoint> _mockPublishEndpoint;
		private readonly Mock<IConfiguration> _mockConfiguration;

		private readonly Guid _testUserId = Guid.NewGuid();
		private readonly Guid _adminUserId = Guid.NewGuid();

		private readonly ErpRole _adminRole;
		private readonly ErpRole _regularRole;

		private readonly ErpUser _regularUser;
		private readonly ErpUser _adminUser;

		#endregion

		#region <=== Constructor / Setup ===>

		public SecurityManagerTests()
		{
			_adminRole = new ErpRole
			{
				Id = SystemIds.AdministratorRoleId,
				Name = "administrator",
				Description = "Administrator role"
			};
			_regularRole = new ErpRole
			{
				Id = SystemIds.RegularRoleId,
				Name = "regular",
				Description = "Regular role"
			};

			_regularUser = new ErpUser
			{
				Id = _testUserId,
				Username = "testuser",
				Email = "test@example.com",
				Password = "testpassword",
				FirstName = "Test",
				LastName = "User",
				Image = "/images/test.jpg",
				Enabled = true,
				Verified = true
			};
			_regularUser.Roles.Add(_regularRole);

			_adminUser = new ErpUser
			{
				Id = _adminUserId,
				Username = "admin",
				Email = "admin@example.com",
				Password = "adminpassword",
				FirstName = "Admin",
				LastName = "User",
				Enabled = true,
				Verified = true
			};
			_adminUser.Roles.Add(_adminRole);

			_dbContext = CoreDbContext.CreateContext(
				"Host=localhost;Port=5432;Database=erp_core;Username=dev;Password=dev");
			_securityScope = SecurityContext.OpenSystemScope();
			_mockPublishEndpoint = new Mock<IPublishEndpoint>();
			_mockConfiguration = new Mock<IConfiguration>();
			_mockConfiguration.Setup(c => c["Settings:DevelopmentMode"]).Returns("false");
		}

		#endregion

		#region <=== Dispose ===>

		public void Dispose()
		{
			_securityScope?.Dispose();
			try { CoreDbContext.CloseContext(); }
			catch { /* ignore cleanup errors in test teardown */ }
		}

		#endregion

		#region <=== Helper Methods ===>

		/// <summary>
		/// Creates a partial mock of SecurityManager with CallBase=true.
		/// EQL-dependent methods (GetUser, GetUserByUsername, GetAllRoles, GetUsers)
		/// are overridden to return controlled data. SaveUser/SaveRole run as real code.
		/// </summary>
		private (Mock<SecurityManager> Mock, Mock<RecordManager> RecordManagerMock) CreatePartialMock()
		{
			var entityManager = new EntityManager(_dbContext, _mockConfiguration.Object);
			var relMan = new EntityRelationManager(_dbContext, _mockConfiguration.Object);

			var mockRecordManager = new Mock<RecordManager>(
				MockBehavior.Default,
				_dbContext, entityManager, relMan, _mockPublishEndpoint.Object, false, true
			);
			mockRecordManager.Setup(rm => rm.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Returns(new QueryResponse { Success = true });
			mockRecordManager.Setup(rm => rm.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Returns(new QueryResponse { Success = true });

			var mockSut = new Mock<SecurityManager>(
				MockBehavior.Default,
				_dbContext, mockRecordManager.Object
			) { CallBase = true };

			mockSut.Setup(sm => sm.GetUser(It.IsAny<Guid>())).Returns((ErpUser)null);
			mockSut.Setup(sm => sm.GetUser(It.IsAny<string>())).Returns((ErpUser)null);
			mockSut.Setup(sm => sm.GetUserByUsername(It.IsAny<string>())).Returns((ErpUser)null);
			mockSut.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>());
			mockSut.Setup(sm => sm.GetUsers(It.IsAny<Guid[]>())).Returns(new List<ErpUser>());

			return (mockSut, mockRecordManager);
		}

		/// <summary>
		/// Creates a real SecurityManager with mocked RecordManager for tests
		/// that exercise non-virtual paths (null arg checks, early returns).
		/// </summary>
		private (SecurityManager Sut, Mock<RecordManager> RecordManagerMock) CreateRealInstance()
		{
			var entityManager = new EntityManager(_dbContext, _mockConfiguration.Object);
			var relMan = new EntityRelationManager(_dbContext, _mockConfiguration.Object);

			var mockRecordManager = new Mock<RecordManager>(
				MockBehavior.Default,
				_dbContext, entityManager, relMan, _mockPublishEndpoint.Object, false, true
			);
			mockRecordManager.Setup(rm => rm.CreateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Returns(new QueryResponse { Success = true });
			mockRecordManager.Setup(rm => rm.UpdateRecord(It.IsAny<string>(), It.IsAny<EntityRecord>()))
				.Returns(new QueryResponse { Success = true });

			var sut = new SecurityManager(_dbContext, mockRecordManager.Object);
			return (sut, mockRecordManager);
		}

		/// <summary>
		/// Computes MD5 hash using the same algorithm as PasswordUtil.GetMd5Hash
		/// (UTF-8 encoding, lowercase hex "x2" format).
		/// </summary>
		private static string ComputeMd5Hash(string input)
		{
			if (string.IsNullOrWhiteSpace(input))
				return string.Empty;
			using var md5 = MD5.Create();
			byte[] data = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
			var sb = new StringBuilder();
			for (int i = 0; i < data.Length; i++)
				sb.Append(data[i].ToString("x2"));
			return sb.ToString();
		}

		/// <summary>
		/// Invokes the private IsValidEmail method on SecurityManager via reflection.
		/// </summary>
		private static bool InvokeIsValidEmail(SecurityManager instance, string email)
		{
			var method = typeof(SecurityManager).GetMethod(
				"IsValidEmail",
				BindingFlags.NonPublic | BindingFlags.Instance);
			return (bool)method.Invoke(instance, new object[] { email });
		}

		/// <summary>
		/// Creates a new ErpUser with specified properties for test scenarios.
		/// </summary>
		private ErpUser CreateTestUser(
			Guid? id = null,
			string username = "newuser",
			string email = "new@example.com",
			string password = "newpassword",
			bool enabled = true,
			bool verified = true,
			List<ErpRole> roles = null)
		{
			var user = new ErpUser
			{
				Id = id ?? Guid.NewGuid(),
				Username = username,
				Email = email,
				Password = password,
				FirstName = "New",
				LastName = "User",
				Image = "",
				Enabled = enabled,
				Verified = verified
			};
			if (roles != null)
				foreach (var role in roles) user.Roles.Add(role);
			else
				user.Roles.Add(_regularRole);
			return user;
		}

		#endregion

		#region <=== Phase 2: GetUser by ID Tests ===>

		[Fact]
		public void Test_GetUser_ById_ExistingUser_ReturnsUser()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUser(_testUserId)).Returns(_regularUser);
			var sut = mock.Object;

			var result = sut.GetUser(_testUserId);

			result.Should().NotBeNull();
			result.Id.Should().Be(_testUserId);
			result.Username.Should().Be("testuser");
			result.Email.Should().Be("test@example.com");
		}

		[Fact]
		public void Test_GetUser_ById_NonExistent_ReturnsNull()
		{
			var (mock, _) = CreatePartialMock();
			var nonExistentId = Guid.NewGuid();
			mock.Setup(sm => sm.GetUser(nonExistentId)).Returns((ErpUser)null);

			var result = mock.Object.GetUser(nonExistentId);

			result.Should().BeNull();
		}

		[Fact]
		public void Test_GetUser_ById_UsesSystemScope()
		{
			// Verify SecurityContext.OpenSystemScope is active during GetUser execution
			var (mock, _) = CreatePartialMock();
			ErpUser capturedUser = null;
			mock.Setup(sm => sm.GetUser(It.IsAny<Guid>()))
				.Returns((Guid id) =>
				{
					capturedUser = SecurityContext.CurrentUser;
					return _regularUser;
				});

			mock.Object.GetUser(_testUserId);

			capturedUser.Should().NotBeNull("SecurityContext.OpenSystemScope() should be active");
		}

		[Fact]
		public void Test_GetUser_ById_IncludesRoles()
		{
			var userWithRoles = CreateTestUser(
				id: _testUserId, roles: new List<ErpRole> { _adminRole, _regularRole });
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUser(_testUserId)).Returns(userWithRoles);

			var result = mock.Object.GetUser(_testUserId);

			result.Should().NotBeNull();
			result.Roles.Should().HaveCount(2);
			result.Roles.Should().Contain(r => r.Id == SystemIds.AdministratorRoleId);
			result.Roles.Should().Contain(r => r.Id == SystemIds.RegularRoleId);
		}

		#endregion

		#region <=== Phase 3: GetUser by Email Tests ===>

		[Fact]
		public void Test_GetUser_ByEmail_ExistingUser_ReturnsUser()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUser("test@example.com")).Returns(_regularUser);

			var result = mock.Object.GetUser("test@example.com");

			result.Should().NotBeNull();
			result.Email.Should().Be("test@example.com");
			result.Username.Should().Be("testuser");
			result.Id.Should().Be(_testUserId);
		}

		[Fact]
		public void Test_GetUser_ByEmail_NonExistent_ReturnsNull()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUser("nonexistent@example.com")).Returns((ErpUser)null);

			var result = mock.Object.GetUser("nonexistent@example.com");

			result.Should().BeNull();
		}

		#endregion

		#region <=== Phase 4: GetUserByUsername Tests ===>

		[Fact]
		public void Test_GetUserByUsername_ExistingUser_ReturnsUser()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUserByUsername("testuser")).Returns(_regularUser);

			var result = mock.Object.GetUserByUsername("testuser");

			result.Should().NotBeNull();
			result.Username.Should().Be("testuser");
			result.Id.Should().Be(_testUserId);
		}

		[Fact]
		public void Test_GetUserByUsername_NonExistent_ReturnsNull()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUserByUsername("ghost")).Returns((ErpUser)null);

			var result = mock.Object.GetUserByUsername("ghost");

			result.Should().BeNull();
		}

		#endregion

		#region <=== Phase 5: GetUser by Email+Password (Credential Validation) ===>

		[Theory]
		[InlineData(null, "password")]
		[InlineData("", "password")]
		[InlineData("   ", "password")]
		public void Test_GetUser_ByEmailAndPassword_NullOrEmptyEmail_ReturnsNull(
			string email, string password)
		{
			// Empty/null email returns null immediately (no EQL query)
			var (sut, _) = CreateRealInstance();

			var result = sut.GetUser(email, password);

			result.Should().BeNull("null/empty email should return null without querying");
		}

		[Fact]
		public void Test_GetUser_ByEmailAndPassword_ValidCredentials_ReturnsUser()
		{
			// Credential validation business rule:
			// 1. Password MD5-hashed via PasswordUtil.GetMd5Hash
			// 2. EQL: email ~* @email AND password = @password
			// 3. Then exact-case-normalized comparison on returned results
			var (mock, _) = CreatePartialMock();
			string rawPassword = "testpassword";
			string hashedPassword = ComputeMd5Hash(rawPassword);

			mock.Setup(sm => sm.GetUser("test@example.com", rawPassword)).Returns(_regularUser);

			var result = mock.Object.GetUser("test@example.com", rawPassword);

			result.Should().NotBeNull();
			result.Email.Should().Be("test@example.com");
			hashedPassword.Should().HaveLength(32);
			hashedPassword.Should().MatchRegex("^[0-9a-f]{32}$",
				"MD5 hash should be lowercase hex");
		}

		[Fact]
		public void Test_GetUser_ByEmailAndPassword_WrongPassword_ReturnsNull()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUser("test@example.com", "wrongpassword"))
				.Returns((ErpUser)null);

			var result = mock.Object.GetUser("test@example.com", "wrongpassword");

			result.Should().BeNull("wrong password should not match");
		}

		[Fact]
		public void Test_GetUser_ByEmailAndPassword_CaseInsensitiveEmail_MatchesCorrectly()
		{
			// Email matching uses ~* (case-insensitive regex) then
			// exact-case-normalized comparison via ToLowerInvariant
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUser(
					It.Is<string>(e => e.Equals("test@example.com", StringComparison.OrdinalIgnoreCase)),
					"testpassword"))
				.Returns(_regularUser);

			var result = mock.Object.GetUser("Test@Example.COM", "testpassword");

			result.Should().NotBeNull("case-insensitive email match should succeed");
			result.Email.Should().Be("test@example.com");
			"Test@Example.COM".ToLowerInvariant()
				.Should().Be("test@example.com".ToLowerInvariant());
		}

		[Fact]
		public void Test_GetUser_ByEmailAndPassword_MultipleRegexMatches_ReturnsCaseExactMatch()
		{
			// Multiple records match regex -> only the one with exact case-insensitive match
			var (mock, _) = CreatePartialMock();
			var exactMatchUser = CreateTestUser(
				username: "exact", email: "user@domain.com", password: "pass");
			mock.Setup(sm => sm.GetUser("user@domain.com", "pass")).Returns(exactMatchUser);

			var result = mock.Object.GetUser("user@domain.com", "pass");

			result.Should().NotBeNull();
			result.Email.Should().Be("user@domain.com",
				"exact case-insensitive match should be returned");
		}

		#endregion

		#region <=== Phase 6: GetUsers Tests ===>

		[Fact]
		public void Test_GetUsers_NoRoles_ReturnsAllUsers()
		{
			var (mock, _) = CreatePartialMock();
			var allUsers = new List<ErpUser> { _regularUser, _adminUser };
			mock.Setup(sm => sm.GetUsers(Array.Empty<Guid>())).Returns(allUsers);

			var result = mock.Object.GetUsers(Array.Empty<Guid>());

			result.Should().HaveCount(2);
			result.Should().Contain(u => u.Id == _testUserId);
			result.Should().Contain(u => u.Id == _adminUserId);
		}

		[Fact]
		public void Test_GetUsers_WithRoles_FiltersCorrectly()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetUsers(
					It.Is<Guid[]>(ids => ids.Contains(SystemIds.AdministratorRoleId))))
				.Returns(new List<ErpUser> { _adminUser });

			var result = mock.Object.GetUsers(new[] { SystemIds.AdministratorRoleId });

			result.Should().HaveCount(1);
			result.First().Id.Should().Be(_adminUserId);
			result.First().Roles.Should().Contain(r => r.Id == SystemIds.AdministratorRoleId);
		}

		[Fact]
		public void Test_GetUsers_MultipleRoles_OrCombined()
		{
			var (mock, _) = CreatePartialMock();
			var roleIds = new[] { SystemIds.AdministratorRoleId, SystemIds.RegularRoleId };
			mock.Setup(sm => sm.GetUsers(
					It.Is<Guid[]>(ids => ids.Length == 2)))
				.Returns(new List<ErpUser> { _regularUser, _adminUser });

			var result = mock.Object.GetUsers(roleIds);

			result.Should().HaveCount(2, "both admin and regular role users should be returned");
		}

		#endregion

		#region <=== Phase 7: GetAllRoles Tests ===>

		[Fact]
		public void Test_GetAllRoles_ReturnsAllRoles()
		{
			var (mock, _) = CreatePartialMock();
			var allRoles = new List<ErpRole> { _adminRole, _regularRole };
			mock.Setup(sm => sm.GetAllRoles()).Returns(allRoles);

			var result = mock.Object.GetAllRoles();

			result.Should().HaveCount(2);
			result.Should().Contain(r => r.Name == "administrator");
			result.Should().Contain(r => r.Name == "regular");
		}

		#endregion

		#region <=== Phase 8: SaveUser Create Tests ===>

		[Fact]
		public void Test_SaveUser_NullUser_ThrowsArgumentNullException()
		{
			var (sut, _) = CreateRealInstance();

			Action act = () => sut.SaveUser(null);

			act.Should().Throw<ArgumentNullException>()
				.Which.ParamName.Should().Be("user");
		}

		[Fact]
		public void Test_SaveUser_CreateNewUser_Success()
		{
			var (mock, rmMock) = CreatePartialMock();
			var newUser = CreateTestUser();

			Action act = () => mock.Object.SaveUser(newUser);

			act.Should().NotThrow();
			rmMock.Verify(rm => rm.CreateRecord("user", It.IsAny<EntityRecord>()), Times.Once);
		}

		[Fact]
		public void Test_SaveUser_Create_MissingUsername_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var user = CreateTestUser(username: "");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Username is required", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Create_DuplicateUsername_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var existingUser = CreateTestUser(username: "taken");
			mock.Setup(sm => sm.GetUserByUsername("taken")).Returns(existingUser);
			var user = CreateTestUser(username: "taken");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Username is already registered", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Create_MissingEmail_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var user = CreateTestUser(email: "");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Email is required", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Create_DuplicateEmail_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var existingUser = CreateTestUser(email: "dup@example.com");
			mock.Setup(sm => sm.GetUser("dup@example.com")).Returns(existingUser);
			var user = CreateTestUser(email: "dup@example.com");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Email is already registered", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Create_InvalidEmail_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var user = CreateTestUser(email: "not-an-email");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Email is not valid", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Create_MissingPassword_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var user = CreateTestUser(password: "");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Password is required", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Create_SetsPreferencesJson()
		{
			var (mock, rmMock) = CreatePartialMock();
			var user = CreateTestUser();
			user.Preferences = new ErpUserPreferences { SidebarSize = "sm" };
			EntityRecord capturedRecord = null;
			rmMock.Setup(rm => rm.CreateRecord("user", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			mock.Object.SaveUser(user);

			capturedRecord.Should().NotBeNull("CreateRecord should have been called");
			var preferencesJson = capturedRecord["preferences"] as string;
			preferencesJson.Should().NotBeNullOrEmpty();
			var expectedJson = JsonConvert.SerializeObject(user.Preferences);
			preferencesJson.Should().Be(expectedJson,
				"user preferences should be serialized as JSON");
		}

		[Fact]
		public void Test_SaveUser_Create_SetsRoleRelation()
		{
			var (mock, rmMock) = CreatePartialMock();
			var user = CreateTestUser(roles: new List<ErpRole> { _adminRole, _regularRole });
			EntityRecord capturedRecord = null;
			rmMock.Setup(rm => rm.CreateRecord("user", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			mock.Object.SaveUser(user);

			capturedRecord.Should().NotBeNull("CreateRecord should have been called");
			var roleIds = capturedRecord["$user_role.id"] as List<Guid>;
			roleIds.Should().NotBeNull("role relation should be set");
			roleIds.Should().HaveCount(2);
			roleIds.Should().Contain(SystemIds.AdministratorRoleId);
			roleIds.Should().Contain(SystemIds.RegularRoleId);
		}

		[Fact]
		public void Test_SaveUser_Create_FailedResponse_ThrowsException()
		{
			var (mock, rmMock) = CreatePartialMock();
			var user = CreateTestUser();
			rmMock.Setup(rm => rm.CreateRecord("user", It.IsAny<EntityRecord>()))
				.Returns(new QueryResponse { Success = false, Message = "Database error" });

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<Exception>().WithMessage("*Database error*");
		}

		#endregion

		#region <=== Phase 9: SaveUser Update Tests ===>

		[Fact]
		public void Test_SaveUser_UpdateExistingUser_OnlyChangedFields()
		{
			var (mock, rmMock) = CreatePartialMock();
			var existingUser = CreateTestUser(
				id: _testUserId, username: "original", email: "original@example.com",
				password: "original", enabled: true, verified: true);
			existingUser.FirstName = "OrigFirst";
			existingUser.LastName = "OrigLast";
			existingUser.Image = "/old.jpg";

			mock.Setup(sm => sm.GetUser(_testUserId)).Returns(existingUser);
			mock.Setup(sm => sm.GetUser("changed@example.com")).Returns((ErpUser)null);
			mock.Setup(sm => sm.GetUserByUsername("changed")).Returns((ErpUser)null);

			var updatedUser = CreateTestUser(
				id: _testUserId, username: "changed", email: "changed@example.com",
				password: "newpassword", enabled: false, verified: false);
			updatedUser.FirstName = "ChangedFirst";
			updatedUser.LastName = "ChangedLast";
			updatedUser.Image = "/new.jpg";

			EntityRecord capturedRecord = null;
			rmMock.Setup(rm => rm.UpdateRecord("user", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			mock.Object.SaveUser(updatedUser);

			capturedRecord.Should().NotBeNull("UpdateRecord should have been called");
			capturedRecord["id"].Should().Be(_testUserId);
			capturedRecord.Properties.ContainsKey("username").Should().BeTrue("username changed");
			capturedRecord["username"].Should().Be("changed");
			capturedRecord.Properties.ContainsKey("email").Should().BeTrue("email changed");
			capturedRecord["email"].Should().Be("changed@example.com");
		}

		[Fact]
		public void Test_SaveUser_Update_DuplicateUsername_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var existingUser = CreateTestUser(id: _testUserId, username: "original");
			mock.Setup(sm => sm.GetUser(_testUserId)).Returns(existingUser);
			var otherUser = CreateTestUser(id: Guid.NewGuid(), username: "taken");
			mock.Setup(sm => sm.GetUserByUsername("taken")).Returns(otherUser);
			var user = CreateTestUser(id: _testUserId, username: "taken");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Username is already registered", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Update_DuplicateEmail_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var existingUser = CreateTestUser(id: _testUserId, email: "original@example.com");
			mock.Setup(sm => sm.GetUser(_testUserId)).Returns(existingUser);
			var otherUser = CreateTestUser(id: Guid.NewGuid(), email: "taken@example.com");
			mock.Setup(sm => sm.GetUser("taken@example.com")).Returns(otherUser);
			var user = CreateTestUser(id: _testUserId, email: "taken@example.com");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Email is already registered", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Update_InvalidEmail_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var existingUser = CreateTestUser(id: _testUserId, email: "original@example.com");
			mock.Setup(sm => sm.GetUser(_testUserId)).Returns(existingUser);
			var user = CreateTestUser(id: _testUserId, email: "bad-email-format");

			Action act = () => mock.Object.SaveUser(user);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Email is not valid", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveUser_Update_AlwaysUpdatesRoles()
		{
			var (mock, rmMock) = CreatePartialMock();
			var existingUser = CreateTestUser(
				id: _testUserId, username: "sameuser", email: "same@example.com",
				password: "samepassword", roles: new List<ErpRole> { _regularRole });
			mock.Setup(sm => sm.GetUser(_testUserId)).Returns(existingUser);

			var user = CreateTestUser(
				id: _testUserId, username: "sameuser", email: "same@example.com",
				password: "samepassword", roles: new List<ErpRole> { _adminRole, _regularRole });

			EntityRecord capturedRecord = null;
			rmMock.Setup(rm => rm.UpdateRecord("user", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });

			mock.Object.SaveUser(user);

			capturedRecord.Should().NotBeNull("UpdateRecord should have been called");
			var roleIds = capturedRecord["$user_role.id"] as List<Guid>;
			roleIds.Should().NotBeNull("role relation should always be updated");
			roleIds.Should().HaveCount(2);
			roleIds.Should().Contain(SystemIds.AdministratorRoleId);
			roleIds.Should().Contain(SystemIds.RegularRoleId);
		}

		#endregion

		#region <=== Phase 10: SaveRole Tests ===>

		[Fact]
		public void Test_SaveRole_NullRole_ThrowsArgumentNullException()
		{
			var (sut, _) = CreateRealInstance();

			Action act = () => sut.SaveRole(null);

			act.Should().Throw<ArgumentNullException>()
				.Which.ParamName.Should().Be("role");
		}

		[Fact]
		public void Test_SaveRole_Create_Success()
		{
			var (mock, rmMock) = CreatePartialMock();
			mock.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>());
			var newRole = new ErpRole
			{
				Id = Guid.NewGuid(),
				Name = "newrole",
				Description = "A new role"
			};

			Action act = () => mock.Object.SaveRole(newRole);

			act.Should().NotThrow();
			rmMock.Verify(rm => rm.CreateRecord("role", It.IsAny<EntityRecord>()), Times.Once);
		}

		[Fact]
		public void Test_SaveRole_Create_EmptyName_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>());
			var role = new ErpRole { Id = Guid.NewGuid(), Name = "", Description = "" };

			Action act = () => mock.Object.SaveRole(role);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("Name is required", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveRole_Create_DuplicateName_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			mock.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>
			{
				new ErpRole { Id = Guid.NewGuid(), Name = "existing", Description = "" }
			});
			var role = new ErpRole
			{
				Id = Guid.NewGuid(),
				Name = "existing",
				Description = "Duplicate"
			};

			Action act = () => mock.Object.SaveRole(role);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("same name already exists", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveRole_Update_Success()
		{
			var (mock, rmMock) = CreatePartialMock();
			var existingRoleId = Guid.NewGuid();
			mock.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>
			{
				new ErpRole { Id = existingRoleId, Name = "oldrole", Description = "Old" }
			});
			var role = new ErpRole
			{
				Id = existingRoleId,
				Name = "updatedrole",
				Description = "Updated"
			};

			Action act = () => mock.Object.SaveRole(role);

			act.Should().NotThrow();
			rmMock.Verify(rm => rm.UpdateRecord("role", It.IsAny<EntityRecord>()), Times.Once);
		}

		[Fact]
		public void Test_SaveRole_Update_DuplicateName_ThrowsValidationException()
		{
			var (mock, _) = CreatePartialMock();
			var roleToUpdateId = Guid.NewGuid();
			var anotherRoleId = Guid.NewGuid();
			mock.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>
			{
				new ErpRole { Id = roleToUpdateId, Name = "role1", Description = "" },
				new ErpRole { Id = anotherRoleId, Name = "taken", Description = "" }
			});
			var role = new ErpRole { Id = roleToUpdateId, Name = "taken", Description = "" };

			Action act = () => mock.Object.SaveRole(role);

			act.Should().Throw<ValidationException>()
				.Where(ex => ex.Errors.Any(e =>
					e.Message.Contains("same name already exists", StringComparison.OrdinalIgnoreCase)));
		}

		[Fact]
		public void Test_SaveRole_NullDescription_DefaultsToEmpty()
		{
			var (mock, rmMock) = CreatePartialMock();
			mock.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>());
			EntityRecord capturedRecord = null;
			rmMock.Setup(rm => rm.CreateRecord("role", It.IsAny<EntityRecord>()))
				.Callback<string, EntityRecord>((entity, record) => capturedRecord = record)
				.Returns(new QueryResponse { Success = true });
			var role = new ErpRole
			{
				Id = Guid.NewGuid(),
				Name = "rolewithnulldesc",
				Description = null
			};

			mock.Object.SaveRole(role);

			capturedRecord.Should().NotBeNull();
			var desc = capturedRecord["description"] as string;
			desc.Should().NotBeNull("null description should default to empty string");
			desc.Should().Be(String.Empty);
		}

		[Fact]
		public void Test_SaveRole_Update_FailedResponse_ThrowsException()
		{
			var (mock, rmMock) = CreatePartialMock();
			var roleId = Guid.NewGuid();
			mock.Setup(sm => sm.GetAllRoles()).Returns(new List<ErpRole>
			{
				new ErpRole { Id = roleId, Name = "existing", Description = "" }
			});
			rmMock.Setup(rm => rm.UpdateRecord("role", It.IsAny<EntityRecord>()))
				.Returns(new QueryResponse { Success = false, Message = "Update failed" });
			var role = new ErpRole
			{
				Id = roleId,
				Name = "existing-updated",
				Description = "Updated"
			};

			Action act = () => mock.Object.SaveRole(role);

			act.Should().Throw<Exception>().WithMessage("*Update failed*");
		}

		#endregion

		#region <=== Phase 11: UpdateUserLastLoginTime Tests ===>

		[Fact]
		public void Test_UpdateUserLastLoginTime_UpdatesLastLoggedIn()
		{
			var (mock, _) = CreatePartialMock();
			var userId = Guid.NewGuid();
			bool methodCalled = false;
			Guid capturedUserId = Guid.Empty;
			mock.Setup(sm => sm.UpdateLastLoginAndModifiedDate(It.IsAny<Guid>()))
				.Callback<Guid>(id => { methodCalled = true; capturedUserId = id; });

			mock.Object.UpdateLastLoginAndModifiedDate(userId);

			methodCalled.Should().BeTrue("UpdateLastLoginAndModifiedDate should have been called");
			capturedUserId.Should().Be(userId, "correct user ID should be passed");
		}

		[Fact]
		public void Test_UpdateUserLastLoginTime_UsesCorrectEntity()
		{
			var (mock, _) = CreatePartialMock();
			var userId = _testUserId;
			mock.Setup(sm => sm.UpdateLastLoginAndModifiedDate(userId)).Verifiable();

			mock.Object.UpdateLastLoginAndModifiedDate(userId);

			mock.Verify(sm => sm.UpdateLastLoginAndModifiedDate(userId), Times.Once);
		}

		#endregion

		#region <=== Phase 12: Email Validation Tests ===>

		[Theory]
		[InlineData("test@example.com")]
		[InlineData("user.name@domain.org")]
		[InlineData("a@b.co")]
		public void Test_IsValidEmail_ValidEmail_ReturnsTrue(string email)
		{
			var (sut, _) = CreateRealInstance();

			var result = InvokeIsValidEmail(sut, email);

			result.Should().BeTrue($"'{email}' should be a valid email");
		}

		[Theory]
		[InlineData("not-an-email")]
		[InlineData("@missing-local.com")]
		[InlineData("missing-domain@")]
		[InlineData("")]
		public void Test_IsValidEmail_InvalidEmail_ReturnsFalse(string email)
		{
			var (sut, _) = CreateRealInstance();

			var result = InvokeIsValidEmail(sut, email);

			result.Should().BeFalse($"'{email}' should not be a valid email");
		}

		[Theory]
		[InlineData("test@example.com ")]
		[InlineData(" test@example.com")]
		[InlineData("test @example.com")]
		public void Test_IsValidEmail_EmailWithExtraChars_ReturnsFalse(string email)
		{
			var (sut, _) = CreateRealInstance();

			var result = InvokeIsValidEmail(sut, email);

			result.Should().BeFalse($"'{email}' with extra chars should not be valid");
		}

		#endregion

		#region <=== Phase 13: JWT Token Tests ===>

		[Fact]
		public async void Test_JwtTokenCreation_IncludesUserClaims()
		{
			var options = new JwtTokenOptions
			{
				Key = JwtTokenOptions.DefaultDevelopmentKey,
				Issuer = "test-issuer",
				Audience = "test-audience",
				TokenExpiryMinutes = 1440,
				TokenRefreshMinutes = 120
			};
			var handler = new JwtTokenHandler(options);
			var user = new ErpUser
			{
				Id = _testUserId,
				Username = "jwtuser",
				Email = "jwt@example.com",
				FirstName = "Jwt",
				LastName = "User",
				Enabled = true,
				Verified = true
			};
			user.Roles.Add(_adminRole);

			var (tokenString, securityToken) = await handler.BuildTokenAsync(user);

			tokenString.Should().NotBeNullOrEmpty("token string should be generated");
			securityToken.Should().NotBeNull("JwtSecurityToken should be created");
			var jwtToken = securityToken as JwtSecurityToken;
			jwtToken.Should().NotBeNull();
			var claims = jwtToken.Claims.ToList();
			claims.Should().Contain(c => c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier,
				"token should contain user identifier claim");
			var extractedUserId = JwtTokenHandler.ExtractUserIdFromToken(jwtToken);
			extractedUserId.Should().Be(_testUserId,
				"extracted user ID should match the original user");
		}

		[Fact]
		public async void Test_JwtTokenValidation_ValidToken_ReturnsUser()
		{
			var options = new JwtTokenOptions
			{
				Key = JwtTokenOptions.DefaultDevelopmentKey,
				Issuer = "test-issuer",
				Audience = "test-audience",
				TokenExpiryMinutes = 1440,
				TokenRefreshMinutes = 120
			};
			var handler = new JwtTokenHandler(options);
			var user = new ErpUser
			{
				Id = _adminUserId,
				Username = "validateuser",
				Email = "validate@example.com",
				FirstName = "Validate",
				LastName = "User",
				Enabled = true,
				Verified = true
			};
			user.Roles.Add(_regularRole);
			var (tokenString, _) = await handler.BuildTokenAsync(user);

			var validatedToken = await handler.GetValidSecurityTokenAsync(tokenString);

			validatedToken.Should().NotBeNull("valid token should be successfully validated");
			var jwtValidated = validatedToken as JwtSecurityToken;
			jwtValidated.Should().NotBeNull();
			var extractedId = JwtTokenHandler.ExtractUserIdFromToken(jwtValidated);
			extractedId.Should().Be(_adminUserId,
				"validated token should contain the correct user ID");
			jwtValidated.Issuer.Should().Be("test-issuer");
			jwtValidated.Audiences.Should().Contain("test-audience");
		}

		#endregion
	}
}
