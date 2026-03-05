using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;

namespace WebVella.Erp.Service.Core.Controllers
{
	/// <summary>
	/// Core Platform authentication, JWT token issuance, and user preference management controller.
	///
	/// Extracted from the monolith's <c>WebApiController.cs</c> (lines 339-492 and 4270-4314)
	/// and <c>ApiControllerBase.cs</c> (response helpers).
	///
	/// Endpoints:
	/// <list type="bullet">
	///   <item><c>GetJwtToken</c>: Authenticates via email/password, issues a JWT token ([AllowAnonymous])</item>
	///   <item><c>GetNewJwtToken</c>: Refreshes an existing JWT token ([AllowAnonymous])</item>
	///   <item><c>ToggleSidebarSize</c>: Toggles the user's sidebar size preference (sm↔lg)</item>
	///   <item><c>ToggleSectionCollapse</c>: Toggles a section's collapsed/uncollapsed state</item>
	/// </list>
	///
	/// Class-level <c>[Authorize]</c> enforces JWT authentication by default on all endpoints.
	/// JWT token endpoints override with <c>[AllowAnonymous]</c> since they are the authentication
	/// entry points (and the existing token may be expired for refresh).
	///
	/// DI: SecurityManager for user/role CRUD and credential validation,
	/// JwtTokenHandler for JWT creation/validation/refresh, IConfiguration for settings.
	/// </summary>
	[Authorize]
	[ApiController]
	public class SecurityController : Controller
	{
		private readonly SecurityManager _securityManager;
		private readonly JwtTokenHandler _jwtTokenHandler;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Constructs the SecurityController with required dependencies.
		/// Replaces the monolith pattern of <c>new SecurityManager()</c> and <c>AuthService</c> singletons
		/// with proper DI injection for testability and microservice isolation.
		/// </summary>
		/// <param name="securityManager">User/role CRUD and credential validation service.</param>
		/// <param name="jwtTokenHandler">JWT token creation, validation, and refresh handler (from SharedKernel).</param>
		/// <param name="configuration">Application configuration for DevelopmentMode flag and JWT settings.</param>
		public SecurityController(
			SecurityManager securityManager,
			JwtTokenHandler jwtTokenHandler,
			IConfiguration configuration)
		{
			_securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
			_jwtTokenHandler = jwtTokenHandler ?? throw new ArgumentNullException(nameof(jwtTokenHandler));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		#region << Response Helpers (from ApiControllerBase.cs) >>

		/// <summary>
		/// Standard response handler that aligns the HTTP status code with the response semantics.
		/// If the response contains errors or is marked unsuccessful, sets the HTTP status code
		/// to BadRequest (400) if no specific status was set, or to the model's StatusCode otherwise.
		/// Returns the response as JSON.
		///
		/// Preserved exactly from monolith <c>ApiControllerBase.cs</c> lines 16-30.
		/// </summary>
		/// <param name="response">The response model to return.</param>
		/// <returns>A JSON IActionResult with the appropriate HTTP status code.</returns>
		protected IActionResult DoResponse(BaseResponseModel response)
		{
			if (response.Errors.Count > 0 || !response.Success)
			{
				if (response.StatusCode == HttpStatusCode.OK)
					HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				else
					HttpContext.Response.StatusCode = (int)response.StatusCode;
			}

			return Json(response);
		}

		/// <summary>
		/// Returns a 400 Bad Request response. In development mode (configured via
		/// <c>Settings:DevelopmentMode</c>), includes exception details in the response
		/// message for debugging. In production, uses a generic error message.
		///
		/// Preserved from monolith <c>ApiControllerBase.cs</c> lines 44-62.
		/// Adapted: <c>ErpSettings.DevelopmentMode</c> replaced with IConfiguration lookup.
		/// </summary>
		/// <param name="response">The response model to populate with error info.</param>
		/// <param name="message">Optional user-friendly error message.</param>
		/// <param name="ex">Optional exception for development-mode diagnostics.</param>
		/// <returns>A JSON IActionResult with HTTP 400 status.</returns>
		protected IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			bool developmentMode = _configuration.GetValue<bool>("Settings:DevelopmentMode", false);
			if (developmentMode)
			{
				if (ex != null)
					response.Message = ex.Message + ex.StackTrace;
			}
			else
			{
				if (string.IsNullOrEmpty(message))
					response.Message = "An internal error occurred!";
			}

			HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
			return Json(response);
		}

