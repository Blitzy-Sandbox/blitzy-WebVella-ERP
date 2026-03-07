// =========================================================================
// WebVella ERP — API Gateway / Backend-for-Frontend (BFF) Service Entry Point
// =========================================================================
// Minimal hosting API (.NET 10) that configures the Gateway with:
//   - JWT + Cookie dual-mode authentication (matching monolith Startup.cs)
//   - AuthenticationMiddleware for per-request SecurityContext scope setup
//   - RequestRoutingMiddleware for Strangler Fig routing to backend microservices
//   - ErrorHandlingMiddleware for BaseResponseModel error envelopes
//   - Razor Pages for BFF UI (login, logout, record pages)
//   - Newtonsoft.Json serialization (backward-compatible API v3 contract)
//   - IHttpClientFactory for service proxying (consumed by RequestRoutingMiddleware)
//   - JwtTokenHandler singleton for AuthenticationMiddleware JWT validation
//   - RouteConfiguration binding from appsettings.json ServiceRoutes section
//   - CORS, response compression, static files, health checks
//   - GatewayAppContext singleton for legacy compatibility
//
// Derived from monolith WebVella.Erp.Site/Program.cs + Startup.cs patterns.
// The Gateway does NOT run ERP business logic locally — it delegates to
// backend microservices via HttpClient and gRPC client calls.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using WebVella.Erp.Gateway.Configuration;
using WebVella.Erp.Gateway.Middleware;
using WebVella.Erp.Gateway.Models;
using WebVella.Erp.Gateway.Services;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Security;

// =========================================================================
// Create the WebApplication builder — .NET 10 minimal hosting API
// Replaces the monolith's WebHost.CreateDefaultBuilder + UseStartup<Startup>
// pattern (WebVella.Erp.Site/Program.cs lines 14-17).
// appsettings.json is loaded automatically by CreateBuilder.
// =========================================================================
var builder = WebApplication.CreateBuilder(args);

// Suppress Kestrel Server header to reduce information leakage (defense-in-depth)
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
    options => options.AddServerHeader = false);

// =========================================================================
// Npgsql legacy timestamp behavior — preserved from monolith Startup.cs
// line 40. Required until system tables are migrated to timestamptz columns.
// Also needed for any indirect Npgsql usage through SharedKernel.
// =========================================================================
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// =========================================================================
// ErpSettings initialization — bind from appsettings.json Settings section
// Preserves monolith's ErpSettings static configuration pattern used by
// Razor layouts (_AppMaster, _SystemMaster) and BaseErpPageModel.
// =========================================================================
ErpSettings.Initialize(builder.Configuration);

// =========================================================================
// JSON.NET Default Settings — preserved from monolith Startup.cs lines 83-86
// Sets global Newtonsoft.Json defaults with UTC DateTimeZoneHandling to
// preserve backward-compatible API response format. The monolith used
// ErpDateTimeJsonConverter; the Gateway uses standard UTC handling since
// it does not have direct access to ErpSettings.TimeZoneName for custom
// DateTime conversion — backend services handle timezone-aware serialization.
// =========================================================================
JsonConvert.DefaultSettings = () => new JsonSerializerSettings
{
    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
    DateParseHandling = DateParseHandling.DateTime
};

// =========================================================================
// SECTION: Service Registration (from Startup.ConfigureServices)
// =========================================================================

// -----------------------------------------------------------------------
// Localization — preserved from monolith Startup.cs lines 45-46
// -----------------------------------------------------------------------
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
var locale = builder.Configuration["Settings:Locale"] ?? "en-US";
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(locale);
});

// -----------------------------------------------------------------------
// Response Compression — preserved from monolith Startup.cs lines 48-49
// GzipCompressionProvider with CompressionLevel.Optimal
// -----------------------------------------------------------------------
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Optimal);
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<GzipCompressionProvider>();
});

// -----------------------------------------------------------------------
// Routing — lowercase URLs preserved from monolith Startup.cs line 50
// -----------------------------------------------------------------------
builder.Services.AddRouting(options => { options.LowercaseUrls = true; });

// -----------------------------------------------------------------------
// CORS — open policy for development, matching monolith Startup.cs
// lines 58-64. Production deployments should restrict origins via config.
// -----------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (allowedOrigins != null && allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            policy.WithOrigins("https://localhost")
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

