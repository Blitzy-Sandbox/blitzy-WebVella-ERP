using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Xunit;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.SharedKernel.Models
{
    /// <summary>
    /// Unit tests for the <see cref="ErpUser"/> model from the SharedKernel.
    /// Validates constructor defaults, property get/set behavior, Newtonsoft.Json
    /// serialization annotations ([JsonProperty] / [JsonIgnore]), the computed
    /// IsAdmin property, and the cross-service identity claims support added
    /// during the monolith-to-microservices migration.
    /// </summary>
    public class ErpUserTests
    {
        // ────────────────────────────────────────────────────────────────
        // Region: Constructor / Default Value Tests
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Constructor_SetsDefaultId_ToGuidEmpty()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.Id.Should().Be(Guid.Empty);
        }

        [Fact]
        public void Constructor_SetsDefaultEmail_ToEmptyString()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.Email.Should().Be(string.Empty);
        }

        [Fact]
        public void Constructor_SetsDefaultPassword_ToEmptyString()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.Password.Should().Be(string.Empty);
        }

        [Fact]
        public void Constructor_SetsDefaultFirstName_ToEmptyString()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.FirstName.Should().Be(string.Empty);
        }

        [Fact]
        public void Constructor_SetsDefaultLastName_ToEmptyString()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.LastName.Should().Be(string.Empty);
        }

        [Fact]
        public void Constructor_SetsDefaultUsername_ToEmptyString()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.Username.Should().Be(string.Empty);
        }

        [Fact]
        public void Constructor_SetsDefaultEnabled_ToTrue()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.Enabled.Should().BeTrue();
        }

        [Fact]
        public void Constructor_SetsDefaultVerified_ToTrue()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert
            user.Verified.Should().BeTrue();
        }

        [Fact]
        public void Constructor_SetsDefaultRoles_ToEmptyList()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert — Roles should be initialized but empty (Roles has private set, init to new List<ErpRole>())
            user.Roles.Should().NotBeNull();
            user.Roles.Should().BeEmpty();
        }

        // ────────────────────────────────────────────────────────────────
        // Region: Property Get/Set Tests
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Id_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedId = Guid.NewGuid();

            // Act
            user.Id = expectedId;

            // Assert
            user.Id.Should().Be(expectedId);
        }

        [Fact]
        public void Username_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedUsername = "testuser";

            // Act
            user.Username = expectedUsername;

            // Assert
            user.Username.Should().Be(expectedUsername);
        }

        [Fact]
        public void Email_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedEmail = "test@webvella.com";

            // Act
            user.Email = expectedEmail;

            // Assert
            user.Email.Should().Be(expectedEmail);
        }

        [Fact]
        public void Password_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedPassword = "Str0ng!P@ssw0rd";

            // Act
            user.Password = expectedPassword;

            // Assert
            user.Password.Should().Be(expectedPassword);
        }

        [Fact]
        public void FirstName_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedFirstName = "John";

            // Act
            user.FirstName = expectedFirstName;

            // Assert
            user.FirstName.Should().Be(expectedFirstName);
        }

        [Fact]
        public void LastName_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedLastName = "Doe";

            // Act
            user.LastName = expectedLastName;

            // Assert
            user.LastName.Should().Be(expectedLastName);
        }

        [Fact]
        public void Image_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedImage = "/images/avatar.png";

            // Act
            user.Image = expectedImage;

            // Assert
            user.Image.Should().Be(expectedImage);
        }

        [Fact]
        public void Enabled_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();

            // Act — default is true, so set to false to verify the setter works
            user.Enabled = false;

            // Assert
            user.Enabled.Should().BeFalse();
        }

        [Fact]
        public void Verified_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();

            // Act — default is true, so set to false to verify the setter works
            user.Verified = false;

            // Assert
            user.Verified.Should().BeFalse();
        }

        [Fact]
        public void CreatedOn_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedDate = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);

            // Act
            user.CreatedOn = expectedDate;

            // Assert
            user.CreatedOn.Should().Be(expectedDate);
        }

        [Fact]
        public void LastLoggedIn_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var expectedDate = new DateTime(2025, 12, 25, 14, 0, 0, DateTimeKind.Utc);

            // Act
            user.LastLoggedIn = expectedDate;

            // Assert
            user.LastLoggedIn.Should().Be(expectedDate);
        }

        [Fact]
        public void LastLoggedIn_DefaultIsNull()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert — LastLoggedIn is DateTime? and has no constructor default, so it should be null
            user.LastLoggedIn.Should().BeNull();
        }

        [Fact]
        public void Preferences_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var user = new ErpUser();
            var prefs = new ErpUserPreferences { SidebarSize = "lg" };

            // Act
            user.Preferences = prefs;

            // Assert
            user.Preferences.Should().NotBeNull();
            user.Preferences.SidebarSize.Should().Be("lg");
        }

        // ────────────────────────────────────────────────────────────────
        // Region: JSON Serialization Tests
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Serialize_IncludesJsonPropertyNames()
        {
            // Arrange — Create a user with values set for all serializable properties
            var user = new ErpUser
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                Email = "admin@webvella.com",
                FirstName = "Admin",
                LastName = "User",
                Image = "/img/admin.png",
                CreatedOn = DateTime.UtcNow,
                LastLoggedIn = DateTime.UtcNow,
                Preferences = new ErpUserPreferences { SidebarSize = "md" }
            };

            // Act
            var json = JsonConvert.SerializeObject(user);
            var jObj = JObject.Parse(json);

            // Assert — Verify all [JsonProperty] annotated keys are present
            jObj.ContainsKey("id").Should().BeTrue("id should be in JSON");
            jObj.ContainsKey("username").Should().BeTrue("username should be in JSON");
            jObj.ContainsKey("email").Should().BeTrue("email should be in JSON");
            jObj.ContainsKey("firstName").Should().BeTrue("firstName should be in JSON");
            jObj.ContainsKey("lastName").Should().BeTrue("lastName should be in JSON");
            jObj.ContainsKey("image").Should().BeTrue("image should be in JSON");
            jObj.ContainsKey("createdOn").Should().BeTrue("createdOn should be in JSON");
            jObj.ContainsKey("lastLoggedIn").Should().BeTrue("lastLoggedIn should be in JSON");
            jObj.ContainsKey("is_admin").Should().BeTrue("is_admin should be in JSON");
            jObj.ContainsKey("preferences").Should().BeTrue("preferences should be in JSON");
            jObj.ContainsKey("claims").Should().BeTrue("claims should be in JSON");
        }

        [Fact]
        public void Serialize_ExcludesPassword()
        {
            // Arrange
            var user = new ErpUser { Password = "secret123" };

            // Act
            var json = JsonConvert.SerializeObject(user);
            var jObj = JObject.Parse(json);

            // Assert — Password has [JsonIgnore], so it must NOT appear in serialized JSON
            jObj.ContainsKey("password").Should().BeFalse("Password is decorated with [JsonIgnore]");
            jObj.ContainsKey("Password").Should().BeFalse("Password (PascalCase) should also not appear");
        }

        [Fact]
        public void Serialize_ExcludesEnabled()
        {
            // Arrange
            var user = new ErpUser { Enabled = true };

            // Act
            var json = JsonConvert.SerializeObject(user);
            var jObj = JObject.Parse(json);

            // Assert — Enabled has [JsonIgnore], so it must NOT appear in serialized JSON
            jObj.ContainsKey("enabled").Should().BeFalse("Enabled is decorated with [JsonIgnore]");
            jObj.ContainsKey("Enabled").Should().BeFalse("Enabled (PascalCase) should also not appear");
        }

        [Fact]
        public void Serialize_ExcludesVerified()
        {
            // Arrange
            var user = new ErpUser { Verified = true };

            // Act
            var json = JsonConvert.SerializeObject(user);
            var jObj = JObject.Parse(json);

            // Assert — Verified has [JsonIgnore], so it must NOT appear in serialized JSON
            jObj.ContainsKey("verified").Should().BeFalse("Verified is decorated with [JsonIgnore]");
            jObj.ContainsKey("Verified").Should().BeFalse("Verified (PascalCase) should also not appear");
        }

        [Fact]
        public void Serialize_ExcludesRoles()
        {
            // Arrange — Add a role to confirm it still does NOT serialize
            var user = new ErpUser();
            user.Roles.Add(new ErpRole { Id = Guid.NewGuid(), Name = "TestRole" });

            // Act
            var json = JsonConvert.SerializeObject(user);
            var jObj = JObject.Parse(json);

            // Assert — Roles has [JsonIgnore], so it must NOT appear in serialized JSON
            jObj.ContainsKey("roles").Should().BeFalse("Roles is decorated with [JsonIgnore]");
            jObj.ContainsKey("Roles").Should().BeFalse("Roles (PascalCase) should also not appear");
        }

        [Fact]
        public void Deserialize_SetsJsonProperties()
        {
            // Arrange — JSON with all visible property names as defined by [JsonProperty]
            var userId = Guid.NewGuid();
            var createdOn = new DateTime(2025, 1, 15, 8, 0, 0, DateTimeKind.Utc);
            var lastLoggedIn = new DateTime(2025, 6, 20, 12, 0, 0, DateTimeKind.Utc);

            var json = $@"{{
                ""id"": ""{userId}"",
                ""username"": ""jdoe"",
                ""email"": ""jdoe@webvella.com"",
                ""firstName"": ""Jane"",
                ""lastName"": ""Doe"",
                ""image"": ""/img/jane.png"",
                ""createdOn"": ""{createdOn:O}"",
                ""lastLoggedIn"": ""{lastLoggedIn:O}"",
                ""preferences"": {{ ""sidebar_size"": ""sm"" }},
                ""claims"": {{ ""tenant_id"": ""abc-123"" }}
            }}";

            // Act
            var user = JsonConvert.DeserializeObject<ErpUser>(json);

            // Assert — All properties should be correctly mapped from their JSON names
            user.Should().NotBeNull();
            user.Id.Should().Be(userId);
            user.Username.Should().Be("jdoe");
            user.Email.Should().Be("jdoe@webvella.com");
            user.FirstName.Should().Be("Jane");
            user.LastName.Should().Be("Doe");
            user.Image.Should().Be("/img/jane.png");
            user.CreatedOn.Should().Be(createdOn);
            user.LastLoggedIn.Should().Be(lastLoggedIn);
            user.Preferences.Should().NotBeNull();
            user.Preferences.SidebarSize.Should().Be("sm");
            user.Claims.Should().ContainKey("tenant_id");
            user.Claims["tenant_id"].Should().Be("abc-123");
        }

        [Fact]
        public void Serialize_Roundtrip_PreservesVisibleProperties()
        {
            // Arrange — Create a fully populated user
            var originalUser = new ErpUser
            {
                Id = Guid.NewGuid(),
                Username = "roundtrip_user",
                Email = "roundtrip@webvella.com",
                Password = "should_not_survive_roundtrip",
                FirstName = "Round",
                LastName = "Trip",
                Image = "/img/roundtrip.png",
                Enabled = false,
                Verified = false,
                CreatedOn = new DateTime(2025, 3, 10, 9, 0, 0, DateTimeKind.Utc),
                LastLoggedIn = new DateTime(2025, 6, 1, 18, 30, 0, DateTimeKind.Utc),
                Preferences = new ErpUserPreferences { SidebarSize = "xl" }
            };
            originalUser.Claims["custom_claim"] = "custom_value";

            // Act — Serialize then deserialize
            var json = JsonConvert.SerializeObject(originalUser);
            var deserialized = JsonConvert.DeserializeObject<ErpUser>(json);

            // Assert — Visible (non-JsonIgnore) properties should be preserved
            deserialized.Should().NotBeNull();
            deserialized.Id.Should().Be(originalUser.Id);
            deserialized.Username.Should().Be(originalUser.Username);
            deserialized.Email.Should().Be(originalUser.Email);
            deserialized.FirstName.Should().Be(originalUser.FirstName);
            deserialized.LastName.Should().Be(originalUser.LastName);
            deserialized.Image.Should().Be(originalUser.Image);
            deserialized.CreatedOn.Should().Be(originalUser.CreatedOn);
            deserialized.LastLoggedIn.Should().Be(originalUser.LastLoggedIn);
            deserialized.Preferences.Should().NotBeNull();
            deserialized.Preferences.SidebarSize.Should().Be("xl");
            deserialized.Claims.Should().ContainKey("custom_claim");
            deserialized.Claims["custom_claim"].Should().Be("custom_value");

            // Excluded properties should NOT survive the roundtrip
            // Password defaults to empty string in the constructor
            deserialized.Password.Should().Be(string.Empty,
                "Password is [JsonIgnore] and should not survive serialization roundtrip");
            // Enabled and Verified reset to constructor defaults (true)
            deserialized.Enabled.Should().BeTrue(
                "Enabled is [JsonIgnore] and should reset to constructor default (true)");
            deserialized.Verified.Should().BeTrue(
                "Verified is [JsonIgnore] and should reset to constructor default (true)");
            // Roles should be empty since they are [JsonIgnore]
            deserialized.Roles.Should().BeEmpty(
                "Roles is [JsonIgnore] and should not survive serialization roundtrip");
        }

        // ────────────────────────────────────────────────────────────────
        // Region: Computed IsAdmin Property Tests
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void IsAdmin_WithNoRoles_ReturnsFalse()
        {
            // Arrange — Default user has no roles
            var user = new ErpUser();

            // Act & Assert
            user.IsAdmin.Should().BeFalse("no roles means user is not an admin");
        }

        [Fact]
        public void IsAdmin_WithAdminRole_ReturnsTrue()
        {
            // Arrange — Add the administrator role
            var user = new ErpUser();
            user.Roles.Add(new ErpRole
            {
                Id = SystemIds.AdministratorRoleId,
                Name = "Administrator"
            });

            // Act & Assert
            user.IsAdmin.Should().BeTrue(
                "user has a role with Id == SystemIds.AdministratorRoleId (BDC56420-CAF0-4030-8A0E-D264938E0CDA)");
        }

        [Fact]
        public void IsAdmin_WithNonAdminRoles_ReturnsFalse()
        {
            // Arrange — Add only non-admin roles
            var user = new ErpUser();
            user.Roles.Add(new ErpRole
            {
                Id = SystemIds.RegularRoleId,
                Name = "Regular"
            });
            user.Roles.Add(new ErpRole
            {
                Id = SystemIds.GuestRoleId,
                Name = "Guest"
            });

            // Act & Assert
            user.IsAdmin.Should().BeFalse("none of the roles match AdministratorRoleId");
        }

        [Fact]
        public void IsAdmin_WithMixedRoles_ReturnsTrue()
        {
            // Arrange — Add both admin and non-admin roles
            var user = new ErpUser();
            user.Roles.Add(new ErpRole
            {
                Id = SystemIds.RegularRoleId,
                Name = "Regular"
            });
            user.Roles.Add(new ErpRole
            {
                Id = SystemIds.AdministratorRoleId,
                Name = "Administrator"
            });
            user.Roles.Add(new ErpRole
            {
                Id = SystemIds.GuestRoleId,
                Name = "Guest"
            });

            // Act & Assert
            user.IsAdmin.Should().BeTrue("one of the roles matches AdministratorRoleId");
        }

        [Fact]
        public void IsAdmin_SerializesAs_is_admin()
        {
            // Arrange — Create admin user
            var user = new ErpUser();
            user.Roles.Add(new ErpRole
            {
                Id = SystemIds.AdministratorRoleId,
                Name = "Administrator"
            });

            // Act
            var json = JsonConvert.SerializeObject(user);
            var jObj = JObject.Parse(json);

            // Assert — The JSON key must be "is_admin" (snake_case), not "IsAdmin" (PascalCase)
            jObj.ContainsKey("is_admin").Should().BeTrue("JsonProperty name is 'is_admin'");
            jObj.ContainsKey("IsAdmin").Should().BeFalse("PascalCase should NOT appear in JSON");
            jObj["is_admin"].Value<bool>().Should().BeTrue("user has admin role");
        }

        [Fact]
        public void IsAdmin_IsReadOnly_InJson()
        {
            // Arrange — JSON claims is_admin is true, but no admin role is provided.
            // Since IsAdmin is a computed getter (reads from Roles), deserialization
            // cannot force it to true without the actual admin role being present.
            var json = @"{
                ""id"": ""00000000-0000-0000-0000-000000000001"",
                ""username"": ""fakeadmin"",
                ""is_admin"": true
            }";

            // Act
            var user = JsonConvert.DeserializeObject<ErpUser>(json);

            // Assert — IsAdmin must still compute from Roles (which is empty after deserialization)
            user.Should().NotBeNull();
            user.IsAdmin.Should().BeFalse(
                "IsAdmin is a computed getter that checks Roles collection, " +
                "and deserialization cannot inject roles via the is_admin JSON key");
            user.Roles.Should().BeEmpty("Roles is [JsonIgnore] and cannot be set via JSON");
        }

        // ────────────────────────────────────────────────────────────────
        // Region: Cross-Service Identity Claims Tests
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void FromClaims_MapsClaimTypes_ToProperties()
        {
            // Arrange — Build a realistic set of JWT claims
            var userId = Guid.NewGuid();
            var adminRoleId = SystemIds.AdministratorRoleId;
            var regularRoleId = SystemIds.RegularRoleId;

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "claimsuser"),
                new Claim(ClaimTypes.Email, "claims@webvella.com"),
                new Claim(ClaimTypes.GivenName, "Claims"),
                new Claim(ClaimTypes.Surname, "User"),
                new Claim("image", "/img/claims.png"),
                new Claim(ClaimTypes.Role, adminRoleId.ToString()),
                new Claim("role_name", "Administrator"),
                new Claim(ClaimTypes.Role, regularRoleId.ToString()),
                new Claim("role_name", "Regular")
            };

            // Act
            var user = ErpUser.FromClaims(claims);

            // Assert — Verify each standard claim type maps to the correct property
            user.Should().NotBeNull();
            user.Id.Should().Be(userId);
            user.Username.Should().Be("claimsuser");
            user.Email.Should().Be("claims@webvella.com");
            user.FirstName.Should().Be("Claims");
            user.LastName.Should().Be("User");
            user.Image.Should().Be("/img/claims.png");

            // Roles should contain both roles with correct Ids and Names
            user.Roles.Should().HaveCount(2);
            user.Roles.Any(r => r.Id == adminRoleId && r.Name == "Administrator").Should().BeTrue();
            user.Roles.Any(r => r.Id == regularRoleId && r.Name == "Regular").Should().BeTrue();

            // IsAdmin should be true since admin role is present
            user.IsAdmin.Should().BeTrue();

            // Claims dictionary should contain all claim type-value pairs
            user.Claims.Should().ContainKey(ClaimTypes.NameIdentifier);
            user.Claims[ClaimTypes.NameIdentifier].Should().Be(userId.ToString());
            user.Claims.Should().ContainKey(ClaimTypes.Name);
            user.Claims[ClaimTypes.Name].Should().Be("claimsuser");
            user.Claims.Should().ContainKey(ClaimTypes.Email);
            user.Claims[ClaimTypes.Email].Should().Be("claims@webvella.com");
            user.Claims.Should().ContainKey("image");
            user.Claims["image"].Should().Be("/img/claims.png");
        }

        [Fact]
        public void ToClaims_MapsProperties_ToClaimTypes()
        {
            // Arrange — Create a user with all properties populated
            var userId = Guid.NewGuid();
            var adminRoleId = SystemIds.AdministratorRoleId;

            var user = new ErpUser
            {
                Id = userId,
                Username = "exportuser",
                Email = "export@webvella.com",
                FirstName = "Export",
                LastName = "User",
                Image = "/img/export.png"
            };
            user.Roles.Add(new ErpRole { Id = adminRoleId, Name = "Administrator" });
            user.Claims["custom_key"] = "custom_value";

            // Act
            var claims = user.ToClaims();

            // Assert — Verify standard claims are mapped correctly
            claims.Should().NotBeNull();

            claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)
                .Should().NotBeNull();
            claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value
                .Should().Be(userId.ToString());

            claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)
                .Should().NotBeNull();
            claims.First(c => c.Type == ClaimTypes.Name).Value
                .Should().Be("exportuser");

            claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)
                .Should().NotBeNull();
            claims.First(c => c.Type == ClaimTypes.Email).Value
                .Should().Be("export@webvella.com");

            claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)
                .Should().NotBeNull();
            claims.First(c => c.Type == ClaimTypes.GivenName).Value
                .Should().Be("Export");

            claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)
                .Should().NotBeNull();
            claims.First(c => c.Type == ClaimTypes.Surname).Value
                .Should().Be("User");

            // Image claim
            claims.FirstOrDefault(c => c.Type == "image")
                .Should().NotBeNull();
            claims.First(c => c.Type == "image").Value
                .Should().Be("/img/export.png");

            // Role claims
            claims.Any(c => c.Type == ClaimTypes.Role && c.Value == adminRoleId.ToString())
                .Should().BeTrue("admin role ID should be in claims");
            claims.Any(c => c.Type == "role_name" && c.Value == "Administrator")
                .Should().BeTrue("admin role name should be in claims");

            // Custom claims from the Claims dictionary
            claims.Any(c => c.Type == "custom_key" && c.Value == "custom_value")
                .Should().BeTrue("custom claims from the Claims dictionary should be included");
        }

        [Fact]
        public void Claims_DefaultIsEmptyDictionary()
        {
            // Arrange & Act
            var user = new ErpUser();

            // Assert — Claims dictionary should be initialized but empty
            user.Claims.Should().NotBeNull();
            user.Claims.Should().BeEmpty();
        }
    }
}