		/// <summary>
		/// Returns a 404 Not Found response with the provided response model as JSON body.
		/// Preserved from monolith <c>ApiControllerBase.cs</c> lines 38-42.
		/// </summary>
		/// <param name="response">The response model to return.</param>
		/// <returns>A JSON IActionResult with HTTP 404 status.</returns>
		protected IActionResult DoItemNotFoundResponse(BaseResponseModel response)
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(response);
		}

		/// <summary>
		/// Returns a 404 Not Found response with an empty JSON body.
		/// Preserved from monolith <c>ApiControllerBase.cs</c> lines 32-36.
		/// </summary>
		/// <returns>A JSON IActionResult with HTTP 404 status and empty body.</returns>
		protected IActionResult DoPageNotFoundResponse()
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(new { });
		}

		#endregion

		#region << JWT Token Auth >>

		/// <summary>
		/// Authenticates a user by email and password, then issues a JWT token containing all
		/// claims required for cross-service identity propagation (user ID, username, email,
		/// first name, last name, roles, image) so downstream services can authorize requests
		/// without calling back to the Core service (AAP 0.8.3).
		///
		/// Route: POST /api/v3.0/auth/jwt/token
		///
		/// [AllowAnonymous] — CRITICAL: This is the authentication entry point; must be accessible
		/// without an existing token.
		///
		/// Extracted from monolith <c>WebApiController.cs</c> lines 4270-4290.
		/// Adapted: <c>AuthService.GetTokenAsync(email, password)</c> replaced with
		/// <c>SecurityManager.GetUser(email, password)</c> + <c>JwtTokenHandler.BuildTokenAsync(user)</c>.
		/// Request model changed from <c>JwtTokenLoginModel</c> to <c>JObject</c> for flexibility.
		/// </summary>
		/// <param name="submitObj">JSON body containing <c>email</c> and <c>password</c> fields.</param>
		/// <returns>A ResponseModel with <c>{ token, expiration }</c> on success, or error details on failure.</returns>
		[AllowAnonymous]
		[HttpPost("~/api/v3.0/auth/jwt/token")]
		public async Task<IActionResult> GetJwtToken([FromBody] JObject submitObj)
		{
			var response = new ResponseModel
			{
				Timestamp = DateTime.UtcNow,
				Success = true,
				Errors = new List<ErrorModel>()
			};

			try
			{
				if (submitObj == null)
					throw new ArgumentException("Request body is required.");

				string email = submitObj["email"]?.ToString();
				string password = submitObj["password"]?.ToString();

				if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "Email and password are required";
					response.Errors.Add(new ErrorModel("credentials", "", "Email and password are required"));
					return DoResponse(response);
				}

				// Validate credentials via SecurityManager
				// GetUser(string, string) authenticates via email + MD5-hashed password comparison
				var user = _securityManager.GetUser(email, password);
				if (user == null)
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "Invalid email or password";
					response.Errors.Add(new ErrorModel("credentials", "", "Invalid email or password"));
					return DoResponse(response);
				}

				if (!user.Enabled)
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "User account is disabled";
					response.Errors.Add(new ErrorModel("account", "", "User account is disabled"));
					return DoResponse(response);
				}

				// Generate JWT token with full claims for cross-service identity propagation.
				// BuildTokenAsync delegates to ErpUser.ToClaims() which produces:
				// NameIdentifier(Id), Name(Username), Email, GivenName(FirstName),
				// Surname(LastName), image, Role(role.Id), role_name(role.Name),
				// plus token_refresh_after timestamp.
				var (tokenString, token) = await _jwtTokenHandler.BuildTokenAsync(user);

				response.Object = new { token = tokenString, expiration = token.ValidTo };
				response.Success = true;
			}
			catch (Exception e)
			{
				response.Success = false;
				bool devMode = _configuration.GetValue<bool>("Settings:DevelopmentMode", false);
				response.Message = devMode ? e.Message + e.StackTrace : "An internal error occurred!";
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Refreshes an existing (possibly near-expiration) JWT token by extracting the user identity
		/// from the Authorization header, looking up the current user state from the database,
		/// and issuing a new token with fresh expiration and current claims.
		///
		/// Route: POST /api/v3.0/auth/jwt/token/new
		///
		/// [AllowAnonymous] — CRITICAL: The existing token may be expired or near-expiration,
		/// so this endpoint must be accessible without valid authentication.
		///
		/// Extracted from monolith <c>WebApiController.cs</c> lines 4292-4309.
		/// Adapted: <c>AuthService.GetNewTokenAsync(model.Token)</c> replaced with
		/// JwtTokenHandler.GetValidSecurityTokenAsync + ExtractUserIdFromToken + SecurityManager.GetUser
		/// + JwtTokenHandler.RefreshTokenAsync. Token extracted from Authorization header instead of body.
		/// </summary>
		/// <returns>A ResponseModel with <c>{ token }</c> on success, or error details on failure.</returns>
		[AllowAnonymous]
		[HttpPost("~/api/v3.0/auth/jwt/token/new")]
		public async Task<IActionResult> GetNewJwtToken()
		{
			var response = new ResponseModel
			{
				Timestamp = DateTime.UtcNow,
				Success = true,
				Errors = new List<ErrorModel>()
			};

			try
			{
				// Extract existing token from the Authorization header (Bearer scheme)
				string authHeader = HttpContext.Request.Headers["Authorization"].ToString();
				string existingToken = null;
				if (!string.IsNullOrWhiteSpace(authHeader) &&
					authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
				{
					existingToken = authHeader.Substring("Bearer ".Length).Trim();
				}

				if (string.IsNullOrWhiteSpace(existingToken))
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "Authorization token is required";
					response.Errors.Add(new ErrorModel("token", "", "Authorization token is required"));
					return DoResponse(response);
				}

				// Validate the existing token (checks signature, issuer, audience)
				var jwtToken = await _jwtTokenHandler.GetValidSecurityTokenAsync(existingToken);
				if (jwtToken == null)
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "Invalid or expired token";
					response.Errors.Add(new ErrorModel("token", "", "Invalid or expired token"));
					return DoResponse(response);
				}

				// Extract user ID from the validated token's NameIdentifier claim
				Guid? userId = JwtTokenHandler.ExtractUserIdFromToken(jwtToken);
				if (userId == null || userId.Value == Guid.Empty)
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "Cannot extract user identity from token";
					response.Errors.Add(new ErrorModel("token", "", "Cannot extract user identity from token"));
					return DoResponse(response);
				}

				// Look up the current user state from the database for fresh claims.
				// This ensures that role changes, name updates, etc. are reflected in the new token.
				var currentUser = _securityManager.GetUser(userId.Value);
				if (currentUser == null || !currentUser.Enabled)
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "User not found or account is disabled";
					response.Errors.Add(new ErrorModel("user", "", "User not found or account is disabled"));
					return DoResponse(response);
				}

				// Issue a new token with fresh expiration and the current user's claims
				var refreshedToken = await _jwtTokenHandler.RefreshTokenAsync(existingToken, currentUser);
				if (string.IsNullOrEmpty(refreshedToken))
				{
					response.Success = false;
					response.StatusCode = HttpStatusCode.Unauthorized;
					response.Message = "Token refresh failed";
					response.Errors.Add(new ErrorModel("token", "", "Token refresh failed"));
					return DoResponse(response);
				}

				response.Object = new { token = refreshedToken };
				response.Success = true;
			}
			catch (Exception e)
			{
				response.Success = false;
				bool devMode = _configuration.GetValue<bool>("Settings:DevelopmentMode", false);
				response.Message = devMode ? e.Message + e.StackTrace : "An internal error occurred!";
			}

			return DoResponse(response);
		}

		#endregion

		#region << User Preferences >>

		/// <summary>
		/// Toggles the current user's sidebar size preference between "sm" (collapsed) and
		/// "lg" (expanded). An empty/null sidebar size defaults to "lg" on first toggle.
		///
		/// Route: POST /api/v3.0/user/preferences/toggle-sidebar-size
		///
		/// Extracted from monolith <c>WebApiController.cs</c> lines 339-375.
		/// Adapted:
		/// <list type="bullet">
		///   <item><c>AuthService.GetUser(User)</c> → <c>ErpUser.FromClaims(User.Claims)</c> + <c>SecurityManager.GetUser(Guid)</c></item>
		///   <item><c>new UserPreferencies().SetSidebarSize()</c> → direct preference modification + <c>SecurityManager.SaveUser()</c></item>
		///   <item>Saves within <c>SecurityContext.OpenSystemScope()</c> to bypass permission checks</item>
		/// </list>
		/// </summary>
		/// <returns>A BaseResponseModel indicating success or failure.</returns>
		[HttpPost("~/api/v3.0/user/preferences/toggle-sidebar-size")]
		public IActionResult ToggleSidebarSize()
		{
			var response = new BaseResponseModel();
			try
			{
				// Reconstruct user identity from JWT claims in the incoming request
				var claimsUser = ErpUser.FromClaims(User.Claims);
				if (claimsUser == null || claimsUser.Id == Guid.Empty)
					throw new Exception("Could not resolve current user from claims");

				// Load full user record with preferences from the database
				var currentUser = _securityManager.GetUser(claimsUser.Id);
				if (currentUser == null)
					throw new Exception("Current user not found");

				var currentUserPreferences = currentUser.Preferences ?? new ErpUserPreferences();

				// Toggle sidebar size (preserved from monolith lines 348-359)
				string targetSidebarSize;
				switch (currentUserPreferences.SidebarSize)
				{
					case "sm":
						targetSidebarSize = "lg";
						break;
					case "lg":
						targetSidebarSize = "sm";
						break;
					default:
						targetSidebarSize = "lg";
						break;
				}

				currentUserPreferences.SidebarSize = targetSidebarSize;
				currentUser.Preferences = currentUserPreferences;

				// Save updated preferences within system scope to bypass permission checks.
				// OpenSystemScope() sets the security context to the built-in system user
				// which has unlimited permissions (Administrator role).
				using (SecurityContext.OpenSystemScope())
				{
					_securityManager.SaveUser(currentUser);
				}

				response.Success = true;
				response.Message = "success";
				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				return Json(response);
			}
		}

		/// <summary>
		/// Toggles a section's collapsed/uncollapsed state in the user's component data
		/// preferences for the <c>WebVella.Erp.Web.Components.PcSection</c> component.
		///
		/// Manages two lists per user: <c>collapsed_node_ids</c> and <c>uncollapsed_node_ids</c>.
		/// When collapsing: removes the nodeId from uncollapsed list, adds to collapsed list.
		/// When uncollapsing: removes from collapsed list, adds to uncollapsed list.
		///
		/// Route: POST /api/v3.0/user/preferences/toggle-section-collapse
		///
		/// Extracted from monolith <c>WebApiController.cs</c> lines 377-492.
		/// Adapted:
		/// <list type="bullet">
		///   <item><c>AuthService.GetUser(User)</c> → <c>ErpUser.FromClaims(User.Claims)</c> + <c>SecurityManager.GetUser(Guid)</c></item>
		///   <item><c>UserPreferencies.Get/SetComponentData()</c> → direct <c>ComponentDataDictionary</c> access + <c>SaveUser()</c></item>
		/// </list>
		/// </summary>
		/// <param name="nodeId">The GUID of the section node to toggle. Required.</param>
		/// <param name="isCollapsed">True if the section should be collapsed; false if uncollapsed.</param>
		/// <returns>A BaseResponseModel indicating success or failure.</returns>
		[HttpPost("~/api/v3.0/user/preferences/toggle-section-collapse")]
		public IActionResult ToggleSectionCollapse(Guid? nodeId = null, bool isCollapsed = false)
		{
			var response = new BaseResponseModel();
			try
			{
				if (nodeId == null)
					throw new Exception("nodeId query param is required");

				// Reconstruct user identity from JWT claims
				var claimsUser = ErpUser.FromClaims(User.Claims);
				if (claimsUser == null || claimsUser.Id == Guid.Empty)
					throw new Exception("Could not resolve current user from claims");

				// Load full user record with preferences from the database
				var currentUser = _securityManager.GetUser(claimsUser.Id);
				if (currentUser == null)
					throw new Exception("Current user not found");

				var prefs = currentUser.Preferences ?? new ErpUserPreferences();
				var componentDataDict = prefs.ComponentDataDictionary ?? new EntityRecord();

				const string componentName = "WebVella.Erp.Web.Components.PcSection";

				// Retrieve existing component data for PcSection, handling multiple serialization formats
				EntityRecord componentData = null;
				if (componentDataDict.Properties.ContainsKey(componentName) && componentDataDict[componentName] != null)
				{
					if (componentDataDict[componentName] is EntityRecord existingData)
					{
						componentData = existingData;
					}
					else if (componentDataDict[componentName] is string strData)
					{
						componentData = JsonConvert.DeserializeObject<EntityRecord>(strData);
					}
					else if (componentDataDict[componentName] is JObject jObj)
					{
						componentData = jObj.ToObject<EntityRecord>();
					}
				}

				var collapsedNodeIds = new List<Guid>();
				var uncollapsedNodeIds = new List<Guid>();

				if (componentData == null)
				{
					// First time — create empty component data record
					componentData = new EntityRecord();
					componentData["collapsed_node_ids"] = new List<Guid>();
					componentData["uncollapsed_node_ids"] = new List<Guid>();
				}
				else
				{
					// Parse collapsed_node_ids from component data.
					// Handles string (JSON), List<Guid>, and JArray formats for backward compatibility.
					// Preserved from monolith lines 404-428.
					if (componentData.Properties.ContainsKey("collapsed_node_ids") && componentData["collapsed_node_ids"] != null)
					{
						if (componentData["collapsed_node_ids"] is string strCollapsed)
						{
							try
							{
								collapsedNodeIds = JsonConvert.DeserializeObject<List<Guid>>(strCollapsed);
							}
							catch
							{
								throw new Exception("WebVella.Erp.Web.Components.PcSection component data object in user preferences not in the correct format. collapsed_node_ids should be List<Guid>");
							}
						}
						else if (componentData["collapsed_node_ids"] is List<Guid> listCollapsed)
						{
							collapsedNodeIds = listCollapsed;
						}
						else if (componentData["collapsed_node_ids"] is JArray jArrCollapsed)
						{
							collapsedNodeIds = jArrCollapsed.ToObject<List<Guid>>();
						}
						else
						{
							throw new Exception("Unknown format of collapsed_node_ids");
						}
					}

					// Parse uncollapsed_node_ids from component data.
					// Same multi-format handling as collapsed_node_ids.
					// Preserved from monolith lines 430-455.
					if (componentData.Properties.ContainsKey("uncollapsed_node_ids") && componentData["uncollapsed_node_ids"] != null)
					{
						if (componentData["uncollapsed_node_ids"] is string strUncollapsed)
						{
							try
							{
								uncollapsedNodeIds = JsonConvert.DeserializeObject<List<Guid>>(strUncollapsed);
							}
							catch
							{
								throw new Exception("WebVella.Erp.Web.Components.PcSection component data object in user preferences not in the correct format. uncollapsed_node_ids should be List<Guid>");
							}
						}
						else if (componentData["uncollapsed_node_ids"] is List<Guid> listUncollapsed)
						{
							uncollapsedNodeIds = listUncollapsed;
						}
						else if (componentData["uncollapsed_node_ids"] is JArray jArrUncollapsed)
						{
							uncollapsedNodeIds = jArrUncollapsed.ToObject<List<Guid>>();
						}
						else
						{
							throw new Exception("Unknown format of uncollapsed_node_ids");
						}
					}
				}

				// Toggle collapsed/uncollapsed state.
				// Preserved exactly from monolith lines 458-475.
				if (isCollapsed)
				{
					// New state is collapsed:
					// 1. Remove from uncollapsed list
					uncollapsedNodeIds = uncollapsedNodeIds.FindAll(x => x != nodeId.Value).ToList();
					// 2. Add to collapsed list if not already present
					if (!collapsedNodeIds.Contains(nodeId.Value))
						collapsedNodeIds.Add(nodeId.Value);
				}
				else
				{
					// New state is uncollapsed:
					// 1. Remove from collapsed list
					collapsedNodeIds = collapsedNodeIds.FindAll(x => x != nodeId.Value).ToList();
					// 2. Add to uncollapsed list if not already present
					if (!uncollapsedNodeIds.Contains(nodeId.Value))
						uncollapsedNodeIds.Add(nodeId.Value);
				}

				// Update component data with modified lists
				componentData["collapsed_node_ids"] = collapsedNodeIds;
				componentData["uncollapsed_node_ids"] = uncollapsedNodeIds;

				// Store updated component data back into user preferences
				componentDataDict[componentName] = componentData;
				prefs.ComponentDataDictionary = componentDataDict;
				currentUser.Preferences = prefs;

				// Save updated preferences within system scope to bypass permission checks
				using (SecurityContext.OpenSystemScope())
				{
					_securityManager.SaveUser(currentUser);
				}

				response.Success = true;
				response.Message = "success";
				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				return Json(response);
			}
		}

		#endregion
	}
}