// -----------------------------------------------------------------------
// MVC + Razor Pages — preserved from monolith Startup.cs lines 67-80
// Configures Newtonsoft.Json as the JSON serializer for API contract
// backward compatibility (BaseResponseModel envelope, [JsonProperty]).
// NOTE: NO .AddRazorRuntimeCompilation() in production Gateway.
// -----------------------------------------------------------------------
builder.Services.AddMvc()
    .AddRazorPagesOptions(options =>
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/login");
    })
    .AddNewtonsoftJson(options =>
    {
        // Gateway uses standard Newtonsoft.Json settings with UTC handling.
        // ErpDateTimeJsonConverter is service-specific (requires ErpSettings.TimeZoneName)
        // and is applied only by backend services during serialization.
        options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
        options.SerializerSettings.DateParseHandling = DateParseHandling.DateTime;
    });

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// -----------------------------------------------------------------------
// Authentication — JWT + Cookie dual-mode
// Preserved EXACTLY from monolith Startup.cs (WebVella.Erp.Site) lines 88-125:
//   - Cookie auth for browser sessions (Razor Pages login)
//   - JWT Bearer auth for API clients
//   - PolicyScheme "JWT_OR_COOKIE" selects based on Authorization header
// -----------------------------------------------------------------------
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "JWT_OR_COOKIE";
    options.DefaultChallengeScheme = "JWT_OR_COOKIE";
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // Prevent transmission over HTTP (CWE-1004)
    options.Cookie.SameSite = SameSiteMode.Lax; // CSRF protection while allowing top-level navigations
    options.Cookie.Name = "erp_auth_base"; // EXACT cookie name from monolith
    options.LoginPath = new PathString("/login");
    options.LogoutPath = new PathString("/logout");
    options.AccessDeniedPath = new PathString("/error?access_denied");
    options.ReturnUrlParameter = "returnUrl";
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    // Token validation parameters — preserved from monolith Startup.cs lines 104-113
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Settings:Jwt:Issuer"],
        ValidAudience = builder.Configuration["Settings:Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Settings:Jwt:Key"]
                ?? JwtTokenOptions.DefaultDevelopmentKey))
    };
})
.AddPolicyScheme("JWT_OR_COOKIE", "JWT_OR_COOKIE", options =>
{
    // ForwardDefaultSelector logic — preserved from monolith Startup.cs lines 117-124
    // Routes to JWT Bearer if Authorization header starts with "Bearer ",
    // otherwise routes to Cookie authentication.
    options.ForwardDefaultSelector = context =>
    {
        string authorization = context.Request.Headers[Microsoft.Net.Http.Headers.HeaderNames.Authorization].ToString();
        if (!string.IsNullOrEmpty(authorization) && authorization.StartsWith("Bearer "))
            return JwtBearerDefaults.AuthenticationScheme;

        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
});

// -----------------------------------------------------------------------
// JwtTokenHandler — Shared JWT token validation/creation handler
// Registered as a singleton for AuthenticationMiddleware to consume.
// Binds JwtTokenOptions from appsettings.json Settings:Jwt section
// (Key, Issuer, Audience, TokenExpiryMinutes, TokenRefreshMinutes).
// -----------------------------------------------------------------------
// SECURITY — Startup key validation warnings
var gatewayJwtKey = builder.Configuration["Settings:Jwt:Key"]
    ?? JwtTokenOptions.DefaultDevelopmentKey;
if (JwtTokenOptions.IsDefaultKey(gatewayJwtKey))
{
    Console.Error.WriteLine("[Gateway] SECURITY WARNING: JWT signing key " +
        "is the built-in default development key. Set 'Settings:Jwt:Key' " +
        "configuration or WEBVELLA_JWT_KEY environment variable before " +
        "deploying to production.");
}

var jwtOptions = new JwtTokenOptions
{
    Key = gatewayJwtKey,
    Issuer = builder.Configuration["Settings:Jwt:Issuer"] ?? "webvella-erp",
    Audience = builder.Configuration["Settings:Jwt:Audience"] ?? "webvella-erp",
    TokenExpiryMinutes = double.TryParse(
        builder.Configuration["Settings:Jwt:TokenExpiryMinutes"], out var expiry) ? expiry : 1440,
    TokenRefreshMinutes = double.TryParse(
        builder.Configuration["Settings:Jwt:TokenRefreshMinutes"], out var refresh) ? refresh : 120
};
builder.Services.Configure<JwtTokenOptions>(opts =>
{
    opts.Key = jwtOptions.Key;
    opts.Issuer = jwtOptions.Issuer;
    opts.Audience = jwtOptions.Audience;
    opts.TokenExpiryMinutes = jwtOptions.TokenExpiryMinutes;
    opts.TokenRefreshMinutes = jwtOptions.TokenRefreshMinutes;
});
builder.Services.AddSingleton(new JwtTokenHandler(jwtOptions));

// -----------------------------------------------------------------------
// RouteConfiguration binding — drives RequestRoutingMiddleware URL routing
// Binds ServiceRoutes, GrpcEndpoints, and RouteMappings sections from
// appsettings.json into a strongly-typed RouteConfiguration instance.
// Members accessed: CoreServiceUrl, CrmServiceUrl, ProjectServiceUrl,
//                   MailServiceUrl, ReportingServiceUrl, AdminServiceUrl
// -----------------------------------------------------------------------
var routeConfig = new RouteConfiguration();
builder.Configuration.GetSection("ServiceRoutes").Bind(routeConfig);
// RouteMappings is a separate top-level section in appsettings.json, not nested under ServiceRoutes.
// Bind it separately to populate the URL-to-service mapping dictionary.
builder.Configuration.GetSection("RouteMappings").Bind(routeConfig.RouteMappings);
builder.Services.Configure<RouteConfiguration>(opts =>
{
    opts.CoreServiceUrl = routeConfig.CoreServiceUrl;
    opts.CrmServiceUrl = routeConfig.CrmServiceUrl;
    opts.ProjectServiceUrl = routeConfig.ProjectServiceUrl;
    opts.MailServiceUrl = routeConfig.MailServiceUrl;
    opts.ReportingServiceUrl = routeConfig.ReportingServiceUrl;
    opts.AdminServiceUrl = routeConfig.AdminServiceUrl;
    opts.CoreServiceGrpc = routeConfig.CoreServiceGrpc;
    opts.CrmServiceGrpc = routeConfig.CrmServiceGrpc;
    opts.ProjectServiceGrpc = routeConfig.ProjectServiceGrpc;
    opts.MailServiceGrpc = routeConfig.MailServiceGrpc;
    foreach (var kvp in routeConfig.RouteMappings)
        opts.RouteMappings[kvp.Key] = kvp.Value;
});

// -----------------------------------------------------------------------
// IHttpClientFactory — consumed by RequestRoutingMiddleware for proxying
// HTTP requests to backend microservices. Uses factory pattern to avoid
// socket exhaustion (recommended over direct HttpClient instantiation).
// Named clients are registered for each backend service with pre-configured
// base addresses, timeouts, and default Accept headers.
// -----------------------------------------------------------------------
builder.Services.AddHttpClient("CoreService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ServiceRoutes:CoreServiceUrl"] ?? "http://localhost:8084");
    client.Timeout = TimeSpan.FromSeconds(600); // Matches EQL query timeout per AAP 0.8.3
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient("CrmService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ServiceRoutes:CrmServiceUrl"] ?? "http://localhost:8082");
    client.Timeout = TimeSpan.FromSeconds(600);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient("ProjectService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ServiceRoutes:ProjectServiceUrl"] ?? "http://localhost:8092");
    client.Timeout = TimeSpan.FromSeconds(600);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient("MailService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ServiceRoutes:MailServiceUrl"] ?? "http://localhost:8090");
    client.Timeout = TimeSpan.FromSeconds(600);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient("ReportingService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ServiceRoutes:ReportingServiceUrl"] ?? "http://localhost:8088");
    client.Timeout = TimeSpan.FromSeconds(600);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient("AdminService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ServiceRoutes:AdminServiceUrl"] ?? "http://localhost:8086");
    client.Timeout = TimeSpan.FromSeconds(600);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});
