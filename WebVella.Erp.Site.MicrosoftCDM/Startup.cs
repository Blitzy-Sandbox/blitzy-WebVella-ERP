using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using WebVella.Erp.Web;
using WebVella.Erp.Web.Middleware;
using WebVella.Erp.Plugins.SDK;
using WebVella.Erp.Plugins.MicrosoftCDM;

namespace WebVella.Erp.Site.MicrosoftCDM
{
	public class Startup
	{
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
					builder => builder.WithOrigins("http://localhost:3000", "http://localhost").AllowAnyHeader().AllowAnyMethod().AllowCredentials());
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
						options.Cookie.Name = "erp_auth_crm";
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
					});

			//Brute-force / DoS defense (A04 Insecure Design): register the built-in ASP.NET Core rate limiter.
			//A named "login" policy (fixed window, partitioned by client IP, 5 requests/minute) throttles repeated
			//login attempts. It is opt-in per endpoint, so normal ERP traffic is unaffected (behavior-preserving).
			//The primary account lockout (after 5 failed attempts) is enforced at the login page hook; this is
			//defense-in-depth that pairs with it.
			services.AddRateLimiter(options =>
			{
				options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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
				//HSTS (A05): instruct browsers to use HTTPS only. Enabled outside Development.
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
			.UseErpPlugin<MicrosoftCDMPlugin>()
			.UseErpPlugin<SdkPlugin>()
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
