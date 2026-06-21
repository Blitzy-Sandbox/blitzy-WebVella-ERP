using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WebVella.Erp.Api;
using WebVella.Erp.Api.Models.AutoMapper;
using WebVella.Erp.Database;
using WebVella.Erp.Jobs;
using WebVella.Erp.Utilities;
using WebVella.Erp.Web.Middleware;
using WebVella.Erp.Web.Models;
using WebVella.Erp.Web.Models.AutoMapper;
using WebVella.Erp.Web.Services;
using WebVella.TagHelpers;

namespace WebVella.Erp.Web
{
	public static class ErpMvcServicesExtensions
	{
		public static IServiceCollection AddErp(this IServiceCollection services)
		{
			services.AddSingleton<IErpService, ErpService>();
			services.AddTransient<AuthService>();
			services.AddScoped<ErpRequestContext>();
			services.Configure<RazorViewEngineOptions>(options => { options.ViewLocationExpanders.Add(new ErpViewLocationExpander()); });
			services.ConfigureOptions(typeof(WebConfigurationOptions));
			services.AddSingleton<IHostedService, ErpJobScheduleService>();
			services.AddSingleton<IHostedService, ErpJobProcessService>();
			services.AddScoped<CircuitHandler, SecuritityCircuitHandler>();

			//Security: password hashing strategy (salted PBKDF2 KDF) registered once for all hosts (A02/A07)
			services.AddSingleton<IPasswordHasher, ErpPasswordHasher>();

			//Security: JSON deserialization allowlist binder (A08) - register the shared instance used at the TypeNameHandling sites
			services.AddSingleton(ErpSerializationBinder.Instance);

			//Security (A07 - Authentication/Session Failures, CWE-613): server-side authentication ticket store so that
			//logout truly invalidates the session and a replayed pre-logout cookie can no longer authenticate. Without a
			//SessionStore the cookie handler embeds the whole ticket in the (self-contained, encrypted) cookie, so
			//SignOutAsync only deletes the browser's copy and a captured cookie stays valid until ExpiresUtc. Backing the
			//handler with MemoryCacheTicketStore makes the cookie carry only an opaque session key; SignOutAsync then
			//removes the server-side entry, after which the replayed cookie resolves to a missing key and is rejected.
			//AddMemoryCache() is idempotent (TryAdd-based) so it is safe even if a host also registers it. The
			//post-configuration targets the DEFAULT cookie scheme - the scheme every WebVella.Erp.Site* host authenticates
			//with - and runs after each host's AddAuthentication().AddCookie(...), so this single central registration
			//covers all seven hosts without per-host wiring and without changing AuthService's public API.
			services.AddMemoryCache();
			services.AddSingleton<ITicketStore, MemoryCacheTicketStore>();
			services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
				.Configure<ITicketStore>((options, ticketStore) => options.SessionStore = ticketStore);

			return services;
		}

		/// <summary>
		/// Registers the <see cref="SecurityHeadersMiddleware"/> (A05 - Security Misconfiguration) at the CURRENT
		/// position in the request pipeline. This is intentionally a SEPARATE entry point from <see cref="UseErp"/>
		/// so every host can place the security-headers middleware at the very FRONT of its pipeline - ahead of
		/// UseStaticFiles, UseAuthentication and UseAuthorization. That placement makes the mandated security
		/// headers decorate EVERY response surface (including static files and the short-circuited 302
		/// authentication-challenge redirects, which UseErp() runs too late to cover), and - because OnStarting
		/// callbacks fire LIFO - makes this middleware's callback the LAST to run, so it can overwrite the
		/// framework's Razor X-Frame-Options value with the required DENY. The middleware definition stays
		/// centralized here, so all seven WebVella.Erp.Site* hosts inherit identical behavior from this single source.
		/// </summary>
		public static IApplicationBuilder UseErpSecurityHeaders(this IApplicationBuilder app)
		{
			app.UseMiddleware<SecurityHeadersMiddleware>();
			return app;
		}

