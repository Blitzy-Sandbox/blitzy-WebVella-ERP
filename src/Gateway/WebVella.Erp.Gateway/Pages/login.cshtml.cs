using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.Gateway.Models;
using ResponseModel = WebVella.Erp.SharedKernel.Models.ResponseModel;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Login page model for the Gateway/BFF layer.
	/// Adapted from WebVella.Erp.Web.Pages.LoginModel (120 lines).
	///
	/// Authentication flow:
	/// 1. User submits credentials via the login form
	/// 2. Gateway sends credentials to Core service /api/v3/en_US/auth/login
	/// 3. Core service validates credentials and returns ResponseModel with ErpUser data
	/// 4. Gateway creates ClaimsPrincipal from ErpUser.ToClaims() and signs in via cookie auth
	/// 5. User is redirected to ReturnUrl or home page
	///
	/// Key differences from monolith:
	/// - No local AuthService — authentication delegated to Core service via HTTP
	/// - No HookManager calls — hook-based lifecycle removed per AAP
	/// - Brand logo loaded from IConfiguration instead of ErpAppContext.Current singleton
	/// - OnPost is now async (OnPostAsync) due to HTTP calls to Core service
	///
	/// [AllowAnonymous] overrides the default [Authorize] on BaseErpPageModel,
	/// making this page accessible without authentication.
	/// </summary>
	[AllowAnonymous]
	public class LoginModel : BaseErpPageModel
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<LoginModel> _logger;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Named HttpClient key for the Core microservice.
		/// Must be registered in Program.cs via builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// Default brand logo path matching the monolith theme default
		/// (WebVella.Erp.Web.Models.Theme.BrandLogo = "/_content/WebVella.Erp.Web/assets/logo.png").
		/// </summary>
		private const string DefaultBrandLogoPath = "/_content/WebVella.Erp.Web/assets/logo.png";

		/// <summary>
		/// Username field bound from the login form.
		/// Preserved from monolith: [BindProperty] public string Username.
		/// </summary>
		[BindProperty]
		public string Username { get; set; }

		/// <summary>
		/// Password field bound from the login form.
		/// Preserved from monolith: [BindProperty] public string Password.
		/// </summary>
		[BindProperty]
		public string Password { get; set; }

		/// <summary>
		/// Return URL for post-login redirect, bound from query string/form with name "returnUrl".
		/// Uses 'new' modifier to hide the base class ReturnUrl (which has SupportsGet = true)
		/// — preserving the monolith pattern where the login page overrides the bind behavior.
		/// </summary>
		[BindProperty(Name = "returnUrl")]
		public new string ReturnUrl { get; set; }

		/// <summary>
		/// Error message displayed on the login form when authentication fails.
		/// Set by OnPostAsync when credentials are invalid or a service error occurs.
		/// </summary>
		[BindProperty]
		public string Error { get; set; }

		/// <summary>
		/// Brand logo URL displayed in the login card header.
		/// Loaded from Settings:NavLogoUrl configuration or defaults to theme logo path.
		/// </summary>
		public string BrandLogo { get; set; }

		/// <summary>
		/// Constructs the LoginModel with required services injected via DI.
		/// Preserves ErpRequestContext injection from monolith, adds IHttpClientFactory
		/// for Core service calls, ILogger for structured logging, and IConfiguration
		/// for brand logo URL lookup.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request context for routing state.</param>
		/// <param name="httpClientFactory">Factory for creating Core service HTTP clients.</param>
		/// <param name="logger">Structured logger for authentication events.</param>
		/// <param name="configuration">Application configuration for settings lookup.</param>
		public LoginModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] IHttpClientFactory httpClientFactory,
			[FromServices] ILogger<LoginModel> logger,
			[FromServices] IConfiguration configuration)
		{
			ErpRequestContext = reqCtx;
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		/// <summary>
		/// Handles GET requests to the login page.
		/// Adapted from monolith OnGet (source lines 31-59):
		/// - Preserves Init() call for page model initialization
		/// - Preserves already-authenticated redirect (CurrentUser != null → redirect)
		/// - Replaces ErpAppContext.Current theme lookup with IConfiguration brand logo
		/// - Removes HookManager.GetHookedInstances&lt;IPageHook&gt; calls (hooks removed per AAP)
		/// - Preserves BeforeRender() + Page() pattern
		/// </summary>
		/// <returns>Redirect if already authenticated; otherwise renders the login page.</returns>
		public IActionResult OnGet()
		{
			var initResult = Init();
			if (initResult != null) return initResult;

			// If the user is already authenticated, redirect immediately.
			// Preserved from monolith lines 42-48.
			if (CurrentUser != null)
			{
				if (!string.IsNullOrWhiteSpace(ReturnUrl))
					return new LocalRedirectResult(ReturnUrl);
				else
					return new LocalRedirectResult("/");
			}

			// Load brand logo from configuration.
			// Replaces: BrandLogo = theme.BrandLogo; if (!String.IsNullOrWhiteSpace(ErpSettings.NavLogoUrl)) BrandLogo = ErpSettings.NavLogoUrl;
			LoadBrandLogo();

			BeforeRender();
			return Page();
		}

		/// <summary>
		/// Handles POST requests for login form submission.
		/// Adapted from monolith OnPost (source lines 62-115) — now async due to HTTP calls.
		///
		/// Critical transformation from monolith:
		/// - Source: ErpUser user = authService.Authenticate(Username, Password) — local in-process call
		/// - Gateway: HTTP POST to Core service /api/v3/en_US/auth/login, then cookie sign-in
		///
		/// The monolith's authService.Authenticate() both validated credentials AND set the auth cookie.
		/// In the Gateway, this is split into two steps:
		/// 1. Call Core service to validate credentials and get user data (ResponseModel with ErpUser)
		/// 2. Use ASP.NET Core SignInAsync to set the cookie on the Gateway's own auth middleware
		///
		/// Removed from monolith:
		/// - HookManager.GetHookedInstances&lt;IPageHook&gt; pre/post loops
		/// - HookManager.GetHookedInstances&lt;ILoginPageHook&gt; pre/post login hooks
		/// Both replaced by event-driven architecture per AAP.
		/// </summary>
		/// <returns>Redirect on success; re-renders login page with error on failure.</returns>
		public async Task<IActionResult> OnPostAsync()
		{
			// Antiforgery check — preserved from monolith line 64
			if (!ModelState.IsValid)
				throw new Exception("Antiforgery check failed.");

			var initResult = Init();
			if (initResult != null) return initResult;

			try
			{
				_logger.LogInformation("Authentication attempt for user {Username}.", Username);

				// Validate that credentials are provided
				if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
				{
					Error = "Invalid username or password";
					LoadBrandLogo();
					BeforeRender();
					return Page();
				}

				// Call Core service authentication endpoint.
				// Replaces monolith: ErpUser user = authService.Authenticate(Username, Password);
				var httpClient = _httpClientFactory.CreateClient(CoreServiceClientName);
				var httpResponse = await httpClient.PostAsJsonAsync(
					"/api/v3/en_US/auth/login",
					new { Username, Password });

				var responseBody = await httpResponse.Content.ReadAsStringAsync();
				var apiResponse = JsonConvert.DeserializeObject<ResponseModel>(responseBody);

				if (apiResponse != null && apiResponse.Success && apiResponse.Object != null)
				{
					// Deserialize user from the response Object.
					// Uses SharedKernel ErpUser (fully qualified to avoid conflict with Gateway.Models.ErpUser)
					// because SharedKernel.Models.ErpUser has ToClaims() needed for cookie auth.
					WebVella.Erp.SharedKernel.Models.ErpUser user = null;

					if (apiResponse.Object is JObject userJObject)
					{
						user = userJObject.ToObject<WebVella.Erp.SharedKernel.Models.ErpUser>();
					}
					else
					{
						// Fallback: re-serialize and deserialize for non-JObject types
						var userJson = JsonConvert.SerializeObject(apiResponse.Object);
						user = JsonConvert.DeserializeObject<WebVella.Erp.SharedKernel.Models.ErpUser>(userJson);
					}

					if (user != null && user.Id != Guid.Empty)
					{
						// Step 1: Create ClaimsIdentity from the authenticated user's claims.
						// ErpUser.ToClaims() produces standard claims (NameIdentifier, Name, Email,
						// GivenName, Surname, Role, image, role_name, plus custom claims).
						var claims = user.ToClaims();
						var identity = new ClaimsIdentity(
							claims,
							CookieAuthenticationDefaults.AuthenticationScheme);
						var principal = new ClaimsPrincipal(identity);

						// Step 2: Sign in — sets the authentication cookie on the Gateway.
						// This replaces the monolith's authService.Authenticate() which set the
						// cookie internally as part of credential validation.
						await HttpContext.SignInAsync(
							CookieAuthenticationDefaults.AuthenticationScheme,
							principal);

						_logger.LogInformation(
							"User {Username} (ID: {UserId}) successfully authenticated via Core service.",
							Username, user.Id);

						// Redirect to return URL or home — preserved from monolith lines 107-110
						if (!string.IsNullOrWhiteSpace(ReturnUrl))
							return new LocalRedirectResult(ReturnUrl);
						else
							return new LocalRedirectResult("/");
					}
				}

				// Authentication failed — user is null or response unsuccessful.
				// Preserved from monolith lines 100-105.
				Error = "Invalid username or password";

				// If the Core service returned specific error messages, use the first one
				if (apiResponse?.Errors != null && apiResponse.Errors.Count > 0)
				{
					var firstError = apiResponse.Errors[0];
					if (!string.IsNullOrWhiteSpace(firstError.Message))
					{
						Error = firstError.Message;
					}
				}

				_logger.LogWarning("Failed authentication attempt for user {Username}.", Username);
			}
			catch (HttpRequestException ex)
			{
				_logger.LogError(ex,
					"Core service communication error during authentication for user {Username}.",
					Username);
				Error = "Unable to connect to the authentication service. Please try again later.";
			}
			catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
			{
				_logger.LogError(ex,
					"Core service request timed out during authentication for user {Username}.",
					Username);
				Error = "The authentication service is not responding. Please try again later.";
			}
			catch (Exception ex)
			{
				_logger.LogError(ex,
					"Unexpected error during authentication for user {Username}.",
					Username);
				Error = ex.Message;
			}

			// Re-render the login page with the error message.
			// Preserved from monolith pattern: set BrandLogo + BeforeRender() + Page()
			LoadBrandLogo();
			BeforeRender();
			return Page();
		}

		/// <summary>
		/// Loads the brand logo URL from configuration.
		/// Replaces the monolith pattern (source lines 50-57):
		///   var theme = ErpAppContext.Current.Theme;
		///   BrandLogo = theme.BrandLogo;
		///   if (!String.IsNullOrWhiteSpace(ErpSettings.NavLogoUrl))
		///       BrandLogo = ErpSettings.NavLogoUrl;
		///
		/// In the Gateway, ErpAppContext.Current singleton is eliminated.
		/// Brand logo URL is read from Settings:NavLogoUrl in appsettings.json,
		/// falling back to the default theme logo path.
		/// </summary>
		private void LoadBrandLogo()
		{
			var navLogoUrl = _configuration["Settings:NavLogoUrl"];
			if (!string.IsNullOrWhiteSpace(navLogoUrl))
			{
				BrandLogo = navLogoUrl;
			}
			else
			{
				BrandLogo = DefaultBrandLogoPath;
			}
		}
	}
}
/*
 * system actions: OnPost: success,error
 * custom actions: none
 */
