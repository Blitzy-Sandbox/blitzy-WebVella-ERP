using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.RateLimiting;
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
using System.Threading.RateLimiting;
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
            // SECURITY (OWASP A05/A02 - CWE-798): layer environment variables OVER the JSON file so the externalized
            // JWT signing key ("Settings__Jwt__Key"), connection string ("Settings__ConnectionString") and encryption
            // key ("Settings__EncryptionKey") supplied at deploy time are observed here. The SymmetricSecurityKey built
            // below from Configuration["Settings:Jwt:Key"] relies on this so a real (non-empty, non-default) key can be
            // provided without committing it to source. Standard precedence applies (env vars override JSON); all
            // configuration KEY names are unchanged (schema-preserving).
            Configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile(configPath).AddEnvironmentVariables().Build();

            services.AddLocalization(options => options.ResourcesPath = "Resources");
            services.Configure<RequestLocalizationOptions>(options => { options.DefaultRequestCulture = new RequestCulture(Configuration["Settings:Locale"]); });

            services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
            services.AddResponseCompression(options => { options.Providers.Add<GzipCompressionProvider>(); });
            services.AddRouting(options => { options.LowercaseUrls = true; });

            //CORS policy declaration (A05): explicit origin allowlist read from Config.json "Settings:CorsOrigins",
            //with a safe localhost fallback when the key is missing/empty. WithOrigins(...) is REQUIRED because
            //AllowAnyOrigin() is incompatible with AllowCredentials() (this host uses cookie auth and ASP.NET Core
            //throws at runtime if both are combined). Origins must have NO trailing slash (exact-match).
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

            //Edge brute-force / DoS protection (A04): in-framework fixed-window rate limiter (net10, no new package).
            //A conservative "login" window pairs with the account-level lockout in Pages/login.cshtml.cs; requests
            //over the limit receive HTTP 429. UseRateLimiter() is added to the pipeline after UseRouting() below.
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
                //Cookie security flags (A07): require HTTPS transport and constrain cross-site sending.
                //SameSite=Lax preserves login redirect flows; Secure=Always requires HTTPS (paired with UseHsts below).
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                //Fully qualified: SameSiteMode is ambiguous because this file also imports Microsoft.Net.Http.Headers
                //(for HeaderNames). CookieBuilder.SameSite expects Microsoft.AspNetCore.Http.SameSiteMode.
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
                // HSTS (A05 / transport): instruct browsers to use HTTPS only. Added in the non-Development branch
                // only (HSTS can poison localhost during dev). Complements the Strict-Transport-Security header
                // emitted by SecurityHeadersMiddleware and is the operational prerequisite for the Secure auth cookie.
                app.UseHsts();

                // Add Error handling middleware which catches all application specific errors and
                // send the request to the following path or controller action.
                app.UseErrorHandlingMiddleware();
                app.UseExceptionHandler("/error");
                app.UseStatusCodePagesWithReExecute("/error");
            }

            //Should be before Static files
            app.UseResponseCompression();

            app.UseCors("ErpCorsPolicy"); //Enable CORS (named origin-allowlist policy) -> before static files so it applies to them too

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

            //Edge rate limiter (A04): placed AFTER UseRouting so endpoint-specific limiters can resolve endpoint
            //metadata, and BEFORE UseAuthentication so throttling applies to unauthenticated login attempts too.
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

