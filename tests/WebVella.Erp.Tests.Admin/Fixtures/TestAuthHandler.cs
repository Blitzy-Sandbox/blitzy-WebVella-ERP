using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebVella.Erp.Tests.Admin.Fixtures
{
    /// <summary>
    /// Custom <see cref="AuthenticationHandler{TOptions}"/> that bypasses JWT validation
    /// for Admin service integration tests. This handler always returns a successful
    /// authentication result with configurable claims, allowing test classes to simulate
    /// authenticated admin users without generating or validating real JWT tokens.
    ///
    /// <para>
    /// Default identity values match the monolith's well-known SystemIds from
    /// <c>WebVella.Erp/Api/Definitions.cs</c> and the first admin user created
    /// in <c>ERPService.cs</c>.
    /// </para>
    ///
    /// <para>
    /// Tests can override the default identity by setting custom HTTP headers:
    /// <list type="bullet">
    ///   <item><c>X-Test-UserId</c> — Override the user's unique identifier</item>
    ///   <item><c>X-Test-Username</c> — Override the username claim</item>
    ///   <item><c>X-Test-Email</c> — Override the email claim</item>
    ///   <item><c>X-Test-Roles</c> — Override roles (comma-separated list)</item>
    /// </list>
    /// </para>
    /// </summary>
    public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        /// <summary>
        /// The authentication scheme name used to register this handler.
        /// Must match the scheme name used in <c>services.AddAuthentication("Test")</c>
        /// within the <see cref="AdminWebApplicationFactory"/>.
        /// </summary>
        public const string TestScheme = "Test";

        /// <summary>
        /// Default user ID matching <c>SystemIds.FirstUserId</c> from
        /// <c>WebVella.Erp/Api/Definitions.cs</c> line 20.
        /// This is the admin user created during ERP bootstrap in <c>ERPService.cs</c>.
        /// </summary>
        public const string DefaultUserId = "EABD66FD-8DE1-4D79-9674-447EE89921C2";

        /// <summary>
        /// Default username for the admin user, matching the first user's username
        /// set in <c>ERPService.cs</c> line 471 during system initialization.
        /// </summary>
        public const string DefaultUsername = "administrator";

        /// <summary>
        /// Default email for the admin user, matching the first user's email
        /// set in <c>ERPService.cs</c> line 470 during system initialization.
        /// </summary>
        public const string DefaultEmail = "erp@webvella.com";

        /// <summary>
        /// Administrator role ID matching <c>SystemIds.AdministratorRoleId</c> from
        /// <c>WebVella.Erp/Api/Definitions.cs</c> line 15.
        /// Used for reference when constructing role-based authorization test scenarios.
        /// </summary>
        public const string AdministratorRoleId = "BDC56420-CAF0-4030-8A0E-D264938E0CDA";

        /// <summary>
        /// Regular role ID matching <c>SystemIds.RegularRoleId</c> from
        /// <c>WebVella.Erp/Api/Definitions.cs</c> line 16.
        /// Used for constructing non-admin user test scenarios via the
        /// <c>X-Test-Roles</c> header override mechanism.
        /// </summary>
        public const string RegularRoleId = "F16EC6DB-626D-4C27-8DE0-3E7CE542C55F";

        /// <summary>
        /// Default role name for admin claims. This matches the administrator role name
        /// used in <c>SecurityContext.cs</c> line 26 and is required for
        /// <c>[Authorize(Roles = "administrator")]</c> to pass on AdminController endpoints.
        /// </summary>
        public const string AdminRoleName = "administrator";

        /// <summary>
        /// Initializes a new instance of the <see cref="TestAuthHandler"/> class.
        /// Uses the .NET 10 compatible 3-parameter constructor signature
        /// (ISystemClock was removed in .NET 8+).
        /// </summary>
        /// <param name="options">The monitor for the authentication scheme options.</param>
        /// <param name="logger">The logger factory for creating loggers.</param>
        /// <param name="encoder">The URL encoder for encoding redirect URIs.</param>
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        /// <summary>
        /// Handles the authentication request by always returning a successful result
        /// with configurable claims. The default identity represents the system admin user
        /// with the administrator role, matching the monolith's first user bootstrap.
        ///
        /// <para>
        /// Tests can override identity values by setting the following request headers:
        /// <list type="bullet">
        ///   <item><c>X-Test-UserId</c> — Override <see cref="DefaultUserId"/></item>
        ///   <item><c>X-Test-Username</c> — Override <see cref="DefaultUsername"/></item>
        ///   <item><c>X-Test-Email</c> — Override <see cref="DefaultEmail"/></item>
        ///   <item><c>X-Test-Roles</c> — Comma-separated role names (overrides default admin role)</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <returns>
        /// A <see cref="Task{TResult}"/> containing <see cref="AuthenticateResult.Success"/>
        /// with an <see cref="AuthenticationTicket"/> wrapping the constructed claims principal.
        /// This handler never returns a failure result.
        /// </returns>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Start with default admin identity values
            var userId = DefaultUserId;
            var username = DefaultUsername;
            var email = DefaultEmail;
            var roles = new List<string> { AdminRoleName };

            // Allow tests to override identity via custom HTTP headers.
            // This enables testing different authorization scenarios (e.g., non-admin users,
            // different user IDs for ownership checks) without creating separate handler instances.
            if (Request.Headers.TryGetValue("X-Test-UserId", out var userIdHeader))
            {
                userId = userIdHeader.ToString();
            }

            if (Request.Headers.TryGetValue("X-Test-Username", out var usernameHeader))
            {
                username = usernameHeader.ToString();
            }

            if (Request.Headers.TryGetValue("X-Test-Email", out var emailHeader))
            {
                email = emailHeader.ToString();
            }

            if (Request.Headers.TryGetValue("X-Test-Roles", out var rolesHeader))
            {
                roles = new List<string>(rolesHeader.ToString().Split(','));
            }

            // Build the claims list with both standard ASP.NET Core claim types and
            // JWT-standard claim names for cross-compatibility:
            // - ClaimTypes.NameIdentifier + "sub" both carry the user ID
            // - ClaimTypes.Name + "username" both carry the username
            // - ClaimTypes.Email carries the email address
            // - ClaimTypes.Role carries each role for [Authorize(Roles = "...")] support
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Email, email),
                new Claim("sub", userId),
                new Claim("username", username),
            };

            // Add role claims — each role gets its own ClaimTypes.Role claim entry,
            // which is required for ASP.NET Core's [Authorize(Roles = "administrator")]
            // attribute to function correctly on AdminController endpoints.
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
            }

            var identity = new ClaimsIdentity(claims, TestScheme);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, TestScheme);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
