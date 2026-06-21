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

			//Portability (QA Issue 3 — Linux content-root casing): use the exact tracked casing "Config.json"
			//(capital C) so AddJsonFile resolves on case-sensitive file systems when run from the content root.
			string configPath = "Config.json";
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
				options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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

			//HSTS (A05/A07): emit Strict-Transport-Security with the prompt-specified value (1 year + includeSubDomains).
			services.AddHsts(options =>
			{
				options.MaxAge = TimeSpan.FromDays(365);
				options.IncludeSubDomains = true;
			});

			services.AddRateLimiter(options =>
			{
				options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

				//Named policy retained for explicit opt-in via [EnableRateLimiting("login")].
				options.AddFixedWindowLimiter("login", limiterOptions =>
				{
					limiterOptions.PermitLimit = 5;
					limiterOptions.Window = TimeSpan.FromMinutes(1);
					limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
					limiterOptions.QueueLimit = 0;
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
							PermitLimit = 5,
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
				// Enforce HTTPS via HSTS in non-development environments (paired with the Secure cookie policy).
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

