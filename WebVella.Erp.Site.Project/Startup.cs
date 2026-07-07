using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

			string configPath = "config.json";
			// SECURITY (A02/A05 secret migration — CWE-798): read environment variables so secrets removed from config.json
			// (Settings:ConnectionString, Settings:EncryptionKey, Settings:Jwt:Key) resolve at runtime from env (e.g. Settings__Jwt__Key).
			// Env vars override config.json; deployments that still keep values in config.json are unaffected.
			Configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile(configPath).AddEnvironmentVariables().Build();


			services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
			services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });
			services.AddRouting(options => { options.LowercaseUrls = true; });

			//CORS policy declaration
			// SECURITY (A05 overly-permissive CORS — CWE-942): AllowAnyOrigin() lets ANY website call the API. Replaced with an
			// explicit origin allowlist. AllowAnyOrigin() also cannot be combined with AllowCredentials(); the named policy keeps
			// the Blazor WASM dev origin (:3333) and the CKEditor upload flow working while blocking arbitrary cross-origin callers.
			services.AddCors(options =>
			{
				options.AddPolicy("AllowNodeJsLocalhost",
					builder => builder.WithOrigins("http://localhost:3333", "http://localhost:3000", "http://localhost", "http://localhost:2202").AllowAnyMethod().AllowCredentials());
			});

			// SECURITY (A02/A05 cleartext transport - CWE-319): configure HSTS so app.UseHsts() (in Configure) emits the
			// mandated baseline 'Strict-Transport-Security: max-age=31536000; includeSubDomains' (365 days) rather than the
			// ASP.NET Core default (30 days, no includeSubDomains), matching the sibling hosts' security-header standard.
			services.AddHsts(options =>
			{
				options.MaxAge = TimeSpan.FromDays(365);
				options.IncludeSubDomains = true;
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
				// SECURITY (A07 insecure session cookie — CWE-614 cleartext transport / CWE-1275 missing SameSite):
				// force the auth cookie to travel only over HTTPS and add CSRF mitigation via SameSite=Lax.
				options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
				// Fully qualified to disambiguate from Microsoft.Net.Http.Headers.SameSiteMode (both namespaces are imported).
				options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
				options.Cookie.Name = "erp_auth_project";
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

			services.AddErp();
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			app.UseRequestLocalization(new RequestLocalizationOptions
			{
				DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(CultureInfo.GetCultureInfo("en-US"))
			});

			// SECURITY (A05 missing security headers — CWE-693): emit the hardening response-header baseline
			// (X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, X-XSS-Protection, CSP report-only)
			// for EVERY response. Registered early so headers apply to static files, errors, and all pages.
			app.UseSecurityHeaders();

			//env.EnvironmentName = EnvironmentName.Production;
			// Add the following to the request pipeline only in development environment.
			if (string.Equals(env.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
			{
				app.UseDeveloperExceptionPage();
			}
			else
			{
				// SECURITY (A02/A05 cleartext transport — CWE-319): enforce HTTPS and enable HSTS outside development
				// (dev typically serves plain HTTP). UseHsts() emits Strict-Transport-Security: max-age + includeSubDomains.
				app.UseHsts();
				app.UseHttpsRedirection();
				// Add Error handling middleware which catches all application specific errors and
				// send the request to the following path or controller action.
				app.UseErrorHandlingMiddleware();
				app.UseExceptionHandler("/error");
				app.UseStatusCodePagesWithReExecute("/error");
			}

			//Should be before Static files
			app.UseResponseCompression();

            // SECURITY (A05 — CWE-942): apply the named origin allowlist instead of the removed permissive default policy.
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

