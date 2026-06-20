using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Threading.RateLimiting;
using WebVella.Erp.Plugins.Next;
using WebVella.Erp.Web;
using WebVella.Erp.Web.Middleware;
using WebVella.TagHelpers;

namespace WebVella.Erp.Site.Next
{
	public class Startup
	{
		public Startup()
		{
		}

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			//legacy until we fix system tables
			AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
			services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
			services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });
			services.AddRouting(options => { options.LowercaseUrls = true; });

			//CORS policy declaration
			services.AddCors(options =>
			{
				options.AddPolicy("AllowNodeJsLocalhost",
					//Security (A05/CWE-942): explicit origins only (no AllowAnyOrigin). AllowAnyHeader is required so
					//credentialed cross-origin API calls carrying custom headers (e.g. content-type, antiforgery) succeed.
					builder => builder.WithOrigins("http://localhost:3000", "http://localhost").AllowAnyMethod().AllowAnyHeader().AllowCredentials());
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

			services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
					.AddCookie(options =>
					{
						options.Cookie.HttpOnly = true;
						options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
						options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
						options.Cookie.Name = "erp_auth_next";
						options.LoginPath = new PathString("/login");
						options.LogoutPath = new PathString("/logout");
						options.AccessDeniedPath = new PathString("/error?access_denied");
						options.ReturnUrlParameter = "returnUrl";
					});

			//HSTS (A05/A07): emit Strict-Transport-Security with the prompt-specified value (1 year + includeSubDomains).
			services.AddHsts(options =>
			{
				options.MaxAge = TimeSpan.FromDays(365);
				options.IncludeSubDomains = true;
			});

			//Security: brute-force throttling (A04) using the built-in .NET rate limiter (net10.0; no new package).
			//Scoped to POST /login per client IP so the rest of the ERP UI keeps full functional parity.
			services.AddRateLimiter(options =>
			{
				options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

				//Named policy available for explicit opt-in by the login page ([EnableRateLimiting("login")]).
				options.AddPolicy("login", httpContext =>
					RateLimitPartition.GetFixedWindowLimiter(
						partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
						factory: _ => new FixedWindowRateLimiterOptions
						{
							PermitLimit = 5,
							Window = TimeSpan.FromMinutes(1),
							QueueLimit = 0
						}));

				//Self-contained defense-in-depth: throttle ONLY POST /login; everything else is unlimited.
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
				app.UseHsts();
				// Add Error handling middleware which catches all application specific errors and
				// send the request to the following path or controller action.
				app.UseErrorHandlingMiddleware();
				app.UseExceptionHandler("/error");
				app.UseStatusCodePagesWithReExecute("/error");
			}

			//Should be before Static files
			app.UseResponseCompression();

			app.UseCors("AllowNodeJsLocalhost"); //Enable CORS -> should be before static files to enable for it too

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
			app.UseRateLimiter();
			app.UseAuthentication();
			app.UseAuthorization();

			app
			.UseErpPlugin<NextPlugin>()
			.UseErp()
			.UseErpMiddleware();

			app.UseEndpoints(endpoints =>
			{
				endpoints.MapRazorPages();
				endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
			});
		}
	}
}