// Default (unnamed) HttpClient for RequestRoutingMiddleware general proxying
builder.Services.AddHttpClient();

// -----------------------------------------------------------------------
// Gateway Application Services — DI registration
// ServiceProxyRegistry provides centralized HttpClient lookup for Razor
// Pages and controllers without hardcoding service URLs.
// -----------------------------------------------------------------------
builder.Services.AddSingleton<IServiceProxyRegistry, ServiceProxyRegistry>();

// ErpRequestContext — scoped per-request context required by Razor Page models
// (LoginModel, LogoutModel, BaseErpPageModel) for URL routing state resolution.
// Replaces the monolith's scoped ErpRequestContext registration from AddErp().
builder.Services.AddScoped<WebVella.Erp.Gateway.Models.ErpRequestContext>();

// -----------------------------------------------------------------------
// Health Checks — Gateway liveness and readiness
// -----------------------------------------------------------------------
builder.Services.AddHealthChecks();

// =========================================================================
// SECTION: App Pipeline Configuration (from Startup.Configure)
// =========================================================================
var app = builder.Build();

// -----------------------------------------------------------------------
// GatewayAppContext singleton — backward compatibility with monolith
// ErpAppContext.Current pattern used by BaseErpPageModel and Razor layouts.
// -----------------------------------------------------------------------
GatewayAppContext.Current = GatewayAppContext.FromServices(app.Services);