		public static IApplicationBuilder UseErp(this IApplicationBuilder app, List<JobType> additionalJobTypes = null, string configFolder = null)
		{
			using (var secCtx = SecurityContext.OpenSystemScope())
			{
				IConfiguration configuration = app.ApplicationServices.GetService<IConfiguration>();
				IWebHostEnvironment env = app.ApplicationServices.GetService<IWebHostEnvironment>();

				if (!ErpSettings.IsInitialized) {
					// Security/portability (QA Issue 3 — Linux content-root casing): the committed configuration
					// file is tracked as "Config.json" (capital C). On case-sensitive file systems (Linux) the
					// loader MUST request the exact tracked casing, otherwise AddJsonFile throws
					// FileNotFoundException: config.json when the host runs from its normal content root. This is
					// the single shared config loader the five cookie-only hosts (Crm/Mail/MicrosoftCDM/Next/Sdk)
					// rely on, so the casing fix here covers them all. Safe on Windows/macOS (case-insensitive).
					string configPath = "Config.json";
					if (!string.IsNullOrWhiteSpace(configFolder))
						configPath = System.IO.Path.Combine(configFolder, configPath);

					// Security (A02/A05 - CWE-798/CWE-1188): layer environment-variable configuration on top of
					// the committed JSON so externalized secrets reach ErpSettings.Initialize. ASP.NET Core maps
					// the "__" delimiter in an environment variable name to the ":" configuration hierarchy, so
					// Settings__EncryptionKey -> Settings:EncryptionKey and Settings__Jwt__Key -> Settings:Jwt:Key.
					// Environment variables are added LAST so they OVERRIDE the JSON placeholders, allowing
					// Config.json to ship empty/placeholder secret values while the real secrets are supplied at
					// runtime via environment variables / user-secrets / a secret store. This is the single
					// configuration source used by every host, so all 7 site hosts inherit identical secret-overlay
					// behavior regardless of how their own Startup builds the host IConfiguration.
					var configurationBuilder = new ConfigurationBuilder()
						.SetBasePath(env.ContentRootPath)
						.AddJsonFile(configPath)
						.AddEnvironmentVariables();
					ErpSettings.Initialize(configurationBuilder.Build());
				}

				var defaultThreadCulture = CultureInfo.DefaultThreadCurrentCulture;
				var defaultThreadUICulture = CultureInfo.DefaultThreadCurrentUICulture;

				CultureInfo customCulture = new CultureInfo("en-US");
				customCulture.NumberFormat.NumberDecimalSeparator = ".";

				IErpService service = null;
				try
				{
					DbContext.CreateContext(ErpSettings.ConnectionString);

					service = app.ApplicationServices.GetService<IErpService>();

					var cfg = ErpAutoMapperConfiguration.MappingExpressions; // var cfg = new AutoMapper.Configuration.MapperConfigurationExpression();
					ErpAutoMapperConfiguration.Configure(cfg);
					ErpWebAutoMapperConfiguration.Configure(cfg);

					//this method append plugin automapper configuration
					service.SetAutoMapperConfiguration();

					//this should be called after plugin init
					ErpAutoMapper.Initialize(cfg);

					//we used en-US based culture settings for initialization and patch execution
					{
						CultureInfo.DefaultThreadCurrentCulture = customCulture;
						CultureInfo.DefaultThreadCurrentUICulture = customCulture;

						service.InitializeSystemEntities();

						CultureInfo.DefaultThreadCurrentCulture = defaultThreadCulture;
						CultureInfo.DefaultThreadCurrentUICulture = defaultThreadUICulture;
					}

					CheckCreateHomePage();

					service.InitializeBackgroundJobs(additionalJobTypes);

					ErpAppContext.Init(app.ApplicationServices);

					{
						//switch culture for patch executions and initializations
						CultureInfo.DefaultThreadCurrentCulture = customCulture;
						CultureInfo.DefaultThreadCurrentUICulture = customCulture;

						//this is called after automapper setup
						service.InitializePlugins(app.ApplicationServices);

						CultureInfo.DefaultThreadCurrentCulture = defaultThreadCulture;
						CultureInfo.DefaultThreadCurrentUICulture = defaultThreadUICulture;
					}

				}
				finally
				{
					DbContext.CloseContext();
					CultureInfo.DefaultThreadCurrentCulture = defaultThreadCulture;
					CultureInfo.DefaultThreadCurrentUICulture = defaultThreadUICulture;
				}

				//this is handled by background services now
				//if (service != null)
				//	service.StartBackgroundJobProcess();

				return app;
			}
		}

		public static IApplicationBuilder UseErpPlugin<T>(this IApplicationBuilder app) where T : ErpPlugin, new()
		{
			using (var secCtx = SecurityContext.OpenSystemScope())
			{
				var plugin = new T();
				var service = app.ApplicationServices.GetService<IErpService>();
				service.Plugins.Add(plugin);
				return app;
			}
		}

		private static void CheckCreateHomePage()
		{
			var pageSrv = new PageService();

			var pageId = new Guid("560e77c5-6184-418e-8d49-51ae83c9773d");
			var name = @"home";
			var label = "Home";
			string iconClass = null;
			var system = false;
			var layout = @"";
			var weight = 10;
			var type = (PageType)((int)0);
			var isRazorBody = false;
			Guid? appId = null;
			Guid? entityId = null;
			Guid? nodeId = null;
			Guid? areaId = null;
			string razorBody = null;
			var labelTranslations = new List<TranslationResource>();

			using (var connection = DbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();
					if (!pageSrv.GetAll(transaction: DbContext.Current.Transaction, useCache: false).Any(x => x.Id == pageId))
					{
						pageSrv.CreatePage(pageId, name, label, labelTranslations, iconClass, system, weight, type, appId, entityId, nodeId, areaId, isRazorBody, razorBody, layout, WebVella.Erp.Database.DbContext.Current.Transaction);
						pageSrv.CreatePageBodyNode(new Guid("3a4e8154-9f48-4ba5-9e11-36fa5e7a80c9"), null, pageId, null, 1, "WebVella.Erp.Web.Components.PcApplications", "", @"""{}""", WebVella.Erp.Database.DbContext.Current.Transaction);
					}
					connection.CommitTransaction();
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}
	}
}
