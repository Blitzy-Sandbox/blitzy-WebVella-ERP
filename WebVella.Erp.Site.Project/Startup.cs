using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.RateLimiting;
using WebVella.Erp.Plugins.Next;
using WebVella.Erp.Plugins.Project;
using WebVella.Erp.Plugins.SDK;
using WebVella.Erp.Web;
using WebVella.Erp.Web.Middleware;

namespace WebVella.Erp.Site.Project
{

	public class Startup
	{
		public IConfigurationRoot Configuration { get; private set; } = null;
		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			//legacy until we fix system tables
			AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

			// PORTABILITY (Linux case-sensitive filesystems): use the EXACT committed filename casing "Config.json".
			// A lowercase "config.json" lookup throws FileNotFoundException on case-sensitive filesystems even though
			// the build/publish output contains the file as "Config.json".
			string configPath = "Config.json";
			// Security (A02/A05): allow externalized secrets to be supplied at runtime via environment
			// variables (e.g. Settings__Jwt__Key, Settings__EncryptionKey). The Config.json placeholders for
			// these keys are intentionally empty; with no env override the host fails fast at startup.
			Configuration = new ConfigurationBuilder()
				.SetBasePath(Directory.GetCurrentDirectory())
				.AddJsonFile(configPath)
				.AddEnvironmentVariables()
				.Build();


			services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
			services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });
			services.AddRouting(options => { options.LowercaseUrls = true; });

			//CORS policy declaration
			//services.AddCors(options =>
			//{
			//	options.AddPolicy("AllowNodeJsLocalhost",
			//		builder => builder.WithOrigins("http://localhost:3333", "http://localhost:3000", "http://localhost", "http://localhost:2202").AllowAnyMethod().AllowCredentials());
			//});
            // Security (A05 Security Misconfiguration; CWE-942): explicit CORS origin allowlist sourced from
            // Settings:Cors:AllowedOrigins (Config.json), replacing the previous permissive any-origin policy.
            // AllowCredentials() is valid here because the origins are explicit; credentials must never be
            // combined with a wildcard origin.
            var corsAllowedOrigins = Configuration.GetSection("Settings:Cors:AllowedOrigins").Get<string[]>()
                ?? new[] { "http://localhost:3333", "http://localhost:3000", "http://localhost", "http://localhost:2202" };
            services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.WithOrigins(corsAllowedOrigins)
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
				// Security (A07 Auth Failures; CWE-614/CWE-1275): send the auth cookie over HTTPS only and
				// constrain cross-site sending. SameSite=Lax preserves top-level navigation (e.g. login redirects).
				options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
				// Fully qualified: this file also imports Microsoft.Net.Http.Headers (for HeaderNames), which
				// declares a SameSiteMode of its own; CookieBuilder.SameSite expects the ASP.NET Core Http type.
				options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
				options.Cookie.Name = "erp_auth_project";
				options.LoginPath = new PathString("/login");
				options.LogoutPath = new PathString("/logout");
				options.AccessDeniedPath = new PathString("/error?access_denied");
				options.ReturnUrlParameter = "returnUrl";
				//SECURITY (OWASP A04 Insecure Design / A07 - API auth boundary): for requests under "/api", return an
				//API-appropriate 401/403 status instead of a 302 redirect to the cookie login page. Interactive
				//(browser, non-API) requests keep the normal login / access-denied redirect so existing flows are
				//unchanged. PathString.StartsWithSegments is ordinal case-insensitive and matches "/api" and "/api/...".
				options.Events = new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationEvents
				{
					OnRedirectToLogin = context =>
					{
						if (context.Request.Path.StartsWithSegments("/api"))
							context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;
						else
							context.Response.Redirect(context.RedirectUri);
						return System.Threading.Tasks.Task.CompletedTask;
					},
					OnRedirectToAccessDenied = context =>
					{
						if (context.Request.Path.StartsWithSegments("/api"))
							context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden;
						else
							context.Response.Redirect(context.RedirectUri);
						return System.Threading.Tasks.Task.CompletedTask;
					}
				};
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

			// Security (A04 Insecure Design; CWE-307/CWE-799): per-host brute-force defense. Register a NAMED
			// "login" fixed-window limiter (5 requests / minute). The login page opts into this policy, so all
			// other endpoints keep functional parity. Pairs with the in-process lockout in login.cshtml.cs.
			services.AddRateLimiter(options =>
			{
				options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

				//Named policy retained for explicit opt-in via [EnableRateLimiting("login")] on the login page.
				options.AddPolicy("login", httpContext =>
					RateLimitPartition.GetFixedWindowLimiter(
						partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
						factory: _ => new FixedWindowRateLimiterOptions
						{
							PermitLimit = 5,
							Window = TimeSpan.FromMinutes(1),
							QueueLimit = 0
						}));

				//A04 brute-force defense: the GlobalLimiter actually enforces throttling on POST /login (the named
				//policy alone was never attached to any endpoint). Every other request is unlimited for parity.
				options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
				{
					if (HttpMethods.IsPost(httpContext.Request.Method) &&
						httpContext.Request.Path.StartsWithSegments("/login"))
					{
						return RateLimitPartition.GetFixedWindowLimiter(
							partitionKey: "login:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
							factory: _ => new FixedWindowRateLimiterOptions
							{
								PermitLimit = 5,
								Window = TimeSpan.FromMinutes(1),
								QueueLimit = 0
							});
					}
					return RateLimitPartition.GetNoLimiter("unlimited");
				});
			});

			//HSTS (A05/A07 - CWE-1021/CWE-693): configure Strict-Transport-Security with the prompt-specified value
			//(1 year + includeSubDomains) so the UseHsts() call in the pipeline emits the exact header. The central
			//SecurityHeadersMiddleware additionally force-sets this exact value, guaranteeing it on every response.
			services.AddHsts(options =>
			{
				options.MaxAge = TimeSpan.FromDays(365);
				options.IncludeSubDomains = true;
			});

			services.AddErp();
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			app.UseRequestLocalization(new RequestLocalizationOptions
			{
				DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(CultureInfo.GetCultureInfo("en-US"))
			});

			//env.EnvironmentName = EnvironmentName.Production;
			// Add the following to the request pipeline only in development environment.
			if (string.Equals(env.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
			{
				app.UseDeveloperExceptionPage();
			}
			else
			{
				// Security (A05 Security Misconfiguration; CWE-693): enforce HTTPS via HSTS in non-development
				// environments only (HSTS on http/localhost in dev would break local browsing).
				app.UseHsts();

				// Add Error handling middleware which catches all application specific errors and
				// send the request to the following path or controller action.
				app.UseErrorHandlingMiddleware();
				app.UseExceptionHandler("/error");
				app.UseStatusCodePagesWithReExecute("/error");
			}

			//Should be before Static files
			app.UseResponseCompression();

            //app.UseCors("AllowNodeJsLocalhost"); //Enable CORS -> should be before static files to enable for it too
            app.UseCors(); //Enable CORS -> should be before static files to enable for it too

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

			// Security (A04 Insecure Design; CWE-307/CWE-799): activate the rate limiter after routing so the
			// endpoint-specific "login" policy resolves, and before authentication.
			app.UseRateLimiter();

			app.UseAuthentication();
			app.UseAuthorization();

			app
			.UseErpPlugin<NextPlugin>()
			.UseErpPlugin<SdkPlugin>()
			.UseErpPlugin<ProjectPlugin>()
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