// -----------------------------------------------------------------------
// Request Localization — preserved from monolith Startup.cs lines 134-143
// -----------------------------------------------------------------------
var supportedCultures = new[] { new CultureInfo(locale) };
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(supportedCultures[0]),
    // Formatting numbers, dates, etc.
    SupportedCultures = supportedCultures,
    // UI strings that we have localized.
    SupportedUICultures = supportedCultures
});

// -----------------------------------------------------------------------
// Error Handling — environment-dependent behavior
// Development: UseDeveloperExceptionPage for detailed stack traces.
// Production: ErrorHandlingMiddleware wraps exceptions in BaseResponseModel
//             envelope format for backward-compatible REST API v3 error responses.
// Preserved from monolith Startup.cs lines 147-158.
// -----------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseExceptionHandler("/error");
    app.UseStatusCodePagesWithReExecute("/error");
}

// -----------------------------------------------------------------------
// Response Compression — before static files (monolith pattern, line 161)
// -----------------------------------------------------------------------
app.UseResponseCompression();

// Security Headers Middleware — defense-in-depth HTTP response headers
// X-Content-Type-Options: nosniff — prevents MIME type sniffing
// X-Frame-Options: DENY — prevents clickjacking via iframes
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});

// -----------------------------------------------------------------------
// CORS — before static files to enable CORS for static assets too
// Preserved from monolith Startup.cs line 164.
// -----------------------------------------------------------------------
app.UseCors();

// -----------------------------------------------------------------------
// Static Files — preserved from monolith Startup.cs lines 166-176
// Long-lived cache headers for versioned static assets (CSS, JS, images).
// 12-month max-age and +1 year Expires header.
// -----------------------------------------------------------------------
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = false,
    OnPrepareResponse = ctx =>
    {
        const int durationInSeconds = 60 * 60 * 24 * 30 * 12;
        ctx.Context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] =
            "public,max-age=" + durationInSeconds;
        ctx.Context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.Expires] =
            new[] { DateTime.UtcNow.AddYears(1).ToString("R") }; // Format RFC1123
    }
});
// Workaround for Blazor static file serving — preserved from monolith
// Startup.cs line 176: https://github.com/dotnet/aspnetcore/issues/9588
app.UseStaticFiles();

// -----------------------------------------------------------------------
// Routing — preserved from monolith Startup.cs line 177
// -----------------------------------------------------------------------
app.UseRouting();

// -----------------------------------------------------------------------
// Authentication + Authorization — must be between UseRouting and UseEndpoints
// Preserved from monolith Startup.cs lines 179-180.
// -----------------------------------------------------------------------
app.UseAuthentication();
app.UseAuthorization();

// -----------------------------------------------------------------------
// Gateway Middleware — NEW, replaces monolith's UseErpMiddleware + UseJwtMiddleware
// (WebVella.Erp.Web/Middleware/ErpAppBuilderExtensions.cs lines 7-17)
//
// AuthenticationMiddleware: Per-request JWT+Cookie auth context setup.
//   Validates JWT tokens via JwtTokenHandler, extracts user identity from
//   claims, establishes SecurityContext scope, and preserves cookie-based
//   auth with stale sign-out behavior.
//
// RequestRoutingMiddleware: Strangler Fig pattern routing engine.
//   Intercepts /api/v3/ requests and proxies them to backend microservices
//   based on RouteConfiguration URL pattern matching. Non-API requests
//   (Razor Pages, static files) fall through to the standard pipeline.
// -----------------------------------------------------------------------
app.UseMiddleware<AuthenticationMiddleware>();
app.UseMiddleware<RequestRoutingMiddleware>();

// -----------------------------------------------------------------------
// Health Check endpoint
// -----------------------------------------------------------------------
app.MapHealthChecks("/health");

// -----------------------------------------------------------------------
// Endpoints — Razor Pages + MVC Controllers
// Preserved from monolith Startup.cs lines 191-192.
// -----------------------------------------------------------------------
app.MapRazorPages();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// -----------------------------------------------------------------------
// Run the Gateway
// -----------------------------------------------------------------------
app.Run();
