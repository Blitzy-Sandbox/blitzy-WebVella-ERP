using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using System;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using WebVella.Erp.Plugins.Next;
using WebVella.Erp.Plugins.SDK;
using WebVella.Erp.Web;
using WebVella.Erp.Web.Middleware;

namespace WebVella.Erp.Site.Sdk
{
	public class Startup
	{
		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			//legacy until we fix system tables
			AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

			// SECURITY (A05 Security Misconfiguration / A02 Cryptographic Failures — CWE-665 Improper Initialization,
			// CWE-798 Use of Hard-coded Credentials): initialize ErpSettings here, in this in-scope host, from a merged
			// configuration so the secrets removed from config.json (connection string, encryption key, JWT signing key)
			// — supplied via environment variables in production — reach ErpSettings without being committed. Done here
			// rather than in the shared UseErp() helper (which only reads config.json); UseErp()'s IsInitialized guard
			// then skips its own initialization.
			if (!WebVella.Erp.ErpSettings.IsInitialized)
			{
				var erpConfiguration = new ConfigurationBuilder()
					.SetBasePath(System.IO.Directory.GetCurrentDirectory())
					.AddJsonFile("config.json", optional: true)
					.AddEnvironmentVariables()
					.Build();
				WebVella.Erp.ErpSettings.Initialize(erpConfiguration);
			}
			services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
			services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });
			services.AddRouting(options => { options.LowercaseUrls = true; });

			//CORS policy declaration
			services.AddCors(options =>
			{
				options.AddPolicy("AllowNodeJsLocalhost",
					builder => builder.WithOrigins("http://localhost:3000", "http://localhost").AllowAnyMethod().AllowCredentials());
			});

			services.AddDetection();


			services.AddMvc()

				.AddRazorPagesOptions(options =>
				{
					options.Conventions.AuthorizeFolder("/");
					options.Conventions.AllowAnonymousToPage("/login");
					options.Conventions.AllowAnonymousToPage("/dev");
				})
				.AddNewtonsoftJson(options =>
				{
					options.SerializerSettings.Converters.Add(new ErpDateTimeJsonConverter());
				});

			services.AddControllersWithViews();
			services.AddRazorPages().AddRazorRuntimeCompilation();
			services.AddServerSideBlazor().AddCircuitOptions(options => {  options.DetailedErrors = true; });
			//adds global datetime converter for json.net
			JsonConvert.DefaultSettings = () => new JsonSerializerSettings
			{
				Converters = new List<JsonConverter> { new ErpDateTimeJsonConverter() }
			};

			services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
					.AddCookie(options =>
					{
						options.Cookie.HttpOnly = true;
						// SECURITY (A05/A07 / CWE-614 Sensitive Cookie Without 'Secure' flag, CWE-1275 weak SameSite):
						// send the auth cookie only over HTTPS and restrict cross-site sending to mitigate cookie
						// theft over cleartext and CSRF. Requires HTTPS in non-dev (see UseHttpsRedirection/UseHsts below).
						// REGRESSION FIX (A05/A07): gate Secure on the environment so local Development over plain HTTP still
						// receives the auth cookie (SameAsRequest); non-Development stays HTTPS-only (Always).
						options.Cookie.SecurePolicy =
							string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase)
								? CookieSecurePolicy.SameAsRequest
								: CookieSecurePolicy.Always;
						// Fully-qualified: 'SameSiteMode' is ambiguous between Microsoft.Net.Http.Headers and
						// Microsoft.AspNetCore.Http (both imported); Cookie.SameSite requires the ASP.NET Core Http type.
						options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
						options.Cookie.Name = "erp_auth_sdk";
						options.LoginPath = new PathString("/login");
						options.LogoutPath = new PathString("/logout");
						options.AccessDeniedPath = new PathString("/error?access_denied");
						options.ReturnUrlParameter = "returnUrl";
					});

			// SECURITY (A05 / CWE-693 Protection Mechanism Failure): configure HSTS so UseHsts() emits the mandated
			// baseline "Strict-Transport-Security: max-age=31536000; includeSubDomains" (1 year). Header is only sent
			// over HTTPS and only in non-development environments (see Configure()).
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
				// Add Error handling middleware which catches all application specific errors and
				// send the request to the following path or controller action.
				app.UseErrorHandlingMiddleware();
				app.UseExceptionHandler("/error");
				app.UseStatusCodePagesWithReExecute("/error");
				// SECURITY (A02/A05 / CWE-319 Cleartext Transmission of Sensitive Information): force HTTP->HTTPS so
				// credentials and the Secure auth cookie are never sent in cleartext. Gated to non-dev only.
				app.UseHttpsRedirection();
				// SECURITY (A05 / CWE-693 Protection Mechanism Failure): emit HSTS (max-age=31536000; includeSubDomains,
				// per AddHsts) so browsers pin HTTPS. Gated to non-dev so local HTTP debugging is unaffected.
				app.UseHsts();
			}

			// SECURITY (A05 / CWE-693 Protection Mechanism Failure): emit baseline hardening response headers on every
			// response (X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, X-XSS-Protection,
			// and Content-Security-Policy-Report-Only). Registered before static files so all responses are covered.
			app.UseSecurityHeaders();

			//Should be before Static files
			app.UseResponseCompression();

			app.UseCors("AllowNodeJsLocalhost"); //Enable CORS -> should be before static files to enable for it too

			app.UseStaticFiles(new StaticFileOptions
			{
				ServeUnknownFileTypes = true,
				OnPrepareResponse = ctx =>
				{
					const int durationInSeconds = 60 * 60 * 24 * 30 * 12;
					ctx.Context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=" + durationInSeconds;
					ctx.Context.Response.Headers[HeaderNames.Expires] = new[] { DateTime.UtcNow.AddYears(1).ToString("R") }; // Format RFC1123
				}
			});
			app.UseStaticFiles(); //Workaround for blazor to work - https://github.com/dotnet/aspnetcore/issues/9588
			app.UseRouting();
			app.UseAuthentication();
			app.UseAuthorization();

			app
			//.UseErpPlugin<NextPlugin>()
			.UseErpPlugin<SdkPlugin>()
			.UseErp()
			.UseErpMiddleware();

			app.UseEndpoints(endpoints =>
			{
				endpoints.MapBlazorHub(); 
				endpoints.MapRazorPages();
				endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
			});
		}
	}
}

