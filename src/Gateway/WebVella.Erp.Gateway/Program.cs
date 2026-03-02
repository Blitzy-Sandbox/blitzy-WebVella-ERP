// =========================================================================
// WebVella ERP — API Gateway / Backend-for-Frontend (BFF) Service Entry Point
// =========================================================================
// Minimal hosting API that configures the Gateway with:
//   - JWT + Cookie dual-mode authentication (matching monolith Startup.cs)
//   - RequestRoutingMiddleware for Strangler Fig routing to backend microservices
//   - ErrorHandlingMiddleware for BaseResponseModel error envelopes
//   - Razor Pages for BFF UI (login, logout, record pages)
//   - Newtonsoft.Json serialization (backward-compatible API v3 contract)
//   - IHttpClientFactory for service proxying (consumed by RequestRoutingMiddleware)
//   - RouteConfiguration binding from appsettings.json ServiceRoutes section
//   - CORS, response compression, static files, health checks
//   - GatewayAppContext singleton for legacy compatibility
//
// Derived from monolith WebVella.Erp.Site/Startup.cs patterns.
// =========================================================================

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using WebVella.Erp.Gateway.Configuration;
using WebVella.Erp.Gateway.Middleware;
using WebVella.Erp.Gateway.Models;
using WebVella.Erp.Gateway.Services;
using WebVella.Erp.SharedKernel;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------
// Npgsql legacy timestamp behavior — preserved from monolith Startup.cs
// Required until system tables are migrated to timestamptz columns.
// -----------------------------------------------------------------------
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// -----------------------------------------------------------------------
// ErpSettings initialization — bind from appsettings.json Settings section
// Preserves monolith's ErpSettings static configuration pattern used by
// Razor layouts (_AppMaster, _SystemMaster) and BaseErpPageModel.
// -----------------------------------------------------------------------
ErpSettings.Initialize(builder.Configuration);

// -----------------------------------------------------------------------
// Localization — preserved from monolith Startup.cs
// -----------------------------------------------------------------------
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
var locale = builder.Configuration["Settings:Locale"] ?? "en-US";
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(locale);
});

// -----------------------------------------------------------------------
// Response Compression — preserved from monolith Startup.cs
// -----------------------------------------------------------------------
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
builder.Services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });

// -----------------------------------------------------------------------
// Routing — lowercase URLs preserved from monolith
// -----------------------------------------------------------------------
builder.Services.AddRouting(options => { options.LowercaseUrls = true; });

// -----------------------------------------------------------------------
// CORS — open policy for development, matching monolith Startup.cs
// Production deployments should restrict origins via configuration.
// -----------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

// -----------------------------------------------------------------------
// RouteConfiguration binding — drives RequestRoutingMiddleware URL routing
// Binds ServiceRoutes, GrpcEndpoints, and RouteMappings sections from
// appsettings.json into a strongly-typed RouteConfiguration instance.
// -----------------------------------------------------------------------
builder.Services.Configure<RouteConfiguration>(builder.Configuration.GetSection("ServiceRoutes"));

// -----------------------------------------------------------------------
// IHttpClientFactory — consumed by RequestRoutingMiddleware for proxying
// HTTP requests to backend microservices. Uses factory pattern to avoid
// socket exhaustion (recommended over direct HttpClient instantiation).
// Named clients are registered for each backend service to allow
// per-service configuration (timeout, base address, headers).
// -----------------------------------------------------------------------
builder.Services.AddHttpClient("CoreService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServiceRoutes:CoreServiceUrl"] ?? "http://core-service:8080");
    client.Timeout = TimeSpan.FromSeconds(600); // Matches EQL query timeout per AAP 0.8.3
});
builder.Services.AddHttpClient("CrmService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServiceRoutes:CrmServiceUrl"] ?? "http://crm-service:8080");
    client.Timeout = TimeSpan.FromSeconds(600);
});
builder.Services.AddHttpClient("ProjectService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServiceRoutes:ProjectServiceUrl"] ?? "http://project-service:8080");
    client.Timeout = TimeSpan.FromSeconds(600);
});
builder.Services.AddHttpClient("MailService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServiceRoutes:MailServiceUrl"] ?? "http://mail-service:8080");
    client.Timeout = TimeSpan.FromSeconds(600);
});
builder.Services.AddHttpClient("ReportingService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServiceRoutes:ReportingServiceUrl"] ?? "http://reporting-service:8080");
    client.Timeout = TimeSpan.FromSeconds(600);
});
builder.Services.AddHttpClient("AdminService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServiceRoutes:AdminServiceUrl"] ?? "http://admin-service:8080");
    client.Timeout = TimeSpan.FromSeconds(600);
});
// Default (unnamed) HttpClient for RequestRoutingMiddleware general proxying
builder.Services.AddHttpClient();

