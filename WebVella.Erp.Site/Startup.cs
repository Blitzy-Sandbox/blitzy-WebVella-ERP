using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.IO.Compression;
using WebVella.Erp.Plugins.SDK;
using WebVella.Erp.Web;
using WebVella.Erp.Web.Middleware;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace WebVella.Erp.Site
{
    public class Startup
    {
        public IConfigurationRoot Configuration { get; private set; } = null;

        private readonly IWebHostEnvironment environment;

        public Startup(IWebHostEnvironment environment)
        {
            this.environment = environment;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            //legacy until we fix system tables
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            //Portability (QA Issue 3 — Linux content-root casing): the committed file is tracked as "Config.json"
            //(capital C); on case-sensitive file systems the loader must use the exact casing or AddJsonFile throws
            //FileNotFoundException when the host runs from its normal content root. Safe on case-insensitive OSes.
            string configPath = "Config.json";
            //Security (A02/A05): layer environment variables on top of the committed config.json so runtime secret
            //overlays (Settings__Jwt__Key, Settings__EncryptionKey) reach BOTH the host JWT setup below and the
            //central ErpSettings.Initialize in UseErp(). Env vars are added last so they override the JSON placeholders.
            Configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile(configPath).AddEnvironmentVariables().Build();

            services.AddLocalization(options => options.ResourcesPath = "Resources");
            services.Configure<RequestLocalizationOptions>(options => { options.DefaultRequestCulture = new RequestCulture(Configuration["Settings:Locale"]); });

            services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
            services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });
            services.AddRouting(options => { options.LowercaseUrls = true; });

            //CORS policy declaration (A05 - Security Misconfiguration)
            //Origins are read from Config.json (Settings:CorsOrigins) with a safe localhost fallback when the key is missing/empty.
            //A NAMED policy with WithOrigins(...) is required because a wildcard any-origin policy is incompatible with AllowCredentials();
            //this host uses cookie authentication, so credentialed cross-origin requests must be supported.
            //Origins must be specified with NO trailing slash (exact-match).
            var corsOrigins = Configuration.GetSection("Settings:CorsOrigins").Get<string[]>()
                              ?? new[] { "http://localhost:3333", "http://localhost:3000", "http://localhost" };
            services.AddCors(options =>
            {
                options.AddPolicy("ErpCorsPolicy", policy =>
                    policy.WithOrigins(corsOrigins)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials());
            });
            services.AddDetection();

            services.AddMvc()

                .AddRazorPagesOptions(options =>
                {
                    options.Conventions.AuthorizeFolder("/");
                    options.Conventions.AllowAnonymousToPage("/login");
                })
                .AddNewtonsoftJson(options =>
               {
                   options.SerializerSettings.Converters.Add(new ErpDateTimeJsonConverter());
               });

            services.AddControllersWithViews();
            services.AddRazorPages().AddRazorRuntimeCompilation();

            //adds global datetime converter for json.net
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
            };

            services.AddAuthentication(options =>
            {
                options.DefaultScheme = "JWT_OR_COOKIE";
                options.DefaultChallengeScheme = "JWT_OR_COOKIE";
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.HttpOnly = true;
                //A07 - Authentication/Session hardening: require HTTPS-only transmission (Secure) and set SameSite
                //to mitigate CSRF. Secure=Always pairs with UseHsts() in the production pipeline; Lax is used so
                //login redirect flows continue to work (Strict would break cross-site navigations into the app).
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                //Fully qualified: SameSiteMode is defined in BOTH Microsoft.AspNetCore.Http and Microsoft.Net.Http.Headers
                //(the latter is imported for HeaderNames), so qualify to the ASP.NET Core HTTP type the cookie expects.
                options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
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
                     ValidIssuer = Configuration["Settings:Jwt:Issuer"],
                     ValidAudience = Configuration["Settings:Jwt:Audience"],
                     IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Configuration["Settings:Jwt:Key"]))
                 };
             })
              .AddPolicyScheme("JWT_OR_COOKIE", "JWT_OR_COOKIE", options =>
              {
                  options.ForwardDefaultSelector = context =>
                  {
                      string authorization = context.Request.Headers[HeaderNames.Authorization];
                      if (!string.IsNullOrEmpty(authorization) && authorization.StartsWith("Bearer "))
                          return JwtBearerDefaults.AuthenticationScheme;

                      return CookieAuthenticationDefaults.AuthenticationScheme;
                  };
              });


            //HSTS (A05/A07): emit Strict-Transport-Security with the prompt-specified value (1 year + includeSubDomains).
            //Matches the central SecurityHeadersMiddleware literal so the framework UseHsts() default cannot preempt it.
            services.AddHsts(options =>
            {
                options.MaxAge = TimeSpan.FromDays(365);
                options.IncludeSubDomains = true;
            });

            //A04 - Insecure Design: edge-level brute-force protection via the built-in rate limiter
            //(Microsoft.AspNetCore.RateLimiting, in-framework on net10.0 - no extra NuGet package).
            //This fixed-window "login" limiter pairs with the account-level lockout (after 5 failed attempts)
            //integrated at the login hook in WebVella.Erp.Web/Pages/login.cshtml.cs. Rejected requests get HTTP 429.
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // Security (A04; CWE-307/CWE-799) — F-RATELIMIT-POST: commit the 429 response body here so the
                // genuine rate-limit rejection survives UseStatusCodePagesWithReExecute("/error"). Without a started
                // response that re-execution replays the request (preserving its HTTP method) against the /error
                // Razor Page; for a credential POST the page's AutoValidateAntiforgeryToken filter then fails and
                // OVERWRITES the limiter's 429 with a 400 — which is why credential POSTs past PermitLimit were never
                // observed as 429 (only safe-method GETs were). Starting the response (ContentType + body) makes
                // StatusCodePages skip re-execution, so the genuine 429 is preserved uniformly for GET and POST.
                options.OnRejected = async (context, cancellationToken) =>
                {
                    var response = context.HttpContext.Response;
                    if (!response.HasStarted)
                    {
                        response.StatusCode = StatusCodes.Status429TooManyRequests;
                        response.ContentType = "application/json";
                        await response.WriteAsync("{\"success\":false,\"message\":\"Too many requests. Please try again later.\"}", cancellationToken);
                    }
                };

                //Named policy retained for explicit opt-in via [EnableRateLimiting("login")].
                options.AddFixedWindowLimiter("login", opt =>
                {
                    opt.Window = TimeSpan.FromMinutes(1);
                    opt.PermitLimit = 10;
                    opt.QueueLimit = 0;
                    opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                });

                //Security (A04; CWE-307/CWE-799): the named policy above is INERT unless an endpoint opts in, which left
                //the brute-force surface unprotected. A GlobalLimiter makes the throttle self-contained and ACTIVE: it
                //caps per-client-IP requests to the credential surfaces - the Razor login (/login) AND the JWT token
                //issuance endpoints (/api/v3/en_US/auth/jwt/token[/refresh]) - while returning NoLimiter for every other
                //path so normal ERP traffic keeps full functional parity. Pairs with the 5-attempt account lockout.
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    var path = httpContext.Request.Path;
                    bool isAuthPath = path.HasValue &&
                        (path.Value.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
                         || path.Value.StartsWith("/api/v3/en_US/auth/jwt/token", StringComparison.OrdinalIgnoreCase));
                    if (isAuthPath)
                    {
                        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true
                        });
                    }
                    return RateLimitPartition.GetNoLimiter("__no_rate_limit__");
                });
            });

            services.AddErp();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            //Security headers (A05): register SecurityHeadersMiddleware at the FRONT of the pipeline — ahead of
            //UseStaticFiles/UseAuthentication/UseAuthorization — so the 7 security headers decorate every response
            //surface (static files, 302 auth-challenge redirects, error pages), not just endpoint responses, and so
            //its OnStarting callback runs last (LIFO) and can force X-Frame-Options: DENY. Centralized in
            //ErpMvcExtensions.UseErpSecurityHeaders so all 7 hosts inherit identical behavior.
            app.UseErpSecurityHeaders();

            var supportedCultures = new[] { new CultureInfo(Configuration["Settings:Locale"]) };

            app.UseRequestLocalization(new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture(supportedCultures[0]),
                // Formatting numbers, dates, etc.
                SupportedCultures = supportedCultures,
                // UI strings that we have localized.
                SupportedUICultures = supportedCultures
            });

            //env.EnvironmentName = EnvironmentName.Production;
            // Add the following to the request pipeline only in development environment.
            if (string.Equals(env.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                //A05 - transport security: enforce HTTPS via HSTS in non-Development environments only
                //(HSTS can poison localhost during dev). This complements the Strict-Transport-Security header
                //emitted by SecurityHeadersMiddleware and is the operational prerequisite for Secure cookies.
                app.UseHsts();

                // Add Error handling middleware which catches all application specific errors and
                // send the request to the following path or controller action.
                app.UseErrorHandlingMiddleware();
                app.UseExceptionHandler("/error");
                app.UseStatusCodePagesWithReExecute("/error");
            }

            //Should be before Static files
            app.UseResponseCompression();

            //Enable CORS using the named allowlist policy (A05) -> must be before static files so they are covered too
            app.UseCors("ErpCorsPolicy");

            app.UseStaticFiles(new StaticFileOptions
            {
                ServeUnknownFileTypes = false,
                OnPrepareResponse = ctx =>
                {
                    const int durationInSeconds = 60 * 60 * 24 * 30 * 12;
                    ctx.Context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=" + durationInSeconds;
                    ctx.Context.Response.Headers[HeaderNames.Expires] = new[] { DateTime.UtcNow.AddYears(1).ToString("R") }; // Format RFC1123
                }
            });
            app.UseStaticFiles(); //Workaround for blazor to work - https://github.com/dotnet/aspnetcore/issues/9588
            app.UseRouting();

            //A04 - Insecure Design: enable the request rate limiter AFTER routing (so endpoint-specific limiters
            //can resolve endpoint metadata) and BEFORE authentication, to throttle brute-force login attempts.
            app.UseRateLimiter();

			app.UseAuthentication();
			app.UseAuthorization();

			app
			.UseErpPlugin<SdkPlugin>()
            .UseErp()
            .UseErpMiddleware()
            .UseJwtMiddleware();

			
			app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
            });
        }
    }
}

