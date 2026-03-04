using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Gateway.Middleware
{
	/// <summary>
	/// JWT + Cookie dual-mode authentication middleware for the API Gateway.
	///
	/// Combines and adapts two monolith middleware files into a single Gateway-aware pipeline:
	///   1. ErpMiddleware.cs — per-request ERP DB context + security scope setup
	///   2. JwtMiddleware.cs — JWT token extraction, validation, and user attachment
	///
	/// Key architectural differences from the monolith:
	///   - NO database context creation (Gateway has no direct DB access)
	///   - NO SecurityManager database lookups for user resolution
	///   - User identity is extracted entirely from JWT claims via SecurityContext.ExtractUserFromClaims
	///   - JWT validation is delegated to the SharedKernel JwtTokenHandler (instance-based, DI injected)
	///   - Validated JWT tokens are stored in HttpContext.Items["JwtToken"] for downstream
	///     RequestRoutingMiddleware to propagate to backend microservices
	///
	/// Preserved behaviors from the monolith:
	///   - Synchronous IO enablement (IHttpBodyControlFeature.AllowSynchronousIO = true)
	///   - JWT extraction: GetTokenAsync("access_token") → Authorization header → Substring(7) strip
	///   - context.Items["User"] storage for downstream consumers
	///   - ClaimsIdentity replacement with auth type "jwt"
	///   - SecurityContext.OpenScope(user) with IDisposable cleanup in finally block
	///   - Stale cookie sign-out when authenticated but user cannot be extracted
	///   - Swallow-all catch for JWT validation failures (silent degradation)
	/// </summary>
	public class AuthenticationMiddleware
	{
		/// <summary>
		/// Next middleware delegate in the ASP.NET Core pipeline.
		/// Preserved from both ErpMiddleware (line 16) and JwtMiddleware (line 14).
		/// </summary>
		private readonly RequestDelegate _next;

		/// <summary>
		/// Shared JWT token validation handler injected via DI.
		/// Replaces the monolith's static AuthService.GetValidSecurityTokenAsync method
		/// with instance-based configuration using JwtTokenOptions (Key, Issuer, Audience).
		/// </summary>
		private readonly JwtTokenHandler _jwtTokenHandler;

		/// <summary>
		/// Diagnostic logger for authentication events.
		/// Replaces the monolith's silent catch blocks with observable diagnostic output
		/// at Debug level for JWT validation failures.
		/// </summary>
		private readonly ILogger<AuthenticationMiddleware> _logger;

		/// <summary>
		/// Initializes the authentication middleware with required dependencies.
		/// JwtTokenHandler must be registered in DI by Program.cs using JWT settings from config.
		/// </summary>
		/// <param name="next">The next middleware delegate in the pipeline.</param>
		/// <param name="jwtTokenHandler">Shared JWT token validation handler from SharedKernel.</param>
		/// <param name="logger">Logger instance for diagnostic output.</param>
		public AuthenticationMiddleware(
			RequestDelegate next,
			JwtTokenHandler jwtTokenHandler,
			ILogger<AuthenticationMiddleware> logger)
		{
			_next = next ?? throw new ArgumentNullException(nameof(next));
			_jwtTokenHandler = jwtTokenHandler ?? throw new ArgumentNullException(nameof(jwtTokenHandler));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Executes the combined JWT + Cookie authentication pipeline.
		///
		/// Pipeline steps (merged from ErpMiddleware.Invoke + JwtMiddleware.Invoke):
		///   1. Enable synchronous IO (from ErpMiddleware lines 25-27)
		///   2. Extract JWT token from auth ticket or Authorization header (from JwtMiddleware lines 23-36)
		///   3. Validate JWT and extract user from claims (adapted from JwtMiddleware lines 38-62)
		///   4. Fall back to cookie-based auth if no JWT (adapted from ErpMiddleware lines 32-43)
		///   5. Store token for downstream propagation to backend services
		///   6. Call next middleware with security scope cleanup in finally (from ErpMiddleware lines 45-52)
		/// </summary>
		/// <param name="context">The current HTTP context for the request.</param>
		public async Task Invoke(HttpContext context)
		{
			// ============================================================
			// Step 1: Enable synchronous IO
			// Preserved from ErpMiddleware.cs lines 25-27.
			// Required for legacy request body reading in downstream middleware.
			// ============================================================
			var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>();
			if (syncIOFeature != null)
				syncIOFeature.AllowSynchronousIO = true;

			// ============================================================
			// Step 2: JWT Token Extraction
			// Preserved EXACTLY from JwtMiddleware.cs lines 23-36.
			// First attempts to extract from authentication ticket,
			// then falls back to Authorization header with "Bearer " prefix strip.
			// ============================================================
			var token = await context.GetTokenAsync("access_token");
			if (string.IsNullOrWhiteSpace(token))
			{
				token = context.Request.Headers[HeaderNames.Authorization];
				if (!string.IsNullOrWhiteSpace(token))
				{
					// CRITICAL: Preserve exact edge case handling from JwtMiddleware lines 29-32.
					// token.Length <= 7 catches "Bearer" without a value (just the scheme prefix).
					// Substring(7) strips the "Bearer " prefix (7 characters including the space).
					if (token.Length <= 7)
						token = null;
					else
						token = token.Substring(7);
				}
				else
					token = null;
			}

			// ============================================================
			// Step 3: JWT Validation and User Extraction
			// Adapted from JwtMiddleware.cs lines 38-62.
			// KEY CHANGE: Replaces SecurityManager DB lookup with
			// SecurityContext.ExtractUserFromClaims (claims-only, no DB).
			// ============================================================
			IDisposable secCtx = null;

			if (token != null)
			{
				try
				{
					var jwtToken = await _jwtTokenHandler.GetValidSecurityTokenAsync(token);
					if (jwtToken != null && jwtToken.Claims.Any())
					{
						// Extract user from JWT claims WITHOUT database lookup.
						// Replaces monolith pattern: new SecurityManager().GetUser(new Guid(nameIdentifier))
						// This is the key architectural change per AAP Section 0.8.3.
						var user = SecurityContext.ExtractUserFromClaims(jwtToken.Claims);

						if (user != null)
						{
							// Store user in HttpContext.Items for downstream controllers and middleware.
							// Preserved from JwtMiddleware.cs line 49.
							context.Items["User"] = user;

							// Replace HttpContext.User with JWT claims principal.
							// Preserved from JwtMiddleware.cs lines 51-52 with auth type "jwt".
							var identity = new ClaimsIdentity(jwtToken.Claims, "jwt");
							context.User = new ClaimsPrincipal(identity);

							// Open security scope for ambient user identity resolution.
							// Preserved from ErpMiddleware.cs line 35.
							secCtx = SecurityContext.OpenScope(user);
						}
					}
				}
				catch (Exception ex)
				{
					// Swallow-all catch preserved from JwtMiddleware.cs lines 56-59.
					// If JWT validation fails, the user simply won't have access to secure routes.
					// Enhanced with diagnostic logging at Debug level for observability.
					_logger.LogDebug(ex, "JWT validation failed for request {Path}", context.Request.Path);
				}
			}

			// ============================================================
			// Step 4: Cookie-Based User Fallback
			// Adapted from ErpMiddleware.cs lines 32-43.
			// Only runs if no valid JWT user was established in Step 3.
			// KEY CHANGE: Replaces AuthService.GetUser() DB lookup with
			// SecurityContext.ExtractUserFromClaims (claims-only).
			// ============================================================
			if (secCtx == null)
			{
				// Cookie-based auth: extract user from ClaimsPrincipal populated by
				// ASP.NET Core cookie authentication middleware. In the Gateway, we can
				// only extract claims — no DB lookup via AuthService.GetUser().
				if (context.User?.Identity?.IsAuthenticated == true)
				{
					try
					{
						var user = SecurityContext.ExtractUserFromClaims(context.User.Claims);
						if (user != null)
						{
							secCtx = SecurityContext.OpenScope(user);
							context.Items["User"] = user;
						}
						else
						{
							// Preserve stale cookie sign-out behavior from ErpMiddleware.cs lines 39-42.
							// If authenticated but user can't be extracted from claims, the cookie
							// contains invalid/outdated data. Sign out to prevent infinite auth loops.
							await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
						}
					}
					catch
					{
						// Cookie schema changed or claims format is incompatible.
						// Sign out the stale cookie to force re-authentication.
						// Matches AuthService.GetUser catch pattern (source lines 74-78).
						await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
					}
				}
			}

			// ============================================================
			// Step 5: Store token for downstream propagation
			// Enables RequestRoutingMiddleware to forward the JWT to backend
			// microservices via the Authorization header, preserving the
			// JWT propagation chain: Client → Gateway → Backend Service
			// (per AAP Section 0.8.3).
			// ============================================================
			if (!string.IsNullOrWhiteSpace(token))
			{
				context.Items["JwtToken"] = token;
			}

			// ============================================================
			// Step 6: Call next middleware and cleanup
			// Adapted from ErpMiddleware.cs lines 45-52.
			// DIFFERENCE FROM MONOLITH: The monolith disposes both dbCtx
			// and secCtx via Task.Run() (for offloading DB connection cleanup).
			// The Gateway only needs to dispose the security scope — no DB
			// context exists. Synchronous disposal in finally is correct.
			// ============================================================
			try
			{
				await _next(context);
			}
			finally
			{
				// Dispose security scope to pop the user from the AsyncLocal stack.
				// Matches ErpMiddleware.cs lines 50-51 (secCtx disposal).
				if (secCtx != null)
				{
					secCtx.Dispose();
				}
			}
		}
	}
}
