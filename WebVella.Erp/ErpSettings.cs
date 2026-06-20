using Microsoft.Extensions.Configuration;
using System;
using System.Net.Security;

namespace WebVella.Erp
{
	public static class ErpSettings
	{
		public static string EncryptionKey { get; private set; }
		public static string ConnectionString { get; private set; }
		public static string Lang { get; private set; }
		public static string Locale { get; private set; }
		public static string CacheKey { get; private set; }
		public static bool EnableBackgroundJobs { get; private set; }
		public static bool EnableFileSystemStorage { get; private set; }
		public static string FileSystemStorageFolder { get; set; }
		public static bool EnableCloudBlobStorage { get; set; }
		/// <summary>
		/// See https://github.com/aloneguid/storage/blob/develop/doc/blobs.md for details
		/// </summary>
		public static string CloudBlobStorageConnectionString { get; set; }
		public static string DevelopmentTestEntityName { get; set; }
		public static Guid DevelopmentTestRecordId { get; set; }
		public static string DevelopmentTestRecordViewName { get; set; }
		public static string DevelopmentTestRecordListName { get; set; }
		public static string TimeZoneName { get; set; }
		public static string JsonDateTimeFormat { get; set; }

		public static bool EmailEnabled { get; private set; }
		public static string EmailSMTPServerName { get; private set; }
		public static int EmailSMTPPort { get; private set; }
		public static string EmailSMTPUsername { get; private set; }
		public static string EmailSMTPPassword { get; private set; }
		public static string EmailFrom { get; private set; }
		public static string EmailTo { get; private set; }

		public static string NavLogoUrl { get; private set; }
		public static string SystemMasterBackgroundImageUrl { get; private set; }
		public static string AppName { get; private set; }

		public static bool ShowAccounting { get; set; }
		public static bool DevelopmentMode { get; private set; }
		public static int DefaultSRID { get; private set; } = 4326;

		public static IConfiguration Configuration { get; private set; }

		public static bool IsInitialized { get; private set; }

		public static string JwtKey { get; private set; }
		public static string JwtIssuer { get; private set; }
		public static string JwtAudience { get; private set; }

		//API URLs
		public static string ApiUrlTemplateFieldInlineEdit { get; private set; }

		// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188/CWE-798): Allowlist of JWT signing
		// keys that were ever shipped in source and are therefore publicly known. Startup fails fast
		// when the configured Settings:Jwt:Key matches any of these (ordinal, case-sensitive), so JWTs
		// can never be signed with a guessable, public key. The set is:
		//   * "ThisIsMySecretKey" - the short fallback default formerly hardcoded in this class.
		//   * the long literal below - the value committed to the host Config.json files before secret
		//     externalization (WebVella.Erp.Site / WebVella.Erp.Site.Project), which a deployment could
		//     still supply via an environment variable, user-secrets, or a secret-store overlay.
		// Add any future leaked/shipped default here so the fail-fast check stays exhaustive.
		private static readonly string[] InsecureJwtKeyDefaults = new[]
		{
			"ThisIsMySecretKey",
			"ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey"
		};

