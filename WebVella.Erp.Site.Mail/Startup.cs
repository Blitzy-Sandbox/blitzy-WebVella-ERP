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
using WebVella.Erp.Plugins.Mail;
using WebVella.Erp.Plugins.Next;
using WebVella.Erp.Plugins.SDK;
using WebVella.Erp.Web;
using WebVella.Erp.Web.Middleware;

namespace WebVella.Erp.Site.Mail
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
						options.Cookie.Name = "erp_auth_mail";
						options.LoginPath = new PathString("/login");
						options.LogoutPath = new PathString("/logout");
						options.AccessDeniedPath = new PathString("/error?access_denied");
						options.ReturnUrlParameter = "returnUrl";
					});

			//Brute-force / DoS protection (OWASP A04/A05): throttle the authentication endpoint by client IP.
			//A global limiter keeps the protection self-contained (no per-endpoint attribute required) and returns
			//NoLimiter for every non-login path, so normal ERP API traffic is unaffected (functional parity).
			services.AddRateLimiter(options =>
			{
				options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
				options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
				{
					var path = httpContext.Request.Path;
					if (path.HasValue && path.Value.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
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
				//HTTP Strict Transport Security (OWASP A05): instruct browsers to use HTTPS only. Production only.
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
			//.UseErpPlugin<NextPlugin>()
			.UseErpPlugin<SdkPlugin>()
			.UseErpPlugin<MailPlugin>()
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