// -----------------------------------------------------------------------
// Gateway Application Services — DI registration
// -----------------------------------------------------------------------
builder.Services.AddSingleton<IServiceProxyRegistry, ServiceProxyRegistry>();

// -----------------------------------------------------------------------
// MVC + Razor Pages — preserved from monolith Startup.cs
// Configures Newtonsoft.Json as the JSON serializer for API contract
// backward compatibility (BaseResponseModel envelope, [JsonProperty] annotations).
// -----------------------------------------------------------------------
builder.Services.AddMvc()
    .AddRazorPagesOptions(options =>
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/login");
    })
    .AddNewtonsoftJson(options =>
    {
        // Gateway uses standard Newtonsoft.Json settings.
        // The ErpDateTimeJsonConverter is service-specific (requires
        // ErpSettings.TimeZoneName) and is applied only when proxied
        // responses are deserialized from backend services.
        options.SerializerSettings.DateParseHandling = DateParseHandling.DateTime;
    });

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// -----------------------------------------------------------------------
// Authentication — JWT + Cookie dual-mode
// Preserved from monolith Startup.cs (WebVella.Erp.Site):
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
    options.Cookie.Name = "erp_auth_base";
    options.LoginPath = new PathString("/login");
    options.LogoutPath = new PathString("/logout");
    options.AccessDeniedPath = new PathString("/error?access_denied");
    options.ReturnUrlParameter = "returnUrl";
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
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
                ?? "DEVELOPMENT_ONLY_KEY__OVERRIDE_VIA_Settings__Jwt__Key_ENV_VAR"))
    };
})
.AddPolicyScheme("JWT_OR_COOKIE", "JWT_OR_COOKIE", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        string authorization = context.Request.Headers[HeaderNames.Authorization].ToString();
        if (!string.IsNullOrEmpty(authorization) && authorization.StartsWith("Bearer "))
            return JwtBearerDefaults.AuthenticationScheme;

        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
});

// -----------------------------------------------------------------------
// Health Checks — Gateway liveness and readiness
// -----------------------------------------------------------------------
builder.Services.AddHealthChecks();

// -----------------------------------------------------------------------
// Build the application
// -----------------------------------------------------------------------
var app = builder.Build();

// -----------------------------------------------------------------------
// GatewayAppContext singleton — backward compatibility with monolith
// ErpAppContext.Current pattern used by BaseErpPageModel and Razor layouts.
// -----------------------------------------------------------------------
GatewayAppContext.Current = GatewayAppContext.FromServices(app.Services);

// -----------------------------------------------------------------------
// Request Localization — preserved from monolith Startup.cs
// -----------------------------------------------------------------------
var supportedCultures = new[] { new CultureInfo(locale) };
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(supportedCultures[0]),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures
});

// -----------------------------------------------------------------------
// Error Handling — environment-dependent behavior
// Development: UseDeveloperExceptionPage for detailed stack traces.
// Production: ErrorHandlingMiddleware wraps exceptions in BaseResponseModel.
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
// Response Compression — before static files (monolith pattern)
// -----------------------------------------------------------------------
app.UseResponseCompression();

// -----------------------------------------------------------------------
// CORS — before static files to enable CORS for static assets too
// -----------------------------------------------------------------------
app.UseCors();

// -----------------------------------------------------------------------
// Static Files — preserved from monolith with aggressive caching
// 12-month cache for versioned static assets (CSS, JS, images).
// -----------------------------------------------------------------------
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = false,
    OnPrepareResponse = ctx =>
    {
        const int durationInSeconds = 60 * 60 * 24 * 30 * 12;
        ctx.Context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=" + durationInSeconds;
        ctx.Context.Response.Headers[HeaderNames.Expires] = new[] { DateTime.UtcNow.AddYears(1).ToString("R") };
    }
});
// Workaround for Blazor static file serving — preserved from monolith
app.UseStaticFiles();

// -----------------------------------------------------------------------
// Routing
// -----------------------------------------------------------------------
app.UseRouting();

// -----------------------------------------------------------------------
// Authentication + Authorization — must be between UseRouting and UseEndpoints
// -----------------------------------------------------------------------
app.UseAuthentication();
app.UseAuthorization();

// -----------------------------------------------------------------------
// RequestRoutingMiddleware — Strangler Fig pattern
// Intercepts /api/v3/ requests and proxies them to backend microservices
// based on RouteConfiguration URL pattern matching. Non-API requests
// (Razor Pages, static files) fall through to the standard pipeline.
// -----------------------------------------------------------------------
app.UseMiddleware<RequestRoutingMiddleware>();

// -----------------------------------------------------------------------
// Health Check endpoint
// -----------------------------------------------------------------------
app.MapHealthChecks("/health");

// -----------------------------------------------------------------------
// Endpoints — Razor Pages + MVC Controllers
// -----------------------------------------------------------------------
app.MapRazorPages();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// -----------------------------------------------------------------------
// Run the Gateway
// -----------------------------------------------------------------------
app.Run();