		public static void Initialize(IConfiguration configuration)
		{
			Configuration = configuration;
			EncryptionKey = configuration["Settings:EncryptionKey"];
			// 628426@gmail.com 27 Jul 2020 backwards compatibility for projects which still have mispelled EncryiptionKey in config
			if (string.IsNullOrWhiteSpace(EncryptionKey))
			{
				EncryptionKey = configuration["Settings:EncriptionKey"];
			}
			ConnectionString = configuration["Settings:ConnectionString"];
			Lang = string.IsNullOrWhiteSpace(configuration["Settings:Lang"]) ? @"en" : configuration["Settings:Lang"];
			// 125	FLE Standard Time	(GMT+02:00) Helsinki, Kiev, Riga, Sofia, Tallinn, Vilnius
			//TODO - disq about using as default hosting server timezone when not specified in configuration
			// 628426 - I think its better to use the current threads timezone as the default if you don't have one set?
			TimeZoneName = string.IsNullOrWhiteSpace(configuration["Settings:TimeZoneName"]) ? @"FLE Standard Time" : configuration["Settings:TimeZoneName"];
			JsonDateTimeFormat = string.IsNullOrWhiteSpace(configuration["Settings:JsonDateTimeFormat"]) ? "yyyy-MM-ddTHH:mm:ss.fff" : configuration["Settings:JsonDateTimeFormat"];

			Locale = string.IsNullOrWhiteSpace(configuration["Settings:Locale"]) ? "en-US" : configuration["Settings:Locale"];
			CacheKey = string.IsNullOrWhiteSpace(configuration["Settings:CacheKey"]) ? $"{DateTime.Now.ToString("yyyyMMdd")}" : configuration["Settings:CacheKey"];

			EnableFileSystemStorage = string.IsNullOrWhiteSpace(configuration["Settings:EnableFileSystemStorage"]) ? false : bool.Parse(configuration["Settings:EnableFileSystemStorage"]);
			FileSystemStorageFolder = string.IsNullOrWhiteSpace(configuration["Settings:FileSystemStorageFolder"]) ? @"c:\erp-files" : configuration["Settings:FileSystemStorageFolder"];

			EnableCloudBlobStorage = string.IsNullOrWhiteSpace(configuration["Settings:EnableCloudBlobStorage"]) ? false : bool.Parse(configuration["Settings:EnableCloudBlobStorage"]);
			CloudBlobStorageConnectionString = string.IsNullOrWhiteSpace(configuration["Settings:CloudBlobStorageConnectionString"]) ? "disk://path=c:\\erp-files" : configuration["Settings:CloudBlobStorageConnectionString"];

			EnableBackgroundJobs = string.IsNullOrWhiteSpace(configuration["Settings:EnableBackgroundJobs"]) ? true : bool.Parse(configuration["Settings:EnableBackgroundJobs"]);
			// 628426@gmail.com 15 Nov 2020 backwards compatibility for projects which still have mispelled EnableBackgroungJobs in config
			if (string.IsNullOrWhiteSpace(configuration["Settings:EnableBackgroundJobs"]))
			{
				EnableBackgroundJobs = string.IsNullOrWhiteSpace(configuration["Settings:EnableBackgroungJobs"]) ? true : bool.Parse(configuration["Settings:EnableBackgroungJobs"]);
			}

			DevelopmentTestEntityName = string.IsNullOrWhiteSpace(configuration["Development:TestEntityName"]) ? @"test" : configuration["Development:TestEntityName"];
			DevelopmentTestRecordId = new Guid("001ea36f-fd2e-4d1b-b8ee-25d32d4e396c");
			DevelopmentTestRecordViewName = "test";
			DevelopmentTestRecordListName = "test";
			var outGuid = Guid.Empty;
			if (!string.IsNullOrWhiteSpace(configuration["Development:TestRecordId"]) && Guid.TryParse(configuration["Development:TestRecordId"], out outGuid))
			{
				DevelopmentTestRecordId = outGuid;
			}

			EmailEnabled = string.IsNullOrWhiteSpace(configuration[$"Settings:EmailEnabled"]) ? false : bool.Parse(configuration[$"Settings:EmailEnabled"]);
			EmailSMTPServerName = configuration[$"Settings:EmailSMTPServerName"];
			EmailSMTPPort = string.IsNullOrWhiteSpace(configuration[$"Settings:EmailSMTPPort"]) ? 25 : int.Parse(configuration[$"Settings:EmailSMTPPort"]);
			EmailSMTPUsername = configuration[$"Settings:EmailSMTPUsername"];
			EmailSMTPPassword = configuration[$"Settings:EmailSMTPPassword"];
			EmailFrom = configuration[$"Settings:EmailFrom"];
			EmailTo = configuration[$"Settings:EmailTo"];

			NavLogoUrl = configuration[$"Settings:NavLogoUrl"];
			SystemMasterBackgroundImageUrl = configuration[$"Settings:SystemMasterBackgroundImageUrl"];
			AppName = configuration[$"Settings:AppName"];

			DevelopmentMode = string.IsNullOrWhiteSpace(configuration[$"Settings:DevelopmentMode"]) ? false : bool.Parse(configuration[$"Settings:DevelopmentMode"]);

			ShowAccounting = string.IsNullOrWhiteSpace(configuration[$"Settings:ShowAccounting"]) ? false : bool.Parse(configuration[$"Settings:ShowAccounting"]);


			ApiUrlTemplateFieldInlineEdit = string.IsNullOrWhiteSpace(configuration[$"ApiUrlTemplates:FieldInlineEdit"]) ? "/api/v3/en_US/record/{entityName}/{recordId}" : configuration[$"ApiUrlTemplates:FieldInlineEdit"];

			// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188/CWE-798): Conditional fail-fast on the
			// JWT signing key. The requirement is enforced ONLY when a key is configured: a present key must
			// not be one of the shipped insecure default literals (see InsecureJwtKeyDefaults; ordinal,
			// case-sensitive), rejecting BOTH the short code fallback default and the long literal previously
			// committed to the host Config.json files, so tokens can never be signed with a publicly-known key
			// even when an old value is supplied through an environment variable, user-secrets, or a secret-store
			// overlay. An ABSENT key is permitted (JwtKey stays null) so cookie-only hosts that do not use JWT
			// bearer auth can start; JWT-enabled hosts must configure a strong, unique key (>= 32 bytes) via
			// environment variable, user-secrets, or a secret store before starting the application.
			var jwtKey = configuration["Settings:Jwt:Key"];
			if (!string.IsNullOrWhiteSpace(jwtKey))
			{
				// A JWT signing key IS configured: it must not be one of the publicly-known shipped defaults.
				// Fail fast (ordinal, case-sensitive) so tokens can never be signed with a guessable, public key
				// even when an old value is supplied through an environment variable, user-secrets, or a secret
				// store. This preserves the strong fail-fast control for the dangerous case.
				if (IsInsecureJwtKeyDefault(jwtKey))
				{
					throw new Exception("Settings:Jwt:Key is set to one of the shipped insecure defaults. Configure a strong, unique JWT signing key (>= 32 bytes) via environment variable, user-secrets, or a secret store before starting the application.");
				}
				JwtKey = jwtKey;
			}
			else
			{
				// No JWT signing key configured. This is intentionally permitted so cookie-only hosts — which do
				// not use JWT bearer authentication and ship no Settings:Jwt section (WebVella.Erp.Site.Crm,
				// .Site.Mail, .Site.MicrosoftCDM, .Site.Next, .Site.Sdk) — can start without crashing. JwtKey
				// stays null and JWT token issuance/validation is simply unavailable on such a host; AuthService
				// reads ErpSettings.JwtKey only at request time (never at startup), so a null key cannot crash
				// startup. JWT-enabled hosts (WebVella.Erp.Site, .Site.Project) MUST supply a strong, unique key
				// via environment variable / user-secrets / a secret store, which is validated against the
				// insecure-default allowlist above whenever it is present.
				JwtKey = null;
			}
			// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188): Default issuer and audience are
			// DISTINCT so they are never equal when neither is configured. The configuration key strings
			// are unchanged (schema-preserving), so deployments that set explicit Settings:Jwt:Issuer /
			// Settings:Jwt:Audience are unaffected. Deployments that relied on the old shared default value
			// ("webvella-erp") must now set explicit matching issuer/audience in configuration.
			JwtIssuer = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Issuer"]) ? "webvella-erp-issuer" : configuration["Settings:Jwt:Issuer"];
			JwtAudience = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Audience"]) ? "webvella-erp-audience" : configuration["Settings:Jwt:Audience"];

			IsInitialized = true;
		}

		// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188/CWE-798): Returns true when the
		// supplied JWT signing key matches any known-insecure default that was previously shipped in
		// source (the short code fallback or a value committed to a host Config.json). Comparison is
		// ordinal and case-sensitive. The configured value is never echoed back to callers or logs.
		private static bool IsInsecureJwtKeyDefault(string jwtKey)
		{
			foreach (var insecureDefault in InsecureJwtKeyDefaults)
			{
				if (string.Equals(jwtKey, insecureDefault, StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}
	}
}
