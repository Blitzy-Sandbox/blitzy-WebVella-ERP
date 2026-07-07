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

            string configPath = "config.json";
            // SECURITY (A02/A05 / CWE-798): layer user-secrets (dev) and environment variables (prod) OVER config.json so secrets
            // removed from config.json (connection string, encryption key, JWT signing key) are supplied without being committed.
            Configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(configPath)
                .AddUserSecrets<Startup>()
                .AddEnvironmentVariables()
                .Build();

            services.AddLocalization(options => options.ResourcesPath = "Resources");
            services.Configure<RequestLocalizationOptions>(options => { options.DefaultRequestCulture = new RequestCulture(Configuration["Settings:Locale"]); });

            services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
            services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });
            services.AddRouting(options => { options.LowercaseUrls = true; });

            //CORS policy declaration
            //services.AddCors(options =>
            //{
            //    options.AddPolicy("AllowNodeJsLocalhost",
            //        builder => builder.WithOrigins("http://localhost:3333", "http://localhost:3000", "http://localhost").AllowAnyMethod().AllowCredentials());
            //});
            // SECURITY (A05 / CWE-942): explicit CORS origin allowlist instead of AllowAnyOrigin(). AllowCredentials() cannot be
            // combined with AllowAnyOrigin(); the allowlist keeps the Blazor WASM dev client and CKEditor upload flow working.
            services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.WithOrigins("http://localhost:3333", "http://localhost:3000", "http://localhost")
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials());
            });

            // SECURITY (A05 / CWE-319): configure HSTS so UseHsts() emits 'Strict-Transport-Security: max-age=31536000; includeSubDomains'.
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

            // SECURITY (A02/A05 / CWE-798): the JWT signing key must be supplied via user-secrets/environment variables (Settings:Jwt:Key);
            // no insecure default is baked in. Fail fast with a clear message if it is missing.
            var jwtKey = Configuration["Settings:Jwt:Key"];
            if (string.IsNullOrWhiteSpace(jwtKey))
                throw new InvalidOperationException("Settings:Jwt:Key is not configured. Provide it via user-secrets (dev) or the Settings__Jwt__Key environment variable (prod). See SECURITY.md.");

            services.AddAuthentication(options =>
            {
                options.DefaultScheme = "JWT_OR_COOKIE";
                options.DefaultChallengeScheme = "JWT_OR_COOKIE";
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.Name = "erp_auth_base";
                // SECURITY (A07 / CWE-614, CWE-1275): Secure (HTTPS-only) + SameSite=Lax prevent plaintext-HTTP transmission and mitigate CSRF.
                // (Secure presumes HTTPS — see UseHttpsRedirection/UseHsts in Configure.)
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Fully-qualified: both Microsoft.AspNetCore.Http and Microsoft.Net.Http.Headers (used for HeaderNames) define SameSiteMode;
                // CookieBuilder.SameSite is Microsoft.AspNetCore.Http.SameSiteMode. Qualifying avoids CS0104 without altering using directives.
                options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
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
                     IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
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
                // Add Error handling middleware which catches all application specific errors and
                // send the request to the following path or controller action.
                app.UseErrorHandlingMiddleware();
                app.UseExceptionHandler("/error");
                app.UseStatusCodePagesWithReExecute("/error");

                // SECURITY (A05 / CWE-319): enforce HTTPS transport and emit HSTS ONLY outside Development (avoids breaking local HTTP dev flows).
                // UseHsts() emits Strict-Transport-Security: max-age=31536000; includeSubDomains (configured via AddHsts).
                app.UseHttpsRedirection();
                app.UseHsts();
            }

            // SECURITY (A05 / CWE-693): emit the baseline security response headers (incl. report-only Content-Security-Policy) on EVERY
            // response; registered early (before static files) so static-file responses are covered too.
            app.UseSecurityHeaders();

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

